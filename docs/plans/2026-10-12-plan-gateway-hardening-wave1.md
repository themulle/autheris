# Implementation Plan: Wave 1 Gateway Hardening & Protocol Hygiene

**Document ID:** `PLAN-WAVE1-HARDENING-2026-10-12`  
**Date:** 2026-10-12  
**Status:** COMPLETED & VERIFIED ✅  
**Author:** Delivery Project Manager, Solution Architect & Security Expert  
**Target Branch:** `feat/gateway-hardening-wave1`  
**Target Path:** `docs/plans/2026-10-12-plan-gateway-hardening-wave1.md`  
**Parent Plan:** [PLAN-GH-ISSUES-ROADMAP-2026-10-12](file:///root/autheris/docs/plans/2026-10-12-plan-github-issues-roadmap.md)  
**Feature Documentation:** [F-SEC-05 Gateway Hardening & Adaptive Defense Suite](file:///root/autheris/docs/features/f-sec-05-gateway-hardening-defense.md)  

---

## 1. Executive Summary & Objectives

This implementation plan delivers **Wave 1 (Quick Wins)** of the GitHub Issues Roadmap, resolving 5 critical operational, security, and protocol issues without impacting the concurrent AST Target Dialect Generator workstream:

| Issue | Title | Complexity | Component Touchpoint |
| :---: | :--- | :---: | :--- |
| **#44** | Exempt authenticated requests from pre-auth IP rate limit | Low | `PreAuthIpRateLimitingMiddleware.cs`, `InMemoryRateLimiterService.cs` |
| **#42** | Option to disable OData, Arrow export, Arrow Flight SQL, Iceberg REST catalog | Low | `GatewayOptions.cs`, `GatewayApplicationBuilderExtensions.cs` |
| **#41** | Advertise & accept Negotiate only when Kerberos is configured (401 on malformed) | Low | `GatewayServiceCollectionExtensions.cs`, `NegotiateHardening.cs` |
| **#31** | Caller context in the audit log (`X-Caller-*` headers) | Low | `SecurityPrincipalContext.cs`, `SecurityContextFactory.cs`, Audit Emitter |
| **#30** | Distinguishable error codes without an existence oracle | Medium | `ErrorSanitizingFilter.cs`, `GatewayExceptionFilter.cs` |

---

## 2. Technical Architecture & Work Package Specifications

### WP-1: Issue #44 – Auth-Exempt Pre-Auth IP Rate Limit & Dynamic Retry-After
* **Problem:** In multi-user proxy environments (e.g. backend service forwarding individual user bearer tokens from one IP), all users share the pre-auth IP bucket (100 req/60s). Valid bearer token calls exhaust the IP limit, returning HTTP 429 with static `Retry-After: 59`.
* **Design Solution:**
  1. In `PreAuthIpRateLimitingMiddleware.cs`:
     - Inspect incoming `Authorization` header. If it contains a `Bearer ` token (or a configured service bearer token), check if bearer-auth exemption is enabled (`options.ExemptAuthenticatedBearerTokens = true`, default `true`).
     - Exemption does not bypass rate-limiting; it delegates rate enforcement entirely to the downstream `PostAuthSidRateLimitingMiddleware` (per-user SID bucket) and client cost quota middleware.
  2. In `PreAuthIpRateLimitOptions`:
     - Add `public bool ExemptBearerTokens { get; init; } = true;`
  3. Dynamic `Retry-After`:
     - Ensure that when a 429 is emitted, `context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString()` calculates exact remaining window seconds from `RateLimitResult` (already supported in `InMemoryRateLimiterService` and `RedisRateLimiterService`).
* **Test Strategy (TDD Red-Green):**
  - Unauthenticated requests from IP `192.168.1.50` exceed limit and receive 429 with dynamic `Retry-After`.
  - Authenticated requests carrying `Authorization: Bearer <valid-jwt>` from the same IP bypass the pre-auth limiter and proceed to authentication.

---

### WP-2: Issue #42 – Protocol Kill-Switches (OData, Arrow, Flight SQL, Iceberg)
* **Problem:** OData, Arrow IPC export, Arrow Flight SQL, and Iceberg REST Catalog are mapped unconditionally in `GatewayApplicationBuilderExtensions.cs`. Operators cannot reduce their attack surface.
* **Design Solution:**
  1. Add `ODataOptions` in `GatewayOptions.cs`:
     ```csharp
     public sealed class ODataOptions
     {
         public bool Enabled { get; init; } = true;
     }
     ```
  2. In `GatewayOptions.cs`:
     - `public ODataOptions OData { get; init; } = new();`
     - Utilize existing `Arrow.Enabled` and `Arrow.EnableFlightSql`.
     - Utilize `Lakehouse.Enabled` (default `false`) or add `Lakehouse.IcebergRestCatalogEnabled` (default `true` when Lakehouse enabled).
  3. In `GatewayApplicationBuilderExtensions.cs`:
     ```csharp
     if (gatewayOptions.OData.Enabled)
     {
         app.MapODataEndpoints(gatewayOptions);
     }
     if (gatewayOptions.Arrow.Enabled)
     {
         app.MapArrowExportEndpoints();
     }
     if (gatewayOptions.Arrow.Enabled && gatewayOptions.Arrow.EnableFlightSql)
     {
         app.MapArrowFlightSqlEndpoints();
     }
     if (gatewayOptions.Lakehouse.Enabled)
     {
         app.MapIcebergRestCatalogEndpoints();
     }
     ```
  4. Response Behavior: Disabled routes naturally return HTTP 404 Not Found.
* **Test Strategy (TDD Red-Green):**
  - Verify that setting `Gateway:OData:Enabled = false` returns 404 for `/odata/$metadata`.
  - Verify that setting `Gateway:Arrow:Enabled = false` returns 404 for `/api/v1/export/arrow`.
  - Verify that setting `Gateway:Lakehouse:Enabled = false` returns 404 for `/v1/default/namespaces`.

---

### WP-3: Issue #41 – Kerberos / Negotiate Scheme Cleanup & 401 Error Handling
* **Problem:**
  - 401 responses emit `WWW-Authenticate: Negotiate` even when no Kerberos/AD provider is configured.
  - A malformed `Authorization: Negotiate ...` header results in unhandled 500 error instead of 401.
* **Design Solution:**
  1. In `AuthenticationServiceCollectionExtensions.cs` / `GatewayServiceCollectionExtensions.cs`:
     - Only register `AddNegotiate(...)` and set `NegotiateDefaults.AuthenticationScheme` as fallback challenge when `gatewayOptions.Authentication.RequireKerberosOnly || gatewayOptions.Authentication.Adfs.Enabled`.
     - When Kerberos is NOT configured, fallback challenge must default to `GatewayAuthSchemes.JwtBearer` (or `Basic` if Basic is enabled).
  2. In `NegotiateHardening.Configure(...)`:
     - Add `OnAuthenticationFailed` handler to intercept token parsing exceptions and cleanly call `context.Fail(ex)` or return 401 without unhandled 500.
* **Test Strategy (TDD Red-Green):**
  - With Entra ID enabled and Kerberos disabled, verify a 401 response contains `WWW-Authenticate: Bearer` and NO `Negotiate`.
  - Send `Authorization: Negotiate invalid-base64-payload` and assert response is HTTP 401 (never 500).

---

### WP-4: Issue #31 – Caller Context in WORM Audit Log
* **Problem:** Microservices calling Autheris on behalf of users need to record application name, version, and query name in the WORM audit trail.
* **Design Solution:**
  1. Define value object in `Autheris.Domain.Security`:
     ```csharp
     public sealed record CallerContext(string? Application, string? Version, string? QueryName);
     ```
  2. Extend `SecurityPrincipalContext` with `public CallerContext? Caller { get; init; }`.
  3. In `SecurityContextFactory.cs`:
     - Extract optional headers:
       - `X-Caller-Application` (or `X-Application-Name`)
       - `X-Caller-Version` (or `X-Application-Version`)
       - `X-Caller-Query-Name` (or `X-Query-Name`)
     - Sanitize headers (limit string length to 128 characters, strip control characters).
  4. In `AuditLogEntry.cs` / Audit Emitter:
     - When `CallerContext` is present, record it in `details_json`:
       `"caller": { "app": "...", "ver": "...", "query": "..." }`.
* **Test Strategy (TDD Red-Green):**
  - Execute request with caller headers.
  - Query audit log and verify `caller` block is present in `details_json`.
  - Execute request without headers -> verify existing audit structure is unaffected.

---

### WP-5: Issue #30 – Distinguishable Error Codes Without an Existence Oracle
* **Problem:** Clients need machine-readable error codes (`COLUMN_NOT_RELEASED`, `FILTER_ON_MASKED_COLUMN`, `COST_LIMIT`, `ROW_LIMIT`, `TIMEOUT`, `RATE_LIMIT`), but unknown tables must not be distinguishable from denied tables.
* **Design Solution:**
  1. In `ErrorSanitizingFilter.cs`:
     - Add the specific client error codes to `WhitelistedSafeCodes`:
       `"COLUMN_NOT_RELEASED"`, `"FILTER_ON_MASKED_COLUMN"`, `"COST_LIMIT"`, `"ROW_LIMIT"`.
  2. Maintain Anti-Oracle Invariant:
     - If the table is not discoverable / unknown to the caller, keep the existing behavior: return generic `FORBIDDEN` / `Access denied.`.
     - If the table is known (e.g. visible in caller's catalog projection) but a specific column is denied or masked, return the precise code with sanitized message.
* **Test Strategy (TDD Red-Green):**
  - Calling an unknown table returns generic `FORBIDDEN`.
  - Requesting an unreleased column on a known table returns `COLUMN_NOT_RELEASED`.

---

## 3. Phase 3: Security & Threat Model Review

- [x] **Zero-Trust Default:** Bearer exemption in WP-1 only bypasses IP count; it never bypasses identity token validation or SID rate limiting.
- [x] **Fail-Closed Anti-Oracle:** Error codes in WP-5 strictly check dataset visibility before returning specific codes.
- [x] **Replay / Spoofing Defense:** Caller context headers in WP-4 are marked as untrusted caller metadata in the WORM audit log and do not grant permissions.
- [x] **Attack Surface Reduction:** WP-2 and WP-3 allow disabling unused protocols and schemes.

---

## 4. Phase 4: Delivery Sequence (Red-Green-Refactor)
 
- [x] **Step 1:** Implement WP-1 (#44 Pre-Auth Rate Limit) + Unit Tests in `Autheris.Tests.Unit`.
- [x] **Step 2:** Implement WP-2 (#42 Protocol Kill Switches) + Integration Tests.
- [x] **Step 3:** Implement WP-3 (#41 Negotiate Cleanup) + Authentication Tests.
- [x] **Step 4:** Implement WP-4 (#31 Caller Context in Audit) + Audit Tests.
- [x] **Step 5:** Implement WP-5 (#30 Machine-Readable Error Codes) + GraphQL Error Tests.
- [x] **Step 6:** Run full test suite (`dotnet test Autheris.sln`) -> verify 100% green.

