# Handover: AST Compiler X1 Cutover (PLAN-AST-DIALECT-GEN-16)

**Updated:** 2026-10-13 (evening) · **Parent plan:** [`2026-10-10-plan-ast-dialect-generator.md`](2026-10-10-plan-ast-dialect-generator.md) (§28 X1 execution plan, §29 Phase 3 review, implementation logs at the end of the file) · **Master plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md)

This document lets another agent continue the X1 cutover track. It records where the work stands, what remains before the squash, and the working rules. The parent plan holds the technical detail. Read its §28, §29 and the implementation logs at the end before changing code.

## 1. Working rules

- **Lifecycle:** every package goes through the AGENTS.md 6-phase lifecycle. Phase 4 TDD developer agents run on **Sonnet at most, never Opus** (user directive).
- **Pushing:** the user approved pushing intermediate states of the `feat/ast-x1-*` branches. **Push after every commit:** the `/work` directory is lost when the dev container is recreated, and that has already happened once. The user opens all pull requests; there is no `gh` CLI.
- **Breaking changes are allowed.** Autheris has no production deployment yet. Still list every client-visible change in the plan logs and the squash message. Security-relevant decisions still need Phase 3.
- **Out of scope without user approval:** the GraphQL tree compiler migration (OQ-GQL).
- **Language:** English for code, comments, commits and docs. `docs/` (outside `docs/plans/`) describes current behaviour only, with no status or WIP notes.

## 2. Environment

- `/root` and `/root/autheris` are a **9p mount of the Windows C: drive and are very slow**. Work in native clones under `/work`.
- **Rebuild after a container loss:**
  1. `git clone --branch feat/ast-x1-cutover <origin> /work/autheris-int`.
  2. Set `git config user.name/user.email` from `/root/autheris`.
  3. `dotnet restore Autheris.sln` with `NUGET_PACKAGES=/work/nuget DOTNET_CLI_HOME=/work/dotnethome` (about 3 minutes).
  4. `dotnet tool restore`.
- Export `NUGET_PACKAGES=/work/nuget DOTNET_CLI_HOME=/work/dotnethome` for every `dotnet` command. For Stryker also export `VSTEST_CONNECTION_TIMEOUT=600`.
- **Testcontainers:** set `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal CI=true`; the container CLI is `docker`. Run at most one Oracle container at a time, and stop only containers your own runs leave behind.
- **Machine limits:** 12 GB RAM and 16 cores. Builds use `-m:2`. Do not run Stryker, a full Integration suite and another full suite at the same time.
- **Gate profile:** `SQLGATE_PROFILE=pr`.

## 3. Branches

| Branch | State |
|---|---|
| `feat/ast-x1-cutover` | **Main line**, at `e2a57e2` (pushed). Contains:<br>• all X0 packages, merged with `main` (`b4de3de`)<br>• the pre-X1 review fixes<br>• X1 part A (wiring) and part B (legacy deletion, architecture guards, frozen gate goldens, README)<br>• Phase 3 fixes (M-A1..L-A1, re-check M-2, L-1..L-6)<br>• Phase 5 fixes<br>• the ported Stryker survivor tests<br>• Phase 6 docs<br>• the pre-squash fixes |
| `feat/ast-x1-stryker-cutover` | M-12 Stryker work on the cutover head (worktree `/work/x1-stryker2`). Merge it into the main line when done. |
| `feat/ast-x1-integration`, `-integration-main`, `-prefix`, `-secfix`, `-secfix2`, `-p5fix`, `-docs`, `-smallfix`, `-stryker` | Intermediate branches, all contained in the main line (except `-stryker`, whose tests were ported). Kept for history. |
| `docs/ast-x1-handover` | Old copy of this handover. Superseded by this file on the main line. |

Last full verification of the main line, at `a924f2b` and the later fix branches:

| Suite | Passed |
|---|---|
| Unit | 5379 |
| TrinoSqlEngine (Debug) | 7716 |
| Architecture | 62 |
| Extensions | 233 |
| SqlCompilerGate (`pr`) | 181 |
| Integration | 1024 (one SQLite timing test is load-sensitive) |

## 4. Remaining before the X1 squash (in order)

1. **Stryker M-12 on the cutover head.**
   - It is in progress on `feat/ast-x1-stryker-cutover`. Measure every group of the `stryker-config.json` mutate scope (`Ast/Security`, `Ast/Emit`, the `AstSecurityVisitor*` partials, `AstSimplificationVisitor`).
   - Every file must reach at least 85%.
   - Write the "Stryker coverage implementation log (M-12, cutover head)". Pre-cutover scores are in that branch's history and in the plan.
   - Merge the branch into the main line.
2. **G8 re-check on an idle machine:**
   - Run the benchmarks, then `tools/SqlGate.BenchCheck`. After legacy removal BenchCheck uses the frozen baseline and `legacyGen2PerThousandOps`.
   - X1 added per-statement pipeline costs that G8 does not measure: the governance fingerprint read, dependent-table checks and HMACs. A pipeline microbenchmark is a "later" item.
3. **M-11 revert re-rehearsal on the final squash.** Phase 3 binding, see the "Phase 3 re-check" log.
   - Order and set: X1, then X0x (`-m 1 3da0248`), X0y (`c9bc3c9 45e88bb bf2dd42`, keep `cd953de`), X0l (`5201a34 8d8a754 385ed74 6ed50f3 a5a06b3 a37d2aa 1490f43`), X0g `d9c75eb`, X0f `3c57b01 393e210`, X0e `bfa181b`, X0d `3b5a1d1`.
   - Use `git revert --no-commit` for each step and finish with one commit.
   - Write every manual resolution as an exact command.
   - After the revert, run the build, G1, G7 and the Integration subset (WebSQL tenant isolation, REST/MCP dataset row policy).
   - Write the resulting procedure, plus the list of controls lost on revert, into the squash message draft. That section is currently a placeholder.
   - Recommended: a CI-green `rollback/x1` branch.
4. **Process items (user and repository owner):**
   - Phase 3 approval for each of the 15 `accepted-differences.json` rows that are still `"approval": "pending"`, especially the Oracle `FewerRows` group.
   - Make `sql-compiler-gate` a required check.
   - PolicyPredicateLint must report 0 unparsable production predicates before the first deployment.
5. **Squash** `feat/ast-x1-cutover` into one commit with the drafted message (part B log in the parent plan, kept up to date by later logs). The user opens the PR.

## 5. After X1

- **WP-X1-DBX:** Databricks to Production after a green G9 run and S-2. It includes the Databricks hardening L7 from the X0 Phase 5 review.
- **WP-X2:** post-cutover hardening (parent plan §11.7).
- **"Later" items:**
  - the X0 Phase 5 table: L5, L9-L13 and the nits;
  - the X1 Phase 5 log: the pipeline microbenchmark;
  - the X1 Phase 3 re-check log.
- **Then:** every other open item in `docs/plans/`, one after another, each through the 6 phases on its own branch (user directive).

## 6. Ops and stakeholder items

- Verify the nuget.org signing certificate fingerprints out of band. Add the successor certificate before 2027-05-18.
- Create the protected `databricks-g9` environment and its secrets (S-4).
- Oracle driver license review (S-1). It blocks announcing Oracle as supported, not the code.
- **Before the first deployment:**
  - provide `GovernanceDb:AuditHmacKeyVaultRef` (or `DataMasking:HmacSecretKeyVaultRef`); without it the host does not start outside Development and Test;
  - set `DefaultSchema` per PostgreSQL and Oracle source;
  - run PolicyPredicateLint.
- Runbook for clearing a tenant-registry collision: see `docs/features/f-dialect-02-ast-sql-compiler.md`.
