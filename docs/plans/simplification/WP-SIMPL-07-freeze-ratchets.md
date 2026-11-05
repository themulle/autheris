# WP-SIMPL-07 Freeze mechanics and program ratchets
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-07-freeze-mechanics-and-program-ratchets) | Issues: #515 (Stage 0) | Owner decisions: OD-17 (defaults binding), D9 (400-line ceiling, default taken) | Security conditions: SEC-SIMPL-70, SEC-SIMPL-72, SEC-SIMPL-73

## 1. Goal and outcome
Make the feature freeze mechanical: five decrease-only architecture ratchets (Map* endpoints, Options classes, `*Enabled` flags, Application top-level folders, plan-id comments), a PR template with freeze checklist and label rules, and a CI net-LOC check (+400 net src lines, `size-exception` label). Outcome: any PR that grows one of the five counts fails `ProgramRatchetTests`; any PR above +400 net src lines without `size-exception` or `security` fails the size step.

## 2. Preconditions and dependencies
- #516 (CI hang fix) and #517 (job timeouts) merged first (plan rule R2/R9). Rebase on main after they land.
- No WP dependency. Runs in lane L-RAT, parallel to WP-SIMPL-01..03, 10(before), 36.
- The ci.yml step is `[WF]`: workflow edits need owner approval; put it in its own PR (PR 2).
- Owner action (not the developer): tag `pre-simplification` on the base commit before WP-SIMPL-11 merges.

## 3. Scope
### In scope
- `tests/Autheris.Tests.Architecture/ProgramRatchetTests.cs` (new) and five baseline files under `tests/Autheris.Tests.Architecture/Baselines/`.
- `.github/PULL_REQUEST_TEMPLATE.md` (new; none exists today).
- `.github/scripts/pr-net-loc.sh` (new) and one step in `.github/workflows/ci.yml` job `build-and-test`.
### Out of scope
- Lowering any baseline below current counts (done by the deleting WPs, which must lower the baseline in the same PR).
- Test-suite ratchets (Substitute.For, Received, sleeps): WP-SIMPL-23; `TestSleepRatchetTests` already exists.
- Branch protection, labels creation in GitHub (owner settings).

## 4. Current state (verified on 63cbb893)
- Existing ratchet pattern: `tests/Autheris.Tests.Architecture/SourceSizeRatchetArchitectureTests.cs` (baseline `Baselines/source-size-ceilings.txt`, "path|ceiling"), `TestSleepRatchetTests.cs` (`Baselines/test-sleep-counts.txt`).
- Shared helper `tests/Autheris.Tests.Architecture/RepositoryScan.cs`: `RepoRoot` (l.18), `ProductionFiles` (l.21, all `src/**/*.cs`), `EnumerateFiles` (l.52), `ReadBaseline` (l.79), `ParseCountBaseline` (l.94, "key|count"), `Normalize` (l.111), `IsCommentLine` (l.113).
- Counts: `git grep -E '\.Map(Get|Post|Put|Delete|Patch|Group|Methods)\('` in src = 305 call sites (the plan's 275 used a narrower pattern; the test's own regex defines the baseline). `src/Autheris.Application` has 49 top-level directories (plan baseline table: 49).
- `.github/` has no PR template; `.github/scripts/` exists; workflows: ci.yml (jobs build-and-test l.17, integration-tests l.65, spark-integration-tests l.104, helm-render l.139, sql-compiler-gate l.166).

## 5. Implementation steps
1. Red: create `ProgramRatchetTests` (sealed, primary ctor `ITestOutputHelper output`, same style as `SourceSizeRatchetArchitectureTests`) with the five facts below, each reading a baseline that does not exist yet -> fail.
2. Counting rules (static helpers in the same class, `[GeneratedRegex]` partial methods; class is `sealed partial`):
   - Endpoints: regex `\.Map(Get|Post|Put|Delete|Patch|Methods|Group|Hub|GraphQL)\s*\(` over `RepositoryScan.ProductionFiles`, skipping comment lines. Baseline `Baselines/endpoint-count.txt`: single line `total|<n>`.
   - Options classes: `class\s+\w+Options\b` (declarations, not `IOptions<`) in ProductionFiles. Baseline `options-class-count.txt` `total|<n>`.
   - Enabled flags: `public\s+bool\s+\w*Enabled\s*\{` in ProductionFiles. Baseline `enabled-flag-count.txt` `total|<n>`.
   - Application folders: `Directory.GetDirectories(src/Autheris.Application)` minus `bin`, `obj`, `Properties`; baseline `application-folders.txt` one name per line (allow-list, sorted ordinal).
   - Plan-id comments: on comment lines only (`//`, `///`, `/*`, `*`), regex `\b(REV-\d+|SEC-[A-Z0-9-]+|AST-X\d+|ARCH-MNT-\d+|PLAN-[A-Z]+-\d+|WP-[A-Z0-9.-]+|REQ-[A-Z]+-\d+)\b`; count per project (first path segment under `src/`). Baseline `plan-id-comment-counts.txt` `project|count`.
3. Assertion semantics (all five): actual > baseline -> fail with message naming the metric, actual, baseline and "decrease-only ratchet; lower the baseline when you delete, never raise it (SEC-SIMPL-70: security PRs use the `freeze-exception` label, the ratchet file is not edited)". actual < baseline -> fail with "baseline is stale, lower it to <actual>" (keeps the file tight, same as source-size ratchet). Folder test: any folder not in the allow-list fails; allow-list entries without a folder fail as stale. Plan-id: a project missing from the baseline has baseline 0.
4. Green: generate the five baseline files from the current tree (counts printed via `output.WriteLine` on failure; copy them in). Commit counts exactly as measured on the PR's merge base.
5. `.github/PULL_REQUEST_TEMPLATE.md`: sections Summary, Linked issue, Freeze checklist (`[ ] no new feature, endpoint, option, *Enabled flag or Application folder`, `[ ] ratchet baselines only lowered`, `[ ] net src lines <= 400 or size-exception justified`), Labels: `freeze-exception` (security only: GHSA id, Dependabot/Trivy alert or Phase 3 condition; requires CODEOWNERS security approval and a neutral title, SEC-SIMPL-70/01), `size-exception` (paragraph why it cannot be split + link to owner approval; max two per week; deletion PRs of whole subsystems list SEC-SIMPL-02 items, SEC-SIMPL-72), Workflow changes note (SEC-SIMPL-73: state any security gate added/loosened/removed).
6. `.github/scripts/pr-net-loc.sh` (bash, `set -euo pipefail`): input base ref (default `origin/main`); `git diff --numstat "$BASE"...HEAD -- src ':(exclude)src/**/Migrations/**' ':(exclude)**/*.g.cs' ':(exclude)**/*.Designer.cs' ':(exclude)**/Generated/**'`; skip binary rows (`-`); net = sum(add) - sum(del); print `net_src_lines=<n>`; exit 1 if net > 400 and env `PR_LABELS` contains neither `size-exception` nor `security` nor `freeze-exception`.
7. `[WF]` ci.yml, job `build-and-test`, step "PR net-LOC check" with `if: github.event_name == 'pull_request'`, checkout `fetch-depth: 0`, env `PR_LABELS: ${{ join(github.event.pull_request.labels.*.name, ',') }}`, `run: bash .github/scripts/pr-net-loc.sh origin/${{ github.base_ref }}`. No new permissions, no `pull_request_target` (SEC-SIMPL-73).

## 6. Tests (project `tests/Autheris.Tests.Architecture`)
- `ProgramRatchetTests.Endpoint_count_does_not_grow`
- `ProgramRatchetTests.Options_class_count_does_not_grow`
- `ProgramRatchetTests.Enabled_flag_count_does_not_grow`
- `ProgramRatchetTests.Application_top_level_folders_are_allow_listed`
- `ProgramRatchetTests.Plan_id_comments_do_not_grow`
- `ProgramRatchetTests.Counting_rules_match_known_samples` (Theory over inline strings: `app.MapGet("/x", ...)` counts 1, `// app.MapGet(` counts 0, `IOptions<FooOptions>` counts 0, `public bool FooEnabled { get; set; }` counts 1, `// SEC-SIMPL-70 note` counts 1 plan id).
- `ProgramRatchetTests.Pr_template_declares_freeze_and_size_labels` [Trait("Category","Security")]: template contains `freeze-exception`, `size-exception`, `SEC-SIMPL-70`.
- Script check (bash, run manually and in PR description): a scratch branch adding 401 lines under `src/` exits 1; with `PR_LABELS=size-exception` exits 0; adding 500 lines under `src/**/Migrations/` exits 0.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build --filter "FullyQualifiedName~ProgramRatchetTests"`
- `bash .github/scripts/pr-net-loc.sh origin/main` (prints `net_src_lines=`)
- `shellcheck .github/scripts/pr-net-loc.sh` if available.

## 8. Acceptance criteria
- [ ] Five baseline files committed, values equal to the counts on the merge base; all `ProgramRatchetTests` green.
- [ ] Adding one `app.MapGet` in src locally makes `Endpoint_count_does_not_grow` fail (manual negative check noted in PR).
- [ ] PR template exists with freeze checklist and both label rules.
- [ ] ci.yml step fails a PR above +400 net src lines without label; passes with `size-exception`/`security`/`freeze-exception`.
- [ ] No workflow permission changes; no `pull_request_target`.

## 9. PR slicing
1. PR 1 (tests only, ~+300 test lines): `ProgramRatchetTests`, five baselines, PR template.
2. PR 2 `[WF]` (~+40 lines): `pr-net-loc.sh` + ci.yml step; owner approval required.

## 10. Risks, rollback, must NOT
- Risk: concurrent WPs change counts -> stale-baseline failures on rebase; resolve by re-measuring on rebase, never by loosening the comparison.
- Must NOT raise a baseline, add tolerance, or add `[Fact(Skip=...)]`. Must NOT count tests/ or modules/ in these ratchets. Must NOT use `pull_request_target`.
- Rollback: revert the PR; no runtime impact.

## 11. Estimate
+~340 lines (tests, script, template), 0 src. 2 pd.

## Decisions taken in this spec
- Ratchets fail on both growth and staleness (tight baselines), mirroring `SourceSizeRatchetArchitectureTests`.
- The endpoint regex is the test's own; its first measured value is the baseline (305 call sites by the broad grep; the 275 in the plan table is not reused).
- `security` and `freeze-exception` labels also bypass the size step (SEC-SIMPL-70).
