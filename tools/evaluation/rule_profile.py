"""Rule metadata; experimental modes require an independently built native engine."""
SUPPORTED_MODE = "libriichi-hanchan"
REQUESTED_MODES = (SUPPORTED_MODE, "doman-quick", "doman-full")
RULE_DIFFERENCES = [
    "Pinned OneVsThree runs standard eight-hand hanchan, not Doman quick four-hand matches",
    "libriichi extends beyond scheduled rounds below 30000; Doman has no extension",
    "libriichi final-dealer finish requires 30000 and first place; Doman does not require 30000",
    "Doman single-yakuman values, four-yakuman cap, multiple wins, abortive draws, nagashi and pao need engine parity tests",
    "Doman 80/120-minute limits and game input latency are not simulated",
]


DOMAN_DIFFERENCES = [
    "Experimental rules, not a certification of complete Doman parity",
    "80/120-minute limits, game input latency and FFXIV Rating changes are not simulated",
    "Existing hanchan checkpoint observations retain real East/South winds and original progress encoding; no trained quick-match length feature",
    "Four-riichi continuation and quick no-South-extension follow the official exhaustive abortive-draw list and quick-match length; controlled live parity remains unverified",
    "Double-ron honba/stick allocation, pao edge cases and riichi-kan wait restrictions still require controlled FF14 parity fixtures",
]


def rule_profile(mode=SUPPORTED_MODE, *, allow_experimental=False):
    if mode in ("doman-quick", "doman-full") and allow_experimental:
        return dict(match_mode=mode, scheduled_rounds=1 if mode == "doman-quick" else 2,
                    rule_set="doman-experimental-v1", doman_compatible=False,
                    rule_differences=DOMAN_DIFFERENCES[:])
    if mode != SUPPORTED_MODE:
        raise ValueError(f"{mode} is not implemented by the pinned libriichi arena; "
                         "use compare_models.py with an explicitly validated --doman-engine")
    return dict(match_mode=mode, scheduled_rounds=2, rule_set="libriichi",
                doman_compatible=False, rule_differences=RULE_DIFFERENCES[:])
