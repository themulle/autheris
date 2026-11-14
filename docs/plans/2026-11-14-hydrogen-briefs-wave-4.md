# Phase 4 Hydrogen Developer Briefs — Wave 4 (Plus Wave 5 Queue Summary)

- **Date:** 2026-11-14
- **Baseline:** `origin/main` @ `2b2876bd` (sequenced immediately after Wave 3 `docs/plans/2026-11-14-hydrogen-briefs-wave-3.md`).
- **Global Constraints for All Hydrogen Workers:**
  1. **English Only:** All identifiers, comments, error messages, logs, and commit messages MUST be in English.
  2. **Filtered Local Unit Tests Only:** Run `dotnet build -c Release -warnaserror` (one project or solution when build lock is free) and `dotnet test ... --no-build --filter "<Filter>"`. NEVER run the full unfiltered test suite or Testcontainers integration suite locally.
  3. **Zero `file`+`://` Links in Markdown:** Never write literal `file`+`://` URI strings in any `.md` file (`DocsIntegrityTests.No_absolute_file_links`).
  4. **Clean `bin/obj` After Push:** Run `find src tests -maxdepth 2 \( -name bin -o -name obj \) -exec rm -rf {} +` after pushing your branch to keep `/work` disk usage low.

---

## Wave 4 Conflict & DDL Serialization Matrix

| Slot | Work Package | Branch / Worktree | DDL Step | Shared-File Serialization Rules |
| :--- | :--- | :--- | :--- | :--- |
| **W4-1** | **`WP-SIMPL-47 PR 1`** (Consent API Schema & Domain + `CONSENTS.grant_source`) | `feat/simpl-47-pr1-schema-domain` (`/work/wt/simpl-47-pr1-schema-domain`) | **`0014_consent_api`** *(Single DDL slot in Wave 4; starts after Wave 3 `0013_catalog_discovery` merges)* | Touches `Migrations/Steps/{postgresql,sqlserver}/0014_consent_api*`, `GovernanceTable.cs`, `ConsentModels.cs`, `GovernanceModels.cs`, `GovernanceRepository.Consent*.cs`. Disjoint from W4-2, W4-3, W4-4. Unblocks `WP-OI-CAT-4 (#671)` and `WP-SIMPL-47 PR 2`. |
| **W4-2** | **`WP-SIMPL-41 PR 3`** (Authorized Discovery Core, Channels, Audit & Rate Limits) | `feat/simpl-41-pr3-discovery-core` (`/work/wt/simpl-41-pr3-discovery-core`) | None *(Requires Wave 3 `WP-SIMPL-41 PR 2` merged on `main`)* | Touches `src/Autheris.Application/Catalog/Discovery/**`, `CatalogDiscoveryService.cs`, `McpDatasetCatalog.cs`, `Query.Catalog.cs`, `CatalogApiEndpoints.cs`, `CatalogSearchEngine.cs`. Disjoint from W4-1, W4-3, W4-4. Unblocks `#681` and `#682-B`. |
| **W4-3** | **`WP-OI-SQL-479-A`** (`#479` Dialect Semantics PR A: Oracle `MOD`/`TRUNC`, Databricks `DIV`, `IntegralOperandClassifier`) | `feat/oi-sql-479a-div-mod` (`/work/wt/oi-sql-479a-div-mod`) | None *(Starts after Wave 3 `WP-OI-SQL-486` merges on `TrinoSqlEngine`)* | Touches `src/TrinoSqlEngine/**` (`Expressions.cs`, `SqlDialectGeneratorBase.Expressions.cs`, `OracleDialectGenerator.cs`, `DatabricksDialectGenerator.cs`, `GovernedSqlCompiler.cs`, `CompileCacheKey.cs`). Disjoint from W4-1, W4-2, W4-4. |
| **W4-4** | **`WP-OI-SCALE-2 (#673)`** (Batch Table Metadata Upsert `UpsertTableMetadataBatchAsync` + Ingestion Callers) | `feat/oi-scale-673-batch-upsert` (`/work/wt/oi-scale-673-batch-upsert`) | None *(Zero DDL — runs in parallel with W4-1!)* | Touches `IGovernanceRepository.cs`, `GovernanceRepository.Catalog.cs`, `GovernanceRepository.CatalogSql.cs`, `DbtMetadataIngestionService.cs`, `OpenMetadataSyncService.cs`, `DataCatalogSyncService.cs`. Disjoint from W4-1, W4-2, W4-3. |

---

## Brief 1 — `WP-SIMPL-47 PR 1`: External Consent API Schema (`0014_consent_api`), Domain Models & Safe `grant_source` Backfill

- **Worktree:** `/work/wt/simpl-47-pr1-schema-domain`
- **Branch:** `feat/simpl-47-pr1-schema-domain` (branch from `origin/main` after Wave 3 `WP-SIMPL-41 PR 2` `0013_catalog_discovery` merges)
- **Plan & Security References:**
  - `docs/plans/simplification/WP-SIMPL-47-external-catalog-and-consent-api.md` (Section 5 Steps 1–2, Section 6a `SEC-SIMPL-47-01..12`, Section 9 PR 1)
  - `docs/plans/2026-11-13-plan-issues-catalog-and-governance.md` (`WP-OI-CAT-4` / `#671`)
  - `docs/plans/2026-11-13-sec-issues-group-3.md` (`SEC-OI-671-1..2`) & `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SEC3-1`)

### Mandatory Security Conditions
- **`SEC-OI-671-1` (Safe `CONSENTS.grant_source` Migration Backfill):** Existing rows in `CONSENTS` MUST default/backfill `grant_source` to `'CONSENT_REQUEST'` (`VARCHAR(24) NOT NULL DEFAULT 'CONSENT_REQUEST'`). NEVER backfill existing rows wholesale as `'DECLARATIVE'`, because doing so would allow a subsequent `DeclarativeConsentApplyService` run (`#671`) to revoke human-approved or bulk consents.
- **`SEC-SIMPL-47-01..03` (Foundation Schema Invariants):** Every new governance table MUST be registered in the closed `GovernanceTable` enum (`src/Autheris.Infrastructure/Persistence/Dialects/GovernanceTable.cs:7-144`) and mapped in `ToSqlName()`. Both PostgreSQL and SQL Server forward-only migration steps (`0014_consent_api.sql` + `0014_consent_api.shape.json`) and baseline schemas (`PostgreSqlBaselineSchema.cs`, `SqlServerBaselineSchema.cs`) MUST stay in exact structural sync (`StepSqlLint` & migration shape tests).

### Exact Changes to Make
1. **Migration Step `0014_consent_api` & Baseline Schemas:**
   - Create `src/Autheris.Infrastructure/Persistence/Migrations/Steps/postgresql/0014_consent_api.sql` (+ `.shape.json`) and `src/Autheris.Infrastructure/Persistence/Migrations/Steps/sqlserver/0014_consent_api.sql` (+ `.shape.json`), and update `PostgreSqlBaselineSchema.cs` and `SqlServerBaselineSchema.cs`:
     - **`CONSENT_REQUESTS`**: add columns `version INT NOT NULL DEFAULT 1`, `workflow_mode VARCHAR(16) NOT NULL DEFAULT 'INTERNAL'`, `external_system_id VARCHAR(36) NULL`, `external_reference VARCHAR(256) NULL` (backfill from `itsm_ticket_id` where non-null), `requested_valid_from TIMESTAMPTZ/DATETIMEOFFSET NOT NULL` (backfill from `requested_at`), `updated_at TIMESTAMPTZ/DATETIMEOFFSET NOT NULL` (backfill from `requested_at`), `request_kind VARCHAR(24) NOT NULL DEFAULT 'CONSENT'`, `profile_id VARCHAR(36) NULL`; add index `IX_CONSENT_REQUESTS_TENANT_UPDATED (tenant_id, updated_at, id)`.
     - **`CONSENTS`**: add columns `expiry_notice_sent_at TIMESTAMPTZ/DATETIMEOFFSET NULL`, `grant_record_seq BIGINT NOT NULL DEFAULT 0`, `grant_source VARCHAR(24) NOT NULL DEFAULT 'CONSENT_REQUEST'`, `profile_assignment_id VARCHAR(36) NULL`; add index `IX_CONSENTS_TENANT_GRANT_SOURCE (tenant_id, grant_source)`.
     - **New Tables** (idempotent `CREATE TABLE IF NOT EXISTS` / `IF OBJECT_ID(...) IS NULL`):
       - `PROFILE_ASSIGNMENT_ITEMS (tenant_id VARCHAR(128) NOT NULL, request_id VARCHAR(36) NOT NULL, item_no INT NOT NULL, object_ref VARCHAR(512) NOT NULL, owner_sid VARCHAR(256) NOT NULL, owner_decision VARCHAR(24) NOT NULL, decided_by VARCHAR(256) NULL, decided_at TIMESTAMPTZ/DATETIMEOFFSET NULL, PRIMARY KEY (tenant_id, request_id, item_no, owner_sid))`
       - `CONSENT_WORKFLOW_ROUTES (tenant_id VARCHAR(128) NOT NULL, scope_kind VARCHAR(24) NOT NULL, scope_ref VARCHAR(256) NOT NULL, mode VARCHAR(16) NOT NULL, external_system_id VARCHAR(36) NULL, max_duration_days INT NOT NULL, request_timeout_days INT NOT NULL DEFAULT 30, expiry_notice_lead_hours INT NOT NULL DEFAULT 72, require_resolvable_approver BOOLEAN/BIT NOT NULL DEFAULT TRUE/1, updated_by VARCHAR(256) NOT NULL, updated_at TIMESTAMPTZ/DATETIMEOFFSET NOT NULL, PRIMARY KEY (tenant_id, scope_kind, scope_ref))`
       - `EXTERNAL_WORKFLOW_SYSTEMS (id VARCHAR(36) NOT NULL PRIMARY KEY, tenant_id VARCHAR(128) NOT NULL, name VARCHAR(256) NOT NULL, client_id VARCHAR(256) NOT NULL, webhook_url VARCHAR(2048) NOT NULL, webhook_secret_protected TEXT/NVARCHAR(MAX) NOT NULL, enabled BOOLEAN/BIT NOT NULL DEFAULT TRUE/1, created_by VARCHAR(256) NOT NULL, created_at TIMESTAMPTZ/DATETIMEOFFSET NOT NULL, disabled_at TIMESTAMPTZ/DATETIMEOFFSET NULL, UNIQUE (tenant_id, client_id))`
       - `GRANT_RECORDS (tenant_id VARCHAR(128) NOT NULL, seq BIGINT NOT NULL, id VARCHAR(36) NOT NULL, record_type VARCHAR(24) NOT NULL, consent_id VARCHAR(36) NULL, request_id VARCHAR(36) NULL, grant_source VARCHAR(24) NOT NULL, profile_assignment_id VARCHAR(36) NULL, grantee_type VARCHAR(32) NOT NULL, grantee_ref VARCHAR(256) NOT NULL, object_id VARCHAR(512) NOT NULL, scope_json TEXT/NVARCHAR(MAX) NOT NULL, valid_from TIMESTAMPTZ/DATETIMEOFFSET NULL, valid_until TIMESTAMPTZ/DATETIMEOFFSET NULL, requested_by VARCHAR(256) NOT NULL, decision_json TEXT/NVARCHAR(MAX) NOT NULL, decided_at TIMESTAMPTZ/DATETIMEOFFSET NOT NULL, recorded_at TIMESTAMPTZ/DATETIMEOFFSET NOT NULL, prev_hash CHAR(64) NOT NULL, record_hash CHAR(64) NOT NULL, signature TEXT/NVARCHAR(MAX) NOT NULL, key_id VARCHAR(64) NOT NULL, PRIMARY KEY (tenant_id, seq))`
       - `GRANT_RECORD_HEADS (tenant_id VARCHAR(128) NOT NULL PRIMARY KEY, last_seq BIGINT NOT NULL, last_hash CHAR(64) NOT NULL)`
       - `CONSENT_EVENTS (tenant_id VARCHAR(128) NOT NULL, seq BIGINT NOT NULL, id VARCHAR(36) NOT NULL, type VARCHAR(64) NOT NULL, subject_id VARCHAR(256) NOT NULL, payload_json TEXT/NVARCHAR(MAX) NOT NULL, created_at TIMESTAMPTZ/DATETIMEOFFSET NOT NULL, PRIMARY KEY (tenant_id, seq))`
       - `CONSENT_EVENT_DELIVERIES (event_id VARCHAR(36) NOT NULL, external_system_id VARCHAR(36) NOT NULL, attempts INT NOT NULL DEFAULT 0, next_attempt_at TIMESTAMPTZ/DATETIMEOFFSET NOT NULL, delivered_at TIMESTAMPTZ/DATETIMEOFFSET NULL, dead_lettered_at TIMESTAMPTZ/DATETIMEOFFSET NULL, lease_owner VARCHAR(128) NULL, lease_until TIMESTAMPTZ/DATETIMEOFFSET NULL, PRIMARY KEY (event_id, external_system_id))`
       - `CATALOG_OBJECT_VERSIONS (tenant_id VARCHAR(128) NOT NULL, object_id VARCHAR(512) NOT NULL, content_hash CHAR(64) NOT NULL, seq BIGINT NOT NULL, removed BOOLEAN/BIT NOT NULL DEFAULT FALSE/0, PRIMARY KEY (tenant_id, object_id))`
       - `CATALOG_CHANGES (tenant_id VARCHAR(128) NOT NULL, seq BIGINT NOT NULL, object_id VARCHAR(512) NOT NULL, kind VARCHAR(16) NOT NULL, changed_at TIMESTAMPTZ/DATETIMEOFFSET NOT NULL, PRIMARY KEY (tenant_id, seq))`
2. **`GovernanceTable` Allowlist (`src/Autheris.Infrastructure/Persistence/Dialects/GovernanceTable.cs:7-144`):**
   - Add enum members `CatalogChanges`, `CatalogObjectVersions`, `ConsentEventDeliveries`, `ConsentEvents`, `ConsentWorkflowRoutes`, `ExternalWorkflowSystems`, `GrantRecordHeads`, `GrantRecords`, `ProfileAssignmentItems` in alphabetical order and map them in `ToSqlName()`.
3. **Domain Models:**
   - Create `src/Autheris.Domain/Model/ConsentWorkflowModels.cs`:
     - `public enum ConsentWorkflowMode { Internal = 0, External = 1 }`
     - `public enum ConsentRequestKind { Consent = 0, ProfileAssignment = 1 }`
     - `public enum ConsentGrantSource { ConsentRequest = 0, AccessProfile = 1, Owner = 2, Bulk = 3, Declarative = 4, AccessPlan = 5 }` (with canonical SQL string helpers `"CONSENT_REQUEST"`, `"ACCESS_PROFILE"`, `"OWNER"`, `"BULK"`, `"DECLARATIVE"`, `"ACCESS_PLAN"`).
     - `public enum WorkflowRouteScopeKind { Tenant = 0, DataOwner = 1, DataSource = 2 }`
     - `public sealed record ConsentWorkflowRoute(...)`, `public sealed record ExternalWorkflowSystem(...)`, `public sealed record ExternalDecisionAttribution(...)`, `public sealed record AssertedApprover(...)`.
   - Create `src/Autheris.Domain/Grants/GrantRecord.cs` + `GrantRecordCanonicalizer.cs` (canonical UTF-8 JSON serializer with sorted keys, ISO-8601 UTC timestamps, no whitespace).
   - Extend `Consent` in `src/Autheris.Domain/Model/ConsentModels.cs:76-115`:
     - Add `public ConsentGrantSource GrantSource { get; init; } = ConsentGrantSource.ConsentRequest;`, `public long GrantRecordSeq { get; init; }`, `public Guid? ProfileAssignmentId { get; init; }`, `public DateTimeOffset? ExpiryNoticeSentAt { get; init; }`.
   - Extend `ConsentRequest` in `src/Autheris.Domain/Model/GovernanceModels.cs:33-55`:
     - Add `public int Version { get; init; } = 1;`, `public ConsentWorkflowMode WorkflowMode { get; init; } = ConsentWorkflowMode.Internal;`, `public Guid? ExternalSystemId { get; init; }`, `public string? ExternalReference { get; init; }`, `public DateTimeOffset? RequestedValidFrom { get; init; }`, `public DateTimeOffset? UpdatedAt { get; init; }`, `public ConsentRequestKind RequestKind { get; init; } = ConsentRequestKind.Consent;`, `public Guid? ProfileId { get; init; }`.
4. **Repository Read/Write Wiring (`GovernanceRepository.Consent.cs`, `GovernanceRepository.ConsentGrants.cs`, `GovernanceRepository.ConsentSql.cs`):**
   - Persist and hydrate the new `CONSENTS` (`grant_source`, `grant_record_seq`, `profile_assignment_id`, `expiry_notice_sent_at`) and `CONSENT_REQUESTS` (`version`, `workflow_mode`, `external_system_id`, `external_reference`, `requested_valid_from`, `updated_at`, `request_kind`, `profile_id`) columns.
   - Ensure `DeclarativeConsentApplyService` sets `GrantSource = ConsentGrantSource.Declarative` on consents it creates so `#671` (`WP-OI-CAT-4`) is ready to filter by `GrantSource == ConsentGrantSource.Declarative`.

### Required Unit & Architecture Tests
- `tests/Autheris.Tests.Unit/Governance/Grants/Records/GrantRecordCanonicalizerTests.cs`: `Canonical_Json_IsStable_SortedKeys_UtcIso8601`.
- `tests/Autheris.Tests.Unit/Persistence/Migrations/ConsentApiMigrationStep0014Tests.cs`:
  - `Migration_0014_Passes_StepSqlLint_And_Matches_ShapeJson`
  - `Consents_GrantSource_DefaultsTo_ConsentRequest_NeverDeclarative` (`[Trait("Category","Security")]`, `SEC-OI-671-1`)
  - `ConsentRequest_And_Consent_NewColumns_RoundTrip`.

### Filtered Local Verification Commands
```bash
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~GrantRecordCanonicalizer|FullyQualifiedName~ConsentApiMigrationStep0014|FullyQualifiedName~StepSqlLint|FullyQualifiedName~MigrationShape"
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build
```

---

## Brief 2 — `WP-SIMPL-41 PR 3`: Authorized Discovery Core, Channels (GraphQL/REST/MCP/Search), Audit & Rate Limits

- **Worktree:** `/work/wt/simpl-41-pr3-discovery-core`
- **Branch:** `feat/simpl-41-pr3-discovery-core` (branch from `origin/main` after Wave 3 `WP-SIMPL-41 PR 2` merges)
- **Plan & Security References:**
  - `docs/plans/simplification/WP-SIMPL-41-authorized-data-discovery.md` (Section 5 Steps 5, 6, 10, Section 6 Items 2–6 & 11–12a, Section 13 `SEC-DISC-01..16`)

### Mandatory Security Conditions (`SEC-DISC-02..06`, `SEC-DISC-10..11`, `SEC-DISC-13`)
- **`SEC-DISC-02` (Single Decision Point):** `DiscoveryService` MUST obtain table and column access state ONLY from `IAccessDecisionPoint.DecideManyAsync(subject, objects, AccessOperation.Discover, AccessTransport.Discovery)` (one batch call per page). Never call `ConsentResolutionService`, `GranteeMatcher`, or compute access in discovery.
- **`SEC-DISC-03` (Hidden Objects & Columns):** Tables with `DiscoveryHidden = true` and columns with `DiscoveryHidden = true` are omitted from list, detail, GraphQL, REST, MCP, BM25 search index, and semantic embeddings. Unknown, out-of-tenant, and hidden tables MUST return byte-identical 404/null responses via the same code path (no early-return timing oracle). Hidden objects are visible with `IsHidden = true` ONLY to `GovernanceAdmin` of the tenant or the table's data owner/delegate (never via a delegated `Catalog.ReadAdmin` token scope alone).
- **`SEC-DISC-04` (No Predicate Disclosure):** `RowScope` reports only `Full` or `RowRestricted`; row-filter predicates and parameter values are NEVER included in any discovery DTO.
- **`SEC-DISC-05` & `SEC-DISC-13` (Fail-Closed Tenant Scope & Degraded Overlay):** Always apply `DropForeignVirtualTables` first. Unowned tables (`OwnerTenantId` empty) are in scope ONLY for the catalog-owner tenant or `SharedTables` or when the tenant holds an active Allow consent. Any exception or degraded flag-overlay (`RowOverlay.Degraded`) returns empty/null and increments `discovery_failures_total` (never falls back to an unfiltered list).
- **`SEC-DISC-10` & `SEC-DISC-11` (Audit Query Hash, Rate Limit & Owner Privacy):** Every discovery read writes `CATALOG_READ` (`Summarized`) with `surface`, `objectCount`, `objectIds` (max 50), `queryHash` (SHA-256 of normalized query text, NEVER raw query text), and `tenant`. Rate limit per `(tenant, sid)` via `IRateLimiterService` (`discovery` scope, `RequestsPerMinute` default 120). `DiscoveryOwner` exposes ONLY `(DisplayName, Email)` of active owners — NEVER `AdSid` or `AdAccount`.

### Exact Changes to Make
1. **Discovery Core (`src/Autheris.Application/Catalog/Discovery/`):**
   - `IDiscoveryService.cs`: `ListAsync(DiscoveryQuery, CatalogCaller, CancellationToken)`, `GetAsync(TableIdentifier, CatalogCaller, CancellationToken)`, `SearchAsync(string, DiscoverySearchOptions, CatalogCaller, CancellationToken)`.
   - `DiscoveryTenantScope.cs` (static, pure): `IsInScope(...)` + `DropForeignVirtualTables` + hidden-table filtering (with admin/owner `IsHidden = true` rule).
   - `DiscoveryAccessMapper.cs` (static, pure): maps `TableAccessDecision` from `IAccessDecisionPoint.DecideManyAsync(..., AccessOperation.Discover, AccessTransport.Discovery)` to `(DiscoveryAccessState Access, DiscoveryRowScope? RowScope, IReadOnlyList<DiscoveryColumn> Columns)`:
     - `Clear` -> `DiscoveryColumnState.Clear`
     - `Mask` -> `DiscoveryColumnState.Masked` (`ClearInSomeRows = false`)
     - `RowDependent` -> `DiscoveryColumnState.Masked` with `ClearInSomeRows = true` (OD-41)
     - `Deny` -> `DiscoveryColumnState.NoAccess` (column is listed as `NoAccess`, NOT omitted unless ` column.DiscoveryHidden == true`).
     - When decision is not allowed: check pending request (`DiscoveryAccessState.Requested`) or recently expired grant within 90 days (`DiscoveryAccessState.Expired`, via `IConsentRepository.GetRecentlyExpiredTableIdsAsync`), else `DiscoveryAccessState.None` (all columns `NoAccess`).
   - `DiscoveryCallerGate.cs` & `CatalogDiscoveryOptionsValidator.cs`: enforce authenticated non-anonymous caller (`!= ANONYMOUS_MCP_CLIENT`), resolved tenant, optional `RequiredRoles` / `RequiredScopes` / `TenantOverrides`, hard caps (`MaxSampleRows <= 50`, `MaxArtifactBytes <= 1048576`), rate limiting via `IRateLimiterService`.
   - `DiscoveryService.cs`: orchestrates repository load (with `DatasetCatalogStore` overlay), `DiscoveryTenantScope`, hidden-column stripping (columns, stats, samples), batch owner lookup (`GetDataOwnersForTableAsync`, active only, `(DisplayName, Email)`), single `IAccessDecisionPoint.DecideManyAsync` call per page, `IDiscoveryArtifactStore.GetAsync`, per-scope memoization, `CATALOG_READ` audit with `queryHash`, and fail-closed error handling.
2. **Channel Wiring:**
   - **REST & Application (`src/Autheris.Application/Catalog/Services/CatalogDiscoveryService.cs:89-320`, `src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs`):** Route `ListDatasetsAsync`, `GetDatasetDetailAsync`, `SearchCatalogAsync`, and `SearchCatalogDetailedAsync` through `IDiscoveryService`. Ensure 404 responses for unknown, foreign, and hidden tables are identical.
   - **GraphQL (`src/Autheris.GraphQL/Types/Query.Catalog.cs:110-220`):** Delegate `GetCatalogAsync` to `IDiscoveryService.ListAsync`; extend `TableMetadataDto` with `kind`, `owners`, `access`, `rowScope`, `columns` (`DiscoveryColumn`), `statistics`, `samplesAvailable`; add `catalogObject(domain, schema, name)`.
   - **MCP (`src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs:188,395`):** Route `ListDatasetsAsync`, `DescribeDatasetAsync`, `search_catalog`, and `find_relevant_tables` through `IDiscoveryService`.
   - **Search (`src/Autheris.Application/Catalog/Search/CatalogSearchEngine.cs`, `CatalogSearchWarmupService.cs`):** Exclude `DiscoveryHidden` tables and columns from BM25 documents and semantic embeddings; invalidate/rebuild on `CATALOG_VISIBILITY_CHANGED`; post-filter search hits through `IDiscoveryService` before computing counts, facets, and suggestions.

### Required Unit & Architecture Tests
- `tests/Autheris.Tests.Unit/Catalog/Discovery/DiscoveryTenantScopeTests.cs` (`SEC-DISC-05`)
- `tests/Autheris.Tests.Unit/Catalog/Discovery/DiscoveryHiddenTests.cs` (`SEC-DISC-03`, `SEC-DISC-13`)
- `tests/Autheris.Tests.Unit/Catalog/Discovery/DiscoveryAccessMapperTests.cs` & `DiscoveryParityTests.cs` (`SEC-DISC-02`, `SEC-DISC-04`, `SEC-DISC-11`)
- `tests/Autheris.Tests.Unit/Catalog/Discovery/DiscoveryCallerGateTests.cs` (`SEC-DISC-01`, `SEC-DISC-13`)
- `tests/Autheris.Tests.Unit/Catalog/Discovery/DiscoveryChannelParityTests.cs`
- `tests/Autheris.Tests.Unit/Catalog/Discovery/DiscoveryAuditAndRateLimitTests.cs` (`SEC-DISC-10`)
- `tests/Autheris.Tests.Unit/Catalog/Discovery/DiscoverySecurityTests.cs` (mandatory names from spec Section 6.12a)
- `tests/Autheris.Tests.Architecture/DiscoveryArchitectureTests.cs`: `Discovery_DoesNotReference_ConsentResolution_GranteeMatcher_Or_GrantStoreForAllow` & `DiscoveryNamespace_DoesNotReference_DataSourceExecution` (`SEC-DISC-02`, `SEC-DISC-06`).

### Filtered Local Verification Commands
```bash
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~Discovery"
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build --filter "FullyQualifiedName~Discovery"
```

---

## Brief 3 — `WP-OI-SQL-479-A`: Dialect Semantics PR A (Oracle `MOD`/`TRUNC`, Databricks `DIV`, `IntegralOperandClassifier`)

- **Worktree:** `/work/wt/oi-sql-479a-div-mod`
- **Branch:** `feat/oi-sql-479a-div-mod` (branch from `origin/main` after Wave 3 `WP-OI-SQL-486` merges)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-sql-odata-engine.md` (`WP-OI-SQL-479` PR A: Oracle `%` and integer `/`, Databricks integer `/`)
  - `docs/plans/2026-11-13-sec-issues-group-5.md` (`SEC-OI-479-1..2`) & `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SEC5-2`)

### Mandatory Security Conditions (`SEC-OI-479-1..2` & `Q-SEC5-2`)
- **`SEC-OI-479-1` & `Q-SEC5-2` (Zero AST Duplication & Emitted SQL Length Cap):**
  - Every dialect rewrite of `/` or `%` (`MOD(a, b)`, `TRUNC(a / b)`, `a DIV b`) MUST emit each operand (`Left` and `Right`) **at most once** (no operand duplication that could cause quadratic blowup on nested arithmetic).
  - In `GovernedSqlCompiler.CompileCore` (`src/TrinoSqlEngine/GovernedSqlCompiler.cs:253-262`), in addition to `PolicyExpansionFactor`, enforce an absolute emitted SQL length ceiling of `4 * _engine.MaxQueryLength` (`SqlLimitKind.EmittedSqlLength` or `PolicyExpansionFactor`) so dialect function wrapping on deeply nested expressions never produces unbounded SQL text.
- **`SEC-OI-479-2` (Template Cache Key & Dependency Fingerprint Binding):**
  - Because `BinaryExpression.IsIntegralDivision` depends on the resolved catalog column types of `Left` and `Right`, `TypedPolicyContext.Fingerprint(entry)` / `CompileCacheKey` MUST include column data types (verify and pin with a test that changing a column's catalog type from `INTEGER` to `DECIMAL`/`DOUBLE` invalidates the cached template and switches emission between `TRUNC(a / b)` / `a DIV b` and `a / b`).

### Exact Changes to Make
1. **AST Node (`src/TrinoSqlEngine/Ast/Nodes/Expressions.cs:97-100`):**
   - Extend `BinaryExpression`:
     ```csharp
     public sealed record BinaryExpression(
         Expression Left,
         BinaryOperator Operator,
         Expression Right,
         bool IsIntegralDivision = false) : Expression;
     ```
2. **Integral Operand Classifier (`src/TrinoSqlEngine/Ast/Visitors/IntegralOperandClassifier.cs`, new, wired in `GovernedSqlCompiler.cs:213`):**
   - Create an AST visitor `IntegralOperandClassifier` that runs on the user/annotated AST (alongside `LiteralColumnTypeAnnotator` in `GovernedSqlCompiler.cs:213`) and sets `IsIntegralDivision = true` on `BinaryExpression` where `Operator == BinaryOperator.Divide` and **both** `Left` and `Right` are provably integral:
     - `LiteralExpression { Type: LiteralType.Integer }`
     - `ColumnReference` resolving in `TypedPolicyContext.Catalog` to an integral SQL type (`TINYINT`, `SMALLINT`, `INT`, `INTEGER`, `BIGINT`, `INT2`, `INT4`, `INT8`, or `NUMBER(p, 0)` / `DECIMAL(p, 0)` with scale `0`)
     - `CastExpression` whose target type is one of the integral types above
     - `UnaryExpression { Operator: UnaryOperator.Negate }` whose operand is integral
     - `ParenthesizedExpression` whose inner expression is integral
     - `BinaryExpression` with `Operator` in `{ Add, Subtract, Multiply, Modulo }` (or `Divide` with `IsIntegralDivision == true`) where both operands are integral.
   - Any unknown column type, parameter without integral annotation, floating-point, or decimal with scale > 0 classifies as `false` (emits standard float/decimal `/`).
3. **Oracle Dialect Emission (`src/TrinoSqlEngine/Ast/Generators/OracleDialectGenerator.cs` & `SqlDialectGeneratorBase.Expressions.cs:255-282`):**
   - Override `FormatBinaryExpression` in `OracleDialectGenerator`:
     - When `b.Operator == BinaryOperator.Modulo`: emit `MOD(` + `GenerateExpression(b.Left)` + `, ` + `GenerateExpression(b.Right)` + `)` (Oracle has no `%` operator; `ORA-00911`).
     - When `b.Operator == BinaryOperator.Divide && b.IsIntegralDivision`: emit `TRUNC(` + `FormatStandardBinary(b)` + `)` so Oracle `NUMBER` division truncates toward zero like Trino integer division.
4. **Databricks Dialect Emission (`src/TrinoSqlEngine/Ast/Generators/DatabricksDialectGenerator.cs`):**
   - Override `FormatBinaryExpression` in `DatabricksDialectGenerator`:
     - When `b.Operator == BinaryOperator.Divide && b.IsIntegralDivision`: emit `Left DIV Right` (with appropriate parenthesization via `NeedsParentheses`), because Spark SQL `/` always returns `DOUBLE`/`DECIMAL`.
5. **Emitted SQL Cap (`src/TrinoSqlEngine/GovernedSqlCompiler.cs:256-261`):**
   - Enforce `long hardCap = 4L * _engine.MaxQueryLength;` on `compiled.Sql.Length` (`SEC-OI-479-1` / `Q-SEC5-2`).

### Required Unit Tests (`tests/TrinoSqlEngine.Tests/`)
- `DialectSemantics/OracleModuloAndIntegerDivisionTests.cs`:
  - `Oracle_Modulo_Emits_MOD_Function`
  - `Oracle_IntegerLiteralDivision_Emits_TRUNC`
  - `Oracle_IntegerColumnDivision_Emits_TRUNC`
  - `Oracle_DecimalOrFloatDivision_Emits_StandardSlash`
  - `Oracle_NestedModuloAndDivision_EmitsEachOperandOnce` (`[Trait("Category","Security")]`, `SEC-OI-479-1`)
- `DialectSemantics/DatabricksIntegerDivisionTests.cs`:
  - `Databricks_IntegerDivision_Emits_DIV`
  - `Databricks_DecimalOrFloatDivision_Emits_Slash`
  - `Databricks_CatalogColumnTypeChange_IntToDouble_InvalidatesCacheAndSwitchesFromDivToSlash` (`[Trait("Category","Security")]`, `SEC-OI-479-2`)
  - `DeepNestedDivision_ExceedingFourTimesMaxQueryLength_ThrowsSqlLimitExceeded` (`[Trait("Category","Security")]`, `SEC-OI-479-1`).

### Filtered Local Verification Commands
```bash
dotnet build src/TrinoSqlEngine/TrinoSqlEngine.csproj -c Release -warnaserror
dotnet test tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~OracleModulo|FullyQualifiedName~DatabricksIntegerDivision"
```

---

## Brief 4 — `WP-OI-SCALE-2 (#673)`: Batch Table Metadata Upsert (`UpsertTableMetadataBatchAsync`) & Ingestion Callers

- **Worktree:** `/work/wt/oi-scale-673-batch-upsert`
- **Branch:** `feat/oi-scale-673-batch-upsert` (branch from `origin/main`)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-scale.md` (`WP-OI-SCALE-2` / `#673`: Repository batch upsert for table metadata)
  - `docs/plans/2026-11-13-sec-issues-group-7.md` (`SEC-OI-673-1..5`) & `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SCALE-2`, `Q-SEC7-2`)

### Mandatory Security & Architecture Conditions (`SEC-OI-673-1..5`)
- **`SEC-OI-673-1` (Identical Per-Item Validation Before DB Work):** Every `TableMetadata` item in the batch MUST run the exact same pre-flight validations as single-table upsert (`CatalogSourceNameValidator.Validate(item)`, `item.ValidateRequiredFilterColumns()`, `SemanticVirtualTables.EnsureNamespaceRespected(item)`, and `TableKeyDefinitionValidator.Validate(item)` when `item.ReplaceKeyDefinition` is true) BEFORE opening the transaction. One invalid item rejects the call before any DB mutation.
- **`SEC-OI-673-2` (Deterministic Lock Order & Chunking):** Deduplicate by canonical `TableIdentifier` (last wins or reject duplicate in batch deterministically) and sort items by `(Domain, Schema, TableName)` (`StringComparer.OrdinalIgnoreCase`) before locking/updating rows so concurrent batches cannot deadlock. Process in chunks of at most **500 tables** per transaction (`Q-SCALE-2`, `MaxBatchUpsertChunkSize = 500`), respecting dialect parameter budgets (`SqlServer` < 2,000 params per command, `PostgreSql` < 30,000 params per command).
- **`SEC-OI-673-3` (Restrictive vs. Additive Classification & Immediate Flush):** Within each chunk, compare stored projections before and after (`CatalogTablePersistedProjection.Compare`). If ANY table in the chunk has `CatalogWriteClassification.Restrictive` (e.g., table deactivated, visibility reduced, masking rule added/tightened), the chunk is classified as `Restrictive`: bump `CATALOG_STATE` version immediately inside the chunk transaction and, if a `CatalogWriteScope` is active, call `await writeScope.FlushPendingAsync(ct)` after commit (`#672` invariant). Only purely `Additive` chunks defer version bumps to an active `CatalogWriteScope`.
- **`SEC-OI-673-4` (Per-Table Epoch & Cache Invalidation):** Every changed/created table in the batch MUST have its per-table epoch incremented inside the transaction (`IncrementTableEpochInternalAsync`) and its cache invalidated after commit (`InvalidateTableEpochAsync`). Unchanged tables (`CatalogWriteClassification.Unchanged`) do not bump epochs or `CATALOG_STATE`.
- **`SEC-OI-673-5` (Parameterized SQL Only):** Use `GovernanceTable` enum and `AddBound` parameters exclusively; zero string interpolation of identifiers or metadata values.

### Exact Changes to Make
1. **Interface (`src/Autheris.Application/Common/Interfaces/IGovernanceRepository.cs:9-25` & `AccessPlanningNullStores.cs:15`):**
   - Add to `ITableMetadataRepository`:
     ```csharp
     Task<IReadOnlyList<Autheris.Application.Catalog.CatalogTableUpsertResult>> UpsertTableMetadataBatchAsync(
         IReadOnlyList<TableMetadata> batch,
         CancellationToken ct = default);
     ```
   - Provide a default interface implementation (or null-store implementation in `AccessPlanningNullStores.cs`) for test doubles, and implement the real batched path in `GovernanceRepository.Catalog.cs`.
2. **Repository Implementation (`src/Autheris.Infrastructure/Persistence/GovernanceRepository.Catalog.cs:354-545`):**
   - Refactor `UpsertTableMetadataWithResultAsync(TableMetadata metadata, CancellationToken ct)` (`:357`) to delegate to `(await UpsertTableMetadataBatchAsync([metadata], ct).ConfigureAwait(false))[0]` so single-table and batch upserts share one authoritative code path.
   - Implement `UpsertTableMetadataBatchAsync`:
     - Validate all items up front (`SEC-OI-673-1`). Return `[]` immediately if `batch.Count == 0`.
     - Sort items deterministically by `(Identifier.Domain, Identifier.Schema, Identifier.TableName)` and chunk by `500` (`SEC-OI-673-2`).
     - Per chunk, open connection + transaction, lock and read existing table rows/projections, upsert changed/created tables, columns, masking rules, default visibility for new tables, and key definitions (when `ReplaceKeyDefinition`), increment per-table epochs for changed tables, bump `CATALOG_STATE` once for the chunk (immediately if no `CatalogWriteScope` or if any item in the chunk is `Restrictive`; otherwise record aggregated additive deltas on `writeScope`), commit, and invalidate per-table caches.
3. **Ingestion Callers (`DbtMetadataIngestionService.cs`, `DbtSemanticManifestIngestionService.cs`, `OpenMetadataSyncService.cs`, `DataCatalogSyncService.cs`, `OpenApiIngestionService.cs`):**
   - Replace per-table `await ...UpsertTableMetadataAsync(...)` loops inside catalog sync/ingestion batches with `UpsertTableMetadataBatchAsync` (inside their existing `BeginCatalogWriteScope()`).

### Required Unit Tests
- `tests/Autheris.Tests.Unit/Persistence/GovernanceRepositoryBatchUpsertTests.cs`:
  - `BatchUpsert_EmptyBatch_ReturnsEmpty_NoDbCalls`
  - `BatchUpsert_InvalidItem_FailsBeforeAnyMutation` (`[Trait("Category","Security")]`, `SEC-OI-673-1`)
  - `BatchUpsert_SortsDeterministically_And_ChunksAt500` (`SEC-OI-673-2`)
  - `BatchUpsert_AllUnchanged_DoesNotBumpCatalogVersionOrTableEpochs`
  - `BatchUpsert_AdditiveInsideWriteScope_CoalescesVersionBump`
  - `BatchUpsert_MixedWithOneRestrictiveItem_InsideWriteScope_FlushesImmediately` (`[Trait("Category","Security")]`, `SEC-OI-673-3`)
  - `SingleTableUpsert_DelegatesToOneElementBatch_PreservesExactSemantics`.

### Filtered Local Verification Commands
```bash
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~BatchUpsert|FullyQualifiedName~CatalogWriteScope"
```

---

## Wave 5 Queue Summary (Next Ready Packages After Wave 4)

| Priority | Work Package | Prerequisite from Wave 3 / Wave 4 | DDL Slot | Summary |
| :--- | :--- | :--- | :--- | :--- |
| **W5-1 (DDL)** | **`WP-OI-SCALE-4 PR 2 (#675)`** (`CATALOG_WRITE_LOG` + cursor delta loader) | Wave 3 `WP-OI-SCALE-4 PR 1` + Wave 4 `0014` | **`0015_catalog_write_log`** | Adds `CATALOG_WRITE_LOG` table (`GovernanceTable.CatalogWriteLog`), appends changed table IDs in `GovernanceRepository.Catalog.cs`, and loads only changed tables in `CatalogSnapshotProvider` (`SEC-OI-675-2..4`, `MaxDeltaBatchSize = 500`, fallback to full reload on gap/overflow). |
| **W5-2** | **`WP-OI-CAT-4 (#671)`** (Declarative consent prune scoped to `DECLARATIVE`) | Wave 4 Brief 1 (`WP-SIMPL-47 PR 1` `CONSENTS.grant_source`) | None | Updates `DeclarativeConsentApplyService` so `prune` only revokes consents with `GrantSource == ConsentGrantSource.Declarative` (`SEC-OI-671-1..2`). |
| **W5-3** | **`WP-OI-WEB-681 (#681)` & `WP-OI-WEB-682-B (#682-B)`** (Discovery column descriptions, PII flags & `requestAccess` link) | Wave 4 Brief 2 (`WP-SIMPL-41 PR 3`) | None | Verifies/wires column descriptions, PII flags, masking state, and consent request links across REST/GraphQL/MCP discovery (`SEC-OI-681-1`, `SEC-OI-682-1..3`). |
| **W5-4** | **`WP-OI-SQL-478-B (#478 OData)`** & **`WP-OI-SQL-479-B / D / C`** | Wave 2 `#478-A` & Wave 4 `#479-A` | None | Iterative OData `$filter` binary chain parser (`#478-B`), dialect capability matrix (`#479-B`), `CompileLimits` (`#479-D`), and DuckDB/Snowflake/BigQuery generators (`#479-C`). |
| **W5-5** | **`WP-SIMPL-45 PR 5`** (Governed Write Execution Wiring Across REST/OData/GraphQL/MCP) | Wave 3 `WP-SIMPL-45 PR 4` | None | Completes governed DML write execution wiring and end-to-end security tests (`SEC-SIMPL-45-01..10`). |
| **W5-6** | **`WP-OI-SRCH-625 / 626 / 628a-d`** & **`WP-OI-SCALE-6 (#677)`** | PR `#734` (`#624`) & Wave 4 Brief 2 (`WP-SIMPL-41 PR 3`) | None | Search synonyms (`#625`), compound token splitting (`#626`), semantic vector search (`#628a-d`, table vector strictly excludes column names/descriptions per `SEC-OI-628-5..6`), and caller-filtered paged catalog queries (`#677`, `SEC-OI-677-1..3`). |
| **W5-7** | **`WP-SIMPL-47 PR 2`** (`IGrantWriter` & Signed Hash-Chained `GRANT_RECORDS`) | Wave 4 Brief 1 (`WP-SIMPL-47 PR 1`) | None | Routes all `CONSENTS` writes through `IGrantWriter` with ECDSA P-256 signatures and per-tenant hash chain (`SEC-SIMPL-47-03`, `SEC-SIMPL-47-09`). |
