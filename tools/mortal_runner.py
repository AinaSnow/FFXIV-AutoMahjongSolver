"""Versioned acknowledgement envelope around unchanged MJAI events. Run in Mortal/mortal."""
import json
import os
import sys


def create_engine():
    sys.path.insert(0, os.getcwd())
    import prelude  # noqa: F401
    import torch
    from config import config
    from model import Brain, DQN
    from engine import MortalEngine
    from libriichi.mjai import Bot
    device_name = config["control"].get("device", "cpu")
    if str(device_name).startswith("cuda") and not torch.cuda.is_available():
        device_name = "cpu"
    device = torch.device(device_name)
    state = torch.load(config["control"]["state_file"], weights_only=True, map_location="cpu")
    cfg = state["config"]
    version = cfg["control"].get("version", 1)
    brain = Brain(version=version, **{k: cfg["resnet"][k] for k in ("num_blocks", "conv_channels")}).eval()
    dqn = DQN(version=version).eval()
    brain.load_state_dict(state["mortal"])
    dqn.load_state_dict(state["current_dqn"])
    engine = MortalEngine(brain, dqn, version=version, is_oracle=False, device=device,
                          enable_amp=bool(config["control"].get("enable_amp", False)) and device.type == "cuda",
                          enable_quick_eval=True, enable_rule_based_agari_guard=True, name="mortal")
    return engine


def create_bot():
    engine = create_engine()
    from libriichi.mjai import Bot
    return Bot(engine, 0)


def serve(bot, source, sink):
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
            reaction = bot.react(json.dumps(request["event"], separators=(",", ":")))
            response["reaction"] = json.loads(reaction) if reaction and request["event"].get("can_act", True) else None
        except Exception as exc:
            response["error"] = f"{type(exc).__name__}: {exc}"
        sink.write(json.dumps(response, separators=(",", ":")) + "\n")
        sink.flush()
        if "error" in response:
            return


if __name__ == "__main__":
    serve(create_bot(), sys.stdin, sys.stdout)
