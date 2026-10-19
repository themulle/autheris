# PRD: REQ-SEC-210 - SQL Server Windows Auth NTLM/EPA Diagnostics, v1.1.x Governance DB Upgrade & Container Release Gate

**Document ID:** `REQ-SEC-210`  
**Date:** 2026-10-19  
**Status:** APPROVED (Phase 1 Qualified)  
**Author:** Enterprise Product Manager & Strategic Gatekeeper  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**GitHub Issue:** #210  
**Priority:** Wave 1 (Security & Release Blockers), Rank 1.5  

---

## 1. Executive Summary & Problem Statement

### 1.1 The Problems

In Autheris v1.2.0, three interconnected operational and release-blocking issues prevent smooth deployment and customer upgrades:

1. **SQL Server Windows Authentication (NTLM) Fails with Error 18452 Under Extended Protection (EPA):**
   - **Root Cause:** When running inside Linux containers, the managed NTLM implementation (`ManagedNtlmSspiContextProvider` / .NET `SspiContextProvider`) cannot negotiate TLS channel binding and emits a zeroed Channel Binding Token (CBT). When the target SQL Server is configured with Extended Protection for Authentication (EPA / "Erweiterter Schutz") set to `Required`, the server rejects containerized NTLM handshakes with SqlNumber 18452 (*"Login failed. The login is from an untrusted domain and cannot be used with Integrated authentication"*).
   - **Diagnostic Blindspot:** The logs currently categorize 18452 generically without exposing the specific `SqlError.State` (e.g. State 132/142) or underlying TDS error tokens, preventing administrators from distinguishing between account lockout, bad domain, and EPA enforcement.
   - **Test Endpoint Failure:** Invoking `POST /api/v1/catalog/datasources/{id}/test` during initial onboarding throws `500 KeyNotFoundException` because `DatasourceTestingService` only searches `_metadataRepository.GetAllTablesAsync()`. If tables have not yet been registered or catalog load previously failed due to auth rejection, connectivity testing cannot be executed.

2. **Existing `governance.db` Upgrade Path from v1.1.x Halts Startup:**
   - **Root Cause:** In v1.2.0, multiple database tables (`AUDIT_PURGE_CHECKPOINTS`, `CALLER_ATTRIBUTES`, `SEMANTIC_MANIFESTS`, `TABLE_RELATION_KEYS`, `autheris_cdc_*`) and columns (`CONSENTS.grantee_sid_norm`) were added directly to baseline schema definitions (migration `0001`) instead of an incremental forward migration.
   - **Symptom:** Upgrading from v1.1.x using an existing `governance.db` skips migration step 1 (already recorded in `autheris_schema_migrations`). The `GovernanceSchemaShapeVerifier` then detects schema drift against `governance-shape.sqlite.json` and immediately halts container startup to fail closed. Existing deployments have no supported non-destructive forward upgrade path.

3. **CI Container Publication Gate Failures:**
   - Publication to GitHub Container Registry (GHCR) is blocked by three failing unit/architecture tests:
     1. `BenchmarkDockerfiles_AreHardened`: `benchmarks/load/docker/Dockerfile.gql` lacks `ASPNETCORE_ENVIRONMENT=Benchmark` and benchmark settings.
     2. `CutoverArchitectureTests.NoCommandTextAssignmentOutsideTheAllowlist`: `Autheris.Extensions.Cdc.CustomQuery.CustomQueryPollerService` is missing from the architectural CommandText allowlist.
     3. `ICdcStreamStore` DI Registration: `RegisterGovernanceAggregateStores` in `GatewayGovernanceServiceExtensions.cs` registers repository interfaces but omits `ICdcStreamStore` and `ICdcStreamKeyStore` in the default host DI container.

### 1.2 Target Audience
- **Enterprise Platform Administrators:** Operating Autheris in Kubernetes/containerized environments connecting to Active Directory-integrated Microsoft SQL Server clusters.
- **Operations & SRE Teams:** Upgrading existing production deployments from v1.1.x to v1.2.0 without loss of consent records, delegation history, or tamper-evident audit chains.
- **Release Engineers & Security Gatekeepers:** Requiring pristine CI pipeline gates before publishing multi-arch container images to GHCR.

---

## 2. Strategic & Architectural Fit, Gatekeeping & Alternatives

### 2.1 Anti-Pattern Gatekeeping & Safe Alternatives

| Request / Proposed Anti-Pattern | Hazard & Vulnerability | Approved Safe Alternative |
|---|---|---|
| Auto-disabling EPA or hacking raw CBT tokens in container NTLM | Violates Zero-Trust; raw forged CBT tokens weaken transport security; Linux containers cannot generate Windows host SSPI channel bindings. | **Document clear administrative mitigation paths:** (1) Set EPA to `Allowed` or `Off` on SQL Server if NTLM must be used from containers; (2) **Preferred:** Use Kerberos authentication with a Keytab (`F-AUTH-04`) which supports cross-platform delegation; (3) Use native SQL Server authentication with TLS 1.3. |
| Dropping and recreating `governance.db` on schema mismatch | Destructive data loss: permanently destroys existing consent records, four-eyes delegations, and tamper-evident audit hash chains. | Provide an official, idempotent, non-destructive forward migration script `tools/migrations/upgrade_governance_v1_1_to_v1_2.sql` that alters existing tables, adds missing columns, backfills `CONSENTS.grantee_sid_norm = grantee_sid`, creates missing tables, and updates `autheris_schema_migrations`. |
| Relaxing `CutoverArchitectureTests` CommandText checks | Bypasses compile-time injection defense across the data access layer. | Explicitly register `CustomQueryPollerService` in the architectural allowlist with strict rationale (read-only poller batch queries). |

---

## 3. User Stories & Acceptance Criteria

### US-1: Diagnostic Transparency for SQL Server Authentication & EPA
*As a system administrator, when SQL Server rejects Autheris' container login with error 18452, I want clear diagnostic logs including `SqlError.State` and explicit configuration guidance regarding Extended Protection (EPA) and Kerberos keytabs, so that I can resolve Active Directory integration issues quickly.*

- **AC-210-1.1:** `SqlServerWindowsAuthConfigurator.ClassifyOpenException` and the connection probe extract and surface `SqlError.State`, `SqlError.Number`, and server error message in diagnostics without leaking credentials.
- **AC-210-1.2:** Classified error message for 18452 explicitly indicates potential Extended Protection (EPA/CBT) enforcement or untrusted domain status, advising EPA configuration verification or Kerberos keytab usage.
- **AC-210-1.3:** Documentation in `docs/features/f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md` and configuration guides clearly describes EPA behavior with containerized NTLM and provides setup instructions for Kerberos keytab auth and SQL Auth alternatives.

### US-2: Data Source Connectivity Testing Before Catalog Registration
*As an administrator configuring a new SQL Server data source, I want `POST /api/v1/catalog/datasources/{id}/test` to verify connectivity using configured data sources even before tables are cataloged, so that I can validate network and authentication setup.*

- **AC-210-2.1:** If `datasourceId` is not found in `ITableMetadataRepository`, `DatasourceTestingService` falls back to querying `_gateway.Value.Sql` (and configured datasources) by name or identifier.
- **AC-210-2.2:** When a match is found in configuration, the service runs the SQL probe directly against the configured endpoint and returns the structured `DatasourceTestResult`.
- **AC-210-2.3:** Returns HTTP 404 (or structured failure probe) with a helpful message only if the datasource is neither in the catalog nor in gateway options; eliminates the unhandled `KeyNotFoundException` HTTP 500 error.

### US-3: Non-Destructive v1.1.x to v1.2.0 `governance.db` Upgrade Path
*As an operator upgrading Autheris from v1.1.x to v1.2.0, I want an official forward migration script and clear documentation to upgrade my SQLite `governance.db` in-place without losing consents or audit records.*

- **AC-210-3.1:** Deliver `tools/migrations/upgrade_governance_v1_1_to_v1_2.sql` containing idempotent DDL:
  - Creates tables `AUDIT_PURGE_CHECKPOINTS`, `CALLER_ATTRIBUTES`, `SEMANTIC_MANIFESTS`, `TABLE_RELATION_KEYS`, and `autheris_cdc_*`.
  - Adds missing columns (`grantee_sid_norm` on `CONSENTS`, etc.).
  - Backfills `CONSENTS.grantee_sid_norm = grantee_sid` for all existing records.
  - Updates or registers migration versioning in `autheris_schema_migrations`.
- **AC-210-3.2:** Upgraded database passes `GovernanceSchemaShapeVerifier` check without schema drift errors.
- **AC-210-3.3:** All pre-existing consent rows, audit log entries, and delegation entries remain intact, functional, and verifiable.

### US-4: Container Publication Gate Clearance in CI
*As a release engineer, I want CI automated test suites to pass 100% so that production multi-arch container images publish cleanly to GHCR.*

- **AC-210-4.1:** `benchmarks/load/docker/Dockerfile.gql` is updated with hardened non-root user, `ASPNETCORE_ENVIRONMENT=Benchmark`, and appropriate appsettings, satisfying `BenchmarkDockerfiles_AreHardened`.
- **AC-210-4.2:** `CutoverArchitectureTests.NoCommandTextAssignmentOutsideTheAllowlist` includes `Autheris.Extensions.Cdc.CustomQuery.CustomQueryPollerService` in the allowlist.
- **AC-210-4.3:** `RegisterGovernanceAggregateStores` in `GatewayGovernanceServiceExtensions.cs` registers `ICdcStreamStore` and `ICdcStreamKeyStore` from `TRepository`, ensuring default host DI passes validation.

---

## 4. Implementation Scope & File Plan

| Subsystem | Target Files | Nature of Change |
|---|---|---|
| **SQL Server Auth Diagnostics** | `src/Autheris.Application/Sql/SqlServerWindowsAuthConfigurator.cs`<br/>`docs/features/f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md` | Capture `SqlError.State`; enhance EPA guidance in error messages and documentation. |
| **Datasource Test Endpoint** | `src/Autheris.Application/Catalog/Services/DatasourceTestingService.cs` | Check `_gateway.Value.Sql` when catalog table lookup returns empty; eliminate 500 error. |
| **Governance DB Upgrade** | `tools/migrations/upgrade_governance_v1_1_to_v1_2.sql`<br/>`docs/operations/governance-db-upgrade.md` (or deployment docs) | Forward migration script for SQLite schema upgrade from v1.1.x to v1.2.0. |
| **CI Gate: Hardened Dockerfile** | `benchmarks/load/docker/Dockerfile.gql` | Add `ASPNETCORE_ENVIRONMENT=Benchmark` and required configuration copy. |
| **CI Gate: Architecture Allowlist**| `tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs` | Add `CustomQueryPollerService` to CommandText assignment allowlist. |
| **CI Gate: Host DI Registration** | `src/Autheris.Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs` | Register `ICdcStreamStore` and `ICdcStreamKeyStore` in `RegisterGovernanceAggregateStores`. |

---

## 5. Security & Governance Review

- **Zero-Trust & Fail-Closed Integrity:** Schema validation remains fail-closed. Migration scripts must be executed explicitly by operators with SQLite write access; no automatic unverified DDL mutations on startup.
- **Credential Hygiene:** Diagnostic logs detailing `SqlError.State` and EPA failures must strictly sanitize connection strings, never logging plain passwords, Kerberos tickets, or NTLM challenge responses.
- **Tamper Evidence:** The upgrade script preserves existing `AUDIT_LOG` records and prepares the database for `AUDIT_PURGE_CHECKPOINTS` without altering historical sequence numbers or hashes.
