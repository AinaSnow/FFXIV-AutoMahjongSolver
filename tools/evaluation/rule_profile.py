"""Capabilities of the pinned, unmodified libriichi arena; never relabel hanchan as Doman."""
SUPPORTED_MODE = "libriichi-hanchan"
REQUESTED_MODES = (SUPPORTED_MODE, "doman-quick", "doman-full")
RULE_DIFFERENCES = [
    "Pinned OneVsThree runs standard eight-hand hanchan, not Doman quick four-hand matches",
    "libriichi extends beyond scheduled rounds below 30000; Doman has no extension",
    "libriichi final-dealer finish requires 30000 and first place; Doman does not require 30000",
    "Doman single-yakuman values, four-yakuman cap, multiple wins, abortive draws, nagashi and pao need engine parity tests",
    "Doman 80/120-minute limits and game input latency are not simulated",
]


def rule_profile(mode=SUPPORTED_MODE):
    if mode != SUPPORTED_MODE:
        raise ValueError(f"{mode} is not implemented by the pinned libriichi arena; "
                         "changing scheduled_rounds alone would not change match termination or settlement")
    return dict(match_mode=mode, scheduled_rounds=2, rule_set="libriichi",
                doman_compatible=False, rule_differences=RULE_DIFFERENCES[:])
