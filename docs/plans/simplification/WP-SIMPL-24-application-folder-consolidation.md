# WP-SIMPL-24 Application folder consolidation (<= 32, ratchet)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-24-application-folder-consolidation--32-ratchet) | Issues: #515 (stage 2) | Owner decisions: OD-5 (ITSM deleted), OD-8 (WORM export deleted), OD-11 (Analytics core), OD-15 (DuckDB/table cache core), OD-18 (Procedures, Docs core), OD-20 (FinOps -> Usage), OD-21 (Mesh core), OD-24 (OData core) | Security conditions: SEC-SIMPL-03 (tests move with code, names stable), SEC-SIMPL-05 (stage order)

## 1. Goal and outcome
Reduce the top-level folders of `src/Autheris.Application` to <= 32 at Stage 2 exit by merging related folders as subfolders of a target folder, with namespaces following folders. Pure move refactoring: no logic change, all tests green, the WP-SIMPL-07 allow-list shrinks in the same PR as each merge, `namespace-assembly-violations.txt` stays header-only.

## 2. Preconditions and dependencies
- Merged first: WP-SIMPL-07 (ratchet + allow-list), WP-SIMPL-17 (-4 folders), WP-SIMPL-18 (borderline deletions), WP-SIMPL-12 (deletes `Integrations`, `Workflows/ItsmWorkflowDispatcher.cs`, `Interfaces/IItsm*`), WP-SIMPL-15 (deletes `Diagnostics/Shadowing`), WP-SIMPL-16 (Flight files only: `Serialization/ArrowFlightSqlServer.cs`, `IArrowFlightSqlServer.cs`; no folder removed).
- Coordinated (not in parallel, whichever merges second rebases): WP-SIMPL-42 (`FinOps` -> new `Usage` folder, net 0 folders), WP-SIMPL-39 (`Procedures` kept as is), WP-SIMPL-40 (`Docs` kept as is), WP-SIMPL-43. `Mesh` (OD-21), `Procedures`, `Docs` (OD-18) and `OData` (core, OD-24) stay top-level folders.
- Any new top-level folder added by WP-SIMPL-38..48 other than WP-SIMPL-42 `Usage` (which replaces `FinOps`) must be placed under an existing folder; otherwise the <= 32 ratchet fails.
- Merge groups (section 5) are independent of each other and may be done in any order, but serially (they all touch the allow-list and DI snapshots).
- Must merge before module extraction WPs that move these namespaces (WP-SIMPL-29 events, WP-SIMPL-27/28) only where they touch the same folders; Events is left untouched here.

## 3. Scope
### In scope
- The seven merge groups of the plan: Observability, Common, DataSources, Runtime, Sql, State, Governance (section 5).
- Distribution of `Application/Interfaces` to owner folders where the owner is unambiguous (Audit, Lineage), the rest to `Common/Interfaces`.
- Test-folder moves mirroring src where tests sit in same-named folders, DI snapshot regeneration, allow-list shrink.
### Out of scope
- `Events` (stays until WP-SIMPL-29), `Services` (24 files; separate distribution not planned), any type rename or logic change, Extensions/Infrastructure folders.
- Further reduction to <= 25 (after module extraction, Stage 3).

## 4. Current state (verified at 63cbb893)
- 49 top-level folders today (re-verified at 88e5ab74). Expected before this WP: 49 - 1 (Integrations, WP-12) - 1 (Diagnostics, WP-15) - 4 (Auditing, DataCatalog, Dbt, OpenMetadata; WP-17) - 1 (SchemaRegistry, WP-18) = 42. Unchanged: FinOps -> Usage (WP-42, net 0), Mesh (core, OD-21), Docs and Procedures (core, OD-18), OData (core, OD-24), Serialization (WP-16 deletes only the Flight files).
- Source folders and namespaces (files): Observability 2 (`Autheris.Application.Observability`), Telemetry 10 (`.Telemetry`), Diagnostics 5 (`.Diagnostics.Shadowing`), Performance 2 (`.Performance.IncrementalDelivery`), Common 6 (`.Common`, `.Common.Hosting`, `.Common.Paging`), Serialization 6 (`.Serialization`), Interfaces 44 (`.Interfaces`), DataSources 9 (`.DataSources`), Connectors 11 (`.Connectors`, `.Connectors.Adapters`, `.Connectors.CrossDomain`), Data 33 (`.Data`, `.Data.Changes`, `.Data.Interfaces`, `.Data.Relations`, `.Data.Services`), Hosting 2 (`.Hosting`), Jobs 15 (`.Jobs.Interfaces`, `.Jobs.Services`), ResourceGroups 3, SqlEndpoints 5 (`.SqlEndpoints.Interfaces`, `.SqlEndpoints.Services`), Caching 14 (`.Caching`, `.Caching.Interfaces`, `.Caching.Services`, `.Caching.TableCache[.Engines|.Governance]`), State 8 (`.State`), Workflows 2 (`ConsentRecertificationWorkflowService.cs`, `ItsmWorkflowDispatcher.cs`).
- Type-name collision check inside each merge group: none (verified by declaration scan).
- DI snapshots `tests/Autheris.Tests.Unit/DependencyInjection/Snapshots/composition-*.txt` contain fully qualified names.

## 5. Implementation steps
Rule for every group: `git mv` the source folder to `<Target>/<Sub>`; namespace `Autheris.Application.<Old>[.X]` -> `Autheris.Application.<Target>.<Sub>[.X]`; repo-wide `sed` on the namespace string across `src` and `tests` (including baselines and DI snapshots), remove duplicated `using` lines; shrink the allow-list; build and test. One PR per group.
1. Observability: `Telemetry/*` -> `Observability/Telemetry/`; `Performance/IncrementalDelivery` -> `Observability/IncrementalDelivery` (namespace `Autheris.Application.Observability.IncrementalDelivery`); `Diagnostics` -> `Observability/Diagnostics` only if WP-15 has not deleted it. Allow-list: -Telemetry, -Performance, (-Diagnostics).
2. Common: `Serialization/*` -> `Common/Serialization/`. `Interfaces`: `IAuditChainAnchorStore`, `IAuditPurgeKeyProvider` -> `Audit/` (namespace `Autheris.Application.Audit`); `ILineageGraphStore`, `ILineageImpactAnalyzerService`, `IOpenLineageClient` -> `Lineage/` (namespace of the existing Lineage files); all others -> `Common/Interfaces/` (namespace `Autheris.Application.Common.Interfaces`). Interfaces already deleted by WP-12/OD-8 (`IItsm*`, `IJustificationTriageService`, `IAuditWormExportService`) must not be recreated. Allow-list: -Serialization, -Interfaces.
3. DataSources: `Connectors/*` -> `DataSources/Connectors/`; `Data/{Changes,Interfaces,Relations,Services}` -> `DataSources/{Changes,Interfaces,Relations,Services}/` and `Data/*.cs` root files -> `DataSources/` (namespaces `Autheris.Application.DataSources.Changes` etc.). Allow-list: -Connectors, -Data.
4. Runtime (new folder): `Hosting/*` -> `Runtime/Hosting/`, `Jobs/*` -> `Runtime/Jobs/`, `ResourceGroups/*` -> `Runtime/ResourceGroups/`. Allow-list: -Hosting, -Jobs, -ResourceGroups, +Runtime.
5. Sql: `SqlEndpoints/*` -> `Sql/Endpoints/` (namespace `Autheris.Application.Sql.Endpoints.*`; if `Sql/Endpoints` already exists, use `Sql/SqlEndpoints`). Allow-list: -SqlEndpoints.
6. State: `Caching/*` -> `State/Caching/` (namespaces `Autheris.Application.State.Caching[.TableCache...]`). Allow-list: -Caching.
7. Governance: `Workflows/ConsentRecertificationWorkflowService.cs` -> `Governance/Workflows/` (namespace `Autheris.Application.Governance.Workflows`). Allow-list: -Workflows.
8. After the last group: set the ratchet maximum in `ProgramRatchetTests` (or its baseline) to the resulting count, which must be <= 32.

## 6. Tests
- Existing: all Unit, Integration, Extensions and Architecture tests unchanged except namespaces/usings; test method names stable (SEC-SIMPL-03). Test files in `tests/Autheris.Tests.Unit/<OldFolder>/` move to the mirrored path only if the folder exists; namespace follows.
- `ProgramRatchetTests.Application_top_level_folders_are_allow_listed` (allow-list shrinks per PR), `NamespaceAssemblyConsistencyTests` (baseline header-only), project-edge test (`Baselines/project-edges.txt` unchanged).
- New in `tests/Autheris.Tests.Architecture/ProgramRatchetTests.cs`: `Application_top_level_folder_count_is_at_most_32` (counts directories under `src/Autheris.Application` excluding `bin`/`obj`; asserts <= 32). Added in the last PR.
- DI composition snapshot tests: regenerated, namespace-only diff.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (one build at a time).
- `dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build` and `tests/Autheris.Extensions.Tests` once per PR.
- `git grep -n -E 'Autheris\.Application\.(Telemetry|Performance|Diagnostics|Serialization|Interfaces|Connectors|Data|Hosting|Jobs|ResourceGroups|SqlEndpoints|Caching|Workflows)\b' -- src tests` returns nothing at the end (note `\b` keeps `DataSources` out of the `Data` match).
- `find src/Autheris.Application -mindepth 1 -maxdepth 1 -type d ! -name bin ! -name obj | wc -l` <= 32.

## 8. Acceptance criteria
- [ ] Application top-level folder count <= 32 (expected 31: 42 before this WP minus 11).
- [ ] grep in section 7 returns nothing; `git diff -M --stat` shows renames only plus using/namespace lines.
- [ ] Allow-list equals the actual folder set; `Application_top_level_folder_count_is_at_most_32` green.
- [ ] `namespace-assembly-violations.txt` and `project-edges.txt` unchanged.
- [ ] All test projects green; DI snapshot diffs are namespace-only.

## 9. PR slicing
Seven PRs, one per group (section 5 steps 1-7), step 8 in the last one. Each is a move: net added src lines ~0. Order suggestion by blast radius: 7, 5, 4, 1, 6, 3, 2 (Interfaces last; touches most files).

## 10. Risks, rollback, and things the developer must NOT do
- Risk: namespace churn conflicts with parallel lanes; keep PRs short-lived, rebase right before merge; rollback = revert the PR.
- `sed` on `Autheris.Application.Data` must use a word boundary so `DataSources`/`DataCatalog` stay intact; review the diff for accidental hits.
- Do NOT change logic, type names, visibility, logging categories or metric names. Exception: if a logger category string equals the namespace, keep the old string literal (dashboards depend on it).
- Do NOT merge Events, Services, Security, Policy, Identity, Governance subfolders beyond step 7.
- Do NOT add baseline entries to make tests pass.

## 11. Estimate
Src ~0 net (moves, ~1,500 using lines touched); tests ~+15 (one new test). 3 person-days.

## Decisions taken in this spec
1. Merged folders become subfolders of the target (`Observability/Telemetry`), not flattened, except `Data` which is flattened into `DataSources` (same domain, no collisions).
2. `Interfaces` distribution: only Audit and Lineage interfaces go to owners; the rest to `Common/Interfaces` to limit churn.
3. Arithmetic (under OD-18/20/21/24): 42 folders after WP-12/15/17/18, minus 11 here (group 1: -2, group 2: -2, group 3: -2, group 4: -3 +1, group 5: -1, group 6: -1, group 7: -1) = 31, one folder of slack below 32; no additional merges needed. Any new top-level folder from WP-38..48 must be placed under an existing folder (WP-42 `Usage` replaces `FinOps`, net 0). If WP-12/15/17/18 are not merged first, the count would be 32-35 and this WP must wait.
4. `Performance/IncrementalDelivery` goes to Observability per plan; namespace keeps only `IncrementalDelivery` as sub-part.
