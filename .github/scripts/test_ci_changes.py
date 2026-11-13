import importlib.util
import os
import tempfile
import pathlib
import unittest

_spec = importlib.util.spec_from_file_location("ci_changes", pathlib.Path(__file__).with_name("ci-changes.py"))
ci = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(ci)


class CiChangesTests(unittest.TestCase):
    def test_docs_only_skips_dotnet(self):
        for paths in (["docs/plans/x.md"], ["README.md", "docs/a/b.png"], ["benchmarks/results/a.json"],
                      ["deploy/podman-compose.yaml"], [".github/ISSUE_TEMPLATE/bug.yml"], ["LICENSE"]):
            self.assertFalse(ci.classify(paths), paths)

    def test_helm_chart_change_runs_dotnet(self):
        self.assertTrue(ci.classify(["deploy/helm/autheris/values.yaml"]))
        self.assertTrue(ci.classify(["docs/a.md", "deploy/helm/autheris/Chart.yaml"]))

    def test_src_change_runs_dotnet(self):
        self.assertTrue(ci.classify(["src/Autheris.Api/Program.cs"]))
        self.assertTrue(ci.classify(["tests/Autheris.Tests.Unit/A.cs"]))
        self.assertTrue(ci.classify(["Directory.Build.props"]))
        self.assertTrue(ci.classify(["global.json"]))
        self.assertTrue(ci.classify(["Autheris.sln"]))

    def test_mixed_docs_and_src_runs_dotnet(self):
        self.assertTrue(ci.classify(["docs/a.md", "src/Autheris.Api/Program.cs"]))

    def test_empty_or_failed_diff_runs_dotnet(self):
        self.assertTrue(ci.classify([]))
        self.assertTrue(ci.classify(["", "  "]))
        self.assertTrue(ci.decide("pull_request", "abc", "def", diff=lambda *a: None))

        def boom(*a):
            raise RuntimeError("x")
        self.assertTrue(ci.decide("pull_request", "abc", "def", diff=boom))
        self.assertTrue(ci.changed_paths("push", "", "b") is None)
        self.assertTrue(ci.changed_paths("pull_request", "", "b") is None)

    def test_workflow_change_runs_dotnet(self):
        self.assertTrue(ci.classify([".github/workflows/ci.yml"]))
        self.assertTrue(ci.classify([".github/scripts/ci-changes.py"]))
        self.assertTrue(ci.classify([".github/CODEOWNERS"]))

    def test_md_under_src_runs_dotnet(self):
        self.assertTrue(ci.classify(["src/Autheris.Api/Assets/readme.md"]))
        self.assertTrue(ci.classify(["tests/x/notes.md"]))

    def test_md_under_deploy_helm_runs_dotnet(self):
        self.assertTrue(ci.classify(["deploy/helm/autheris/README.md"]))

    def test_rename_from_src_to_docs_runs_dotnet(self):
        # --no-renames lists both sides; the src side is code.
        self.assertTrue(ci.classify(["src/Autheris.Api/A.md", "docs/A.md"]))

    def test_git_failure_runs_dotnet(self):
        self.assertTrue(ci.changed_paths("pull_request", "deadbeef" * 5, "cafebabe" * 5) is None)
        self.assertTrue(ci.decide("pull_request", "deadbeef" * 5, "cafebabe" * 5))

    def test_non_sha_base_or_head_runs_dotnet(self):
        for base, head in [("HEAD", "b" * 40), ("a" * 40, "HEAD~1"), ("--output=x", "b" * 40), ("A" * 40, "b" * 40), ("a" * 39, "b" * 40)]:
            self.assertTrue(ci.changed_paths("pull_request", base, head) is None, (base, head))

    def test_main_writes_dotnet_true_to_github_output(self):
        with tempfile.TemporaryDirectory() as d:
            out = os.path.join(d, "out")
            saved = {k: os.environ.get(k) for k in ("CI_EVENT", "CI_BASE", "CI_HEAD", "GITHUB_OUTPUT")}
            try:
                os.environ.update(CI_EVENT="schedule", CI_BASE="", CI_HEAD="", GITHUB_OUTPUT=out)
                self.assertEqual(0, ci.main())
                self.assertEqual("dotnet=true\n", open(out, encoding="utf-8").read())
            finally:
                for k, v in saved.items():
                    if v is None:
                        os.environ.pop(k, None)
                    else:
                        os.environ[k] = v

    def test_push_event_always_runs_dotnet(self):
        self.assertTrue(ci.changed_paths("push", "a" * 40, "b" * 40) is None)
        self.assertTrue(ci.decide("push", "a" * 40, "b" * 40))

    def test_unknown_event_runs_dotnet(self):
        self.assertTrue(ci.changed_paths("schedule", "a", "b") is None)
        self.assertTrue(ci.decide("workflow_dispatch", "a", "b"))
        self.assertTrue(ci.changed_paths("push", ci.ZERO_SHA, "b") is None)

    def test_path_traversal_or_unnormalized_runs_dotnet(self):
        for p in ["docs/../src/A.cs", "/docs/a.md", "docs\\a.md", "docs//a.md", "./docs/a.md", "docs/./a.md"]:
            self.assertTrue(ci.classify([p]), p)


if __name__ == "__main__":
    unittest.main()
