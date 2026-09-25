import sys
from pathlib import Path
import struct
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from audit_kan_dora import audit_packets, bonus_count


class KanDoraEvidenceTests(unittest.TestCase):
    def sample(self, marker=84):
        def row(message, data):
            return dict(message_id=message, payload_hex=data.hex())
        opening = bytearray(104)  # first marker 1m -> 2m
        kan = bytearray(24)
        struct.pack_into("<II", kan, 0, 1, 0x130)
        struct.pack_into("<4H", kan, 12, 88, 88, 88, 89)
        replacement = bytearray(24)
        struct.pack_into("<II", replacement, 0, 1, 0x200)
        struct.pack_into("<H", replacement, 10, marker)
        result = [0] * 64
        result[0], result[1], result[4] = 27, 10, 1
        result[13:23] = [1, 2, 3, 9, 10, 11, 18, 19, 20, 27]
        result[26], result[42] = 23, 6 | (1 << 16)  # 5 dora + 1 red, no ura
        result[58:63] = [0x10116, 255, 255, 255, 255]  # flagged 5s marker -> 6s, zero
        return [row(637, opening), row(638, kan), row(638, replacement),
                row(639, struct.pack("<64I", *result))]

    def test_result_independently_counts_added_dora_quad_red_and_flagged_ura(self):
        result = audit_packets(self.sample())["results"][0]
        self.assertTrue(result["matches"])
        self.assertEqual(4, result["added_dora_bonus"])
        self.assertEqual([6, 0, 1], result["expected"])

    def test_wrong_added_marker_does_not_pass_result_audit(self):
        result = audit_packets(self.sample(marker=80))["results"][0]
        self.assertFalse(result["matches"])
        self.assertEqual([2, 0, 1], result["expected"])

    def test_duplicate_indicators_count_twice_and_honors_wrap(self):
        self.assertEqual(2, bonus_count([(0, 0)], [8, 8]))
        self.assertEqual(2, bonus_count([(27, 0), (31, 0)], [30, 33]))

    def test_unknown_replacement_marker_is_not_silently_dropped(self):
        with self.assertRaises(ValueError):
            audit_packets(self.sample(marker=65535))

    def test_empty_packet_list_does_not_invent_results(self):
        self.assertEqual([], audit_packets([])["results"])

    def test_result_without_revealed_hand_is_reported_as_skipped_not_a_pass(self):
        rows = self.sample()
        result = bytearray.fromhex(rows[-1]["payload_hex"])
        struct.pack_into("<II", result, 0, 255, 0)
        rows[-1]["payload_hex"] = result.hex()
        audit = audit_packets(rows)
        self.assertEqual([], audit["results"])
        self.assertEqual(1, len(audit["skipped_results"]))
