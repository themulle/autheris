import importlib.util
import io
import json
import pathlib
import unittest

_spec = importlib.util.spec_from_file_location(
    "integration_shard_filter",
    pathlib.Path(__file__).with_name("integration-shard-filter.py"),
)
isf = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(isf)


class IntegrationShardFilterTests(unittest.TestCase):
    def test_every_class_in_exactly_one_shard_for_4_and_6(self):
        weights = json.loads((isf.project / "ci-shard-weights.json").read_text(encoding="utf-8"))
        discovered = isf.classes()
        self.assertGreater(len(discovered), 50)

        for count in (4, 6):
            shards = isf.assign(discovered, weights, count)
            self.assertEqual(count, len(shards))
            filters = [isf.shard_filter(shards, i) for i in range(count)]

            assigned = [name for shard in shards for name in shard]
            self.assertEqual(sorted(discovered), sorted(assigned))

            for cls in discovered:
                fqn = f"{isf.NAMESPACE}{cls}.SampleTestMethod"
                matching = [i for i, f in enumerate(filters) if isf.matches_shard_filter(f, fqn)]
                self.assertEqual(1, len(matching), f"{cls} matched shards {matching} for count={count}")

    def test_last_shard_is_exact_complement(self):
        names = ["AlphaTests", "BetaTests", "GammaTests", "DeltaTests", "EpsilonTests", "ZetaTests"]
        weights = {"AlphaTests": 50.0, "BetaTests": 40.0, "GammaTests": 30.0, "DeltaTests": 20.0}
        shards = isf.assign(names, weights, 6)
        last_filter = isf.shard_filter(shards, 5)

        first_five = {n for s in shards[:-1] for n in s}
        for name in first_five:
            self.assertIn(f"FullyQualifiedName!~{isf.NAMESPACE}{name}.", last_filter)
            self.assertFalse(isf.matches_shard_filter(last_filter, f"{isf.NAMESPACE}{name}.Run"))

        for name in shards[-1]:
            self.assertNotIn(f"FullyQualifiedName!~{isf.NAMESPACE}{name}.", last_filter)
            self.assertTrue(isf.matches_shard_filter(last_filter, f"{isf.NAMESPACE}{name}.Run"))

    def test_unknown_class_lands_in_last_shard(self):
        names = ["AlphaTests", "BetaTests", "GammaTests", "DeltaTests", "EpsilonTests", "ZetaTests"]
        weights = {n: float((i + 1) * 10) for i, n in enumerate(names)}
        shards = isf.assign(names, weights, 6)
        filters = [isf.shard_filter(shards, i) for i in range(6)]

        unknown_fqn = f"{isf.NAMESPACE}BrandNewUnrecognizedClassTests.MyTest"
        matching = [i for i, f in enumerate(filters) if isf.matches_shard_filter(f, unknown_fqn)]
        self.assertEqual([5], matching)

    def test_default_weight_is_mean_of_file(self):
        weights = {"A": 10.0, "B": 20.0, "C": 60.0}
        self.assertAlmostEqual(30.0, isf.default_weight(weights))

        # An unweighted class "U" receives the mean weight (30.0), so it sorts ahead of "B" (20.0) and "A" (10.0).
        shards = isf.assign(["A", "B", "C", "U"], weights, 2)
        # Greedy order: C (60) -> shard 0; U (30) -> shard 1; B (20) -> shard 1 (load 50 vs 60); A (10) -> shard 1 (load 60 vs 60)
        self.assertIn("C", shards[0])
        self.assertIn("U", shards[1])

    def test_warning_when_unweighted_share_above_10_percent(self):
        weights = {f"C{i}": 20.0 for i in range(8)}
        # 2 unweighted out of 10 classes with equal mean weight = 20% > 10%
        names_over = [f"C{i}" for i in range(10)]
        buf = io.StringIO()
        isf.assign(names_over, weights, 4, warn_stream=buf)
        self.assertIn("::warning::", buf.getvalue())
        self.assertIn("20.0%", buf.getvalue())

        # 1 unweighted out of 20 classes with equal mean weight = 5% <= 10%
        weights_20 = {f"C{i}": 20.0 for i in range(19)}
        names_under = [f"C{i}" for i in range(20)]
        buf_under = io.StringIO()
        isf.assign(names_under, weights_20, 4, warn_stream=buf_under)
        self.assertEqual("", buf_under.getvalue())

    def test_security_always_selected(self):
        # SEC-OI-522-2: BASE_FILTER keeps all non-Spark/Parity Security tests in PR shards, even when tagged Oracle or Stress.
        for cats in (
            {"Security"},
            {"Security", "Oracle"},
            {"Security", "Stress"},
            {"Security", "Oracle", "Stress"},
            {"Security", "DatabricksContract"},
        ):
            self.assertTrue(isf.matches_category_filter(isf.BASE_FILTER, cats), f"Expected BASE_FILTER to select {cats}")

        # Non-security Oracle, Stress, Spark, and Parity tests are excluded from the PR shard filter.
        for excluded_cats in ({"Oracle"}, {"Stress"}, {"Oracle", "Stress"}, {"Spark"}, {"Parity"}):
            self.assertFalse(isf.matches_category_filter(isf.BASE_FILTER, excluded_cats), f"Expected BASE_FILTER to exclude {excluded_cats}")

        # Across 6 shards, every Security-tagged test method is selected by at least one PR job (shard filter or Spark job).
        weights = json.loads((isf.project / "ci-shard-weights.json").read_text(encoding="utf-8"))
        discovered = isf.classes()
        shards = isf.assign(discovered, weights, 6)
        shard_filters = [isf.shard_filter(shards, i) for i in range(6)]
        for cls in ("Federation.FederationSecurityTests", "OracleGatewayPathTests", "QueryDefenseStressTests"):
            fqn = f"{isf.NAMESPACE}{cls}.SampleSecurityTest"
            for extra in ((), ("Oracle",), ("Stress",)):
                cats = {"Security", *extra}
                matched = [i for i, f in enumerate(shard_filters) if isf.matches_shard_filter(f, fqn, cats)]
                self.assertEqual(1, len(matched), f"{fqn} with {cats} matched shards {matched}")

    def test_unknown_changes_output_runs_jobs(self):
        # SEC-OI-522-3: ci-changes.py fails closed on unknown/empty/error inputs, and ci.yml uses != 'false' for oracle-smoke and spark-integration-tests.
        ci_spec = importlib.util.spec_from_file_location(
            "ci_changes",
            pathlib.Path(__file__).with_name("ci-changes.py"),
        )
        ci_mod = importlib.util.module_from_spec(ci_spec)
        ci_spec.loader.exec_module(ci_mod)

        for event in ("push", "schedule", "workflow_dispatch", "unknown", ""):
            self.assertEqual({"dotnet": True, "oracle": True, "spark": True}, ci_mod.decide_all(event, "", ""))

        self.assertEqual(
            {"dotnet": True, "oracle": True, "spark": True},
            ci_mod.decide_all("pull_request", "a" * 40, "b" * 40, diff=lambda *a: None),
        )
        self.assertEqual(
            {"dotnet": True, "oracle": True, "spark": True},
            ci_mod.decide_all("pull_request", "a" * 40, "b" * 40, diff=lambda *a: []),
        )

        ci_yml = (isf.root / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
        self.assertIn("if: ${{ !cancelled() && needs.changes.outputs.oracle != 'false' }}", ci_yml)
        self.assertIn("if: ${{ !cancelled() && needs.changes.outputs.spark != 'false' }}", ci_yml)


if __name__ == "__main__":
    unittest.main()
