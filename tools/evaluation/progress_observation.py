"""Offline-only v4 observation ablation; never rewrite MJAI winds or legal masks."""
import hashlib
import math
from pathlib import Path

NATIVE = "native"
EAST_PROGRESS = "east-progress-v1"
MODES = (NATIVE, EAST_PROGRESS)
PROGRESS_ROW = 27


def adapt_east_progress(obs):
    # Pinned libriichi 0cff2b5, state/obs_repr.rs: v4 scores occupy eight rows,
    # kyoku one-hot 19:23, honba/sticks 23:25, bakaze/jikaze 25:27.
    if obs.shape != (1012, 34):
        raise ValueError("East progress requires the verified v4 (1012, 34) layout")
    if any(float(value) != (1.0 if col == 27 else 0.0) for col, value in enumerate(obs[25])):
        raise ValueError("East progress only accepts real East wind observations")
    active = []
    for kyoku in range(4):
        row = obs[19 + kyoku]
        if all(float(value) == 1.0 for value in row):
            active.append(kyoku)
        elif any(float(value) != 0.0 for value in row):
            raise ValueError("Unrecognized kyoku encoding")
    if len(active) != 1:
        raise ValueError("Expected exactly one active kyoku channel")
    kyoku = active[0]
    if not all(math.isclose(float(value), kyoku / 7, abs_tol=1e-6) for value in obs[PROGRESS_ROW]):
        raise ValueError("Unexpected native progress encoding; refusing double adaptation")
    result = obs.copy()
    result[PROGRESS_ROW][:] = [(kyoku + 4) / 7] * 34
    return result


class EastProgressEngine:
    def __init__(self, engine):
        if engine.version != 4 or engine.is_oracle:
            raise ValueError("East progress requires a non-oracle v4 engine")
        self.engine = engine
        self.adapted_observations = 0
        self.model_identity = dict(engine.model_identity, observation_transform=EAST_PROGRESS,
            observation_transform_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())

    def __getattr__(self, name):
        return getattr(self.engine, name)

    def react_batch(self, obs, masks, invisible_obs):
        if invisible_obs is not None:
            raise ValueError("Hidden observations are forbidden in progress ablation")
        adapted = [adapt_east_progress(sample) for sample in obs]
        self.adapted_observations += len(adapted)
        return self.engine.react_batch(adapted, masks, invisible_obs)


def apply_candidate_transform(engines, mode, match_mode):
    if mode == NATIVE:
        return
    if mode != EAST_PROGRESS or match_mode != "doman-quick":
        raise ValueError("East progress is offline-only and requires doman-quick")
    if engines["candidate"].model_identity["checkpoint_sha256"] != engines["baseline"].model_identity["checkpoint_sha256"]:
        raise ValueError("Progress ablation requires identical candidate and baseline checkpoints")
    engines["candidate"] = EastProgressEngine(engines["candidate"])


def describe_ablation(rules):
    rules["rule_differences"] = [item for item in rules["rule_differences"]
        if not item.startswith("Existing hanchan checkpoint observations")]
    rules["rule_differences"].append(
        "Candidate alone remaps v4 progress to (kyoku_zero_based+4)/7; real East wind, "
        "kyoku, scoring and legal masks remain unchanged. Baseline/opponents retain native encoding. "
        "This untrained feature combination is an experimental distribution shift, not quick-match training.")
