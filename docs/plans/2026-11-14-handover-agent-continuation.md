# Handover 2026-11-15: continuing the plan with another agent

Purpose: everything a new agent (or human) needs to continue without the earlier session. The single source of truth for
status stays [`00-master-plan-overview.md`](00-master-plan-overview.md); this file records the working state, the rules the
owner set, and the next steps in order. Read `CLAUDE.md` and `AGENTS.md` first.

## 0a. Update 2026-11-18: simplification track resumed on .NET 11

- Merged on `main` (Phase 5 reviewed, CI green): PR #808 `WP-SIMPL-40 PR 40c` (GovernanceDocStore cutover, rebased; CRC fail-closed bundle reader, MCP deposit audit on every outcome, official bundle in one transaction), PR #809 `WP-SIMPL-52 PR 4b` (#571 masked catalog text incl. search, GraphQL and golden queries; search snapshot rebuild on rule changes), PR #810 `#678 PR 1` (`Gateway:Catalog:Cache` options, bounded provider cache; dead `TenantCatalogLookup` deleted), PR #811 `WP-SIMPL-31 PR 2c` (SQLite stand-ins removed from non-unit test projects; Parity and Simulation keep SQLite as reference oracle), PR #815 `WP-SIMPL-40 PR 40d` (legacy doc importer; `/app/shared-docs` removed from the Dockerfile with owner authorization).
- Stale items corrected: `WP-SIMPL-44 PR 3` and `WP-SIMPL-50 PR B5/B6/C` were already on `main`; `origin/feat/simpl-14-casbin-pr3/-pr4` deleted (content on `main`); Dependabot #697/#699/#700 closed.
- Owner decisions 2026-11-17: the 400 net src line PR limit is dropped for this track; remaining items go into fewer PRs per area; non-DDL lanes run in parallel (still at most 2 concurrent dotnet builds via the flock slots); DDL stays serialized in the order below, branches may be prepared in parallel and are renumbered at merge (the runner requires a gap-free step catalogue: rename the file pair, update both checksum goldens, `StepSqlLintTests` and the step tests). Issue #813 (search oracle via denied columns) has top priority (PR #817).
- Build environment: `global.json` pins SDK 11.0.100, installed at `/work/dotnet11` (set `DOTNET_ROOT` and `PATH`); `/usr/bin/dotnet` is 10.0.
- The `PR net-LOC check` reads the PR labels from the triggering event: after adding `size-exception`, push a commit (a re-run keeps the old payload).
- Follow-up issue #812: `benchmarks/load` still configures the removed SQLite governance provider.

## 0. Update 2026-11-16: the rollup is merged

- PR #756 (`feat/wave7-rollup-2026-11-15`, 334 commits) was merged into `main` as `0b8fc63bd` with a merge commit after every check was green (waivers: `size-exception`, `freeze-exception`; no `--admin`). Also on `main`: #754 (#625), #757 (#626), #758 (#733), #759 (#730).
- The rollup needed fixes that are now part of it: SQLite-era integration tests moved to PostgreSQL (`PostgresDataSource`, catalog schema `public`, `DefaultSchema` per source), group-resolution claims on hand-built test principals (fail-closed since the rollup), typed `timestamptz` parameters, paged catalog stubs, CrossTransport parity harness rebuilt on PostgreSQL with named projection rules.
- Production bugs found on the way and fixed: a first approval on a table with a sensitive column left the request `APPROVED` without a consent (pipeline routed by grant class); SCIM realms other than the default realm always got 403 (realm claim was stripped); the grant record hash chain broke on PostgreSQL microsecond timestamps; the expiry sweep refused the platform tenant; `CONSENT_REQUEST_NOT_FOUND` was masked as `INTERNAL_SERVER_ERROR`; audit detail keys containing `key` were redacted; a hard row policy with an unevaluable stored pattern now denies with `VF_PATTERN_EVALUATION_FAILED`; stream RLS now honours the stricter decision point column level; `is_active` is bound as boolean.
- Owner decision 2026-11-16: grants may be valid for several years, no compiled-in maximum (`Gateway:Governance:Grants:MaxDuration` is required configuration; optional operator class limits only). See `WP-SIMPL-47` step 7.
- Open design questions for the owner: SQL surfaces return a denied column as `NULL` while other surfaces drop it; should delegated (e.g. Databricks) tables be reachable through WebSQL; should an allow list that clears every column count as unconstrained for Envoy and Lakehouse; should a Sensitive-class individual grant need a second approver (the repository is authoritative, the pipeline no longer routes by grant class); SCIM principals are still degraded by a realm client allow list.
- Needs a security review: three ingress exemptions (`grant-records/{grantId}/export`, `grant-records:verify`, `profile-assignments/{id}/revoke`) and twelve audit endpoint justifications that agents added to baselines; the parity expectation changes (named rules `SqlDeniedColumnIsRedactedConstant`, `SqlRejectsDelegatedSource`, `AdmissionDeniesColumnConstrainedGrant`).
- Next, in order: rebase `feat/simpl-40-pr40c-store-switch` and `feat/simpl-52-pr4b-masked-catalog-text` onto `main`; Dependabot PRs; nightly issues #741, #745, #746; then the remaining plan issues (#670, #671, #678, MCP-1..8, Phase 4 security packages). Owner-only: OPS-01 PAT rotation, protected `release` environment, branch protection on `main`, Helm ingress `/api/admin` decision.
- Agent hint: in auto mode the first `Agent` call is denied because a hook rewrites the prompt; send the call again with exactly the input quoted in the denial and it runs.

## 1. State at handover (2026-11-15)

Merged and pushed to `origin/main` (through governance migration `0022_docs_store`):
- **Core simplification & authz:** `WP-SIMPL-14` (Casbin and `.github/workflows/policy-lint.yml` removed), `WP-SIMPL-22 Chunks A..F` (all DB/host unit baseline entries eliminated), `WP-SIMPL-31 PR 1, PR 2a, PR 2b` (SQLite removed from `TrinoSqlEngine`, data-source runtime, and `Autheris.Tests.Unit`), `WP-SIMPL-39 PR 1..4`, `WP-SIMPL-40 PR 40a & 40b` (`0022_docs_store`), `WP-SIMPL-41 PR 1..4` (discovery core, artifacts & procedures merged; PR 4 snapshot/CI check pending), `WP-SIMPL-42 PR 1..7`, `WP-SIMPL-43 PR 1..5c` (core auth + SCIM), `WP-SIMPL-46 PR 1..6`, `WP-SIMPL-47 PR 1..6`, `WP-SIMPL-48 PR 1..5` (`0019_pattern_grants`), `WP-SIMPL-50 PR A..B3`, `WP-SIMPL-51 PR 1..5`, `WP-SIMPL-52 PR 1a (0020), 1b, 2, 3 (0021), 4a`, `WP-SIMPL-53 PR 1..5`.
- **Open-issue packages:** `WP-OI-SEC-ID-1..5` (`#472, #483, #487, #489`), `WP-OI-SEC-DP-1..14` (`#473, #476, #485, #491, #503`), `#624`, `#627`, `#666 (0016_dbt_unique_id)`, `#667`, `#668`, `#669`, `#672..#676 (0015_policy_epochs_change_version)`, `#677 PR 1, PR 2, PR 3, PR 4a` (get-all-tables baseline reduced to remaining callers; PR 4a snapshot/CI check pending), `#682`, `#749`.

Pushed feature branches on `origin` (state at the 2026-11-15 handover; the rollup row is merged, the others still wait for a rebase onto `main`):

| Branch | Package | State at handover | Next action |
|---|---|---|---|
| `origin/feat/wave7-rollup-2026-11-15` | Full local `main` rollup | **MERGED 2026-11-16 as PR #756 (`0b8fc63bd`)** | none |
| `origin/feat/simpl-40-pr40c-store-switch` | `WP-SIMPL-40 PR 40c` (`GovernanceDocStore` cutover) | Implemented & committed (`287dba9b9`), pushed to `origin` | Rebase onto rollup/main, resolve minor conflicts (`ApiErrorCodes.cs`, `endpoint-count.txt`, DI snapshots), verify & merge |
| `origin/feat/simpl-52-pr4b-masked-catalog-text` | `WP-SIMPL-52 PR 4b` (`#571` masked catalog text) | Implemented & committed (`7f61c6db7`), pushed to `origin` | Rebase onto rollup/main (`GatewayCatalogServiceExtensions.cs`, `AiDataGuardrailService.cs`, `McpDatasetCatalog.cs`), verify & merge |
| Issues #730, #733 | merged on `main` (PR #759, PR #758) | none; the rollup carries both (request principal attached to the security context; GET SQL endpoints select-only). The PDP Degraded precondition stays with WP-SIMPL-46 PR 6 |

## 2. Owner rules & local hardware constraints (binding)

- **Constrained local CPU/RAM build rules:** Run at most `2` concurrent `dotnet` processes across the machine using separate flock locks (`/tmp/autheris-build-slot-1.lock` and `/tmp/autheris-build-slot-2.lock`) with `MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget` and `-c Release -p:AutherisIncludeTestAuth=true --disable-build-servers -m:1 -nodeReuse:false`.
- Never run `Autheris.sln` or unfiltered `Autheris.Tests.Unit` / `Autheris.Tests.Integration` / `Autheris.Tests.Parity` suites locally; compile-check `Autheris.Tests.Integration`, `tools/SqlGate.BenchCheck`, and `tests/Autheris.Tests.SqlCompilerGate` with `--no-dependencies` and offload full/integration suites to GitHub Actions.
- **DDL serialization:** Only one DDL PR in flight at a time (next free migration step on `main` is **`0023`**).
- Merge a PR when no check is red, watch `main` ("CI Build & Test") on the merge commit and repair first. No `--admin`.
- Comments in `src/Autheris.Api` and `src/Autheris.Application` must not contain plan IDs (`WP-`/`SEC-`/`OD-`). English only for docs, comments, and commits. Never include absolute filesystem links inside repo `.md` files (`DocsIntegrityTests`).

## 3. Ratchets & test verification gotchas

- `Autheris.Tests.Architecture`: `ReadOnlyTokenGetRouteTests` and `HostCompositionScopeValidationTests` must run in isolated `--filter` invocations before the remaining `Autheris.Tests.Architecture` (`!=`) run.
- Snapshots & security inventory: sync DI snapshots with `AUTHERIS_UPDATE_SNAPSHOTS=1` (`CompositionDescriptorSnapshotTests`), sync security inventory with `AUTHERIS_UPDATE_SECURITY_INVENTORY=1` (`SecurityTestInventory|SecurityBaselineLock`, staging BOTH `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt` AND `docs/plans/simplification/security-test-inventory.txt`), then re-run clean without env vars.

## 4. Next work, in dependency order

**Update 2026-11-19 (simplification lane B):**
- **Merged on `main`:**
  - #855: `WP-SIMPL-10` footprint "after" file and `WP-SIMPL-35` budgets (OD-37 kept at 325 MB / 3.0 s).
  - #862, #876, #870: `WP-SIMPL-36` sub-packages 5a, 5b and 6a.
  - #863, #864, #877, #882: `WP-SIMPL-31`, `WP-SIMPL-27`, `WP-SIMPL-50` and `WP-SIMPL-48` (parity rows renumbered to S30+).
  - #872, #884, #885: `WP-SIMPL-25` sign-off, hardening, and the core/full image flavors (#885 was merged with the owner's approval).
  - #874: security addenda (WP-38/46/52, profile items).
  - #895: `WP-SIMPL-19` PR-C slice 1.
- **v1.9.0 scope:** #868 (`WP-SIMPL-36` 6b, Worm export removed, breaking) and #900 (`WP-SIMPL-52a` role grantee removal, step `0029`, breaking). #900 lands alone in its own DDL batch, and #904 (step `0030`) lands after it.
- **Held until after the 1.9.0 cut:**
  - #883: `WP-SIMPL-46`, `IUnifiedPolicyDecisionPoint` removed.
  - #887: `WP-SIMPL-38` PR 4, `system.jdbc`.
  - #888: `WP-SIMPL-44/45` container and integration suites.
  - #899: `WP-SIMPL-20` PR B.
  - #902 and #905: `WP-SIMPL-19` PR-C slices 2 and 3 (stacked).
- **Owner review (do not merge):** `WP-SIMPL-38` PR 5, the Trino client interop nightly workflow, on branch `ci/simpl-38-pr5-trino-interop-owner-review`.
- **Still open:**
  - `WP-SIMPL-20` PR A: fold the ConsentPattern and GrantRecord cores after #900, then recompute `MaxAllowedLoc`.
  - `WP-SIMPL-52b`: the profile items migration replaces `access_profiles.masking_mode/target_tables`. It takes the next free step after #904. `consent_row_filters.key_set_*` stays (D-PI-7).
- **Private advisory filed:** GHSA-ff36-3926-36r5 (private, unpublished).

**Update 2026-11-18 night:**
- **Merged on `main`:**
  - Issue PRs: #823, #826 (keys, indexes and required filters on discovery; denied columns of a granted table are no longer listed on the REST detail), #828, #831, #832.
  - Simplification:
    - `WP-SIMPL-41 PR 6a` (#837, step `0026_doc_links`, SQL Server keys through a `link_digest`).
    - `WP-SIMPL-52 PR 6c` (#842, step `0027_drop_legacy_governance_columns`, narrowed to `TABLE_CATALOG_SETTINGS.visibility` and `consent_requests.itsm_ticket_id`).
    - `WP-SIMPL-23` root moves (#840: 127 of 129 root files; the last two wait for an owner edit in `docs/features/f-sql-02-governed-stored-procedures.md`).
    - `WP-SIMPL-32 PR 1` (#841: options inventory and budget ratchets; `Gateway:CatalogSearch:Enabled` removed).
    - `WP-SIMPL-28 PR 1+2` (#844: #488 already fixed by #637; `ISemanticMetricImport`, `NoDbtHealthData`).
- **Open PRs:**
  - #830 (#802 schema rebuild, approved).
  - #827, then #829 (stacked).
  - #843 `WP-SIMPL-41 PR 6b` (approved after the second review).
  - #846 `WP-SIMPL-36` sub-package 7 (review fixes in progress; GHSA-66xj function oracles are tracked by #847, and the x8g9 and rxr4 residual items stay in their private advisories).
- **`WP-SIMPL-49`** (branch `feat/simpl-49-data-products-a`, step renumbered to `0028_data_products`):
  - Phase 3 is done. Conditions SEC-DP-01..22 are in the plan, and SEC-DP-01..05 are fixed.
  - A3 (read procedures) and A4 (write procedures with `ExecuteWrite`) are done.
  - A5 (Git sync) is in progress; A6 is still open.
  - The governance repository LOC budget is raised by the products partial.
- **New follow-up issues:**
  - #835: GraphQL rebuild on restrictive changes.
  - #838: GraphQL discovery lists denied columns.
  - #847: SQL user expressions before the row and tenant predicates.
- **WP-52 follow-ups still open** (live readers):
  - `consents.role_id/role_name`: Role grantee removal.
  - `access_profiles.masking_mode/target_tables`: profile items migration.
  - `consent_row_filters.key_set_*`: a decision is needed.
- **Process:**
  - After #840, `AUTHERIS_UPDATE_SECURITY_INVENTORY=1` appends the renamed unit tests to `security-tests-base.txt`. Keep that file as `main` plus the PR's own tests.
  - Every merge on `main` re-conflicts `CHANGELOG.md` in the open PRs. Merge a green PR before resolving the next one.

State at the end of the 2026-11-18 session. Merged since section 0a: #817 (#813 search oracle, closed), #819 (`WP-SIMPL-52 PR 5a+5b`), #822 (`#677 PR 4b` + `#678 PR 2`; compact search payload deferred, see the catalog-scale plan note), #825 (`WP-SIMPL-41 PR 5`, tier model retired). Remaining, in order:
1. **DDL queue (strictly one at a time; the runner needs a gap-free step catalogue, so a later branch renumbers its step pair, both checksum goldens, `StepSqlLintTests` and its step tests):**
   - DONE: PR #824 `WP-SIMPL-52 PR 6a+6b` (steps `0023_drop_is_sensitive`, `0024_drop_superseded_governance_columns`; all nodes must run the new build before 0023 applies; guard test `TableMetadataCopyArchitectureTests` keeps `ColumnClassifications` on table copies) and PR #821 `WP-SIMPL-54` (step `0025_golden_queries`, #580 closed). `main` now ends at step `0025`; main CI green after #824.
   - NEXT: branch `feat/simpl-41-pr6-linked-docs` (`WP-SIMPL-41 PR 6a`, step `0026_doc_links` is already the right number): `doc_links` table, `discovery_hidden` on both doc tables, `IDocLinkRepository` (SQL in `Persistence/Docs/DocLinkOperations.cs`), `DocLinkService`, 3 PUT routes, audit `DOC_LINKS_CHANGED`/`DOC_VISIBILITY_CHANGED`; merge `origin/main`, Phase 5 review, PR with `freeze-exception` (endpoints 319 -> 322), merge. Accepted defaults: NOT NULL `''` instead of NULL for optional key columns (unique index parity), `ListForSourceAsync`, Procedure targets 404 until 6b, discoverability via owner/shared scope only. `TABLE_CATALOG_SETTINGS.visibility` is still written by `DatasetCatalogStore` inserts (drop it in a later step after removing the writes).
   - Then `WP-SIMPL-41 PR 6b` (no DDL): linked docs in discovery objects, search and MCP (`describe_dataset` documents, `search_docs` filters, `read_document`, `links` on deposit/PUT, untrusted-text pass) with the spec tests.
   - Branch `feat/simpl-49-data-products-a` (`WP-SIMPL-49`, step `0023_data_products`, renumber to the next free step, last): A1 + A2 done (model/store/roles/API, definer load, `products.t_<hash>.<schema>__<name>` identity, catalog persistence and ownership, stage-2 connector, schedule/idle-drop workers, re-projection). Open: A3 read procedures (decided defaults in the plan changelog: columns from `DeclaredOutputs`, non-empty `ProcedureResult` row policy fails closed for now, `ProductSchema` on `ProcedureDefinition`), A4 write procedures, A5 Git sync, A6 discovery/usage/parity rows; then Phase 3 review of the whole branch before the PR.
2. **Follow-ups:** `WP-SIMPL-52 PR 6c` (remove the readers of `consents.role_id/role_name`, `consent_requests.itsm_ticket_id`, `access_profiles.masking_mode/target_tables`, `consent_row_filters.key_set_*`, then a DDL step) and the dropped `TABLE_CATALOG_SETTINGS.visibility` column (WP-41 PR 5 stopped reading it).
3. **Other open WP-SIMPL work (code audit 2026-11-18 against `main`):** WP-10 footprint "after" file; WP-19 PR-C; WP-20 remaining `*SqlCore` files and LOC target; WP-23 test restructuring (6-8 PRs); WP-25 Dockerfile `FLAVOR` arg + Phase 3 sign-off; WP-27 Lakehouse models out of Domain; WP-28 (3) and WP-29 (4) modules; WP-31 SQLite comment hits; WP-32 configuration reduction (~6, not started); WP-35 budgets vs OD-37 (owner); WP-36 sub-packages 5a/5b, 6b/6c, 7 (5); WP-38 PR 4/5; WP-44/45 container and integration suites; WP-46 `IUnifiedPolicyDecisionPoint` removal; WP-48 PR 7 parity rows (id clash with WP-46 S20-S22, owner renumbering decision); WP-50 C8 (`CS0108;CA2012` NoWarn); small leftovers in WP-12, 39, 40, 42.
4. **Blocked offline (missing in `/work/nuget`):** `#628b..d` (`Microsoft.ML.OnnxRuntime` & `Microsoft.ML.Tokenizers`).
5. **Process notes:** the repository LOC budget (`GovernanceRepositoryLocBudgetTests`) is at its limit: put new SQL into operations classes under `src/Autheris.Infrastructure/Persistence/` (examples: `CatalogMetadataBatchReader`, `GoldenQueryStoreOperations`, `Products/*`). Long agent sessions exhaust context around 400-500k tokens: hand large WPs to fresh agents per slice. Review findings in this session repeatedly caught collateral test deletions and security regressions; keep Phase 5 on every PR.

## 5. Owner-only tasks (do not attempt)

OPS-01 rotate the PAT; create the protected `release` environment; PERS-04 residual; Helm ingress `/api/admin` decision; branch protection on `main`.
