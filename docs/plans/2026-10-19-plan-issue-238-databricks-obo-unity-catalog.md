# Implementation Plan: Issue #238 Databricks OBO & Unity Catalog Access Enforcement

**Document ID:** `PLAN-DATA-238` (tracked under `WP-SC-15`)  
**Date:** 2026-10-19  
**Status:** COMPLETED & VERIFIED ✅ (All 94 Databricks unit and security tests passing)  
**Author:** Solution Architect (`project-manager`)  
**Parent PRD:** [`docs/plans/2026-10-19-req-issue-238-databricks-obo-unity-catalog.md`](2026-10-19-req-issue-238-databricks-obo-unity-catalog.md) (`REQ-DATA-238`)  
**Target Milestone:** v1.3.0 (Minor Release / Enterprise Track)  
**Branch:** `feat/issue-238-databricks-obo-unity-catalog`  

---

## 1. Architectural Strategy & Scope

When connecting to Azure Databricks Unity Catalog, Autheris must support delegated enforcement where SQL queries execute with the end-user's Entra ID identity rather than a shared static service principal. This enables Unity Catalog to natively enforce user-level row filters, column masks, and grant tables.

This plan details the implementation across 5 work packages:
- **WP-238-1:** Connector-Agnostic Delegation Abstractions (`Autheris.Domain`).
- **WP-238-2:** Databricks Options & Fail-Closed Startup Validation (`Autheris.Domain` & `Autheris.Api`).
- **WP-238-3:** OAuth 2.0 OBO Provider & Hardened Partitioned Cache (`Autheris.Infrastructure.Databricks`).
- **WP-238-4:** Statement Execution Client & Query Tagging (`Autheris.Infrastructure.Databricks`).
- **WP-238-5:** Comprehensive Unit & Security Test Suite (`Autheris.Tests.Unit`).

---

## 2. Binding Security Conditions (SEC-OBO238)

The Security & Compliance review establishes the following non-negotiable binding conditions:

- **SEC-OBO238-01 (Strict Zero-Fallback Policy):**
  If `Auth.Mode == OAuthObo` or `EnforcementMode` is `Delegated` or `DelegatedDefenceInDepth`, Autheris MUST NEVER fall back to administrative M2M service principal credentials when caller assertions are missing, invalid, expired, or rejected by Entra ID. Any failure in OBO token acquisition MUST fail closed immediately with `DATABRICKS_AUTH_FAILED` (401 Unauthorized with OAuth challenge).
- **SEC-OBO238-02 (Zero Token Leakage in Logs & Telemetry):**
  Raw caller assertions, downstream OBO tokens, client secrets, and refresh tokens MUST NEVER appear in plain text or base64 in application logs, audit events, exception messages, OpenTelemetry span attributes, or Databricks query tags.
- **SEC-OBO238-03 (Cache User Isolation & TTL Bounding):**
  The OBO token cache MUST be partitioned by `(host, clientId, assertionSha256Hash)`. The cached token TTL MUST NOT exceed the shorter of downstream token expiry (`expires_in`) and the caller's JWT expiry (`exp` claim), clamped with a safety margin (60s buffer).
- **SEC-OBO238-04 (Fail-Closed Startup Cross-Validation):**
  If a data source is configured with `EnforcementMode.Delegated` or `EnforcementMode.DelegatedDefenceInDepth`:
  - `KeySetCacheAllowed` MUST be `false` (prevents cross-user tenant-level key cache pollution from user-filtered tables, per Issue #107 / SEC-RF107-01).
  - Databricks data sources MUST have `Auth.Mode == DatabricksAuthMode.OAuthObo`.
  - `ClientId` and either `ClientSecretRef` or `ClientCertificateRef` MUST be configured.
- **SEC-OBO238-05 (Audit Trail Continuity & Bidirectional Tagging):**
  Every Databricks statement executed under OBO MUST transmit `query_tags` containing `autheris_correlation_id` and `autheris_trace_id` so that operations in Autheris audit logs map 1:1 to Databricks Unity Catalog query history and audit logs.

---

## 3. Work Packages & Technical Specification

### WP-238-1: Connector-Agnostic Delegation Abstractions
**Target Assembly:** `Autheris.Domain`

1. **`EnforcementMode` Enum (`Autheris.Domain.Options`):**
   ```csharp
   public enum EnforcementMode
   {
       AutherisEnforced = 0,
       Delegated = 1,
       DelegatedDefenceInDepth = 2
   }
   ```
2. **`DataSourceConnectionOptions` Extension:**
   Add `public EnforcementMode EnforcementMode { get; init; } = EnforcementMode.AutherisEnforced;`
3. **`DelegationKind` Enum & `IDelegationCapability` (`Autheris.Domain.Connectors`):**
   ```csharp
   public enum DelegationKind
   {
       TokenDelegation = 0,
       KerberosConstrainedDelegation = 1,
       AssertedIdentity = 2
   }

   public interface IDelegationCapability
   {
       IReadOnlySet<DelegationKind> SupportedDelegationKinds { get; }
       bool SourceEnforcesRowSecurity { get; }
       bool SourceEnforcesMasking { get; }
       bool SupportsCorrelationTag { get; }
   }
   ```
4. **`DelegationContext` & `IDelegatedCredentialProvider` (`Autheris.Domain.Connectors`):**
   ```csharp
   public sealed record DelegationContext(
       string? CallerToken = null,
       ClaimsPrincipal? Principal = null,
       TenantId? TenantId = null,
       string? CorrelationId = null,
       string? TraceId = null);

   public interface IDelegatedCredentialProvider
   {
       Task<string> GetCredentialAsync(string dataSourceName, DelegationContext context, CancellationToken ct);
   }
   ```

### WP-238-2: Databricks Options & Startup Validation
**Target Assemblies:** `Autheris.Domain`, `Autheris.Api`

1. **`DatabricksAuthMode` Enum Update:**
   Add `OAuthObo = 2`.
2. **`DatabricksOboOptions` Class:**
   ```csharp
   public sealed class DatabricksOboOptions
   {
       public string? TenantId { get; init; }
       public string? ClientId { get; init; }
       public string? ClientSecretRef { get; init; }
       public string? ClientCertificateRef { get; init; }
       public string Scopes { get; init; } = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default";
       public string? Authority { get; init; }
       public bool AllowLongRunning { get; init; } = false;
   }
   ```
   Add `public DatabricksOboOptions? Obo { get; init; }` to `DatabricksAuthOptions`.
3. **`GatewayStartupValidator.ValidateDatabricks`:**
   - If `connection.EnforcementMode is EnforcementMode.Delegated or EnforcementMode.DelegatedDefenceInDepth`:
     - Assert `!connection.KeySetCacheAllowed` (throw `ValidationException` if true).
     - Assert `db.Auth.Mode == DatabricksAuthMode.OAuthObo`.
   - If `db.Auth.Mode == DatabricksAuthMode.OAuthObo`:
     - Assert `db.Auth.Obo != null` and `!string.IsNullOrWhiteSpace(db.Auth.Obo.ClientId)`.
     - Assert `!string.IsNullOrWhiteSpace(db.Auth.Obo.ClientSecretRef) || !string.IsNullOrWhiteSpace(db.Auth.Obo.ClientCertificateRef)`.

### WP-238-3: OAuth 2.0 OBO Provider & Hardened Partitioned Cache
**Target Assembly:** `Autheris.Infrastructure.Databricks`

1. **`IDatabricksCredentialProvider` Interface:**
   ```csharp
   public interface IDatabricksCredentialProvider
   {
       Task<string> AcquireTokenAsync(DatabricksDataSourceOptions db, DelegationContext? context, CancellationToken ct);
   }
   ```
2. **`DatabricksTokenCache` Extension:**
   Extend cache to support caller assertion hash partitioning:
   `TryGet(string host, string clientId, string? callerAssertionHash, DateTimeOffset now, out string token)`
   `Set(string host, string clientId, string? callerAssertionHash, string token, DateTimeOffset expiresAt)`
   `Remove(string host, string clientId, string? callerAssertionHash)`
3. **`DatabricksOboTokenProvider` Implementation:**
   - Handles `OAuthM2M`, `Pat`, and `OAuthObo`.
   - For `OAuthObo`:
     - Validates `context?.CallerToken` is non-empty. If empty -> fails closed with `DatabricksProviderException(DatabricksErrorCodes.AuthFailed, ...)`.
     - Computes SHA-256 hash of caller assertion for bounded cache lookup.
     - Parses caller token `exp` claim to bound TTL.
     - Posts to Entra ID token endpoint:
       `https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token` with parameters:
       `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer`, `client_id`, `client_secret`, `assertion`, `scope`, `requested_token_use=on_behalf_of`.
     - On error, parses OAuth error code (`invalid_grant`, `interaction_required`) and throws sanitized `DatabricksProviderException` without logging raw tokens.
     - Zero fallback to static service principal!

### WP-238-4: Statement Execution Client & Query Tagging
**Target Assembly:** `Autheris.Infrastructure.Databricks`

1. **`DatabricksStatementRequest` Extension:**
   ```csharp
   public sealed record DatabricksStatementRequest(
       string Statement,
       ImmutableArray<DatabricksParameter> Parameters,
       string Catalog,
       string Schema,
       TimeSpan WaitTimeout,
       string? DataSource = null,
       IReadOnlyDictionary<string, string>? QueryTags = null,
       DelegationContext? Delegation = null);
   ```
2. **`DatabricksStatementExecutionClient` Update:**
   - Accepts `IDatabricksCredentialProvider`.
   - Passes `request.Delegation` to `AcquireTokenAsync`.
   - In `BuildBody`: serializes `"query_tags": { ... }` when `request.QueryTags` is non-empty.
3. **`DatabricksDbConnection` & `DatabricksDbCommand`:**
   - Accept and propagate `DelegationContext` and tags when executing queries.

### WP-238-5: Unit & Security Test Suite
**Target Assembly:** `Autheris.Tests.Unit`

Create `DatabricksOboSecurityTests.cs`:
- Successful OBO token exchange and statement execution.
- Missing caller token fails closed (zero SP fallback).
- `invalid_grant` / `interaction_required` fails closed with 401 error.
- Query tags emitted in statement request payload.
- Cache isolation across different callers.
- Zero token leakage in exception messages and logger entries.
- Startup validation enforcement on `EnforcementMode.Delegated` with `KeySetCacheAllowed`.

---

## 4. Verification & Gate Strategy
- Single targeted execution of unit test suite:
  `dotnet test tests/Autheris.Tests.Unit --filter "FullyQualifiedName~Databricks"`
- Verification of 100% test pass rate.
