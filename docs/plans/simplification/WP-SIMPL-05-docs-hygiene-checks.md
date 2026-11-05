# WP-SIMPL-05 Docs hygiene and docs checks
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-05-docs-hygiene-and-docs-checks-513) | Issues: #513 | Owner decisions: OD-17 (accepted defaults, incl. Q15: docs/plans rewritten but not gated) | Security conditions: SEC-SIMPL-74 (docs checks additive; `Backticked_source_paths_exist` is its `Docs_reference_existing_source_paths`)

## 1. Goal and outcome
Docs are navigable and honest, enforced by architecture tests. Measurable, in the gated scope (README.md, root `*.md`, `docs/` outside `docs/plans/`, module READMEs): `file:///` links 0, broken relative links 0, backticked source paths that do not exist 0, `Status:` tags 0, "100% GA"/"Production-Ready" wording 0, German documents 0.

## 2. Preconditions and dependencies
- Merge after WP-SIMPL-04 (same files). WP-SIMPL-03 should also be merged first (it deletes perf docs and links); if not, rebase and rerun the link test.
- Parallel-safe with WP-SIMPL-06 (except `docs/reference/configuration.md`, which this WP's gates must accept).

## 3. Scope
### In scope
- Script-rewrite every `file:///root/autheris/` link to a relative link in all tracked `*.md` (including `CLAUDE.md`, `AGENTS.md`, `docs/plans/**`, `.agents/**`).
- Fix all broken relative links in the gated scope.
- Remove `Status:` lines and "100% GA" / "Production-Ready" wording outside `docs/plans/`.
- Condense `docs/architecture/konzept-data-governance-und-mcp-documentation.md` into `docs/architecture/arc42.md` and delete it.
- New `tests/Autheris.Tests.Architecture/DocsIntegrityTests.cs`.
### Out of scope
- Rewriting feature content (WP-SIMPL-34), plan-id comment cleanup (WP-SIMPL-33), any workflow edit.
- Gating `docs/plans/**` (rewritten only, Q15).

## 4. Current state (verified on 63cbb893)
- `file:///root/autheris/` links: 313 occurrences in 76 tracked `*.md` files.
- Files with `Status:` tags outside docs/plans: 49 (e.g. `docs/adr/ADR-018-governed-stored-procedures.md`, many `docs/features/f-*.md`, `.agents/skills/product-manager/SKILL.md`).
- Files with "100% GA"/"production-ready" outside docs/plans: 41 (incl. `README.md`, `docs/features/README.md`, `.agents/agents/frontend-developer.md`).
- `docs/architecture/` holds `arc42.md`, `autheris-overview.svg`, `konzept-data-governance-und-mcp-documentation.md` (German).
- Plan counted 23 broken relative links (fe0e131d); recount with the new test.
- `RepositoryScan` helper exists in the architecture test project (`OptionsConsumptionInventoryTests.cs:35` uses `RepositoryScan.ReadBaseline`).

## 5. Implementation steps (TDD order)
1. RED: add `DocsIntegrityTests` (section 6) with a shared `GatedMarkdownFiles()` = `git ls-files`-equivalent enumeration from repo root (use `RepositoryScan` root; skip `bin/`, `obj/`, `node_modules/`, `.git/`, `docs/plans/`) of `README.md`, root `*.md`, `docs/**/*.md`, `src/**/README.md`, `modules/**/README.md`. Exclude `.agents/**` and `CHANGELOG.md` from status/English gates but include them in `No_absolute_file_links` (all tracked md). Exclude `docs/adr/**` from `Backticked_source_paths_exist` only (ADRs are historical decisions, same rule as WP-SIMPL-04); ADRs stay in the link and status-tag gates.
2. Link rewrite: one-off script (do not commit it) in the scratchpad: for each tracked `*.md`, replace `file:///root/autheris/<path>` with `os.path.relpath(<path>, dirname(md))`, preserving `#anchor`. Commit the result as a separate commit `docs: rewrite absolute file links to relative`.
3. Fix broken relative links reported by `Relative_links_resolve`: retarget to the moved file or remove the link (keep text). Anchors are not checked.
4. Remove `Status:` lines (regex `^\s*(\*\*)?Status:?(\*\*)?:?\s` at line start, also table rows `| Status |` in feature headers) and replace "100% GA"/"Production-Ready" wording with neutral text (delete the phrase; do not invent a maturity claim).
5. Condense the German concept doc: carry over only content that still describes existing code into an English section "Data governance and MCP" in `arc42.md`; `git rm` the German file; fix links to it.
6. Backticked paths: fix or remove backticked `src/...`, `tests/...`, `docs/...` paths that do not exist.
7. GREEN: all `DocsIntegrityTests` pass.

## 6. Tests
Project `tests/Autheris.Tests.Architecture`, class `DocsIntegrityTests`:
- `No_absolute_file_links` - no `file://` in any tracked `*.md` (all paths, including docs/plans, CLAUDE.md, AGENTS.md).
- `Relative_links_resolve` - every `](target)` in the gated scope that is not `http(s):`, `mailto:` or a pure `#anchor` resolves to an existing file or directory (strip `#...` and `?...`, URL-decode).
- `Backticked_source_paths_exist` - every backticked token matching `^(src|tests|docs|deploy|benchmarks|scripts)/[\w./-]+$` (no `*`, `<`, `{`) in the gated scope except `docs/adr/**` exists. This is the `Docs_reference_existing_source_paths` check named in SEC-SIMPL-74.
- `No_status_tags_outside_plans` - no line matching the step-4 regex and no `100% GA` / `production-ready` (case-insensitive) in the gated scope.
- `Docs_are_english` - per file, German stop-word density (`der|die|das|und|nicht|mit|für|ist|werden|eine` as whole words, case-insensitive) / total words < 0.03; files under 50 words skipped.
Each test reports up to 50 `path:line` offenders. No security-trait tests. No tests deleted.
- Not here: the SEC-SIMPL-01 advisory-marker scan (`DocsAdvisoryMarkerTests.Docs_and_changelog_contain_no_advisory_detail_markers`, scope `docs/**`, `src/**`, `tests/**`, `.github/**`, `CHANGELOG.md`) is owned by WP-SIMPL-02; this WP must keep it green (it adds no GHSA text).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build --filter "FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~DocsClaimsArchitectureTests"`
- `git grep -c 'file:///' -- '*.md'` -> no output.
- `git ls-files docs/architecture` -> no `konzept-*` file.

## 8. Acceptance criteria
- [ ] `file:///` occurrences in tracked `*.md` = 0.
- [ ] All five `DocsIntegrityTests` green in the existing architecture CI job (no workflow change).
- [ ] German concept doc deleted; `arc42.md` has the condensed English section.
- [ ] `CLAUDE.md` and `AGENTS.md` links are relative and resolve.

## 9. PR slicing
One PR, three commits: (1) tests (red), (2) scripted link rewrite, (3) manual fixes (broken links, status tags, German doc). Docs-only plus ~250 test lines; no size exception needed.

## 10. Risks, rollback, must NOT do
- Risk: rewrite touches files of open PRs (merge conflicts); announce the merge window, run the script last after rebase. Rollback: revert the PR.
- Risk: German heuristic false positives on English docs quoting German; tune threshold, never allow-list a whole folder.
- Must NOT gate `docs/plans/`, add a workflow step, or edit src code. Must NOT replace status tags with other status wording.

## 11. Estimate
+~250 test lines; docs ~ -600 lines (German doc condensed, status lines), ~400 lines touched by link rewrite. 2.5 person-days.

## Decisions taken in this spec
- `.agents/**` and `CHANGELOG.md` are outside status/English gates (agent prompts may legitimately say "Status:"), but inside the `file:///` gate.
- `docs/adr/**` is gated for links and status tags only (ADR "Status: Accepted" lines are reworded to "Decision date:" / removed, consistent with "no status tags outside docs/plans"). `Backticked_source_paths_exist` excludes `docs/adr/**` (historical decisions, same rule as WP-SIMPL-04), so later deletion WPs do not have to edit historical ADR text.
- German detection threshold fixed at 0.03 stop-word density.
