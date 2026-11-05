# WP-SIMPL-41 Authorized data discovery (replaces OpenSchema)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#4-work-packages) | Related: [WP-SIMPL-40](WP-SIMPL-40-docs-ingestion-clean.md) (document storage/upload), [WP-SIMPL-39](WP-SIMPL-39-procedures-clean.md) | Issues: #500 (untrusted catalog text, alignment) | Owner decisions: OD-19 (binding, 2026-11-05, with the owner clarifications of the same day recorded in section 12), OD-22 (core access model), OD-4 (catalog search incl. semantic search is core), OD-18 (procedures and docs ingestion stay in core) | Security conditions: SEC-SIMPL-99..SEC-SIMPL-114 (proposed in section 13, to be confirmed by Phase 3)

## 1. Goal and outcome
Every authenticated user of a tenant, and every AI agent or MCP client acting for such a user, can find out which data objects (tables, views, governed stored procedures) exist in the tenant, what they contain (descriptions, columns with descriptions, PII/sensitivity flags, uploaded statistics and pseudonymised samples), who the data owner is, what the caller's own access state is, and can request access (table consent plus optional clear access per PII column) from that owner. Only objects and columns that a data owner or administrator explicitly hid are omitted, without an existence oracle. Discovery never returns row data from a source system. The anonymous `OpenSchema` flag is deleted, and every route that branched on it requires authentication.

Measurable outcome: `git grep -n "OpenSchema\|IsOpenSchemaAllowed" -- src` returns 0 hits. GraphQL `catalog`, REST `/api/v1/catalog/datasets*`, MCP `list_datasets` / `describe_dataset` / `search_catalog` / `find_relevant_tables` and catalog search (BM25 and semantic) return the same object set and the same per-object fields for the same caller (pinned by a channel-parity test). All security tests in section 6 pass.

## 2. Preconditions and dependencies
- Merge after `fix/security-batch-5` (WP-SIMPL-36 first bundle), because it touches `CatalogApiEndpoints.cs` and `Query.Catalog.cs`.
- PR 4 (procedures in discovery) depends on WP-SIMPL-39 PR 4 (`IProcedureCatalog`, `RegisteredProcedure.GovernedTables()`). If WP-SIMPL-39 is not merged yet, PR 4 of this WP waits. PRs 1-3 do not.
- Coordinate with WP-SIMPL-06 and WP-SIMPL-32: they list `OpenSchema` in `tests/Autheris.Tests.Architecture/Baselines/unconsumed-options.txt` and in the configuration reference. This WP deletes the option. Whichever lands second removes the leftover line.
- WP-SIMPL-16 deletes the dev portal. If `src/Autheris.Api/Endpoints/DevPortalAssets.cs` still exists when PR 1 starts, PR 1 removes its OpenSchema badge (lines 14 and 160).
- Migration steps go to `Steps/postgresql` and `Steps/sqlserver`. Add a `Steps/sqlite` step only while WP-SIMPL-11 is not merged.
- PR 6 (linked documents) depends on WP-SIMPL-40 PR 40c (governance-database document store, `DocAccessPolicy`, sanitiser and shared rune rules) and on PR 3 of this WP. Its migration number is the next free number after WP-SIMPL-40's step.
- PRs 1-5 can run in parallel with WP-SIMPL-37/38/40.

## 3. Scope
### In scope
- Delete `GatewayOptions.OpenSchema`, `Catalog.OpenSchema`, `IsOpenSchemaAllowed`, `IsOpenSchemaExplicitlyEnabled` and the `DANGER:open_schema` entry, plus the Quickstart expansion entry. Add a startup failure for the legacy keys.
- Remove the OpenSchema branches in the GraphQL catalog, OData (`$openapi`, Swagger UI, `$metadata`, service document), WebSQL OpenAPI, SQL endpoint OpenAPI and procedure OpenAPI routes.
- A new discovery core in `src/Autheris.Application/Catalog/Discovery/`: tenant object set, explicit-hidden filtering, per-caller access state per table and per column, data owners, uploaded statistics and samples.
- An explicit discovery-hidden flag at table level and at column level, plus a per-table samples-disabled flag: persistence, an admin/owner API and audit.
- An upload contract for pseudonymised samples and statistics, provided by an external process: who may upload, validation, persistence in the governance store, and serving rules.
- Delete the in-memory sample registry, the synthetic row generator and the profile computation.
- Request access from discovery: table consent request with justification, plus an optional per-column clear-access request, routed to the table's data owners.
- Consistent behaviour across GraphQL catalog, REST catalog, MCP tools and catalog search (BM25 and semantic, including index and embedding content).
- Audit of discovery reads, rate limits, and an optional role/scope restriction (default off).
- Linked documents (step 13):
  - A linkage model from document or application to table, view, procedure or column.
  - Exposure of linked document context in discovery, catalog search (BM25 and semantic) and MCP (`describe_dataset`, `list_datasets`, `search_docs`, a new `read_document`).
  - An explicit document hidden flag.
  - Untrusted-text handling per #500 for every discovery text field returned to MCP.
### Out of scope
- Document storage, upload, sanitising, tenant isolation and quotas. WP-SIMPL-40 owns these, and this WP does not re-specify them.
- Data-access enforcement (row filters, masking, column deny). It is unchanged and stays as defined by OD-22. Discovery only reports state.
- The approval workflow itself (`ConsentRequestService` approval, `ConsentDecisionGuard`). It is unchanged except that it carries and applies the requested columns (step 9).
- Computing statistics or samples from source data in any form (owner: always external).
- Populating the table/view discriminator from source systems other than dbt `materialized` (see decision D6).
- Feature docs (no docs phase). Only the operator migration note in `docs/configuration-guide.md` and in the release notes is written.

## 4. Current state (verified on `db45aa80`, branch docs/simplification-plans)
- `src/Autheris.Domain/Options/GatewayOptions.cs:85` `public bool OpenSchema`. `:100` `IsOpenSchemaAllowed => OpenSchema || Catalog.OpenSchema`. `:130` `IsOpenSchemaExplicitlyEnabled` (same expression). `:211` `"DANGER:open_schema (OpenSchema / Catalog.OpenSchema)"`. `src/Autheris.Domain/Model/OpenApiModels.cs:16` `public bool OpenSchema` (the `Catalog` options).
- DANGER entries are blocked outside Development by `StartupChecks/ProductionStartupChecks.cs:73`. `src/Autheris.Api/Configuration/DevConfiguration.cs:168-174` `QuickstartExpansion` contains `"OpenSchema"`. `src/Autheris.Api/Configuration/LegacySwitchGuard.cs:47` `Removed` and `:64` `ThrowIfLegacyKeysSet` are the existing fail-startup mechanism for retired keys.
- OpenSchema branches (11 places in 9 files):
  - `src/Autheris.GraphQL/Types/Query.Catalog.cs:130,156-180`: `GetAllTablesAsync`, then `FilterByContract`, then all tables with all columns, `AccessStatus` computed with `isAdmin: true`. No tenant, consent, hidden or ReBAC filter.
  - `src/Autheris.Api/Endpoints/ODataEndpoints.cs:69-107`: `IsOpenApiAuthorized`, `ConfigureOpenApiAuth` with `AllowAnonymous`.
  - `src/Autheris.Api/Endpoints/ODataEndpoints.Metadata.cs:14-15,20,36-37`.
  - `src/Autheris.Api/Endpoints/WebSqlEndpoints.cs:36-47`.
  - `src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs:47-59`.
  - `src/Autheris.Api/Endpoints/ProcedureEndpointRoutes.cs:44-61`.
  - `DevPortalAssets.cs:14,160`.
  - Comments at `McpEndpoints.cs:135`, `GatewayApplicationBuilderExtensions.cs:173` and `GatewayServiceCollectionExtensions.GraphQL.cs:238`.
  - `src/Autheris.Domain/Options/DevOptions.cs:17` (doc comment).
- Tests referencing OpenSchema: 22 files, 31 lines in `Tests.Unit`. The files are `HostingAuthAndMiddlewareSecurityTests.cs` (9), `IntegrationGapBTests.cs` (6), `DataPathSecurityTests.cs` (4), `GraphQL/CatalogTierShapingTests.cs` (3), `DevSwitchTests.cs` (2), `ProcedureEndpointTests.cs` (2), `Security/SwaggerChallengeApi15Tests.cs` (2), `BypassSemanticsAndDmlGuardrailTests.cs`, `DevPresetAndGovernanceFailClosedSecurityTests.cs` and `Procedures/ProcedureExecutionSecurityTests.cs` (1 each); Integration `McpIntegrationTests.cs` (3), `OpenApiDocsEndToEndTests.cs` (2), `OpenApiIntegrationTests.cs` (2), `CdcWebhookEndToEndTests.cs`, `EndToEndSqliteTests.cs`, `GovernedEntryPointsEndToEndTests.cs`, `Issue341GraphQlInternalErrorTests.cs`, `ReadPathSwitchIntegrationTests.cs` and `Transports/TransportEnforcementTests.cs` (1 each); `Tests.Parity/Infrastructure/ParityHostSettings.cs`, `Sim.ApiHost/SimApiHostMain.cs` and `Tests.Architecture/Baselines/unconsumed-options.txt`.
- Today's discovery visibility is consent-based. `CatalogVisibility.FilterForSubject` (`src/Autheris.Application/Services/CatalogVisibility.cs:70`) shows a table only with an active Allow consent, or through the Issue #111 tier switch (`:85-135`): on `CatalogSurface.Discovery`, `DatasetVisibility.Summary` gives no columns and `Full` gives columns without Deny; `Hidden` drops. `DatasetVisibility` (`TableModels.cs:32`) has `Hidden = 0` as default, and `Table.Visibility` defaults to `Hidden` (`:160`). The persisted tier is in `TABLE_CATALOG_SETTINGS.visibility` (`DatasetCatalogStore.cs:20,228,492`); a missing row means `Hidden` (`:98`). So today "default hidden" applies, the opposite of OD-19.
- `CatalogVisibilityService` (`src/Autheris.Application/Catalog/Services/CatalogVisibilityService.cs:27-32`) is the single discovery visibility entry point (Issue #138). It resolves caller scope, runs the engine, shapes the output and applies a ReBAC `can_query` shrink, then memoizes. Its consumers are `CatalogDiscoveryService`, `McpDatasetCatalog`, `AiDataGuardrailService`, `McpCatalogVisibility` and `CatalogApiEndpoints.cs:808`. An anonymous MCP client SID `ANONYMOUS_MCP_CLIENT` answers empty (`:38,257`).
- `CatalogVisibility.ComputeAccessStatus` (`:24-60`) returns `GRANTED` / `PENDING` / `NONE`. There is no expired state and no row-scope state.
- Tenancy: `TableMetadata.OwnerTenantId` (`TableModels.cs:382`), `TenantCatalogLookup.IsTableVisibleToTenant` (`src/Autheris.Application/Catalog/TenantCatalogLookup.cs:159-179`), `CatalogVisibility.FilterToTenant` (`:236`, ownership derived from consents or `Gateway:Catalog:SharedTables`, `DataCatalogOptions.cs:76`) and `DropForeignVirtualTables` (`:219`).
- Columns: `TableColumn` (`TableModels.cs:213-233`) has `IsSensitive`, `Description` and `LongDescription`, with no hidden flag. PII heuristic: `internal static class CatalogPiiDetector.IsPii(string, bool)` (`CatalogDiscoveryService.cs:833-837`). Column access levels: `ColumnAccessLevel { Deny, Mask, Clear }` (`src/Autheris.Domain/Interfaces/ColumnAccessLevel.cs`), resolved by `ConsentResolutionService.ResolveConsentColumnLevel` (internal, `:416`).
- Owners: `IDataOwnershipRepository.GetDataOwnersForTableAsync` and `IsAuthorizedApproverForTableAsync` (`src/Autheris.Application/Interfaces/IGovernanceRepository.cs:343-348`). `DataOwner` (`GovernanceModels.cs:10-17`) has `AdSid`, `AdAccount`, `DisplayName`, `Email` and `IsActive`.
- Consent requests: `ConsentRequestSubmission` (`IConsentRequestService.cs:21-31`) has table, justification, duration and context, with no columns. `ConsentRequest` (`GovernanceModels.cs:33-49`) has no requested columns. It is persisted in `CONSENT_REQUESTS` (`ConsentSqlCore.cs:45`). GraphQL `Mutation.RequestTableAccessAsync` is at `Mutation.Consent.cs:327`, and the bundled REST request is `POST /api/v1/governance/consents/bundle` (`QueryAnalysisAndProfilingEndpoints.cs:67`).
- Samples and profiles (Issue #80):
  - `SyntheticSampleRowGenerator` (`Application/Catalog/Profiling/SyntheticSampleRowGenerator.cs`, 455 lines) keeps uploaded samples in an in-memory `ConcurrentDictionary` (`:22`), with `MaxPredefinedSampleRows = 100` (`:19`), and generates rows from profiles (`GenerateRows`, `:35`).
  - `ColumnValueProfileService` (353 + 300 lines) computes profiles from values passed in (`ComputeAndStoreProfileAsync(table, column, IEnumerable<object?> values)`, `:43`) and from dbt meta. Snapshots are in memory.
  - Upload: `PUT /api/v1/catalog/datasets/{id}/synthetic-samples` and the legacy `/api/catalog/...` (`CatalogApiEndpoints.cs:284-290`, GovernanceAdmin, handler registers at `:785`). GET at `:292-298`.
  - dbt meta samples: `DbtArtifactStreamingParser.cs:304,377,647-660` and `TableMetadata.PredefinedSyntheticSamples` (`TableModels.cs:416`).
  - `GET /api/v1/catalog/datasets/{dataset}/sample-rows` (`QueryAnalysisAndProfilingEndpoints.cs:76-79`, audit `METADATA_SYNTHETIC_SAMPLE`) calls `McpDatasetCatalog.SampleRowsAsync` (`:959`). With `synthetic=false` it runs a real governed query (`ExecuteTableQueryAsync`, `:1019`); with `synthetic=true` it generates rows (`:970-1016`) and requires consent or tier Full plus `ReleaseProfileToCatalogViewers` (`:979-984`). `DefaultSampleRows = 5`, `MaxSampleRows = 20` (`McpDatasetCatalog.cs:31-32`).
  - `ComputeDatasetProfileHttpRequest` (`QueryAnalysisAndProfilingEndpoints.cs:37`) has no mapped route.
  - `TableMetadata.EstimatedRowCount` (`TableModels.cs:419`) exists.
- Audit: `CATALOG_READ` (`AuditEventTypes.cs:22`), `CATALOG_VISIBILITY_CHANGED` (`:72`, `FailClosed` in `AuditPolicyTable.cs:63`), `CONSENT_REQUESTED` (`:84`), `METADATA_SYNTHETIC_SAMPLE` (`:132`). The visibility/tags admin decision (admin class, owner or delegate) is in `DatasetCatalogAdminService.ApplyAsync` (`:103`, owner check `:369`).
- Rate limiting: `IRateLimiterService` (`src/Autheris.Application/Interfaces/IRateLimiterService.cs:12`), used per caller in `TableStreamEndpoints.cs:232-328`; error code `ApiErrorCodes.RateLimitExceeded` (`:118`). There is no ASP.NET `AddRateLimiter`.
- Migrations: latest step `0005_catalog_min_group_size.sql` in `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{postgresql,sqlserver,sqlite}/`, each with a `.shape.json`.

## 5. Implementation steps (TDD order; red test first in each step)
1. **Delete OpenSchema (PR 1).** Tests: `OpenSchemaRemovalTests` (red).
   - Delete `GatewayOptions.OpenSchema` (`:85`), `IsOpenSchemaAllowed` (`:100`), `IsOpenSchemaExplicitlyEnabled` (`:130`) and the `:211` entry; delete `OpenApiModels.cs:16`.
   - Add `"OpenSchema"` and `"Catalog:OpenSchema"` (relative to `Gateway:`, same convention as the existing entries) to `LegacySwitchGuard.Removed`. The message tells operators: "OpenSchema was removed (OD-19). Authenticated users discover the catalog by default; anonymous catalog access is not supported. Remove the key."
   - Remove `"OpenSchema"` from `DevConfiguration.QuickstartExpansion`.
   - Route changes:
     - OData: `IsOpenApiAuthorized` keeps only authenticated plus `IsOpenApiAdmin` (G3 stays: `$openapi` lists all tenants). `ConfigureOpenApiAuth` always calls `RequireAuthorization()`. In `ODataEndpoints.Metadata.cs` the predicates become `!env.IsDevelopment() && !authenticated`, and `CheckSwaggerAuth` drops the flag.
     - WebSQL, SQL endpoints and procedures: `openapi.json` always `.RequireAuthorization()`.
   - `Query.Catalog.cs`: delete the `isOpenSchema` variable and branch (it is fully replaced in step 6).
   - Update the comments listed in section 4 and remove the `DevPortalAssets` badge if the file still exists.
   - Rewrite the OpenSchema-based tests: assertions that expected anonymous 200 now expect 401/challenge; tests that only set the flag drop it.
2. **Domain model (PR 2).** Create `src/Autheris.Domain/Model/DiscoveryModels.cs`:
   ```csharp
   public enum DiscoveryObjectKind { Table, View, Procedure }
   public enum DiscoveryAccessState { None, Requested, Granted, Expired }
   public enum DiscoveryRowScope { Full, RowRestricted }            // only meaningful when Granted
   public enum DiscoveryColumnState { NoAccess, Masked, Clear }
   public sealed record DiscoveryOwner(string DisplayName, string? Email);   // never Sid/AdAccount
   public sealed record DiscoveryColumn(string Name, string? DisplayName, string DataType, string? Description,
       bool Pii, bool ClearAccessRequiresSeparateApproval, DiscoveryColumnState State, DiscoveryColumnStatistics? Statistics);
   public sealed record DiscoveryObject(DiscoveryObjectKind Kind, string Domain, string Schema, string Name, string? DisplayName,
       string? Description, string Sensitivity, IReadOnlyList<string> Tags, IReadOnlyList<DiscoveryOwner> Owners,
       DiscoveryAccessState Access, DiscoveryRowScope? RowScope, IReadOnlyList<DiscoveryColumn> Columns,
       DiscoveryTableStatistics? Statistics, bool SamplesAvailable, bool IsHidden /* true only in the admin/owner view */);
   public sealed record DiscoveryTableStatistics(long? ApproxRowCount, long? SizeBytes, DateTimeOffset? LastUpdatedAt,
       IReadOnlyList<string>? PartitionColumns, DateTimeOffset UploadedAt);
   public sealed record DiscoveryColumnStatistics(bool? Nullable, double? NullRatio, long? ApproxDistinctCount,
       int? MinLength, int? MaxLength, string? MinValue, string? MaxValue, IReadOnlyList<string>? TopValues);
   ```
   Add to `TableColumn` `public bool DiscoveryHidden { get; init; }`. Add to `Table` `public bool DiscoveryHidden { get; init; }`, `public bool SamplesDisabled { get; init; }` and `public DiscoveryObjectKind ObjectKind { get; init; } = DiscoveryObjectKind.Table;`.
3. **Persistence (PR 2).** Add migration `0006_catalog_discovery.sql` and `.shape.json` per dialect (postgresql, sqlserver, plus sqlite while WP-SIMPL-11 is open). The step is additive and idempotent like 0005:
   - `TABLE_CATALOG_SETTINGS`: add `discovery_hidden BOOLEAN/BIT NOT NULL DEFAULT FALSE/0` and `samples_disabled ... NOT NULL DEFAULT FALSE/0`.
   - New `COLUMN_CATALOG_SETTINGS (table_id, column_name VARCHAR(256), discovery_hidden NOT NULL DEFAULT FALSE, updated_by, updated_at, PRIMARY KEY (table_id, column_name))`.
   - New `CATALOG_DISCOVERY_ARTIFACTS (table_id PRIMARY KEY, tenant_id, samples_json NULL, statistics_json NULL, content_sha256 CHAR(64), uploaded_by, uploaded_at, schema_version INT NOT NULL)`.
   - `CONSENT_REQUESTS`: add `requested_columns_json NULL`.

   Extend `DatasetCatalogStore` to read and write the flags. The overlay sets `Table.DiscoveryHidden` and `SamplesDisabled` and each `TableColumn.DiscoveryHidden`. If reading the flags fails, the overlay is marked `Degraded` (existing pattern `RowOverlay.Secure`, `:31`); a degraded overlay treats every table as hidden (fail-closed) and increments `CatalogVisibilityDegradedCounter`. Add `IDiscoveryArtifactStore` (Application) and `DiscoveryArtifactStore` (Infrastructure, same dialect helpers as `DatasetCatalogStore`) with `GetAsync(TableId)`, `UpsertAsync(DiscoveryArtifacts, actor)` and `DeleteAsync(TableId, actor)`. The `TABLE_CATALOG_SETTINGS.visibility` column is no longer read (it stays in the schema; a later forward-only step may drop it).
4. **Flag API and audit (PR 2).** Extend `DatasetCatalogEdit` and `DatasetCatalogAdminService.ApplyAsync` with `DiscoveryHidden?`, `HiddenColumns?` (full replacement set) and `SamplesDisabled?`. Authorization is the existing rule (`:369`): GovernanceAdmin of the tenant, or the table's data owner or delegate (`IsAuthorizedApproverForTableAsync`); everyone else gets 404 for out-of-tenant tables (no oracle) and 403 otherwise.
   - New route `PUT /api/v1/catalog/datasets/{id}/discovery` (`RequireAuthorization()`, `.WithAudit(AuditLevel.Full, AuditEventTypes.CatalogVisibilityChanged)`). Body: `{ hidden: bool?, hiddenColumns: string[]?, samplesDisabled: bool? }`. Unknown column names give 400 `INVALID_COLUMNS`.
   - The service writes one `CATALOG_VISIBILITY_CHANGED` record per change with before and after values (table flag, added and removed hidden columns, samples flag), actor and source. The record is FailClosed: when audit fails, the change is rolled back (`AuditPolicyTable` already maps the event to FailClosed).
   - Delete `PUT /datasets/{id}/visibility` (`CatalogApiEndpoints.cs:352`) and its handler (decision D3).
   - The dbt ingestion may set `DiscoveryHidden` from meta `autheris_discovery_hidden: true` (table or column). This is a one-way ratchet: ingestion never un-hides what an owner hid.
5. **Discovery core (PR 3).** Create the following in `src/Autheris.Application/Catalog/Discovery/`:
   - `IDiscoveryService`:
     - `ValueTask<DiscoveryPage> ListAsync(DiscoveryQuery q, CatalogCaller caller, CancellationToken ct)`
     - `ValueTask<DiscoveryObject?> GetAsync(TableIdentifier id, CatalogCaller caller, CancellationToken ct)`
     - `ValueTask<IReadOnlyList<DiscoveryObject>> SearchAsync(string text, DiscoverySearchOptions o, CatalogCaller caller, CancellationToken ct)`
   - `DiscoveryTenantScope` (static, pure). `IsInScope(table, tenant, catalogOwner, sharedTables, tenantAllowConsentTables)` is true when:
     - `OwnerTenantId == tenant`;
     - or `OwnerTenantId` is empty and (the caller's tenant is the catalog owner, `CallerScopeResolver.IsOwner`, or the table is listed in `SharedTables`);
     - or the tenant holds an active Allow consent on the table.

     Always apply `DropForeignVirtualTables` first. Hidden tables are removed after scoping, except in the admin/owner view (`IsHidden = true`), which is shown only to GovernanceAdmin of the tenant and to the table's data owners or delegates.
   - `DiscoveryAccessResolver` (static, pure). Inputs: table, the caller's tenant-scoped active consents, pending requests, recently expired consents (up to 90 days) and caller scopes. Output: `(Access, RowScope, per-column State)`.
     - `Granted`: an active Allow and no unconditional Deny (same predicate as `ComputeAccessStatus`, `:35-48`).
     - `RowRestricted`: any applicable Allow or Deny consent of the caller on the table has `RowFilters.Count > 0`. The predicate, its values and other subjects' filters are never returned.
     - Column state:
       - Deny for the caller gives `NoAccess`.
       - Otherwise, with `Pii = column.IsSensitive || CatalogPiiDetector.IsPii(column.ColumnName, column.IsSensitive)`, the state is `Clear` only when a consent resolves `Clear` for that column, else `Masked` for PII columns. Non-PII columns resolve `Clear` or `Masked` per consent.
       - Without a grant every column is `NoAccess`.

       The resolver must call the same column resolution as the data path (`ConsentResolutionService.ResolveConsentColumnLevel`, made `internal` visible to the Discovery namespace in the same assembly, so no new copy). `ClearAccessRequiresSeparateApproval = Pii`.
     - `Requested`: no grant and a pending request (statuses as in `:50-56`). `Expired`: no grant, no pending request and a consent of the caller on the table expired within 90 days. Otherwise `None`.

     If `IConsentRepository` has no query for expired consents, add `GetRecentlyExpiredTableIdsAsync(Sid, TenantId, DateTimeOffset since, CancellationToken)` to the interface, the SQL Server and PostgreSQL implementations and the contract suite of WP-SIMPL-19.
   - `DiscoveryService`:
     1. Load the tenant tables with `ITableMetadataRepository.GetAllTablesAsync` (repository overlay includes the flags), then scope, then drop hidden objects.
     2. Drop hidden columns from `Columns`, statistics and samples.
     3. Load owners in a batch (`GetDataOwnersForTableAsync`; `IsActive` only; map to `DiscoveryOwner(DisplayName, Email)`).
     4. Resolve access with the caller's consents loaded once per request.
     5. Load artifacts with `IDiscoveryArtifactStore.GetAsync` (statistics inline; `SamplesAvailable = samples present && !SamplesDisabled`).

     Rules:
     - Paging: `first <= 200`, cursor-based, same as `FilterAndPaginateCatalog`.
     - Any exception in steps 1-4 answers empty or null (fail-closed, as in `CatalogVisibilityService`), logs and increments a `discovery_failures_total` counter. Never fall back to an unfiltered list.
     - Memoize per DI scope like `CatalogVisibilityService` (`MemoKey` includes sid, claims, tenant and surface).
   - Caller gate (`DiscoveryCallerGate`): the caller must be authenticated with a non-null user SID that is not `ANONYMOUS_MCP_CLIENT`, and must have a resolved tenant. Optional restriction: new `CatalogDiscoveryOptions` under `Gateway:Catalog:Discovery`, consisting of `RequiredRoles` (string[], default empty = off), `RequiredScopes` (default empty), `TenantOverrides` (dictionary tenant -> {RequiredRoles, RequiredScopes}), `RequestsPerMinute` (default 120), `AccessRequestsPerHour` (default 30), `MaxSampleRows` (default 20, hard cap 50) and `MaxArtifactBytes` (default 262144, hard cap 1048576). Validate the options with `IValidateOptions` (caps enforced, otherwise startup fails). A caller who fails the gate gets 403 `DISCOVERY_NOT_PERMITTED` on REST, the GraphQL error code `FORBIDDEN`, and an MCP tool error with the same code. Unauthenticated callers get 401/challenge.
6. **Channels (PR 3).**
   - GraphQL: `Query.GetCatalogAsync` (`Query.Catalog.cs:110`) delegates to `IDiscoveryService.ListAsync`. Extend `TableMetadataDto` with `kind`, `owners`, `access`, `rowScope`, `columns` (as `DiscoveryColumn`: replaces the column-name list; keep `columnNames` as a deprecated alias for one release), `statistics` and `samplesAvailable`. Add `catalogObject(domain, schema, name)` (null when not discoverable) and `catalogSamples(domain, schema, name)` (step 8).
   - REST: `GET /api/v1/catalog/datasets`, `/datasets/{*datasetId}` and `/search` call `IDiscoveryService` through `CatalogDiscoveryService.ListDatasetsAsync`, `GetDatasetDetailAsync` and `SearchCatalogAsync` / `SearchCatalogDetailedAsync` (signatures stay; the internals switch to `IDiscoveryService`). The legacy `/api/catalog` group keeps the same handlers.
   - MCP: `McpDatasetCatalog.ListDatasetsAsync` (`:188`), `DescribeDatasetAsync` (`:395`), `search_catalog` and `find_relevant_tables` use `IDiscoveryService`. `describe_dataset` returns the same columns, states, owners and statistics. Add a tool `request_access(dataset, justification, durationDays, clearColumns?)` (step 9), listed only when MCP write tools are allowed for the client (existing read-only token rule) and otherwise answering with the unknown-tool error.
   - Search: the `CatalogSearchEngine` index document and the semantic embedding text are built without hidden columns and without hidden tables. `CatalogSearchWarmupService` rebuilds (or invalidates the affected documents) on `CATALOG_VISIBILITY_CHANGED`. Search results are always post-filtered through `IDiscoveryService` (tenant scope plus hidden), and the hit count, facets, suggestions and "did you mean" are computed after filtering.
   - Error semantics: an unknown, out-of-tenant or hidden object gives exactly the same response on every channel: REST 404 `NOT_FOUND` "Requested dataset was not found.", GraphQL `null`, MCP the existing "unknown dataset" error. Status, body and headers are identical, and the same code path is used (no early return before the repository call).
7. **Remove sample generation and profile computation (PR 4).**
   - Delete `SyntheticSampleRowGenerator.cs`, `ColumnValueProfileService.cs`, `ColumnValueProfileService.Helpers.cs`, `IColumnValueProfileService.cs` (including `ISyntheticSampleRowGenerator`, `ColumnProfileOptions`, `ColumnProfileSnapshot`), `ComputeDatasetProfileHttpRequest`, and their DI registration in `GatewayCatalogServiceExtensions.cs`.
   - Delete `TableMetadata.ReleaseProfileToCatalogViewers` and every profile usage in `McpDatasetCatalog` (`:420,981`). Remove the `synthetic` parameter from `SampleRowsAsync` and `HandleSampleRowsAsync`.
   - `GET /api/v1/catalog/datasets/{dataset}/sample-rows` without the synthetic flag stays a governed data read (real query, full data-path enforcement, audit `TABLE_QUERY` semantics unchanged). It is data access, not discovery (decision D7). Its route name `GetDatasetSampleRowsWithSyntheticSupport` becomes `GetDatasetSampleRows`.
   - dbt: `DbtArtifactStreamingParser.ExtractSyntheticSamples` stays as an upload source. `DbtMetadataIngestionService` (`:690-707`) writes the samples through `IDiscoveryArtifactStore.UpsertAsync` (actor `dbt-ingestion`, same validation as step 8) instead of `TableMetadata.PredefinedSyntheticSamples`, which is deleted.
8. **Artifact upload and serving (PR 4).** Replace `PUT/GET .../synthetic-samples` with the following routes:
   - `PUT /api/v1/catalog/datasets/{id}/discovery-artifacts`, body `{ schemaVersion: 1, samples?: [ {col: value} ], statistics?: { table: {...}, columns: { name: {...} } } }`, maps to `DiscoveryTableStatistics` and `DiscoveryColumnStatistics` without `UploadedAt`.
   - `DELETE` on the same path.
   - `GET /api/v1/catalog/datasets/{id}/samples`, the discovery read for any gated caller.

   Remove the legacy `/api/catalog/datasets/{id}/synthetic-samples` routes.

   **Who may upload:** GovernanceAdmin of the tenant, the table's data owner or delegate, or a principal with the new tenant-scoped role `CatalogArtifactUploader` (new policy `GatewayPolicies.CatalogArtifactUploader`, role evaluated by `GatewayRoleEvaluator` within the caller's tenant only). An out-of-tenant or hidden table gives 404; any other unauthorized caller gets 403.

   **Validation** (any violation gives 400 `INVALID_ARTIFACT` with the first violation code, and nothing is stored):
   - Body size `<= MaxArtifactBytes` (checked on the raw request length before deserialization; `Kestrel`/endpoint `RequestSizeLimit` set to the same value).
   - `samples.Count <= MaxSampleRows`.
   - Every sample key and statistics column name must be an existing catalog column of the table (case-insensitive). Unknown names are rejected, and so are columns with `DiscoveryHidden = true` (`HIDDEN_COLUMN`, which reveals nothing new to an authorized uploader).
   - Scalar values only (string `<= 1024` chars, number, bool, null).
   - `topValues.Count <= 20`.
   - `0 <= nullRatio <= 1`, and counts must be `>= 0`.

   **Persistence:** the store keeps the canonical JSON and `content_sha256`. Audit uses the new `CATALOG_DISCOVERY_ARTIFACTS_CHANGED` (Tier A, `FailClosed`, before/after hashes, row and column counts, actor and source; never values).

   **Serving:**
   - Samples and statistics are served as uploaded (the uploader is responsible for pseudonymisation; OD-19 owner decision). Autheris still drops, at read time, every column that is hidden now (the flag may change after upload), returns at most `MaxSampleRows` rows, and returns nothing when the table is hidden or `SamplesDisabled`.
   - Samples are never filtered or reshaped by the caller's grants and are never mixed with source rows.
   - Without an artifact the response is `{ "samples": [], "available": false }` (REST), `samplesAvailable: false` (GraphQL/MCP), and `statistics: null`.
   - Sample reads are audited with `METADATA_SYNTHETIC_SAMPLE` (renamed constant `MetadataSampleRead`, same string value kept for audit continuity).
9. **Request access (PR 5).**
   - Extend `ConsentRequestSubmission` with `IReadOnlyList<string>? ClearColumns = null` and `ConsentRequest` with `List<string> RequestedClearColumns` (persisted as `requested_columns_json`).
   - `ConsentRequestService.SubmitAsync`:
     1. Resolves the table through `IDiscoveryService.GetAsync`; when it is not discoverable, it throws `ConsentRequestException("NOT_FOUND")`, mapped to the same 404.
     2. Validates that each column exists, is not hidden and is `Pii` (a clear request only makes sense for masked PII columns; others give 400 `INVALID_COLUMNS`, the same code for unknown and hidden).
     3. Applies the existing justification (5-2000) and duration (1-365) rules.
     4. Routes the request to the table's data owners (existing approval chain).
     5. Writes `CONSENT_REQUESTED` with the column names.
     6. Rejects a duplicate pending request for the same table and column set as idempotent (returns the existing id).
   - On approval, `ConsentDecisionGuard` and the approval path create the table consent plus column rules `Clear` only for the approved columns (the approver may approve a subset). Without approved clear columns, PII columns stay masked by default.
   - Channels: GraphQL `requestTableAccess` gets `clearColumns: [String!]`, REST `POST /api/v1/catalog/datasets/{id}/access-requests` `{ justification, durationDays, clearColumns? }` (`RequireAuthorization`, audit `CONSENT_REQUESTED`), and the MCP `request_access` tool. All three call `IConsentRequestService.SubmitAsync`.
10. **Audit and rate limits (PR 3, PR 5).**
    - Every discovery list, detail, search and samples call writes `CATALOG_READ` (Summarized) with the fields `surface` (graphql|rest|mcp|search), `objectCount`, `objectIds` (up to 50; for detail the one id), `queryHash` (SHA-256 of the normalized search text, never the text) and `tenant`.
    - The existing `.WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead)` stays on the REST routes, and the service-level record covers GraphQL and MCP.
    - Rate limits: per (tenant, sid), via `IRateLimiterService`, with scope `discovery` (`RequestsPerMinute`) for read calls and `discovery-access-request` (`AccessRequestsPerHour`) for request access. On excess: 429 `RATE_LIMIT_EXCEEDED` with `Retry-After`, the GraphQL error code `RATE_LIMITED`, or the MCP tool error `RATE_LIMITED`.
    - The `warn_disable_rate_limiting` switch also disables these limits; the bypass is already reported as WARN.
11. **Retire the tier model (PR 5).**
    - Delete `DatasetVisibility`, the tier switch in `CatalogVisibility.FilterForSubject` (`:85-135`; `CatalogSurface.Discovery` handling) and `CatalogView.Summary`/`Full`, together with their uses in `CatalogViewShaping`, `CatalogDiscoveryService` (`:448,569,639`) and `McpDatasetCatalog` (`:420,981,1256`).
    - `CatalogVisibilityService` stays only as the data-access-side visibility used by `AiDataGuardrailService`, data fields and `sample-rows`; rename its doc comment. `CatalogSurface.Discovery` is deleted when no call site remains (`CatalogSurfaceCallSiteGuardTests` updated).
    - Delete the tier tests whose subject no longer exists (step 6.4).
13. **Linked documents (PR 6, after WP-SIMPL-40 PR 40c).** Documents and applications come from the WP-SIMPL-40 store (`doc_official_documents`, `doc_tenant_documents`, `IUniversalDocStore`, `DocAccessPolicy`, `UntrustedMarkdownSanitizer`). The MCP doc tools today are `search_docs`, `lookup_error_code`, `get_procedure`, `list_doc_topics`, `list_applications` and `deposit_documentation` (`McpDocTools.cs:15-20`); there is no read tool.
    - **Linkage model.** Add migration step `0008_doc_links` (next free number after WP-SIMPL-40's step) with table `doc_links`:
      - Columns: `tenant_id varchar(128) NOT NULL`, `link_id uuid PK`, `source_kind` (`Document`|`Application`), `app_id`, `category NULL`, `doc_id NULL` (null for `Application`), `doc_tenant_id NULL` (null = official document), `target_kind` (`Table`|`View`|`Procedure`|`Column`), `target_domain`, `target_schema`, `target_name`, `target_column NULL` (required when `target_kind = Column`), `created_by`, `created_at_utc`.
      - Unique key: (`tenant_id`, source, target).
      - `tenant_id` is always the tenant of the target object, so links are tenant-scoped even for official documents (OD-9: no cross-tenant write).
      - A document belongs to its application through its `app_id` (existing). An application linked to a dataset expresses "program based on this data".

      Add `discovery_hidden BOOLEAN NOT NULL DEFAULT FALSE` to both document tables.

      Add `IDocLinkRepository` (partial `GovernanceRepository.DocLinks.cs`, unified repository rules of WP-SIMPL-20) with `ListForTargetsAsync(tenant, targets)`, `ListForDocumentAsync(tenant, docKey)`, `ReplaceForSourceAsync(tenant, source, targets, actor)` (one transaction) and `DeleteForSourceAsync`. When a document is deleted or purged (WP-SIMPL-40 delete/purge paths), its links are deleted in the same transaction.
    - **Who may link or hide.** The document writer per `DocAccessPolicy` (GovernanceAdmin of the tenant for tenant documents; canonical ClusterAdmin for official documents, only within its request tenant), or the data owner/delegate or GovernanceAdmin of the target table. Every target must be discoverable for the actor; otherwise the call fails with 404, identical to unknown.

      Routes:
      - `PUT /api/v1/docs/apps/{appId}/documents/{category}/{docId}/links`, body `{ targets: [{kind, domain, schema, name, column?}] }` (max 50 targets).
      - `PUT /api/v1/docs/apps/{appId}/links` for application links.
      - `PUT /api/v1/docs/apps/{appId}/documents/{category}/{docId}/discovery`, body `{ hidden: bool }`.

      `deposit_documentation` and the PUT document route accept an optional `links` array (same validation).

      Audit: new event types `DOC_LINKS_CHANGED` and `DOC_VISIBILITY_CHANGED` (Tier A, FailClosed, before/after link sets or flag, never content).
    - **Visibility rule (OD-19).** A linked document or application is returned to a caller only when all three hold:
      - the document is readable under `DocAccessPolicy.ResolveReader` (official or same tenant);
      - the document is not `discovery_hidden`;
      - the link target is discoverable for the caller (in tenant, not hidden; for `Column`, the column is not hidden).

      Otherwise the link is silently omitted. A hidden or foreign document addressed directly gives the WP-SIMPL-40 "not found" response. Documents never carry row data, because only Markdown from the WP-SIMPL-40 store is served.
    - **Discovery output.** `DiscoveryObject` gains `IReadOnlyList<DiscoveryDocumentRef> Documents` and `IReadOnlyList<DiscoveryApplicationRef> Applications`, and `DiscoveryColumn` gains `Documents`:
      ```csharp
      public sealed record DiscoveryDocumentRef(string AppId, string Category, string DocId, string Title, string? Summary,
          bool Official, string Trust /* "official" | "untrusted" */, DateTimeOffset UpdatedAt);
      public sealed record DiscoveryApplicationRef(string AppId, string? Name, int DocumentCount);
      ```
      - `Summary` is the first 300 characters of the sanitised body, cut on a rune boundary.
      - At most 10 document refs per object and 5 per column, newest first, with a `documentsTruncated` flag.
      - GraphQL and REST expose the same fields.
    - **Catalog search.** The search document of a dataset (BM25 and the semantic embedding text) includes the titles and summaries of its visible linked documents. Content of hidden documents, or of documents linked only to hidden columns, is never indexed. Index invalidation runs on `DOC_LINKS_CHANGED`, `DOC_VISIBILITY_CHANGED` and document replace or delete (the WP-SIMPL-40 index refresh tick also triggers invalidation of affected datasets). Search hits carry `matchedInDocument: true` when the match came only from document text.
    - **MCP.**
      - `describe_dataset` returns `documents`, `applications` and per-column `documents` as above.
      - `list_datasets` returns only `documentCount` per dataset (token economy).
      - `search_docs` gains optional `dataset` and `column` arguments that restrict results to documents linked to that discoverable target. Its results include the linked targets of each hit (only discoverable ones).
      - New tool `read_document(appId, category, docId, offset?)` returns the sanitised Markdown with `trust`, `UntrustedContentNotice` for tenant content, and the 64 KiB per-result cap of WP-SIMPL-40 (`truncated: true`, `nextOffset`).
      - Total discovery payload per MCP tool result is capped at 64 KiB (refs dropped newest-last with `truncated: true`).
    - **Untrusted text (#500).** Every free-text field that discovery returns to MCP is passed through the shared rune rules (`UntrustedTextRunes` / `McpCatalogText`, as extracted by WP-SIMPL-40 step 2) and length caps, and is wrapped as untrusted data. This covers descriptions, display names, owner names, tags, document titles and summaries, and uploaded sample and statistics string values. Each MCP tool result carries `trust` per text block and a single `UntrustedContentNotice` when any tenant-authored text is present. Text is never interpolated into tool guidance strings (`McpDatasetCatalog.cs:233,370` style guidance uses only counts and identifiers).
14. **Operator migration note (PR 1).** Add a "Removed: OpenSchema" subsection in `docs/configuration-guide.md` (reference text, no status): the key is removed, startup fails if it is set, use authenticated discovery, and hide objects with `PUT .../discovery`. Note the default change "visible unless hidden" and that the old `visibility` tier is ignored. Add the same note to the release notes / CHANGELOG entry of the release.

## 6. Tests
Unit tests go in `tests/Autheris.Tests.Unit` (folders as named); integration tests in `tests/Autheris.Tests.Integration`. [S] means `[Trait("Category","Security")]`.
1. `Security/OpenSchemaRemovalTests`:
   - [S] `LegacyOpenSchemaKey_Set_StartupFails` (both keys, every environment).
   - [S] `OpenApiDocuments_Anonymous_Return401Challenge` (Theory: `/odata/v4/$openapi`, `/api/v1/sql/openapi.json`, `/api/v1/sql-endpoints/openapi.json` (verify the group prefix in `SqlEndpointRoutes.cs`), `/api/v1/procedures/openapi.json`, `/odata/v4/$metadata`, `/odata/v4/` service document, Swagger UI) in Production.
   - [S] `GraphQlCatalog_Anonymous_ReturnsUnauthorized`.
   - [S] `ActiveBypasses_ContainNoOpenSchemaEntry`.
   - `Quickstart_ExpansionHasNoOpenSchema`.
2. `Catalog/Discovery/DiscoveryTenantScopeTests`:
   - [S] `ForeignTenantTable_NotListed_AndDetail404`.
   - [S] `ForeignVirtualTable_NeverAdmitted_EvenWithConsent`.
   - `OwnTenantTable_ListedWithoutConsent`.
   - `SharedTable_Listed`.
   - `CatalogOwner_SeesUnownedTables`.
   - [S] `UnownedTable_NotListedForNonOwnerTenant`.
3. `Catalog/Discovery/DiscoveryHiddenTests`:
   - [S] `HiddenTable_AbsentFromListSearchSemanticMcpGraphQl`.
   - [S] `HiddenTable_DetailResponseIdenticalToUnknown` (status, body, headers byte-equal on REST; `null` on GraphQL; same MCP error).
   - [S] `HiddenColumn_AbsentFromColumnsStatisticsSamplesAndSearchIndex`.
   - [S] `SearchOnHiddenColumnName_ReturnsNoHit_AndCountUnchanged`.
   - `AdminAndOwner_SeeHiddenWithFlag`.
   - [S] `NonOwner_NeverSeesIsHiddenTrue`.
   - [S] `FlagReadFailure_TreatsAllAsHidden`.
4. `Catalog/Discovery/DiscoveryAccessResolverTests`:
   - `NoConsent_AccessNone_AllColumnsNoAccess`.
   - `PendingRequest_AccessRequested`.
   - `ExpiredConsent_AccessExpired`.
   - `AllowWithRowFilter_RowRestricted`.
   - [S] `RowRestricted_ResponseContainsNoPredicateOrValues` (serialized DTO contains no filter text or values from the consent).
   - `PiiColumn_DefaultMasked_ClearOnlyWithClearRule`.
   - `DeniedColumn_ListedAsNoAccess_NotOmitted`.
   - [S] `ColumnState_EqualsDataPathResolution` (Theory over consent fixtures: discovery state equals the level the data path applies).
5. `Catalog/Discovery/DiscoveryCallerGateTests`:
   - [S] `Anonymous_401`.
   - [S] `AnonymousMcpClientSid_Empty`.
   - [S] `NoTenant_Forbidden`.
   - `RestrictionOff_AnyAuthenticatedUserAllowed`.
   - [S] `RequiredRole_Missing_Forbidden_OnAllChannels`.
   - `TenantOverride_Applies`.
   - [S] `OptionsAboveHardCaps_StartupFails`.
6. `Catalog/Discovery/DiscoveryChannelParityTests` (extends the idea of `CatalogChannelParityMatrixTests`): [S] `SameCaller_SameObjectsAndFields_AcrossGraphQlRestMcpSearch`.
7. `Catalog/Discovery/DiscoveryArtifactUploadTests`:
   - [S] `Upload_ByNonOwnerNonUploader_403`.
   - [S] `Upload_ToForeignOrHiddenTable_404`.
   - `Upload_ByUploaderRole_StoredAndAudited`.
   - [S] `Upload_HiddenColumn_Rejected`.
   - [S] `Upload_UnknownColumn_Rejected`.
   - [S] `Upload_TooLarge_RejectedBeforeDeserialization`.
   - [S] `Upload_TooManyRows_Rejected`.
   - [S] `UploadAudit_ContainsNoValues`.
   - [S] `UploadAuditFailure_RollsBack`.
8. `Catalog/Discovery/DiscoveryArtifactServingTests`:
   - [S] `ColumnHiddenAfterUpload_NotServed`.
   - [S] `SamplesDisabled_NoSamplesServed`.
   - [S] `Samples_IndependentOfCallerGrants_NeverSourceRows` (the mock data source records zero queries during a discovery samples call).
   - `NoArtifact_AvailableFalse`.
   - [S] `ServedRows_CappedAtMaxSampleRows`.
9. `Architecture` (`tests/Autheris.Tests.Architecture`):
   - [S] `DiscoveryNamespace_DoesNotReference_DataSourceExecution` (no reference from `Autheris.Application.Catalog.Discovery` to the gateway execution, SQL engine or data source connector types).
   - [S] `NoProfileOrSampleGeneratorTypesExist`.
10. `ConsentRequests/DiscoveryAccessRequestTests`:
    - [S] `RequestForHiddenOrForeignTable_404`.
    - [S] `ClearColumns_HiddenOrUnknown_SameError`.
    - `ClearColumns_NonPii_Rejected`.
    - `Request_RoutedToTableOwners`.
    - `Approval_SubsetOfClearColumns_CreatesClearRulesOnlyForApproved`.
    - `DuplicatePending_Idempotent`.
    - [S] `AccessRequestRateLimit_429`.
11. `Catalog/Discovery/DiscoveryAuditAndRateLimitTests`:
    - [S] `EveryDiscoveryRead_WritesCatalogRead_WithQueryHashNotText`.
    - [S] `DiscoveryReadRateLimit_429_PerTenantAndSid`.
    - `FlagChange_WritesVisibilityChangedBeforeAfter`.
12. Integration `CatalogDiscoveryEndToEndTests` (PostgreSQL Testcontainers, real migration 0006):
    - `Migration0006_IdempotentAndAdditive`.
    - [S] `Discovery_TwoTenants_NoCrossTenantLeak`.
    - `FlagsPersistAcrossRestart`.

13. `Catalog/Discovery/DiscoveryDocumentLinkTests` (unit, with `FakeDocumentRepository` from WP-SIMPL-40 and a fake `IDocLinkRepository`):
    - `DescribeDataset_ReturnsLinkedDocumentsAndApplications`.
    - `ColumnLink_ReturnedOnColumn`.
    - [S] `HiddenDocument_AbsentFromDiscoverySearchAndMcp`.
    - [S] `LinkToHiddenTableOrColumn_Omitted`.
    - [S] `OtherTenantDocument_NeverLinkedOrReturned`.
    - [S] `ReadDocument_HiddenOrForeign_IdenticalToMissing`.
    - [S] `Link_ToNonDiscoverableTarget_404`.
    - [S] `Link_ByNonWriterNonOwner_403`.
    - [S] `LinkChange_AuditedFailClosed_WithoutContent`.
    - `DocumentDelete_RemovesLinks`.
    - `RefsCapped_TruncatedFlagSet`.
14. `Catalog/Discovery/DiscoveryUntrustedTextTests`:
    - [S] `McpDiscoveryText_SanitisedAndMarkedUntrusted` (Theory: bidi, zero-width, tag characters and control characters in description, owner name, sample value, document title).
    - [S] `GuidanceStrings_ContainNoCatalogText`.
    - [S] `McpResult_CappedAt64KiB`.
15. `Catalog/Discovery/DiscoverySearchDocumentTests`:
    - `DatasetSearch_MatchesLinkedDocumentTitle_MatchedInDocumentTrue`.
    - [S] `HiddenDocumentText_NotIndexed_BM25AndEmbedding`.
    - [S] `SearchDocs_DatasetFilter_OnHiddenDataset_ReturnsEmpty`.
16. Integration `DocLinksRepositoryContractTests` (both engines, WP-SIMPL-19 contract base):
    - `ReplaceForSource_IsAtomic`.
    - [S] `Links_TenantScoped`.

Tests to rewrite or delete:
- OpenSchema assertions in the 22 files listed in section 4 (rewrite to the authenticated behaviour).
- `CatalogDiscoveryTierVisibilityTests`, `CatalogOwnershipAndTierVisibilityTests`, `CatalogTierPersistenceEndToEndTests`, `CatalogViewDefaultsTests`, `CatalogViewShapingTests` and `GraphQL/CatalogTierShapingTests`: delete what tests the tier switch, and move still-valid tenant and column assertions into the new classes.
- `CatalogTagsVisibilityEndpointTests`: the visibility route is gone, so keep the tags part.
- Every test that uses `SyntheticSampleRowGenerator`, `ColumnValueProfileService` or `synthetic=true` is deleted or moved to the artifact tests.

## 7. Verification commands
One build at a time. Check `df -h /work` before the build and clean `bin/obj` after push.
```bash
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~Discovery|FullyQualifiedName~OpenSchemaRemoval|FullyQualifiedName~DiscoveryAccessRequest"
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "Category=Security"
dotnet test tests/Autheris.Tests.Architecture -c Release --no-build
dotnet test tests/Autheris.Tests.Integration -c Release --no-build --filter "FullyQualifiedName~CatalogDiscoveryEndToEnd|FullyQualifiedName~OpenApi"
git grep -n "OpenSchema\|IsOpenSchemaAllowed\|open_schema" -- src tests ':!docs'                                   # expect only LegacySwitchGuard + removal tests
git grep -n "SyntheticSampleRowGenerator\|ColumnValueProfileService\|ReleaseProfileToCatalogViewers\|PredefinedSyntheticSamples\|DatasetVisibility" -- src   # expect 0
git grep -n "GetAllTablesAsync" -- src/Autheris.GraphQL                                                           # expect 0
```
Finally, run the full unit and architecture suites once.

## 8. Acceptance criteria
- [ ] `src` has no OpenSchema property, predicate or `AllowAnonymous` tied to it. Setting either legacy key fails startup with the migration message.
- [ ] Every OpenAPI, Swagger, `$metadata` and catalog route answers an anonymous caller in Production with 401/challenge.
- [ ] An authenticated user without consent lists all non-hidden objects of their tenant, with descriptions, every non-hidden column (including denied and masked ones) with `Pii`, `State`, owners (display name and email only), access state, uploaded statistics and `samplesAvailable`.
- [ ] Hidden tables and columns are absent on GraphQL, REST, MCP, BM25 and semantic search. The detail and request-access responses for hidden, foreign and unknown objects are identical.
- [ ] Column and table states match the data-path resolution for the same caller (parity test green).
- [ ] No discovery response contains a row-filter predicate, filter value or another subject's data.
- [ ] No code computes statistics or generates samples. Discovery samples come only from `CATALOG_DISCOVERY_ARTIFACTS`. A discovery call issues zero data-source queries.
- [ ] Upload: only admin, owner/delegate or `CatalogArtifactUploader` of the tenant. Limits and schema checks are enforced. Audit is FailClosed and contains no values.
- [ ] Request access creates one consent request routed to the table's data owners, with optional clear columns. Approval applies only the approved columns.
- [ ] `CATALOG_READ` is written for every discovery read with `queryHash` (no raw text). Rate limits return 429.
- [ ] Optional role/scope restriction works per tenant, default off.
- [ ] Migration 0006 is additive, idempotent and present for every supported governance dialect.
- [ ] Linked documents and applications appear on discovery, search (BM25 and semantic) and MCP for every authenticated tenant user unless the document or its target is hidden. `read_document` and the filtered `search_docs` work. Links are audited, and document delete removes its links.
- [ ] Every MCP discovery text field is sanitised and marked untrusted (#500), and MCP results are capped at 64 KiB.
- [ ] All tests in section 6 are green, `Category=Security` included. Release build has no warnings.

## 9. PR slicing (<= 400 net added src lines each)
1. **PR 1, OpenSchema removal:** options, LegacySwitchGuard, routes, GraphQL branch, Quickstart and the operator note. It is mostly a deletion (estimated +60 / -220 src), plus test rewrites.
2. **PR 2, model, migration 0006, store, flag API and audit:** steps 2-4 (estimated +380 src).
3. **PR 3, discovery core plus channels, audit and rate limits for reads:** steps 5, 6 and 10 (reads) (estimated +400 src). If it exceeds 400, split it into 3a (core plus GraphQL/REST) and 3b (MCP plus search index/embedding).
4. **PR 4, artifacts and removal of generators/profiles:** steps 7-8, with procedures in discovery as the final commit if WP-SIMPL-39 PR 4 is merged (procedure discoverable when Active and every `GovernedTables()` entry is discoverable for the caller, kind `Procedure`, columns = parameters, access = `IProcedureCatalog.FindVisibleAsync != null ? Granted : None`). Estimated +330 / -1,350 src.
5. **PR 5, request access with clear columns, tier-model removal and access-request rate limit:** steps 9, 10 (requests) and 11 (estimated +250 / -400 src).
6. **PR 6, linked documents and MCP untrusted-text handling:** step 13, after WP-SIMPL-40 PR 40c and PR 3 of this WP (estimated +390 src). If it exceeds 400, split it into 6a (links model, repository, routes, audit) and 6b (discovery/search/MCP exposure, `read_document`, untrusted-text wrapper).

## 10. Risks, rollback, and things the developer must NOT do
- Risk: widening, because the default flips from hidden to visible. Mitigations: tenant scoping is fail-closed for unowned tables, the flag-read failure mode is "all hidden", and the parity and cross-tenant tests are security-tagged. Rollback per PR by revert. Migration 0006 is additive, so a revert of code leaves unused columns (harmless).
- Risk: uploaded statistics or samples that contain real PII. The uploader is responsible (owner decision). Autheris keeps the size and row caps, hidden-column drop, per-table disable and audit hash, so incidents can be traced and the artifact deleted (`DELETE`). Residual risk for Phase 3: section 13, SEC-SIMPL-110.
- Risk: owner email exposure. Show only `Email` of active owners. If an owner has no email, show only `DisplayName`. Never show `AdSid` or `AdAccount`.
- Do NOT:
  - fall back to `GetAllTablesAsync` without scoping;
  - return different status codes or messages for hidden vs unknown;
  - add an early return that skips the repository for hidden tables (timing);
  - include hidden columns in search documents or embeddings;
  - compute any statistic, generate any synthetic value, or query a data source on a discovery path;
  - pass caller-supplied text into audit (hash only);
  - return a row-filter predicate;
  - keep in-memory sample or profile registries;
  - keep the `synthetic` flag on `sample-rows`;
  - weaken the data-access path (deny, mask and row filters stay enforced at query time);
  - add `AllowAnonymous` anywhere in this WP.

## 11. Estimate
Source: about +1,810 / -2,000 lines (net about -190). Tests: about +2,000 / -900. Effort: about 11 person-days (PR 1: 1.0, PR 2: 2.0, PR 3: 3.0, PR 4: 1.5, PR 5: 1.5, PR 6: 2.0).

## 12. Decisions taken in this spec
- D1 (owner, binding): discovery is open to every authenticated user and to agents acting for one; there is no extra role by default. The optional restriction (`RequiredRoles` / `RequiredScopes`, per-tenant override) is off by default. Anonymous discovery does not exist in any environment.
- D2 (owner, binding): everything is visible except what is explicitly hidden, at table and column level. Masked or denied columns are listed with their state. Only hidden objects are omitted, without an existence oracle. Hidden flags are set by GovernanceAdmin of the tenant or by the data owner or delegate, can be set by dbt meta (ratchet), and are audited as `CATALOG_VISIBILITY_CHANGED` FailClosed.
- D3: the Issue #111 tier model (`DatasetVisibility` Hidden/Summary/Full, default Hidden) is retired instead of reinterpreted, because its default contradicts OD-19 and persisted `Hidden` rows cannot be told apart from never-set ones. The persisted `visibility` column is ignored; no production data exists (owner note 2026-10-13).
- D4 (owner, binding): statistics and samples come only from an external upload (or dbt meta through the same validated store). Autheris stores and serves them as-is minus hidden columns and within caps, and generates or computes nothing. The previously specified "computed asynchronously" behaviour is withdrawn.
- D5 (OD-22): PII columns are masked by default. Clear access is a separate, explicit per-column request decided by the data owner. Table state is None/Requested/Granted (Full or RowRestricted)/Expired, and predicates are never shown.
- D6: there is no verified table/view discriminator in `TableModels.cs`. `Table.ObjectKind` is added (default `Table`) and set to `View` by dbt `materialized: view`; other ingestion sources follow later.
- D7: `sample-rows` without the synthetic flag is a governed data read, not discovery, and is kept unchanged.
- D8: the legacy OpenSchema keys fail startup in every environment (not only Production), because Development already serves documentation anonymously through `env.IsDevelopment()` and a silently ignored key would mislead operators.
- D9: tenant scope for tables without `OwnerTenantId`: only the catalog-owner tenant or `SharedTables` (fail-closed in multi-tenant deployments). A single-tenant deployment is the catalog owner and sees everything.
- D10 (owner, binding): linked documents are core discovery context, especially for MCP. Links are stored in the target's tenant. Documents follow the OD-19 rule (visible unless explicitly hidden) on top of the WP-SIMPL-40 reader scope. Semantic search covers linked document titles and summaries through the dataset search document. A separate embedding index over full documents stays out of scope, as in WP-SIMPL-40.

## 13. Phase 3 conditions to add (proposed IDs, to be confirmed by csharp-security-expert)
- SEC-SIMPL-99: no anonymous discovery or OpenAPI route; legacy OpenSchema keys fail startup.
- SEC-SIMPL-100: tenant scoping is fail-closed, and cross-tenant and foreign-virtual-table leaks are pinned by tests.
- SEC-SIMPL-101: hidden objects and columns are absent on all channels including search index and embeddings; hidden, foreign and unknown responses are identical (no existence oracle, no timing shortcut).
- SEC-SIMPL-102: discovery column and table states equal the data-path resolution (parity test), and row-filter predicates are never disclosed.
- SEC-SIMPL-103: discovery issues zero data-source queries (architecture test plus runtime test).
- SEC-SIMPL-104: flag changes and artifact changes are audited FailClosed with before/after values; artifact audit carries hashes, never values.
- SEC-SIMPL-105: artifact upload is restricted to admin, owner/delegate or the tenant-scoped uploader role, with size, row and schema validation before persistence.
- SEC-SIMPL-106: discovery reads are audited with a query hash (no raw text) and rate-limited per tenant and SID; access requests are rate-limited separately.
- SEC-SIMPL-107: owner contact shows display name and email only; no SID or account.
- SEC-SIMPL-108: discovery failures answer empty or null, never unfiltered; a flag-read failure means hidden.
- SEC-SIMPL-109: clear-column requests are only allowed for non-hidden PII columns; approval applies only approved columns.
- SEC-SIMPL-110 (residual risk, accepted by owner decision): uploaded content is the uploader's responsibility. Required: uploader role per tenant, per-table disable, delete route, audit trail.
- SEC-SIMPL-111: the optional role/scope restriction is enforced identically on GraphQL, REST, MCP and search.
- SEC-SIMPL-112: the MCP `request_access` tool is hidden for read-only clients and answers with the unknown-tool error.
- SEC-SIMPL-113: document links are tenant-scoped. Links to hidden or foreign targets and hidden documents are never returned or indexed. Link and hide changes are audited FailClosed.
- SEC-SIMPL-114: every discovery text returned to MCP is sanitised with the shared rune rules, marked untrusted, never interpolated into guidance, and capped at 64 KiB per result (#500).
