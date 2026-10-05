"""Validate the ablation against real native v4 encoding, without loading a model."""
import argparse
import importlib
import json
import numpy as np
from doman_engine import load_doman
from progress_observation import adapt_east_progress


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--doman-engine", required=True)
    args = parser.parse_args()
    load_doman(args.doman_engine)
    PlayerState = importlib.import_module("libriichi_doman.state").PlayerState
    checks = 0
    for kyoku in range(1, 5):
        for seat in range(4):
            state = PlayerState(seat)
            hands = [["?"]*13 for _ in range(4)]
            hands[seat] = ["1m", "2m", "3m", "4m", "5mr", "6m", "7m", "8m", "9m", "1p", "2p", "3p", "E"]
            state.update(json.dumps(dict(type="start_kyoku", bakaze="E", kyoku=kyoku,
                honba=2, kyotaku=1, oya=(kyoku-1), dora_marker="9s", scores=[25000]*4, tehais=hands)))
            state.update(json.dumps(dict(type="tsumo", actor=seat, pai="4p")))
            for kan_select in (False, True):
                obs, mask = state.encode_obs(4, kan_select)
                original, mask_before = obs.copy(), mask.copy()
                result = adapt_east_progress(obs)
                np.testing.assert_array_equal(obs, original)
                np.testing.assert_array_equal(mask, mask_before)
                np.testing.assert_array_equal(np.flatnonzero(np.any(result != obs, axis=1)), [27])
                np.testing.assert_allclose(result[27], (kyoku+3)/7)
                assert obs[25, 27] == 1 and obs[26, 27+(seat-(kyoku-1))%4] == 1
                checks += 1
    print(json.dumps(dict(native_layout_checks=checks, status="passed", changed_rows=[27])))


if __name__ == "__main__":
    main()
