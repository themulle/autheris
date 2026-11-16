import importlib.util
import json
import pathlib
import tempfile
import unittest

_spec = importlib.util.spec_from_file_location(
    "update_shard_weights",
    pathlib.Path(__file__).with_name("update-shard-weights.py"),
)
usw = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(usw)

FIXTURES = pathlib.Path(__file__).with_name("fixtures")


class UpdateShardWeightsTests(unittest.TestCase):
    def test_weight_is_first_start_to_last_end_span(self):
        # In shard-run-1.trx, AlphaTests individual test durations sum to 3.0s (1.0s + 2.0s),
        # but the wall-clock span from first start (08:00:00) to last end (08:00:10) is 10.0s.
        # In shard-run-2.trx, AlphaTests span is 20.0s (09:00:00 to 09:00:20).
        # Average across the 2 runs = 15s; Sub.BetaTests average = (4.2 + 4.4) / 2 = 4.3s.
        run1 = FIXTURES / "shard-run-1.trx"
        run2 = FIXTURES / "shard-run-2.trx"

        spans1 = usw.parse_trx_file(run1)
        self.assertAlmostEqual(10.0, spans1["AlphaTests"])
        self.assertAlmostEqual(4.2, spans1["Sub.BetaTests"])

        weights = usw.compute_weights([run1, run2])
        self.assertEqual({"AlphaTests": 15, "Sub.BetaTests": 4.3}, weights)
        self.assertIsInstance(weights["AlphaTests"], int)
        self.assertIsInstance(weights["Sub.BetaTests"], float)

    def test_output_is_sorted_and_stable(self):
        run1 = FIXTURES / "shard-run-1.trx"
        run2 = FIXTURES / "shard-run-2.trx"
        with tempfile.TemporaryDirectory() as d:
            out_a = pathlib.Path(d) / "a.json"
            out_b = pathlib.Path(d) / "b.json"
            usw.update_weights([run2, run1], out_a)
            usw.update_weights([ FIXTURES ], out_b)

            text_a = out_a.read_text(encoding="utf-8")
            text_b = out_b.read_text(encoding="utf-8")
            self.assertEqual(text_a, text_b)
            parsed = json.loads(text_a)
            self.assertEqual(list(parsed.keys()), sorted(parsed.keys()))


if __name__ == "__main__":
    unittest.main()
