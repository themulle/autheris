# PLAN-SEC-210: SQL Server Windows Auth NTLM/EPA Diagnostics, v1.1.x Governance DB Upgrade & Container Release Gate

**Document ID:** `PLAN-SEC-210`  
**Date:** 2026-10-19  
**Status:** APPROVED (Phase 2 Architectural Implementation Plan)  
**Author:** Solution Architect (`csharp-architect`)  
**Input PRD:** [`docs/plans/2026-10-19-req-issue-210-sqlserver-windows-auth-governance-upgrade.md`](file:///root/autheris/docs/plans/2026-10-19-req-issue-210-sqlserver-windows-auth-governance-upgrade.md) (`REQ-SEC-210`)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](file:///root/autheris/docs/plans/00-master-plan-overview.md)  
**Parent Feature:** `F-AUTH-04`, `F-API-09`, `F-GOV-08`, `REQ-CDC-99`  
**GitHub Issue:** #210  
**Priority:** Wave 1 (Security & Release Blockers), Rank 1.5  

---

## 1. Executive Summary & Problem Decomposition

Autheris v1.2.0 introduces enhanced governance tables, semantic manifests, caller attribute row filters, and CDC stream capabilities. However, three interconnected runtime and release blockers prevent enterprise adoption and block CI container image publication to GHCR:

1. **SQL Server Windows Authentication (NTLM) Failure Under EPA (Error 18452):**
   - In Linux containerized environments, .NET's managed NTLM stack cannot emit TLS Channel Binding Tokens (CBT) across non-Windows host boundaries.
   - When SQL Server enforces Extended Protection for Authentication (EPA = `Required`), authentication fails with `SqlNumber = 18452` (*"Login failed. The login is from an untrusted domain and cannot be used with Integrated authentication"*).
   - Currently, `SqlServerWindowsAuthConfigurator.ClassifyOpenException` groups 18452 and 18456 generically without exposing `SqlError.State` or CBT guidance, and pre-flight connectivity testing (`POST /api/v1/catalog/datasources/{id}/test`) fails with an unhandled `500 KeyNotFoundException` when tables have not yet been cataloged.
2. **Existing `governance.db` v1.1.x Upgrade Drift:**
   - Incremental v1.2.0 tables (`AUDIT_PURGE_CHECKPOINTS`, `CALLER_ATTRIBUTES`, `SEMANTIC_MANIFESTS`, `TABLE_RELATION_KEYS`, `autheris_cdc_*`) and normalized columns (`CONSENTS.grantee_sid_norm`, `ACCESS_PROFILE_ASSIGNMENTS.subject_norm`) were added to the `0001` baseline.
   - Existing v1.1.x databases have migration 1 already recorded, skipping baseline DDL; `GovernanceSchemaShapeVerifier` detects schema drift against `governance-shape.sqlite.json` and immediately halts container startup.
3. **CI Container Publication Gate Failures:**
   - Multi-arch container release in CI is blocked by three failing tests:
     - `SupplyChainAndDeploymentSecurityTests.BenchmarkDockerfiles_AreHardened`: `Dockerfile.gql` specifies `ASPNETCORE_ENVIRONMENT=Development` instead of `Benchmark`.
     - `CutoverArchitectureTests.NoCommandTextAssignmentOutsideTheAllowlist`: `Autheris.Extensions.Cdc.CustomQuery.CustomQueryPollerService` is missing from the architectural allowlist.
     - DI resolution failure: Host DI container omits registrations for `ICdcStreamStore` and `ICdcStreamKeyStore`, causing `CdcStreamRetentionJob` and `CdcStreamAdminService` resolution errors during container bootstrap.

---

## 2. Architecture Decisions (D-1 .. D-5)

- **D-1 (Error 18452 vs. 18456 Diagnostic Differentiation):**  
  *Decision:* Differentiate `SqlNumber == 18452` from `18456` in [`SqlServerWindowsAuthConfigurator.ClassifyOpenException`](file:///root/autheris/src/Autheris.Infrastructure/Persistence/SqlServerWindowsAuthConfigurator.cs#L525). Capture `SqlError.State`, `SqlError.Number`, and `SqlError.Class` in structured exception fields. When 18452 occurs in container environments, flag EPA Channel-Binding Token (CBT) mismatch as the primary suspect and provide actionable guidance (set EPA to `Allowed`/`Off`, use Kerberos Keytab via `F-AUTH-04`, or use SQL Auth). Retain category `LoginRejected` so [`SqlLoginCircuitBreaker`](file:///root/autheris/src/Autheris.Infrastructure/Persistence/SqlLoginCircuitBreaker.cs#L14) continues protecting Active Directory accounts from lockout.
- **D-2 (Dual-Path Datasource Pre-Flight Testing):**  
  *Decision:* In [`DatasourceTestingService.TestDatasourceAsync`](file:///root/autheris/src/Autheris.Application/Catalog/Services/DatasourceTestingService.cs#L63), if `_metadataRepository.GetAllTablesAsync` returns no matching table for `datasourceId`, query `_gateway.Value.DataSources.Connections[datasourceId]`. If configured, run direct connection testing via [`ISqlConnectionFactory.CreateOpenConnectionAsync`](file:///root/autheris/src/Autheris.Application/Interfaces/ISqlConnectionFactory.cs) without requiring catalog tables. If neither catalog nor configuration contains the ID, return HTTP 404 / structured failure result instead of throwing an unhandled `KeyNotFoundException` (500).
- **D-3 (Non-Destructive Forward Upgrade Migration Script):**  
  *Decision:* Author `tools/migrations/upgrade_governance_v1_1_to_v1_2.sql` with idempotent `CREATE TABLE IF NOT EXISTS`, column additions via SQLite table alteration, backfilling (`grantee_sid_norm = grantee_sid`, `subject_norm = subject`), and updating `autheris_schema_migrations` with step 1 checksum reconciliation. This allows operators to upgrade existing databases in-place, satisfying [`GovernanceSchemaShapeVerifier`](file:///root/autheris/src/Autheris.Infrastructure/Persistence/Migrations/Shape/GovernanceSchemaShapeVerifier.cs) with zero data loss.
- **D-4 (CI Container Build Hardening Parity):**  
  *Decision:* Update `benchmarks/load/docker/Dockerfile.gql` to set `ENV ASPNETCORE_ENVIRONMENT=Benchmark`, purge `appsettings.Development.json`, and retain `USER 10001:10001`. Add `CustomQueryPollerService` to the Cecil-based IL command text allowlist in [`CutoverArchitectureTests.cs`](file:///root/autheris/tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs#L383).
- **D-5 (Two-Tiered CDC Stream Store DI Registration):**  
  *Decision:* In `GatewayGovernanceServiceExtensions.RegisterGovernanceAggregateStores`, enforce `where TRepository : ..., ICdcStreamStore, ICdcStreamKeyStore` and register both interfaces mapped to `TRepository`. In `CdcStreamServiceExtensions.AddAutherisCdcStream`, register `services.TryAddSingleton<InMemoryCdcStreamStore>()` and mapped store interfaces as a resilient fallback for non-governance hosts and standalone testing.

---

## 3. Work Package Implementation Plan

```
+----------------------------------------------------------------------------------------------------+
|                                      Issue #210 Architecture Plan                                  |
+----------------------------------------------------------------------------------------------------+
|                                                                                                    |
|  WP-210-1: SQL Server Windows Auth & EPA Diagnostics                                              |
|  ├── SqlServerWindowsAuthConfigurator.ClassifyOpenException                                        |
|  │   ├── Extract SqlError.State, Number, Class via FindSqlException                                |
|  │   ├── Branch 18452: Flag EPA CBT mismatch / untrusted domain + mitigation guide                 |
|  │   └── Branch 18456: Flag invalid credentials / account lockout warning                          |
|  └── Documentation: Update docs/features/f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md         |
|                                                                                                    |
|  WP-210-2: Datasource Pre-Flight Connectivity Testing                                              |
|  └── DatasourceTestingService.TestDatasourceAsync                                                  |
|      ├── Step 1: Catalog lookup via ITableMetadataRepository.GetAllTablesAsync                     |
|      ├── Step 2: Fallback lookup via GatewayOptions.DataSources.Connections[id]                    |
|      ├── Step 3: Direct connection probe via ISqlConnectionFactory.CreateOpenConnectionAsync      |
|      └── Step 4: Graceful 404 / structured error if missing (eliminates 500 KeyNotFoundException) |
|                                                                                                    |
|  WP-210-3: Governance DB Forward Upgrade Script (v1.1.x -> v1.2.x)                                 |
|  ├── tools/migrations/upgrade_governance_v1_1_to_v1_2.sql                                          |
|  │   ├── CREATE TABLE IF NOT EXISTS (AUDIT_PURGE_CHECKPOINTS, CALLER_ATTRIBUTES, etc.)             |
|  │   ├── ALTER TABLE ADD COLUMN (CONSENTS, ACCESS_PROFILE_ASSIGNMENTS, TABLES, etc.)               |
|  │   ├── Backfill grantee_sid_norm, subject_norm                                                   |
|  │   └── Update autheris_schema_migrations version record                                          |
|  └── Verification: Passes GovernanceSchemaShapeVerifier with zero drift                            |
|                                                                                                    |
|  WP-210-4: CI & Container Build Gate Clearance                                                     |
|  ├── Dockerfile.gql: ENV ASPNETCORE_ENVIRONMENT=Benchmark                                          |
|  ├── CutoverArchitectureTests: Allowlist CustomQueryPollerService                                  |
|  └── GatewayGovernanceServiceExtensions: Register ICdcStreamStore & ICdcStreamKeyStore             |
+----------------------------------------------------------------------------------------------------+
```

### 3.1 WP-210-1: SQL Server Connection Error Logging & Diagnostic State
- **Files Modified:**
  - [`src/Autheris.Infrastructure/Persistence/SqlServerWindowsAuthConfigurator.cs`](file:///root/autheris/src/Autheris.Infrastructure/Persistence/SqlServerWindowsAuthConfigurator.cs)
  - [`src/Autheris.Infrastructure/Persistence/SqlServerWindowsAuthException.cs`](file:///root/autheris/src/Autheris.Infrastructure/Persistence/SqlServerWindowsAuthException.cs)
  - [`docs/features/f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md`](file:///root/autheris/docs/features/f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md)
- **Detailed Design:**
  1. In `SqlServerWindowsAuthConfigurator.ClassifyOpenException`:
     - Inspect `sqlEx.Number`:
       - **When `sqlNumber == 18452`:**
         ```csharp
         bool isManagedNtlm = managedNtlmUsed || ManagedNtlmPolicy.Resolve(options.WindowsAuth?.UseManagedNtlm, getEnvironmentVariable);
         string epaHint = isManagedNtlm
             ? "In container/Linux environments using managed NTLM, this error frequently indicates Extended Protection for Authentication (EPA / Channel Binding) is set to 'Required' on SQL Server. Managed NTLM cannot negotiate TLS Channel Binding across platform boundaries. Remediations: (1) Set EPA to 'Allowed' or 'Off' on SQL Server Configuration Manager, (2) Use Kerberos keytab authentication (F-AUTH-04), or (3) Use native SQL Server authentication with TLS."
             : "Verify that the Active Directory domain trust is functioning and that the NetBIOS domain name matches.";
         return new SqlServerWindowsAuthException(
             SqlServerWindowsAuthFailureCategory.LoginRejected,
             $"SQL Server Windows Authentication rejected the login (Error 18452, State {sqlState ?? 0}): Untrusted domain or EPA channel-binding token mismatch. {epaHint}",
             exception,
             sqlNumber, sqlState, sqlClass);
         ```
       - **When `sqlNumber == 18456`:**
         ```csharp
         return new SqlServerWindowsAuthException(
             SqlServerWindowsAuthFailureCategory.LoginRejected,
             $"SQL Server Windows Authentication login was rejected (Error 18456, State {sqlState ?? 0}). " +
             "WARNING: Repeated failed Windows authentication attempts will lock out the Active Directory service account! " +
             "Verify the domain name, username, password secret, and that the AD account has CONNECT/LOGIN permissions on SQL Server.",
             exception,
             sqlNumber, sqlState, sqlClass);
         ```
  2. Populate `DiagnosticFields["SqlState"]`, `DiagnosticFields["SqlNumber"]`, and `DiagnosticFields["SqlClass"]` so downstream loggers and telemetry can emit structured tags without leaking credentials.

### 3.2 WP-210-2: Datasource Pre-Flight Connectivity Testing Fix
- **Files Modified:**
  - [`src/Autheris.Application/Catalog/Services/DatasourceTestingService.cs`](file:///root/autheris/src/Autheris.Application/Catalog/Services/DatasourceTestingService.cs)
  - [`src/Autheris.Application/Catalog/Interfaces/IDatasourceTestingService.cs`](file:///root/autheris/src/Autheris.Application/Catalog/Interfaces/IDatasourceTestingService.cs)
- **Detailed Design:**
  1. In `DatasourceTestingService.TestDatasourceAsync`:
     - Keep catalog matching logic across `_metadataRepository.GetAllTablesAsync(ct)`.
     - When `matchedTable == null`:
       - Check `_gateway?.Value.DataSources?.Connections` for a key matching `datasourceId` (case-insensitive) or matching `connection.ServerName`.
       - If a configured connection is found:
         - Invoke `TestDirectSqlDatasourceAsync(datasourceId, connection, request, user, ct)`.
         - Derive `SqlSourceDescription` via `_sqlSourceDescriber?.Describe(connection)`.
         - Attempt connection via `_sqlConnectionFactory.CreateOpenConnectionAsync(connection, cts.Token)`.
         - Record audit log entry `AuditEventTypes.DatasourceTested` with target `datasource:{datasourceId}`.
         - Return `DatasourceTestResult(DatasourceId: datasourceId, Type: DataSourceType.Sql, IsSuccess: ..., ...)`.
       - If neither catalog table nor configured connection exists:
         - Do **not** throw `KeyNotFoundException`.
         - Return `new DatasourceTestResult(DatasourceId: datasourceId, Type: DataSourceType.Sql, IsSuccess: false, HttpStatusCode: 404, LatencyMs: 0, ErrorMessage: $"Datasource '{datasourceId}' was not found in catalog or gateway configuration.", Diagnostics: ...)` or let endpoint map to HTTP 404 ProblemDetails.

### 3.3 WP-210-3: Governance Database Forward Upgrade Script (v1.1.x -> v1.2.x)
- **Files Added / Modified:**
  - `tools/migrations/upgrade_governance_v1_1_to_v1_2.sql`
  - `docs/operations/governance-db-upgrade.md`
- **Detailed DDL Blueprint:**
  1. **Missing Table DDL:**
     - `AUDIT_PURGE_CHECKPOINTS` (first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature).
     - `CALLER_ATTRIBUTES`, `CALLER_ATTRIBUTE_SERVICE_SETS`, `CALLER_ATTRIBUTE_GENERATION`.
     - `SEMANTIC_MANIFESTS` (tenant_id, manifest_json, updated_at).
     - `TABLE_CATALOG_SETTINGS` (table_id, catalog_visibility, updated_at).
     - `TABLE_RELATION_KEYS` (relation_id, parent_column, child_column, ordinal).
     - `TABLE_ROW_FILTER_STRATEGIES` (table_id, evaluation_strategy, updated_at).
     - `TABLE_TAGS` (table_id, tag, created_at).
     - `autheris_cdc_watermarks`, `autheris_cdc_streams`, `autheris_cdc_stream_events`, `autheris_cdc_row_images`, `autheris_cdc_stream_keys`.
  2. **Schema Alterations & Column Additions:**
     - In SQLite, use safe `ALTER TABLE {tbl} ADD COLUMN {col} {type}`:
       - `TABLES`: `data_source_type INTEGER NOT NULL DEFAULT 0`, `http_endpoint_json TEXT`, `plugin_name TEXT`, `required_filter_columns_json TEXT`, `long_description TEXT`, `doc_source TEXT`.
       - `CONSENTS`: `grantee_sid_norm TEXT`, `renewal_count INTEGER NOT NULL DEFAULT 0`, `description TEXT`.
       - `CONSENT_REQUESTS`: `tenant_id TEXT`, `requester_identifiers_json TEXT`, `context_json TEXT`.
       - `CONSENT_ROW_FILTERS`: `filter_type INTEGER NOT NULL DEFAULT 0`, `dependent_table TEXT`, `dependent_table_alias TEXT`, `foreign_key_column TEXT`, `primary_key_column TEXT`, `subquery_predicate_json TEXT`, `target_temporal_column TEXT`, `dependent_valid_from_column TEXT`, `dependent_valid_to_column TEXT`, `target_table_alias TEXT`, `additional_hops_json TEXT`, `key_set_strategy INTEGER NULL`, `key_set_duration_seconds INTEGER NULL`, `key_set_max_keys INTEGER NULL`.
       - `ACCESS_PROFILE_ASSIGNMENTS`: `subject_norm TEXT`, `created_by TEXT`, `approved_by TEXT`, `approved_at TEXT`, `justification TEXT`, `kind TEXT NOT NULL DEFAULT 'Standard'`, `renewal_count INTEGER NOT NULL DEFAULT 0`.
  3. **Data Backfills & Integrity:**
     ```sql
     UPDATE CONSENTS SET grantee_sid_norm = UPPER(TRIM(grantee_sid)) WHERE grantee_sid_norm IS NULL AND grantee_sid IS NOT NULL;
     UPDATE ACCESS_PROFILE_ASSIGNMENTS SET subject_norm = UPPER(TRIM(subject)) WHERE subject_norm IS NULL AND subject IS NOT NULL;
     ```
  4. **Indexes & Version Table Alignment:**
     - Create indexes: `IX_ACCESS_PROFILE_ASSIGNMENTS_SUBJECT`, `IX_CONSENTS_GRANTEE_NORM`.
     - Reconcile `autheris_schema_migrations` checksum to ensure compatibility with `SqliteGovernanceMigrationRunner`.

### 3.4 WP-210-4: CI & Container Build Gate Clearance
- **Files Modified:**
  - [`benchmarks/load/docker/Dockerfile.gql`](file:///root/autheris/benchmarks/load/docker/Dockerfile.gql)
  - [`tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs`](file:///root/autheris/tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs)
  - [`src/Autheris.Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs`](file:///root/autheris/src/Autheris.Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs)
  - [`src/Autheris.Api/Extensions/DependencyInjection/CdcStreamServiceExtensions.cs`](file:///root/autheris/src/Autheris.Api/Extensions/DependencyInjection/CdcStreamServiceExtensions.cs)
- **Detailed Design:**
  1. In `benchmarks/load/docker/Dockerfile.gql`:
     - Change line 42: `ASPNETCORE_ENVIRONMENT=Benchmark \`
     - Remove line 54: `COPY ... appsettings.Development.json`
     - Preserves `USER 10001:10001` and `appsettings.Benchmark.json`.
  2. In `tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs`:
     - In `CommandTextAllowlist` array (around line 420), add:
       ```csharp
       ("Autheris.Extensions.Cdc.CustomQuery.CustomQueryPollerService", "CDC custom query poller: parameterized incremental change detection queries with bounded batch fetch"),
       ```
  3. In `GatewayGovernanceServiceExtensions.cs`:
     - Update `RegisterGovernanceAggregateStores`:
       ```csharp
       private static void RegisterGovernanceAggregateStores<TRepository, TMigrationRunner>(
           IServiceCollection services,
           bool cdcWebhooksEnabled)
           where TRepository : class,
               // ... existing interfaces ...
               ICdcStreamStore,
               ICdcStreamKeyStore
       ```
     - Register the singletons:
       ```csharp
       services.AddSingleton<ICdcStreamStore>(sp => (ICdcStreamStore)sp.GetRequiredService<TRepository>());
       services.AddSingleton<ICdcStreamKeyStore>(sp => (ICdcStreamKeyStore)sp.GetRequiredService<TRepository>());
       ```
  4. In `CdcStreamServiceExtensions.cs`:
     - Register fallback implementations using `TryAddSingleton`:
       ```csharp
       services.TryAddSingleton<InMemoryCdcStreamStore>();
       services.TryAddSingleton<ICdcStreamStore>(sp => sp.GetRequiredService<InMemoryCdcStreamStore>());
       services.TryAddSingleton<ICdcStreamKeyStore>(sp => sp.GetRequiredService<InMemoryCdcStreamStore>());
       ```

---

## 4. Layering & High-Performance Directives

1. **Clean Architecture Adherence:**
   - `Domain`: `DataSourceConnectionOptions`, `GatewayOptions`, `DataSourceType`. Zero external dependencies.
   - `Application`: `DatasourceTestingService` orchestrates tests using abstractions (`ISqlConnectionFactory`, `ITableMetadataRepository`).
   - `Infrastructure`: `SqlServerWindowsAuthConfigurator` handles driver-specific `SqlException` analysis; migration scripts provide relational persistence schemas.
   - `Api`: DI registrations in `GatewayGovernanceServiceExtensions` wire repositories to services without leaking provider internals.
2. **Zero-Allocation & Memory Efficiency:**
   - Exception classification performs zero-allocation span inspections on message strings (`AsSpan().IndexOfAny`) and reuses static string constants for error categories.
   - Forward migration script executes in a single SQLite transaction with minimal WAL journal overhead.
3. **Fail-Closed & Credential Hygiene:**
   - Connection strings and error messages are rigorously sanitized via `SecretScrubber.Redact`.
   - No plaintext passwords, Kerberos tickets, or NTLM tokens are logged.

---

## 5. Security & Verification Testing Plan

| Test Area | Target Suite | Acceptance Verification |
|---|---|---|
| **SQL Auth Classification** | `SqlServerWindowsAuthConfiguratorTests` | Verifies `ClassifyOpenException` distinguishes 18452 (EPA hint) from 18456 (bad password), capturing `SqlError.State`. |
| **Datasource Test Fallback** | `DatasourceTestingServiceSqlTests` | Verifies `TestDatasourceAsync` succeeds against `gateway_lwe` defined in options without catalog table entries; returns 404 for unknown IDs. |
| **Governance DB Upgrade** | `GovernanceSchemaVerificationTests` | Upgrades a v1.1.x SQLite database using `upgrade_governance_v1_1_to_v1_2.sql` and verifies `GovernanceSchemaShapeVerifier` passes. |
| **Architecture Gate** | `CutoverArchitectureTests` | `NoCommandTextAssignmentOutsideTheAllowlist` passes with `CustomQueryPollerService` included. |
| **Container Hardening** | `SupplyChainAndDeploymentSecurityTests` | `BenchmarkDockerfiles_AreHardened` passes verifying `ASPNETCORE_ENVIRONMENT=Benchmark`. |
| **Host DI Integrity** | `CompositionRootModularizationTests` | Verifies `CdcStreamRetentionJob` and `CdcStreamAdminService` resolve with `ICdcStreamStore` and `ICdcStreamKeyStore`. |

---

## 6. Phase 3 Security Review & Binding Conditions

**Reviewer:** Phase 3 Security Expert (`csharp-security-expert`)  
**Verdict:** **APPROVED WITH CONDITIONS**  
**Audit Scope:** Issue #210 (PLAN-SEC-210 / REQ-SEC-210)  

### 6.1 Binding Security Conditions

- **SEC-SEC210-01 (Credential Sanitization & Information Leakage Prevention):**  
  TDS `SqlError.State`, `SqlError.Number`, and `SqlError.Class` must be extracted purely as numeric values. Connection strings, user passwords, Kerberos credentials, or internal server environment paths must never be emitted into client HTTP responses or unredacted log entries. All client-facing error reasons must be sanitized through `SecretScrubber.Redact()`.
- **SEC-SEC210-02 (AD Account Lockout & Circuit Breaker Enforcement):**  
  Classified error 18452 (EPA / CBT mismatch / untrusted domain) and 18456 (authentication failure) MUST retain category `SqlServerWindowsAuthFailureCategory.LoginRejected`. This ensures `SqlLoginCircuitBreaker.RecordFailure` opens the breaker for `LoginFailureCooldownSeconds` (default 60s), failing closed on subsequent calls and shielding Active Directory accounts from lockout storms.
- **SEC-SEC210-03 (SSRF & Fail-Closed Authorization on Datasource Probes):**  
  `POST /api/v1/catalog/datasources/{id}/test` must strictly require `GatewayPolicies.GovernanceAdmin` authorization. Read-only or unauthenticated tokens must be denied (401/403). The `datasourceId` parameter must strictly match known catalog entities or pre-configured connection keys in `GatewayOptions.DataSources.Connections`; any attempt to pass loopback IPs, cloud metadata endpoints (`169.254.169.254`), private CIDRs, or path traversal segments (`../`) must immediately fail closed with HTTP 404 without socket creation.
- **SEC-SEC210-04 (Migration Non-Destructive Invariance & Audit Chain Preservation):**  
  `tools/migrations/upgrade_governance_v1_1_to_v1_2.sql` must strictly consist of non-destructive, idempotent DDL (`CREATE TABLE IF NOT EXISTS`, `ALTER TABLE ... ADD COLUMN ...`). `DROP TABLE` or destructive truncation is strictly prohibited. Pre-existing consent records and tamper-evident audit hash chains (`AUDIT_LOG_ENTRIES.entry_hash`, `prev_hash`) must remain intact without sequence modification.
- **SEC-SEC210-05 (SID Invariant Normalization for Casbin & Policy Evaluation):**  
  In `upgrade_governance_v1_1_to_v1_2.sql`, backfilling of `CONSENTS.grantee_sid_norm` and `ACCESS_PROFILE_ASSIGNMENTS.subject_norm` MUST be normalized using `UPPER(TRIM(col))` (e.g. `UPDATE CONSENTS SET grantee_sid_norm = UPPER(TRIM(grantee_sid)) ...`). Direct assignment of raw casing is forbidden as mixed/lowercase SIDs can cause Casbin RBAC lookup misses or policy bypasses.
- **SEC-SEC210-06 (CI Container Hardening & Durable CDC Store Registration):**  
  `benchmarks/load/docker/Dockerfile.gql` must set `ENV ASPNETCORE_ENVIRONMENT=Benchmark`, purge `appsettings.Development.json`, and retain non-root execution (`USER 10001:10001`). Development debug endpoints must remain disabled. In `GatewayGovernanceServiceExtensions`, default host DI must register durable stores (`ICdcStreamStore`, `ICdcStreamKeyStore`) backed by `TRepository`. `InMemoryCdcStreamStore` in `CdcStreamServiceExtensions` is restricted to non-durable test fallbacks via `TryAddSingleton`.

### 6.2 Mandatory Security Test Cases

| Condition | Test Case & Location | Mandatory Trait |
|---|---|---|
| **SEC-SEC210-01** | `SqlServerWindowsAuthConfiguratorTests.ClassifyOpenException_RedactsCredentials_AndPreservesNumericDiagnostics` | `[Trait("Category", "Security")]` |
| **SEC-SEC210-02** | `SqlLoginCircuitBreakerTests.RecordFailure_OpensBreaker_On18452And18456` | `[Trait("Category", "Security")]` |
| **SEC-SEC210-03** | `DatasourceTestingSecurityTests.TestEndpoint_RequiresGovernanceAdmin_AndRejectsSsrfOrTraversal` | `[Trait("Category", "Security")]` |
| **SEC-SEC210-04** | `GovernanceMigrationSecurityTests.UpgradeScript_PreservesAuditHashChainsAndConsents` | `[Trait("Category", "Security")]` |
| **SEC-SEC210-05** | `GovernanceMigrationSecurityTests.SidBackfill_NormalizesUppercaseTrimmed_MatchesCasbinLookups` | `[Trait("Category", "Security")]` |
| **SEC-SEC210-06** | `SupplyChainAndDeploymentSecurityTests.BenchmarkDockerfiles_AreHardened` & `CompositionRootModularizationTests.HostDi_ResolvesDurableCdcStores` | `[Trait("Category", "Security")]` |

