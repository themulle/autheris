# WP-SIMPL-03 Remove unverified performance claims
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-03-remove-unverified-performance-claims-511) | Issues: #511 | Owner decisions: none specific (REQ-SIMPL-03) | Security conditions: SEC-SIMPL-01, SEC-SIMPL-74

## 1. Goal and outcome
README and docs (outside `docs/plans/`) contain zero performance figures that do not link a checked-in protocol or results file. The unmeasured comparison doc and the copywriter skill/agent that prescribes the numbers are deleted. An architecture test keeps the count at zero.

## 2. Preconditions and dependencies
- None. Parallel with WP-SIMPL-01, 02, 06..08. WP-SIMPL-09 runs after this WP. WP-SIMPL-04/05 touch README/docs too: merge this first, they rebase.

## 3. Scope
### In scope
- `README.md:25` (link line "64,500+ req/s BenchmarkDotNet reports"): rewrite to "Head-to-head feature comparisons (Apollo, Hasura, Cosmo, Immuta)" without numbers.
- `README.md:56-58` table rows Peak Throughput, P99 Latency Overhead, Hot-Path Allocations: delete the rows; delete the table and its heading if no row remains; remove HotChocolate version numbers and "Native AOT" wording from marketing text (find with `grep -n 'Native AOT\|HotChocolate 1[0-9]' README.md`).
- Delete `docs/comparisons/performance-benchmarks.md`; remove its links in `docs/comparisons/README.md` and any other tracked `*.md` outside `docs/plans/`.
- Delete `.agents/skills/readme-copywriter/` (SKILL.md:36 prescribes the numbers) and `.agents/agents/readme-copywriter.md`; remove their references in `AGENTS.md`.
- New `DocsClaimsArchitectureTests`.
### Out of scope
- `docs/plans/**` (history), `benchmarks/**`, other agents (WP-SIMPL-09), link/status hygiene (WP-SIMPL-05), BenchmarkDotNet projects.

## 4. Current state (verified on 63cbb893)
- `README.md:25` link to `docs/comparisons/README.md` with "64,500+ req/s BenchmarkDotNet reports".
- `README.md:56` 64,500+ req/sec, `:57` 1.82 ms, `:58` 0 B, each linking `docs/comparisons/performance-benchmarks.md`.
- Files referencing `performance-benchmarks.md` or `readme-copywriter`: `.agents/agents/readme-copywriter.md`, `.agents/skills/readme-copywriter/SKILL.md`, `AGENTS.md`, `README.md`, `docs/comparisons/README.md` (plus docs/plans, out of scope).
- `benchmarks/load/results` holds only `.gitkeep`; only measurement is `benchmarks/results/r2r-spike-report.md`.

## 5. Implementation steps (TDD)
1. Red: add `tests/Autheris.Tests.Architecture/DocsClaimsArchitectureTests.cs`. Scope: `README.md`, root `*.md`, `docs/**/*.md` excluding `docs/plans/**`, via `RepositoryScan.EnumerateFiles`. Split each file into paragraphs (blank-line separated; a table row counts as its own paragraph). A paragraph matching `(?i)req/s(ec)?|\bP(50|95|99)\b|\b\d+(\.\d+)?\s?ms\b` must contain a link target starting with `benchmarks/results/` or `benchmarks/protocol/` (relative path, any `../` prefix). Ignore fenced code blocks. Output lists `file:line`. Fails now on README:25, 56-57.
2. If the test flags other files (for example timeout values like `500 ms` in configuration docs), do not weaken the regex: the figure is a configuration value, not a claim, so add an explicit allowlist file `tests/Autheris.Tests.Architecture/Baselines/docs-claims-allowlist.txt` with `path:exact-line-text` entries, each with a comment why; the allowlist may only shrink (assert entries still match).
3. Green: edit README lines 25 and 56-58 and HotChocolate/AOT text; delete `performance-benchmarks.md`; fix `docs/comparisons/README.md` links; delete copywriter skill and agent; update AGENTS.md (remove the agent row/reference only; keep all lifecycle and security text, SEC-SIMPL-74).
4. Run `git grep -n 'performance-benchmarks.md\|readme-copywriter' -- ':!docs/plans'` until empty.

## 6. Tests
- `Autheris.Tests.Architecture`: `DocsClaimsArchitectureTests.Performance_figures_link_a_results_file`.
- `DocsClaimsArchitectureTests.No_reference_to_deleted_performance_benchmarks_doc` (no tracked `*.md` outside docs/plans links `performance-benchmarks.md`).
- `DocsClaimsArchitectureTests.Allowlist_entries_still_match` (only if step 2 creates the allowlist).
- No Security test touched.

## 7. Verification commands
- `dotnet build tests/Autheris.Tests.Architecture -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build --filter "FullyQualifiedName~DocsClaimsArchitectureTests"`
- `git grep -nE '64,500|1\.82 ms|0 B \(Zero-Alloc\)|Native AOT' -- README.md docs ':!docs/plans'` returns nothing.
- `git grep -n 'readme-copywriter\|performance-benchmarks.md' -- ':!docs/plans'` returns nothing.

## 8. Acceptance criteria
- [ ] Unsupported performance claims in README/docs (outside docs/plans) = 0, enforced by the test.
- [ ] `docs/comparisons/performance-benchmarks.md`, `.agents/skills/readme-copywriter/`, `.agents/agents/readme-copywriter.md` deleted; no dangling links.
- [ ] AGENTS.md still contains the Phase 3 security step and fail-closed guidance (diff shows only the copywriter reference removed).
- [ ] No HotChocolate version number in README marketing text.

## 9. PR slicing
One PR: docs deletions and edits plus the architecture test (about +90 test lines, docs net negative).

## 10. Risks, rollback, must NOT do
- Risk: the regex flags legitimate config values; handled by the shrinking allowlist, not by loosening the regex.
- Must NOT: add new numbers, edit docs/plans, delete other agents (WP-SIMPL-09), remove security guidance from AGENTS.md, fix unrelated links (WP-SIMPL-05).

## 11. Estimate
+~90 / -~400 LOC (docs and skill deletions); 1 person-day.

## Decisions taken in this spec
- D-03-1: Paragraph-level matching with table rows as single paragraphs; configuration values go into a shrink-only allowlist.
- D-03-2: The comparison doc is deleted, not rewritten (no protocol exists; WP-SIMPL-10 may add numbers later with results links).
