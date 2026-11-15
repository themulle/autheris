# Handover 2026-11-15: continuing the plan with another agent

Purpose: everything a new agent (or human) needs to continue without the earlier session. The single source of truth for
status stays [`00-master-plan-overview.md`](00-master-plan-overview.md); this file records the working state, the rules the
owner set, and the next steps in order. Read `CLAUDE.md` and `AGENTS.md` first.

## 1. State at handover (2026-11-15)

Merged and pushed to `origin/main` (through governance migration `0022_docs_store`):
- **Core simplification & authz:** `WP-SIMPL-14` (Casbin and `.github/workflows/policy-lint.yml` removed), `WP-SIMPL-22 Chunks A..F` (all DB/host unit baseline entries eliminated), `WP-SIMPL-31 PR 1, PR 2a, PR 2b` (SQLite removed from `TrinoSqlEngine`, data-source runtime, and `Autheris.Tests.Unit`), `WP-SIMPL-39 PR 1..4`, `WP-SIMPL-40 PR 40a & 40b` (`0022_docs_store`), `WP-SIMPL-41 PR 1..4` (discovery core, artifacts & procedures merged; PR 4 snapshot/CI check pending), `WP-SIMPL-42 PR 1..7`, `WP-SIMPL-43 PR 1..5c` (core auth + SCIM), `WP-SIMPL-46 PR 1..6`, `WP-SIMPL-47 PR 1..6`, `WP-SIMPL-48 PR 1..5` (`0019_pattern_grants`), `WP-SIMPL-50 PR A..B3`, `WP-SIMPL-51 PR 1..5`, `WP-SIMPL-52 PR 1a (0020), 1b, 2, 3 (0021), 4a`, `WP-SIMPL-53 PR 1..5`.
- **Open-issue packages:** `WP-OI-SEC-ID-1..5` (`#472, #483, #487, #489`), `WP-OI-SEC-DP-1..14` (`#473, #476, #485, #491, #503`), `#624`, `#627`, `#666 (0016_dbt_unique_id)`, `#667`, `#668`, `#669`, `#672..#676 (0015_policy_epochs_change_version)`, `#677 PR 1, PR 2, PR 3, PR 4a` (get-all-tables baseline reduced to remaining callers; PR 4a snapshot/CI check pending), `#682`, `#749`.

Pushed feature branches on `origin` ready for rebase, conflict resolution, and CI/local verification (NOT yet merged into `main`):

| Branch | Package | State at handover | Next action |
|---|---|---|---|
| `origin/feat/wave7-rollup-2026-11-15` | Full local `main` rollup (267+ local commits + reconciled `origin/main` + `WP-SIMPL-41 PR 4` + `#677 PR 4a` + updated `docs/plans/`) | Pushed to `origin` (`origin/main` enforces GH013 PR-only rule) | Verify `CI Build & Test` on the rollup PR (sync DI snapshots / `security-test-inventory.txt` if drifted) and merge into `origin/main` |
| `origin/feat/simpl-40-pr40c-store-switch` | `WP-SIMPL-40 PR 40c` (`GovernanceDocStore` cutover) | Implemented & committed (`287dba9b9`), pushed to `origin` | Rebase onto rollup/main, resolve minor conflicts (`ApiErrorCodes.cs`, `endpoint-count.txt`, DI snapshots), verify & merge |
| `origin/feat/simpl-52-pr4b-masked-catalog-text` | `WP-SIMPL-52 PR 4b` (`#571` masked catalog text) | Implemented & committed (`7f61c6db7`), pushed to `origin` | Rebase onto rollup/main (`GatewayCatalogServiceExtensions.cs`, `AiDataGuardrailService.cs`, `McpDatasetCatalog.cs`), verify & merge |

## 2. Owner rules & local hardware constraints (binding)

- **Constrained local CPU/RAM build rules:** Run at most `2` concurrent `dotnet` processes across the machine using separate flock locks (`/tmp/autheris-build-slot-1.lock` and `/tmp/autheris-build-slot-2.lock`) with `MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget` and `-c Release -p:AutherisIncludeTestAuth=true --disable-build-servers -m:1 -nodeReuse:false`.
- Never run `Autheris.sln` or unfiltered `Autheris.Tests.Unit` / `Autheris.Tests.Integration` / `Autheris.Tests.Parity` suites locally; compile-check `Autheris.Tests.Integration`, `tools/SqlGate.BenchCheck`, and `tests/Autheris.Tests.SqlCompilerGate` with `--no-dependencies` and offload full/integration suites to GitHub Actions.
- **DDL serialization:** Only one DDL PR in flight at a time (next free migration step on `main` is **`0023`**).
- Merge a PR when no check is red, watch `main` ("CI Build & Test") on the merge commit and repair first. No `--admin`.
- Comments in `src/Autheris.Api` and `src/Autheris.Application` must not contain plan IDs (`WP-`/`SEC-`/`OD-`). English only for docs, comments, and commits. Never include literal `file://` links inside repo `.md` files (`DocsIntegrityTests`).

## 3. Ratchets & test verification gotchas

- `Autheris.Tests.Architecture`: `ReadOnlyTokenGetRouteTests` and `HostCompositionScopeValidationTests` must run in isolated `--filter` invocations before the remaining `Autheris.Tests.Architecture` (`!=`) run.
- Snapshots & security inventory: sync DI snapshots with `AUTHERIS_UPDATE_SNAPSHOTS=1` (`CompositionDescriptorSnapshotTests`), sync security inventory with `AUTHERIS_UPDATE_SECURITY_INVENTORY=1` (`SecurityTestInventory|SecurityBaselineLock`, staging BOTH `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt` AND `docs/plans/simplification/security-test-inventory.txt`), then re-run clean without env vars.

## 4. Next work, in dependency order

1. **Immediate check on resume:** Verify `main` on GitHub Actions (`CI Build & Test`) and locally (`Autheris.Tests.Architecture`, `CompositionDescriptorSnapshotTests`) for the merged `WP-SIMPL-41 PR 4` & `#677 PR 4a` commits, then rebase and merge `origin/feat/simpl-40-pr40c-store-switch` (`WP-SIMPL-40 PR 40c`) and `origin/feat/simpl-52-pr4b-masked-catalog-text` (`W7-52-4B #571`).
2. **Non-DDL queue (ready after step 1):**
   - `WP-SIMPL-40 PR 40d`: `LegacyDocFileImporter`, delete `DocProvenance.cs`, remove `/app/shared-docs` from `Dockerfile`.
   - `WP-SIMPL-41 PR 5`: request access with clear columns, retire `DatasetVisibility` tier model, access-request rate limit.
   - `#677 PR 4b`: migrate remaining `7` callers in `tests/Autheris.Tests.Architecture/Baselines/get-all-tables-callers.txt` (`CatalogSnapshotProvider.cs`, `DiscoveryService.cs`, `CatalogSearchWarmupService.cs`, `TenantCatalogLookup.cs`, `McpDatasetCatalog.cs`, `DbtMetadataIngestionService.cs`, `CatalogSchemaModel.cs`).
   - `#678`: configurable bounded caches (`Gateway:Catalog:Cache`) in `GovernanceRepository.cs` & `TenantCatalogLookup.cs`.
   - `W7-52-5A` & `W7-52-5B`: classification admin endpoints & per-classification clear requests.
   - `WP-SIMPL-31 PR 2c`: remaining non-`Tests.Unit` SQLite references (`Autheris.Tests.Common`, `Autheris.Tests.Integration`, `Autheris.Tests.SqlCompilerGate`, `Autheris.Sim.ApiHost`, `TrinoSqlEngine.Tests`).
   - `WP-SIMPL-44 PR 3` (`W7-44-3`) and `WP-SIMPL-50 PR B5, B6, C`.
   - Dependabot & nightly parity/fuzz triage (`#697, #699, #700`, `parity-nightly`, `simulation-nightly`).
3. **Serialized DDL queue (one step at a time, starting at `0023`):**
   - `0023`: `W7-52-6A` (`0023_drop_is_sensitive` — after remaining `IsSensitive` consumers finish)
   - `0024`: `W7-52-6B` (`0024_drop_superseded_governance_columns`)
   - `0025`: `W7-54-1..3` (`WP-SIMPL-54` golden queries from dbt meta `#580`)
   - `0026`: `WP-SIMPL-41 PR 6` (`doc_links` + `discovery_hidden` on docs, after `WP-SIMPL-40 PR 40c/d` and `WP-SIMPL-41 PR 5`)
   - `0027`: `W7-49-A1..A6` (`WP-SIMPL-49` data products Phase A)
4. **Blocked offline (missing in `/work/nuget`):** `#628b..d` (`Microsoft.ML.OnnxRuntime` & `Microsoft.ML.Tokenizers`).

## 5. Owner-only tasks (do not attempt)

OPS-01 rotate the PAT; create the protected `release` environment; PERS-04 residual; Helm ingress `/api/admin` decision; branch protection on `main`.
