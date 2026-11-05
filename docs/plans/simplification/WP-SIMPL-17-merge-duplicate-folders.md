# WP-SIMPL-17 Merge duplicate folders
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-17-merge-duplicate-folders) | Issues: #515 (stage 1), #459 (sequenced after this WP) | Owner decisions: PRD section 3.2 (OpenMetadata and dbt = MODULE, WP-SIMPL-28); forward-looking note OD-15 (`SemanticMetricCatalogProjection` goes to core `Catalog/Semantic`) | Security conditions: SEC-SIMPL-03 (tests move with code, names stable), SEC-SIMPL-05 (stage order); GHSA-v5c2-29pv-cc86 fix lands before the Catalog merge (plan section on advisories)

## 1. Goal and outcome
Remove four duplicate/vendor-named top-level folders from `src/Autheris.Application`: `Auditing`, `DataCatalog`, `Dbt`, `OpenMetadata`. Application top-level folder count drops by exactly 4 (49 today; 45 if run on today's tree). Pure move/rename refactoring: no behaviour change, all existing tests green, `namespace-assembly-violations.txt` does not grow, no new file in `Application` named after a vendor.

## 2. Preconditions and dependencies
- Merged first: WP-SIMPL-07 (creates `ProgramRatchetTests.Application_top_level_folders_are_allow_listed` and its allow-list), WP-SIMPL-12 (vendor catalog connector deletions; shrinks `DataCatalog` interfaces/models), the security-lane fix for GHSA-v5c2-29pv-cc86 (Data source registration; details: private advisory; merge order only, to avoid a conflicting rename).
- Blocks: WP-SIMPL-24 (folder consolidation), #459 (dbt `meta.indexes`), WP-SIMPL-28 (dbt/OpenMetadata module).
- May run in parallel with engine-lane WPs (13, 21) and persistence lane (11, 19, 20); conflicts only in DI snapshot files (resolve by regenerating).

## 3. Scope
### In scope
- `Application/Auditing` -> `Application/Audit` (namespace `Autheris.Application.Audit`).
- `Application/DataCatalog/{Interfaces,Models,Services}` -> `Application/Catalog/{Interfaces,Models,Services}` (namespace `Autheris.Application.DataCatalog*` -> `Autheris.Application.Catalog*`).
- `Application/Dbt`: Dbt-only adapter types move to `Extensions/Dbt`; contracts and semantic catalog consumed by Application/Infrastructure/GraphQL move to `Application/Catalog/Semantic` (see step 4 table).
- `Application/OpenMetadata`: `IOpenMetadataClient` moves to `Extensions/OpenMetadata`; `IOpenMetadataSyncService` and `OpenMetadataModels.cs` move to `Application/Catalog/OpenMetadata`.
- Matching test moves (`tests/Autheris.Tests.Unit/Auditing/` -> `.../Audit/`), `using` updates, DI composition snapshot regeneration, allow-list shrink.
### Out of scope
- Any logic change, renaming of types, deduplication of behaviour (no duplicate types exist, see section 4).
- #459 (`meta.indexes`), dbt/OpenMetadata module extraction (WP-SIMPL-28), other folder merges (WP-SIMPL-24).
- Moving anything from `Extensions` back into `Application`.

## 4. Current state (verified at 63cbb893)
- `src/Autheris.Application` has 49 top-level folders.
- `Application/Auditing`: 1 file `AuditGroupCommitSink.cs` (287 lines), namespace `Autheris.Application.Auditing`; 8 files reference that namespace: `Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.Catalog.cs`, `Application/Audit/AuditWriter.cs`, the file itself, `tests/Autheris.Tests.Architecture/Baselines/unconsumed-options.txt`, `tests/Autheris.Tests.Unit/Audit/AuditWriterGroupCommitTests.cs`, `tests/Autheris.Tests.Unit/Auditing/AuditGroupCommitSinkTests.cs`, `tests/Autheris.Tests.Unit/DependencyInjection/Snapshots/composition-{dev-sqlite,production-like}.txt`. `Application/Audit` has 9 files, no file/type name collision.
- `Application/DataCatalog`: 14 files (2,084 lines) in `Interfaces/`, `Models/`, `Services/`; 98 files in src/tests reference `Autheris.Application.DataCatalog`. No type name collides with types in `Application/Catalog` (42 files; subfolders `Interfaces Localization Profiling Search Services`).
- `Application/Dbt`: 25 files (2,342 lines), `Interfaces/` (10) + `Services/` (15). Not duplicates of `Extensions/Dbt` (21 files) by file or type name; consumers by project (excluding own file):
  - Api/Extensions only (move to `Extensions/Dbt`): `IDbtMetadataIngestionService`, `IDbtExposurePublisher`, `IDbtContractValidator`, `IDbtWebhookReceiver`, `InMemoryTelemetryMetricsProvider`, `DbtTestTargetMap`, `DbtSemanticManifestIngestionService`, `DbtHealthCircuitBreaker`.
  - Used by Application, Infrastructure or GraphQL (must stay in Application; `Extensions` is referenced only by Api and tests): `IDbtHealthCircuitBreaker`, `IDbtProposalRepository` (Infrastructure), `IDbtTestTargetMap`, `ISemanticManifestStore`, `ITelemetryMetricsProvider`, `ISemanticCatalogService`, `SemanticTenancy`, `SemanticManifestValidator`, `SemanticBaseTableResolver`, `SemanticMetricCatalogProjection`, `InMemorySemanticCatalog`, `PersistentSemanticCatalog`, `SemanticVirtualTables`, `SemanticOptionsValidator`, `SemanticManifestJson`, `SemanticCatalogPartition`, `SemanticCatalogServiceCollectionExtensions`.
- `Application/OpenMetadata`: 3 files (328 lines): `Interfaces/IOpenMetadataClient.cs` (Extensions only), `Interfaces/IOpenMetadataSyncService.cs` (Api, Extensions, GraphQL `Types/Mutation.Catalog.cs`), `OpenMetadataModels.cs` (12 records, used by Extensions and tests).
- `tests/Autheris.Tests.Architecture/Baselines/namespace-assembly-violations.txt` has 1 line (header only, 0 violations). `NamespaceAssemblyConsistencyTests.cs` exists in `tests/Autheris.Tests.Architecture/`. `ProgramRatchetTests` does not exist yet (WP-SIMPL-07).
- DI snapshots `composition-dev-sqlite.txt`, `composition-production-like.txt` contain fully qualified type names of moved types.

## 5. Implementation steps
Each pair is one PR; within a PR: move with `git mv`, then fix namespaces/usings, then run tests. No new behaviour, so the "red" step is the ratchet/arch test after shrinking the allow-list.
1. PR-A Auditing -> Audit. Remove `Auditing` from the WP-SIMPL-07 allow-list first (ratchet test red). `git mv src/Autheris.Application/Auditing/AuditGroupCommitSink.cs src/Autheris.Application/Audit/`; namespace -> `Autheris.Application.Audit`; drop the now-redundant `using Autheris.Application.Auditing;` in `AuditWriter.cs`; fix the Api DI file; `git mv tests/Autheris.Tests.Unit/Auditing/AuditGroupCommitSinkTests.cs tests/Autheris.Tests.Unit/Audit/` and update its namespace to the folder convention used by sibling tests in `tests/Autheris.Tests.Unit/Audit/`; update the namespace string in `unconsumed-options.txt`; regenerate both composition snapshots (use the snapshot test's documented update mechanism; diff must show only namespace changes).
2. PR-B DataCatalog -> Catalog. Remove `DataCatalog` from the allow-list. `git mv` each of `Interfaces/*`, `Models/*`, `Services/*` into `Application/Catalog/{Interfaces,Models,Services}/`. Namespaces: `Autheris.Application.DataCatalog.Interfaces` -> `Autheris.Application.Catalog.Interfaces` etc. Replace usings repo-wide (`git grep -l 'Autheris.Application.DataCatalog' | xargs sed -i 's/Autheris\.Application\.DataCatalog/Autheris.Application.Catalog/g'`), then remove duplicate `using` lines the replacement creates. Regenerate DI snapshots.
3. PR-C OpenMetadata. Remove `OpenMetadata` from allow-list. `git mv Application/OpenMetadata/Interfaces/IOpenMetadataClient.cs src/Autheris.Extensions/OpenMetadata/` (namespace `Autheris.Extensions.OpenMetadata`); `IOpenMetadataSyncService.cs` and `OpenMetadataModels.cs` -> `Application/Catalog/OpenMetadata/` (namespace `Autheris.Application.Catalog.OpenMetadata`). Fix usings in the 28 referencing files listed by `git grep -l 'Autheris.Application.OpenMetadata'`.
4. PR-D Dbt. Remove `Dbt` from allow-list. Move per section 4: the 8 Api/Extensions-only types -> `src/Autheris.Extensions/Dbt/` (namespace `Autheris.Extensions.Dbt`; keep `public` visibility where Api consumes them); the remaining 17 -> `Application/Catalog/Semantic/` (flat, namespace `Autheris.Application.Catalog.Semantic`; the four `IDbt*` contracts keep their names). Update usings in Api (12 files), Application (12), Extensions (7), GraphQL (1), Infrastructure (7), tests (57). Before moving each type, re-run the consumer check (`git grep -l -w <Type> -- src | grep -v Extensions/ | grep -v Api/`); if a type gained an Application/Infrastructure consumer since 63cbb893, it goes to `Catalog/Semantic` instead.
5. In every PR: confirm `src/Autheris.Application/<OldFolder>` no longer exists, update `DeletedFeaturesArchitectureTests` only if it lists these namespaces (it should not; they are renamed, not deleted).

## 6. Tests
- Existing, must stay green unchanged except namespace: `tests/Autheris.Tests.Unit/Audit/AuditGroupCommitSinkTests` (moved), `AuditWriterGroupCommitTests`, all tests referencing the moved namespaces, `Autheris.Extensions.Tests` OpenMetadata/Dbt tests (test method names unchanged, SEC-SIMPL-03).
- `Autheris.Tests.Architecture`: `ProgramRatchetTests.Application_top_level_folders_are_allow_listed` (allow-list shrinks by one per PR), `NamespaceAssemblyConsistencyTests` (baseline stays header-only), project-edge test (`Baselines/project-edges.txt` must not gain an edge).
- New: `ArchitectureTests.Application_has_no_vendor_named_namespaces` in `tests/Autheris.Tests.Architecture/ArchitectureTests.cs`, asserting no type in assembly `Autheris.Application` has a namespace starting with `Autheris.Application.Auditing`, `.DataCatalog`, `.Dbt` or `.OpenMetadata`. Add in PR-A with the four prefixes; it passes fully after PR-D (add each prefix in the PR that removes it).
- DI composition snapshot tests in `tests/Autheris.Tests.Unit/DependencyInjection/` (snapshots regenerated, namespace-only diff).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (one build at a time).
- `dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~Audit|FullyQualifiedName~Catalog|FullyQualifiedName~Dbt|FullyQualifiedName~OpenMetadata|FullyQualifiedName~DependencyInjection"`, then the full Unit project once per PR.
- `dotnet test tests/Autheris.Extensions.Tests/Autheris.Extensions.Tests.csproj -c Release --no-build`
- `git grep -n -E 'Autheris\.Application\.(Auditing|DataCatalog|Dbt|OpenMetadata)\b' -- src tests` returns nothing after PR-D.
- `ls -d src/Autheris.Application/*/ | wc -l` = previous count - 4.

## 8. Acceptance criteria
- [ ] Folders `Application/Auditing`, `DataCatalog`, `Dbt`, `OpenMetadata` absent.
- [ ] grep in section 7 returns no hit; no type lost (`git diff --stat -M` shows renames, net src LOC change within +-50 per PR, excluding usings).
- [ ] `namespace-assembly-violations.txt` unchanged (header only); `project-edges.txt` unchanged.
- [ ] Allow-list contains 4 fewer entries; ratchet test green.
- [ ] All Unit, Architecture and Extensions tests green; DI snapshots differ only in namespaces.

## 9. PR slicing
Four PRs in order A (Auditing), B (DataCatalog), C (OpenMetadata), D (Dbt). All are moves; net added src lines far below 400. B and D touch more than 50 files each; label `refactor-move`, no `size-exception` needed.

## 10. Risks, rollback, and things the developer must NOT do
- Risk: merge conflicts with parallel lanes on usings; keep each PR short-lived, rebase before merge. Rollback: revert the single PR.
- Do NOT change type names, visibility (except `internal` -> `public` only if a moved type becomes unreachable, which should not happen), logic, or log/metric names.
- Do NOT move Application-consumed contracts into `Extensions` (would require Application -> Extensions reference; forbidden by `project-edges.txt`).
- Do NOT add entries to `namespace-assembly-violations.txt` or any baseline other than shrinking the allow-list.
- Do NOT hand-edit DI snapshots beyond the namespace rename; regenerate them.

## 11. Estimate
Src: ~0 net (moves; usings +-200 lines touched). Tests: ~0 net. The plan's -500 to -1,000 does not materialise: no duplicate types exist. 2 person-days.

## Decisions taken in this spec
1. Application/Dbt is not a duplicate of Extensions/Dbt; types consumed by Application/Infrastructure/GraphQL stay in Application under `Catalog/Semantic` (not a new top-level folder), the rest moves to `Extensions/Dbt`.
2. OpenMetadata contracts used by GraphQL stay in Application under `Catalog/OpenMetadata`; only `IOpenMetadataClient` moves to Extensions. WP-SIMPL-28 relocates them into the module later.
3. LOC estimate corrected to ~0 net (plan said -500 to -1,000).
4. Allow-list file name is whatever WP-SIMPL-07 creates; if WP-SIMPL-07 is not merged, this WP waits (no ad-hoc ratchet).
