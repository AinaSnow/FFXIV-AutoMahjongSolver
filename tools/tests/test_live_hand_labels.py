import json
from pathlib import Path
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from live_hand_labels import confirm_actions, decode_hands, decode_result  # noqa: E402
from opponent_stats import summarize  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tests/Mahjong.Plugin.Game.Tests/RegressionFixtures/20261116-758-hand-results.json"
PROFILE = ROOT / "data/protocols/20261103-international-candidate.json"


def fixture_hands():
    document = json.loads(FIXTURE.read_text(encoding="utf-8"))
    packets = json.loads(PROFILE.read_text(encoding="utf-8"))["packets"]
    mapping = {k.upper(): v["messageId"] for k, v in packets.items()}
    for hand in document["hands"]:
        for packet in hand["packets"]:
            assert mapping[packet["opcode"].upper()] == packet["message_id"]
        yield hand


class FixtureLabelTests(unittest.TestCase):
    """The same sanitized hands as Live758HandResultTests (C#)."""

    def test_every_fixture_hand_matches_expected_ui_and_next_opening(self):
        hands = list(fixture_hands())
        self.assertGreaterEqual(len(hands), 9)
        for hand in hands:
            with self.subTest(hand=hand["label"]):
                decoded = decode_hands(hand["packets"])
                label = decoded[0]["label"]
                expected = hand["expected"]
                for key in ("outcome", "winners", "discarder", "deltas", "multi_winner", "tenpai", "riichi"):
                    self.assertEqual(expected[key], label[key], key)
                self.assertIsNone(label["unknown_reason"])
                if len(decoded) > 1:  # the next opening is included unless the hand ended the match
                    self.assertTrue(label["checks"]["next_start_scores"])
                if hand["ui_hand_end"]:
                    self.assertEqual(hand["ui_hand_end"]["deltas"], label["deltas"])
                self.assertTrue(label["checks"]["payment_arithmetic"])

    def test_double_ron_on_a_riichi_declaration_takes_no_deposit(self):
        hand = next(h for h in fixture_hands() if h["label"] == "double-ron-on-riichi-declaration")
        label = decode_hands(hand["packets"])[0]["label"]
        self.assertTrue(label["multi_winner"])
        self.assertIn(label["discarder"], label["riichi"])
        self.assertEqual(0, sum(label["deltas"]))  # the declaration stick never reached the table

    def test_inconsistent_result_degrades_to_unknown(self):
        hand = next(h for h in fixture_hands() if h["label"] == "self-ron")
        packets = [dict(p) for p in hand["packets"]]
        result = next(p for p in packets if p["message_id"] == 640)
        payload = bytearray(bytes.fromhex(result["payload_hex"]))
        struct.pack_into("<i", payload, 20, 1)  # hand value no longer explains the score change
        result["payload_hex"] = payload.hex()
        label = decode_hands(packets)[0]["label"]
        self.assertEqual("unknown", label["outcome"])
        self.assertEqual([], label["winners"])
        self.assertIsNone(label["discarder"])
        self.assertIn("payment arithmetic", label["unknown_reason"])

    def test_unexpected_result_shapes_raise_for_the_caller_to_label_unknown(self):
        with self.assertRaises(ValueError):
            decode_result(640, bytes(256))
        with self.assertRaises(ValueError):
            decode_result(643, struct.pack("<66i", 2, *([0] * 65)))
        empty_second = [0] * 126
        empty_second[63 + 4] = 4
        empty_second[63] = 255
        with self.assertRaises(ValueError):  # empty second block must carry 0xFF tiles
            decode_result(640, struct.pack("<126i", *empty_second))

    def test_opponent_readout_counts_each_deal_in_once(self):
        labels = []
        for hand in fixture_hands():
            label = decode_hands(hand["packets"])[0]["label"]
            labels.append(dict(result=label, riichi=label["riichi"], open_calls=label["open_calls"],
                               discards=label["discards"]))
        stats = summarize(labels)
        rons = sum(h["result"]["outcome"] == "ron" for h in labels)
        deal_ins = stats["seats"]["self"]["deal_in"]["count"] + stats["seats"]["opponents"]["deal_in"]["count"]
        self.assertEqual(rons, deal_ins)
        wins = stats["seats"]["self"]["win"]["count"] + stats["seats"]["opponents"]["win"]["count"]
        self.assertEqual(sum(len(h["result"]["winners"]) for h in labels), wins)


def t(seconds):
    whole = int(seconds)
    return f"2026-11-15T10:00:{whole:02d}.{int(round((seconds - whole) * 1e7)):07d}Z"


def packet(seconds, message, payload):
    return dict(t=t(seconds), message_id=message, payload_hex=payload.hex())


def start(seconds, self_seat=0, honba=0, scores=(250, 250, 250, 250)):
    words = [0] * 26
    words[3], words[6], words[7] = honba, self_seat, 27
    words[8:12] = scores
    words[12:25] = list(range(13))
    return packet(seconds, 637, struct.pack("<26i", *words))


def draw(seconds, seat, physical=0xFFFF, action=0x100):
    return packet(seconds, 638, struct.pack("<iI6Hi", seat, action, physical, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 1))


def call(seconds, seat, action, a, b):
    return packet(seconds, 638, struct.pack("<iI6Hi", seat, action, 0, 0, a, b, 0xFFFF, 0xFFFF, 0))


def discard(seconds, seat, physical, action=0x110):
    return packet(seconds, 641, struct.pack("<iiI6Hi", seat, 0, action, physical, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0))


def ron_result(seconds, winner, loser, value, tile, scores_before):
    block = [0] * 63
    block[0], block[1], block[3], block[4], block[5], block[8] = tile, 13, 1, winner, value // 100, 30
    after = list(scores_before)
    after[winner] += value // 100
    after[loser] -= value // 100
    block[9:13] = after
    empty = [0] * 63
    empty[0], empty[4] = 255, 4
    empty[13:26] = [255] * 13
    return packet(seconds, 640, struct.pack("<126i", *block, *empty))


def action(aid, seconds, kind, label, tile=None, is_red=None, result="Ok"):
    return dict(action_id=aid, t=t(seconds), kind=kind, label=label, tile=tile, is_red=is_red, result=result)


class ServerConfirmationTests(unittest.TestCase):
    """Dispatched actions matched to server events. Physical tile = kind * 4 (+1 for a red five)."""

    def hand(self, *rows):
        return decode_hands([start(0), *rows])

    def test_discard_is_confirmed_only_by_the_same_tile_and_red_identity(self):
        hands = self.hand(draw(1, 0, 16), discard(2, 0, 17), draw(3, 1))  # 16 = 5m, 17 = red 5m
        cases = {(4, True): "server-confirmed", (4, None): "server-confirmed",
                 (4, False): "server-action-mismatch", (5, True): "server-action-mismatch"}
        for (tile, red), status in cases.items():
            with self.subTest(tile=tile, red=red):
                result = confirm_actions(hands, [action(1, 1.5, "Discard", "discard", tile, red)])[1]
                self.assertEqual(status, result["status"])
                self.assertEqual("dahai", result["server_event"]["type"])

    def test_retry_before_one_server_discard_is_superseded(self):
        hands = self.hand(draw(1, 0, 16), discard(2, 0, 16), draw(3, 1))
        result = confirm_actions(hands, [action(1, 1.2, "Discard", "discard", 4, False),
                                         action(2, 1.6, "Discard", "discard", 4, False)])
        self.assertEqual("server-superseded", result[1]["status"])
        self.assertEqual("server-confirmed", result[2]["status"])

    def test_riichi_needs_a_riichi_discard_and_both_steps_are_confirmed(self):
        riichi = self.hand(draw(1, 0, 16), discard(2, 0, 16, 0x111), draw(3, 1))
        plain = self.hand(draw(1, 0, 16), discard(2, 0, 16, 0x110), draw(3, 1))
        actions = [action(1, 1.2, "Riichi", "riichi"), action(2, 1.5, "Discard", "riichi-tsumogiri", 4, False)]
        self.assertEqual(["server-confirmed"] * 2, [r["status"] for r in confirm_actions(riichi, actions).values()])
        self.assertEqual("server-action-mismatch", confirm_actions(plain, actions)[1]["status"])

    def test_pass_is_window_closed_or_no_window_never_confirmed(self):
        hands = self.hand(draw(1, 1), discard(2, 1, 40), draw(3, 2), discard(4, 2, 44), call(5, 0, 0x500, 45, 46))
        result = confirm_actions(hands, [action(1, 2.5, "Pass", "pass"), action(2, 3.5, "Pass", "pass"),
                                         action(3, 4.5, "Pass", "pass")])
        self.assertEqual("server-window-closed", result[1]["status"])  # offer on seat 1's discard closed by a draw
        self.assertEqual("server-no-window", result[2]["status"])      # after a draw no offer is open
        self.assertEqual("server-action-mismatch", result[3]["status"])  # we passed, yet the server shows our pon

    def test_pass_on_a_retained_row_after_our_accepted_pon_is_superseded(self):
        hands = self.hand(draw(1, 1), discard(2, 1, 40), call(3, 0, 0x500, 41, 42))
        result = confirm_actions(hands, [action(1, 2.4, "Pon", "pon"), action(2, 2.6, "Pass", "pass")])
        self.assertEqual("server-confirmed", result[1]["status"])
        self.assertEqual("server-superseded", result[2]["status"])

    def test_chi_list_step_and_variant_choice_are_one_confirmed_call(self):
        hands = self.hand(draw(1, 3), discard(2, 3, 8), call(3, 0, 0x600, 0, 4))
        result = confirm_actions(hands, [action(1, 2.2, "Chi", "chi"), action(2, 2.6, "Chi", "chi-variant")])
        self.assertEqual(["server-confirmed"] * 2, [result[1]["status"], result[2]["status"]])

    def test_ron_needs_a_result_naming_us_and_a_stale_click_has_no_effect(self):
        scores = (250, 250, 250, 250)
        won = self.hand(draw(1, 1), discard(2, 1, 40), ron_result(3, 0, 1, 3900, 10, scores))
        self.assertEqual("server-confirmed", confirm_actions(won, [action(1, 2.5, "Ron", "ron")])[1]["status"])
        self.assertEqual(0, won[0]["label"]["winners"][0])
        self.assertEqual(1, won[0]["label"]["discarder"])
        moved_on = self.hand(draw(1, 1), discard(2, 1, 40), draw(3, 2))
        self.assertEqual("server-no-effect", confirm_actions(moved_on, [action(1, 2.5, "Ron", "ron")])[1]["status"])

    def test_kan_click_without_server_kan_has_no_effect(self):
        hands = self.hand(draw(1, 0, 132), discard(2, 0, 132, 0x112))
        result = confirm_actions(hands, [action(1, 1.5, "AnKan", "ankan", 33), action(2, 1.8, "Pass", "pass")])
        self.assertEqual("server-no-effect", result[1]["status"])
        self.assertEqual("server-window-closed", result[2]["status"])

    def test_no_packets_and_non_game_actions_are_not_evidence(self):
        result = confirm_actions([], [action(1, 1, "Discard", "discard", 4, False),
                                      action(2, 2, "Pass", "hand-result-next"),
                                      action(3, 3, "Discard", "discard", 4, False, result="HookFailed")])
        self.assertEqual("no-server-evidence", result[1]["status"])
        self.assertEqual("not-applicable", result[2]["status"])
        self.assertEqual("not-applicable", result[3]["status"])


if __name__ == "__main__":
    unittest.main()
