"""Validated player-visible exports. Final outcomes are labels, never observations."""
import hashlib
import json
from pathlib import Path

OBS_SHAPES = {1: (938, 34), 2: (942, 34), 3: (934, 34), 4: (1012, 34)}
ARRAY_KEYS = {"obs", "masks", "actions", "hand_index", "dones", "apply_gamma"}


def file_hash(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def hand_contexts(events):
    hands = []
    active = None
    scores = None
    resolved = False
    for event in events:
        kind = event["type"]
        if kind == "start_kyoku":
            current = event.get("scores")
            if active is not None or not isinstance(current, list) or len(current) != 4:
                raise ValueError("Invalid hand opening or scores")
            if any(type(value) is not int for value in current):
                raise ValueError("Scores must be integers")
            if scores is not None and scores != current:
                raise ValueError("Hand scores disagree with previous result")
            scores = current.copy()
            active = dict(hand_index=len(hands), **{key: event[key] for key in
                ("bakaze", "kyoku", "honba", "kyotaku", "oya", "scores")})
            hands.append(active)
            resolved = False
        elif kind == "reach_accepted":
            if active is None or event.get("actor") not in range(4):
                raise ValueError("Riichi outside an active hand")
            scores[event["actor"]] -= 1000
        elif kind in ("hora", "ryukyoku"):
            deltas = event.get("deltas")
            if active is None or not isinstance(deltas, list) or len(deltas) != 4 or any(type(v) is not int for v in deltas):
                raise ValueError("Missing or invalid result deltas")
            scores = [a+b for a,b in zip(scores, deltas)]
            resolved = True
        elif kind == "end_kyoku":
            if active is None or not resolved:
                raise ValueError("Hand ended without a scored result")
            active = None
    if active is not None or not hands:
        raise ValueError("No complete hands")
    return hands, scores


def validate_outcome(scores, ranks, reconstructed):
    if len(scores) != 4 or len(ranks) != 4 or any(type(v) is not int for v in scores+ranks):
        raise ValueError("Invalid final outcome")
    # Pinned Grp awards leftover sticks to the top player. Keep this rule explicit.
    expected = list(reconstructed)
    top = min(range(4), key=lambda seat: (-expected[seat], seat))
    remainder = 100000 - sum(expected)
    if remainder < 0 or remainder % 1000:
        raise ValueError("Final scores do not conserve points and sticks")
    expected[top] += remainder
    order = sorted(range(4), key=lambda seat: (-expected[seat], seat))
    expected_ranks = [order.index(seat)+1 for seat in range(4)]
    if scores != expected or ranks != expected_ranks:
        raise ValueError("Final labels disagree with reconstructed scores/ranks")


def validate_arrays(arrays, version, hand_count):
    import numpy as np
    if set(arrays) != ARRAY_KEYS:
        raise ValueError("Unexpected player export fields (hidden/future inputs forbidden)")
    obs, masks, actions = arrays["obs"], arrays["masks"], arrays["actions"]
    n = len(actions)
    if version not in OBS_SHAPES or obs.shape != (n, *OBS_SHAPES[version]) or masks.shape != (n, 46):
        raise ValueError("Observation/action/mask shape mismatch")
    if obs.dtype != np.float32 or not np.isfinite(obs).all() or masks.dtype != np.bool_:
        raise ValueError("Invalid observation or mask dtype/values")
    for key in ARRAY_KEYS - {"obs", "masks"}:
        if arrays[key].shape != (n,):
            raise ValueError("Unaligned decision metadata")
    if actions.dtype.kind not in "iu" or np.any(actions < 0) or np.any(actions >= 46) or not masks[np.arange(n), actions].all():
        raise ValueError("Illegal action in training sample")
    hands = arrays["hand_index"]
    if hands.dtype.kind not in "iu" or np.any(hands < 0) or np.any(hands >= hand_count) or np.any(hands[1:] < hands[:-1]):
        raise ValueError("Invalid decision-to-hand mapping")
    expected_done = np.r_[hands[1:] != hands[:-1], True] if n else np.empty(0, dtype=np.bool_)
    if arrays["dones"].dtype != np.bool_ or arrays["apply_gamma"].dtype != np.bool_ or not np.array_equal(expected_done, arrays["dones"]):
        raise ValueError("Invalid hand boundary flags")
    return n


def export_player_views(loader, raw, directory, metadata, hands, scores, ranks, version):
    import numpy as np
    games = loader.load_log(raw)
    if sorted(game.take_player_id() for game in games) != [0, 1, 2, 3]:
        raise ValueError("Missing player perspectives")
    directory.mkdir(parents=True, exist_ok=False)
    records = []
    for game in games:
        seat = game.take_player_id()
        if game.take_invisible_obs():
            raise ValueError("Oracle observations must not enter ordinary model training")
        arrays = dict(obs=np.asarray(game.take_obs()), masks=np.asarray(game.take_masks()),
            actions=np.asarray(game.take_actions(), dtype=np.int64),
            hand_index=np.asarray(list(game.take_at_kyoku()), dtype=np.int64),
            dones=np.asarray(game.take_dones(), dtype=np.bool_),
            apply_gamma=np.asarray(game.take_apply_gamma(), dtype=np.bool_))
        if arrays["obs"].size == 0:
            arrays["obs"] = np.empty((0, *OBS_SHAPES[version]), dtype=np.float32)
            arrays["masks"] = np.empty((0, 46), dtype=np.bool_)
        count = validate_arrays(arrays, version, len(hands))
        path = directory/f"player-{seat}.npz"
        pending = path.with_suffix(".pending.npz")
        np.savez_compressed(pending, **arrays)
        pending.replace(path)
        # No pickle, and verify the bytes that will be consumed by later training.
        with np.load(path, allow_pickle=False) as loaded:
            validate_arrays(loaded, version, len(hands))
        labels = dict(**metadata, player_id=seat, architecture_version=version, player_decisions=count,
            final_rank=ranks[seat], final_score=scores[seat], score_delta=scores[seat]-25000,
            hands=hands, observation_transform="native", oracle=False,
            feature_fields=["obs", "masks"], labels_are_model_inputs=False)
        label_path = directory/f"player-{seat}.labels.json"
        label_path.write_text(json.dumps(labels, indent=2), encoding="utf-8")
        records.append(dict(player_id=seat, player_decisions=count, path=path.name,
            sha256=file_hash(path), labels_path=label_path.name, labels_sha256=file_hash(label_path)))
    return records
