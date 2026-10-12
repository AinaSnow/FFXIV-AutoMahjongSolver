"""Versioned acknowledgement envelope around unchanged MJAI events. Run in Mortal/mortal."""
import hashlib
import io
import json
import os
import sys
import time


_model_identity = None


def load_checkpoint(torch_module, path):
    # Hash exactly the bytes passed to torch, not a later read of a replaceable checkpoint path.
    with open(path, "rb") as checkpoint:
        payload = checkpoint.read()
    identity = {"checkpoint_sha256": hashlib.sha256(payload).hexdigest(), "checkpoint_bytes": len(payload)}
    return torch_module.load(io.BytesIO(payload), weights_only=True, map_location="cpu"), identity


def create_engine(*, checkpoint_path=None, name="mortal", device_name=None, enable_amp=None, startup_timings=None):
    stage_started = time.perf_counter()
    def mark(name):
        nonlocal stage_started
        now = time.perf_counter()
        if startup_timings is not None:
            startup_timings[name] = (now - stage_started) * 1000
        stage_started = now

    sys.path.insert(0, os.getcwd())
    import prelude  # noqa: F401
    import torch
    from config import config
    from model import Brain, DQN
    from engine import MortalEngine
    mark("imports_ms")
    if device_name is None:
        device_name = config["control"].get("device", "cpu")
    if str(device_name).startswith("cuda") and not torch.cuda.is_available():
        device_name = "cpu"
    device = torch.device(device_name)
    mark("device_check_ms")
    global _model_identity
    state, identity = load_checkpoint(torch, checkpoint_path if checkpoint_path is not None else config["control"]["state_file"])
    mark("checkpoint_read_hash_load_ms")
    cfg = state["config"]
    version = cfg["control"].get("version", 1)
    identity["architecture_version"] = version
    with open(__file__, "rb") as runner:
        identity["runner_sha256"] = hashlib.sha256(runner.read()).hexdigest()
    brain = Brain(version=version, **{k: cfg["resnet"][k] for k in ("num_blocks", "conv_channels")}).eval()
    dqn = DQN(version=version).eval()
    brain.load_state_dict(state["mortal"])
    dqn.load_state_dict(state["current_dqn"])
    mark("model_construct_load_ms")
    if enable_amp is None:
        enable_amp = bool(config["control"].get("enable_amp", False))
    engine = MortalEngine(brain, dqn, version=version, is_oracle=False, device=device,
                          enable_amp=enable_amp and device.type == "cuda",
                          enable_quick_eval=True, enable_rule_based_agari_guard=True, name=name)
    mark("engine_device_setup_ms")
    # Each offline role retains its own identity; subsequent loads must not relabel it.
    engine.model_identity = identity.copy()
    _model_identity = identity.copy()
    return engine


NATIVE = "native"
EAST_PROGRESS = "east-progress-v1"
OBSERVATION_MODES = (NATIVE, EAST_PROGRESS)
PROGRESS_ROW = 27


def east_progress_or_none(obs):
    """Same remap as tools/evaluation/progress_observation.adapt_east_progress (accepted
    2026-10-12, 1500 groups), but returns None instead of raising when the observation is
    not a real East-round v4 layout, so South/West extensions keep the native input."""
    if tuple(obs.shape) != (1012, 34):
        return None
    if any(float(value) != (1.0 if col == 27 else 0.0) for col, value in enumerate(obs[25])):
        return None
    active = []
    for kyoku in range(4):
        row = obs[19 + kyoku]
        if all(float(value) == 1.0 for value in row):
            active.append(kyoku)
        elif any(float(value) != 0.0 for value in row):
            return None
    if len(active) != 1:
        return None
    kyoku = active[0]
    if not all(abs(float(value) - kyoku / 7) <= 1e-6 for value in obs[PROGRESS_ROW]):
        return None
    result = obs.copy()
    result[PROGRESS_ROW][:] = [(kyoku + 4) / 7] * 34
    return result


class LiveObservationEngine:
    """Per-request observation mode for East-only matches; native passes inputs through untouched."""
    def __init__(self, engine):
        self.engine = engine
        self.mode = NATIVE
        self.adapted = self.passthrough = 0

    def __getattr__(self, name):
        return getattr(self.engine, name)

    def react_batch(self, obs, masks, invisible_obs):
        if self.mode == EAST_PROGRESS and invisible_obs is None:
            remapped = []
            for sample in obs:
                adapted = east_progress_or_none(sample)
                if adapted is None:
                    self.passthrough += 1
                    remapped.append(sample)
                else:
                    self.adapted += 1
                    remapped.append(adapted)
            obs = remapped
        return self.engine.react_batch(obs, masks, invisible_obs)


def create_bot(startup_timings=None):
    engine = LiveObservationEngine(create_engine(startup_timings=startup_timings))
    started = time.perf_counter()
    from libriichi.mjai import Bot
    bot = Bot(engine, 0)
    if startup_timings is not None:
        startup_timings["bot_setup_ms"] = (time.perf_counter() - started) * 1000
    return bot, engine


def serve(bot, source, sink, model_identity=None, engine=None):
    last_sequence = 0
    session = None
    for line in source:
        request = json.loads(line)
        response = {k: request[k] for k in ("session", "hand", "sequence")}
        try:
            if session is None:
                session = request["session"]
            if request["session"] != session or request["sequence"] != last_sequence + 1:
                raise ValueError("session or input sequence mismatch")
            last_sequence = request["sequence"]
            if last_sequence == 1 and model_identity is not None:
                response["model"] = model_identity
            mode = request.get("observation", NATIVE)
            if mode not in OBSERVATION_MODES:
                raise ValueError(f"unsupported observation mode {mode!r}")
            if engine is not None:
                engine.mode = mode
            elif mode != NATIVE:
                raise ValueError("observation mode requires a live observation engine")
            if mode != NATIVE:
                response["observation"] = mode
            reaction = bot.react(json.dumps(request["event"], separators=(",", ":")))
            response["reaction"] = json.loads(reaction) if reaction and request["event"].get("can_act", True) else None
        except Exception as exc:
            response["error"] = f"{type(exc).__name__}: {exc}"
        sink.write(json.dumps(response, separators=(",", ":")) + "\n")
        sink.flush()
        if "error" in response:
            return


if __name__ == "__main__":
    timings = {}
    started = time.perf_counter()
    ready = False
    try:
        bot, live_engine = create_bot(startup_timings=timings)
        ready = True
    finally:
        # stderr only: stdout remains the correlated MJAI response stream.
        print(json.dumps(dict(type="mortal_startup", ready=ready, stages_ms=timings,
                              total_ms=(time.perf_counter() - started) * 1000)), file=sys.stderr, flush=True)
    serve(bot, sys.stdin, sys.stdout, _model_identity, live_engine)
