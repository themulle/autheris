# Security review: src/Autheris.Api (read-only)

Scope: Endpoints, Middleware, Security, Mcp, Extensions/DI wiring, Hosting, Program.cs, Configuration, UI, Serialization, appsettings*.json.
All Map* calls (about 200) were enumerated and every mutating endpoint that carries only `.RequireAuthorization()` was checked for an in-handler role/tenant check. No endpoint was found that mutates global state without one, except the classification endpoints listed below.

### Medium — TOTP enrollment can be (re)bound by any session holder; portal verify endpoint accepts a client-chosen seed
- **File:** `src/Autheris.Api/Endpoints/DevPortalEndpoints.cs:48-128` (also `Endpoints/GovernanceApiEndpoints.cs:109-182`)
- **CWE:** CWE-308 (single-factor authentication in place of the second factor) / CWE-639
- **Confidence:** Likely
- **Issue:** `/portal/2fa/enroll/verify` is mapped in every environment (only `/` and `/getting-started` check `IsDevelopment`). It reads both `secret` and `totpCode` from the request body, verifies the code against that caller-supplied secret, and persists the secret with `secretStore.SetSecretAsync(userSid, secret)`. The server never remembers which secret `GET /portal/2fa/enroll` issued, so the attacker chooses the TOTP seed. The only guard is "user has no secret yet". `/api/v1/governance/2fa/verify-enrollment` is better because it uses the server-side pending secret, but it also accepts `request.SecretBase32` whenever `IDistributedClusterStateProvider` is not registered (single-node). Enrollment needs no re-authentication and no existing second factor. The TOTP is the control for HitL step-up approvals (`/api/governance/hitl/*`, `/portal/approvals/{id}/step-up`) and for classification step-up.
- **Exploit scenario:** An attacker who holds a stolen bearer token or Basic credentials for an approver (DataOwner/DataSteward) who has not enrolled TOTP yet generates their own Base32 seed, computes the current code and POSTs both to `/portal/2fa/enroll/verify`. The seed is now bound to the victim's SID. The attacker then uses it to pass `ApproveStepUpRequestAsync` for tickets that the victim can approve. The "second factor" is not independent of the first.
- **Remediation:** Keep the pending secret server-side only (`totp:pending:{sid}` with TTL) in the portal flow too, and ignore any client-supplied `secret`:
  ```csharp
  var pending = await clusterState.GetAsync<string>($"totp:pending:{sid}", ct)
      ?? return Results.BadRequest(...);
  ```
  Drop the `request.SecretBase32` fallback, and fail closed (503) if the pending store is unavailable. Require recent re-authentication (fresh `auth_time`/`amr`) or an existing-factor challenge before enrollment. Make the check-then-set atomic (`SetIfNotExistsAsync`). Map the portal 2FA pages only for environments that need them.

### Medium — Pre-auth IP rate limit is bypassed with any `Authorization: Bearer` header; the compensating check is unreachable on protected routes
- **File:** `src/Autheris.Api/Middleware/RateLimitingMiddleware.cs:45-56,143-170`; pipeline order `Extensions/GatewayApplicationBuilderExtensions.cs:296-308`
- **CWE:** CWE-770 / CWE-799
- **Confidence:** Confirmed
- **Issue:** `PreAuthIpRateLimitingMiddleware` skips the limiter for any request whose `Authorization` header merely starts with `Bearer ` (`ExemptAuthenticatedRequests` defaults to `true`, `GatewayOptions.cs:815`) and only sets `Autheris_PreAuthExempted`. The IP check for such requests is deferred to `PostAuthSidRateLimitingMiddleware`, which sits after `UseAuthorization()`. For every route that requires authorization, `AuthorizationMiddleware` answers 401 and never calls `next`, so the deferred IP limit never runs. The exemption therefore applies to unauthenticated traffic. For regular endpoints, unauthenticated clients with a junk bearer token get unmetered request volume, each triggering `SelectJwtScheme`/JWT parse and validation, `TokenRevocationMiddleware`, and 401 audit aggregation. Basic-auth brute force is not affected, because a Bearer header selects the JWT scheme.
- **Exploit scenario:** `curl -H 'Authorization: Bearer x.y.z' https://gw/graphql` in a loop from one host bypasses the 100/min/IP limit entirely. It gives CPU and log amplification against the JWT stack, and the exemption also shields other pre-auth probing.
- **Remediation:** Do not exempt on header presence. Apply the IP limit pre-auth to everyone, or exempt only after a cheap cryptographic pre-check, or move the deferred IP check into an `OnChallenge`/`OnAuthenticationFailed` event so it runs for failures. For example, set `ExemptAuthenticatedRequests` default to `false`, and when exempting, still run `CheckPreAuthIpAsync` with a larger "bearer" bucket.

### Medium — Classification `transition` endpoint trusts a client-supplied proposal and reviewer identity (SoD / four-eyes / MFA gating forgeable)
- **File:** `src/Autheris.Api/Endpoints/GovernanceClassificationEndpoints.cs:71-78`; consumed by `Autheris.Application/Governance/Services/ClassificationWorkflowStateMachine.cs:62-170`
- **CWE:** CWE-639 / CWE-345 / CWE-285
- **Confidence:** Confirmed (trust gap), Likely (downstream impact, which depends on the registered workflow hooks)
- **Issue:** `POST /api/governance/classification/transition` (GovernanceAdmin) deserializes a full `TableClassificationTransitionPayload { Proposal, Request }` and calls `stateMachine.TransitionAsync(payload.Proposal, payload.Request)`. Neither object is loaded from the store, and `Request.ReviewerSid` / `ReviewerRole` / `ColumnDecisions` / `StepUpAuthToken` are not bound to the authenticated caller. The state machine then derives `maxEffectiveRank` from the caller-supplied `ColumnDecisions` and derives four-eyes and step-up from that. It also checks the SoD rule `DataOwnerSid == ReviewerSid` using caller-supplied values, persists `proposal with { Status = ... }` via `SaveProposalAsync`, and passes the forged proposal and request to `IGovernanceWorkflowHook`s.
- **Exploit scenario:** One GovernanceAdmin sends `Proposal.Status = "PENDING_GOVERNANCE_REVIEW"`, `Request.ReviewerRole = "DATA_GOVERNANCE_REVIEWER"`, a different `ReviewerSid`, and `ColumnDecisions` lowering every rank to 0. This skips step-up MFA and the second stage and yields `CLASSIFIED`, attributed to an arbitrary SID in audit and hooks. The stored record is overwritten with attacker-defined columns and sensitivity.
- **Remediation:** Remove this endpoint or reduce it to `proposalId` plus decision data. Load the proposal with `store.GetProposalByIdAsync(id)`, build the `TableClassificationApprovalRequest` server-side exactly as the `/approve` handler does (`ReviewerSid = caller SID`, role from the principal), and ignore body-supplied identity fields. In the state machine, gate on `max(proposed, effective)` rank, never only on the reviewer-supplied override.

### Medium — Classification approve/reject: no workflow-state check; an empty `DataOwnerSid` lets any `DataOwner` act on any proposal; a single reviewer can finalize without stage 1
- **File:** `src/Autheris.Api/Endpoints/GovernanceClassificationEndpoints.cs:93-224` (ownership guard at 138-144 and 201-207); `Autheris.Application/.../ClassificationWorkflowStateMachine.cs:240-258`
- **CWE:** CWE-841 (improper enforcement of behavioral workflow) / CWE-862
- **Confidence:** Confirmed (code path), Likely (impact)
- **Issue:**
  1. The ownership guard is `!IsNullOrWhiteSpace(proposal.DataOwnerSid) && DataOwnerSid != caller`. When the proposal has no owner (the default when none is registered), the guard is skipped and every principal with the generic role `DataOwner` (any table, any tenant, since proposals carry no tenant) can approve or reject.
  2. `proposal.Status` is never checked. A `REJECTED` or `CLASSIFIED` proposal can be re-approved. A `ComplianceReviewer`/`ComplianceOfficer` resolves to `DATA_GOVERNANCE_REVIEWER`, and `EvaluateApprovalProgression` returns `CLASSIFIED` for that role even when stage 1 never happened.
  3. `GET /proposals/{id}` has only `.RequireAuthorization()`, so any authenticated user can read any proposal (table, columns, owner SID).
  4. The effective ranks that gate step-up and four-eyes come from the approver's own `ColumnDecisions`, so a single approver can downgrade a sensitive column without MFA or a second reviewer.
- **Exploit scenario:** A `DataOwner` of an unrelated table in another tenant approves (or reverses a rejection of) a proposal for a table with no registered owner. A ComplianceReviewer alone seals a high-risk proposal that required two stages.
- **Remediation:** Fail closed when `DataOwnerSid` is empty (require GovernanceAdmin, or `IDataOwnershipRepository.IsAuthorizedApproverForTableAsync` as the HitL endpoints do). Enforce a state machine: stage 1 only from `PENDING_OWNER_REVIEW`/`UNCLASSIFIED`, stage 2 only from `PENDING_GOVERNANCE_REVIEW`, nothing from terminal states. Add a tenant to proposals and filter reads. Compute gating from the maximum of proposed and effective rank, and require a second distinct SID for stage 2.

### Medium — Bulk consent: self-grant SoD check is trivially bypassable, and Unmasked profiles are created and activated without approval
- **File:** `src/Autheris.Api/Endpoints/GovernanceEndpoints.cs:594-730` (SoD at 620-627)
- **CWE:** CWE-285 / CWE-863
- **Confidence:** Likely
- **Issue:** `POST /api/v1/consents/bulk` lets ClusterAdmin / TenantAdmin / GovernanceAdmin / `SecurityAdmin` create an `Unmasked` access profile for any subject with a 15-character justification and activates it immediately (cache invalidated, no ticket or approver). The only SoD control compares `Subject` to the caller's SID and `Identity.Name`. Subjects are matched as "SIDs or user names" (`AccessProfile.AssignedSubjects`). Other identifiers of the same person are not compared, although `GetUserIdentifiers()` (oid, sub, upn, email, NameIdentifier, ...) exists and is used for the HitL self-approval check. A group SID or role name the admin belongs to is not compared either.
- **Exploit scenario:** An admin without any approval grants cleartext (`Unmasked`) access on `sales.*` to their own UPN/email/oid, or to a group they are in. The "self-grant strictly prohibited" rule is bypassed, and no second person ever sees it.
- **Remediation:** Reject when the subject matches any of `User.GetUserIdentifiers()` and any of the caller's group SIDs/roles. Route `Unmasked` (and large `ValidDays`) through the existing two-person approval, as virtual filters do, instead of immediate activation. Require `GovernanceAdmin` rather than any `TenantAdmin`/`SecurityAdmin` string role for unmasking.

### Medium — Schema Registry is a global, tenant-less store writable and readable by any `Developer`/`DataOwner`
- **File:** `src/Autheris.Api/Endpoints/SchemaRegistryEndpoints.cs:26-157`; `Extensions/DependencyInjection/GatewayCoreServiceExtensions.cs:145` (singleton `InMemorySchemaRegistryRepository`)
- **CWE:** CWE-639 / CWE-284
- **Confidence:** Likely (the registry may be intended as platform-global)
- **Issue:** `RegisteredSchema` has no tenant. Read endpoints (`/services`, `/{service}/latest`, `/{service}/history`) and `publish`/`check` are open to `SchemaPublisherRoles` = {GovernanceAdmin, SchemaAdmin, GatewayAdmin, PlatformAdmin, ClusterAdmin, Developer, DataOwner}, and none of the handlers compares tenants. Ownership is "first registrant wins" (`IsServiceOwner`: no history means any publisher may claim it). The data is in memory, so after a restart or on another replica the history is empty and any publisher can re-claim every service name. `BackstageCatalogExportService` publishes these subgraph SDLs.
- **Exploit scenario:** A Developer in tenant A lists all services and downloads the SDL of tenant B's subgraphs. They can pre-register B's service names (squatting) so B's own publisher is denied (`Only the owner ... may publish`), or re-claim names after a restart.
- **Remediation:** Add `TenantId` to `RegisteredSchema`, to the repository key and to `IsServiceOwner`. Filter reads by `EndpointSecurity.GetRequestTenant(ctx)` unless canonical ClusterAdmin. Use a persistent store. Bind ownership to a stored owner field, not "oldest history row".

### Low — Unauthenticated 404/4xx responses are written to the audit hash chain as `TABLE_QUERY`/`ALLOW` with attacker-chosen target
- **File:** `src/Autheris.Api/Middleware/AccessAuditMiddleware.cs:112-196`; `Autheris.Domain/Audit/AuditContext.cs` (default `Decision = "ALLOW"`)
- **CWE:** CWE-779 / CWE-400
- **Confidence:** Likely
- **Issue:** `AccessAuditMiddleware` is the outermost auditing layer. Only 401/403/429 become DENY (401/429 are aggregated and not persisted individually); 5xx become ERROR; everything else (including 404/405/400 for anonymous requests to non-existent routes or AllowAnonymous endpoints) falls through to `RecordAuditEventAsync` with `EventType = TableQuery`, `Decision = ALLOW` and `TargetTable = Request.Path` (up to the Kestrel line limit). `endpoint == null`, so no exemption applies.
- **Exploit scenario:** An unauthenticated scanner or botnet hitting random paths fills the tamper-evident audit chain (retention 3650 days) with false `TABLE_QUERY` ALLOW events, forcing serialized chain writes and polluting forensics. Combined with the rate-limit bypass above, volume is limited only by the per-IP bucket for the 404 path.
- **Remediation:** For requests with `endpoint == null`, or unauthenticated 404/405, aggregate like 401/429 (`IAuthFailureAggregator`) instead of persisting per-request rows, or exempt them. Cap `TargetTable` length (for example 256 characters). Use a neutral event type such as `HTTP_NOT_FOUND` rather than `TableQuery`/`ALLOW`.

### Low — No host validation (`AllowedHosts`); the CSRF same-origin test trusts the client `Host` header (DNS rebinding against Development anonymous modes and MCP)
- **File:** `src/Autheris.Api/Extensions/GatewayApplicationBuilderExtensions.cs:220-226`; `appsettings.json` (no `AllowedHosts`); `Endpoints/ODataEndpoints.cs:180,193,624` (links built from `Request.Host`)
- **CWE:** CWE-350 / CWE-346
- **Confidence:** Likely
- **Issue:** Neither `appsettings*.json` nor the code configures `AllowedHosts`/`UseHostFiltering` restrictively (default allows all). `isOriginTrusted` is set when `Origin.Authority == Request.Host.Value`. Under DNS rebinding (attacker domain re-resolved to the gateway address), `Origin` and `Host` are both the attacker's domain, so the same-origin test passes and the attacker page can send requests with the required `X-Requested-With` header and read the responses. OData `@odata.context` and OpenAPI links are built from the attacker-controlled Host.
- **Exploit scenario:** A developer running the Quickstart/Development preset (`danger_allow_anonymous_access`, TestAuth, `/api/dev/login/*`, MCP `EnableDeveloperCors`) on localhost or an intranet host visits a malicious page. The page rebinds to the gateway and queries GraphQL/MCP/OData as the anonymous dev admin context. Production is protected by authentication, so impact is limited to dev and intranet-credential-free modes.
- **Remediation:** Set `AllowedHosts` from `Gateway:Hosting:AllowedHosts` (fail closed outside Development when empty) and `app.UseHostFiltering()`. In the CSRF check, compare `Origin` to a configured public origin instead of `Request.Host`, or additionally require `Host` to be in the allow-list.

### Low — Error-message leakage on anonymous and admin endpoints (`ex.Message` returned to callers)
- **File:** `src/Autheris.Api/Endpoints/WebhookEndpoints.cs:120,137,141,145` (AllowAnonymous); `Endpoints/StreamingCdcEndpoints.cs:145,157`; `Endpoints/DbtEndpoints.cs:151,179,371`; `Endpoints/GovernanceEndpoints.cs` (row filter parse error)
- **CWE:** CWE-209
- **Confidence:** Confirmed
- **Issue:** The ITSM webhook endpoints (AllowAnonymous) return `JsonException`/`FormatException`/`ArgumentException` messages verbatim (parser position and internal wording). The CDC subscription endpoint returns the egress-policy exception message (`Target URL blocked by SSRF egress policy: {ex.Message}`), which acts as a DNS/internal-address oracle for SSRF probing by tenant admins. The DBT semantic-manifest endpoint returns the raw exception message for any `Exception`.
- **Exploit scenario:** An authenticated TenantAdmin probes internal hostnames via `POST /api/v1/cdc/subscriptions` and distinguishes "resolves to private range" from "does not resolve" from the message text.
- **Remediation:** Return a fixed message plus a trace id (as `WebSqlEndpoints.GenericBadRequestMessage` does) and log the details server-side.

## Verified strengths
- Startup validator (`GatewayStartupValidator`) is comprehensive and fail-closed: DANGER switches blocked outside Development, wildcard/`/0` proxy networks rejected, `RequireHttpsMetadata`, issuer/audience mandatory, Basic hash policy, plugin integrity, demo seeding and Quickstart blocked in non-Development.
- JWT: issuer, audience, lifetime and signing key always validated. ID-token and no-scope rejection, app-only policy, read-only scope mapping, per-IdP schemes with issuer-based routing (routing only, full validation by the selected scheme). WebSocket token validator re-applies the same parameters and `EntraTokenPolicy`.
- ForwardAuth: trusted-proxy check uses the physical TCP peer (captured before `UseForwardedHeaders`), shared secret required (32 bytes or more, constant-time hashed comparison), admin roles never accepted from headers, tenant header only with explicit allowlist, never cluster admin.
- Basic auth: constant-time and equal-cost path for unknown users, concurrency-capped hashing, per-(user,IP) and per-IP lockouts (IPv6 /64), Argon2/PBKDF2 parameter bounds, plaintext only in Development.
- Tenant isolation in `SecurityContextFactory`: header tenant honoured only for canonical ClusterAdmin; malformed claims fail closed; collision guard and tenant registry deny on error. All reviewed REST handlers (ReBAC, CDC, FinOps, HitL, virtual filters, dbt, async jobs, Iceberg) use the resolved tenant, not client input.
- Fallback authorization policy (authenticated user) covers every endpoint without explicit metadata. AllowAnonymous endpoints are limited to health probes, signed webhooks, OpenSchema-gated docs, logout, RFC 9728 discovery and Development-only `/api/dev/*`.
- CSRF middleware (preflight header, Origin/Referer allowlist), MCP `application/json` enforcement, MCP body cap and batch rejection, `x-autheris-*` edge stripping, strict CSP and security headers; DevPortal/Approvals HTML encodes every dynamic value and uses nonces.
- Webhook handlers use bounded reads, HMAC with fixed-time comparison, a 5-minute timestamp window, and no signature bypass for GitOps config-sync. Egress/SSRF checks at registration (CDC, OpenAPI ingest, datasource test). Flight tickets are HMAC-signed and subject-bound. DuckDB OLAP runs with `enable_external_access=false` and a locked configuration.
- TestAuth is compiled out of CI publish artifacts (`AutherisIncludeTestAuth=false`, with a binary string check) and is Development-gated at runtime. Production Dockerfiles default to `ASPNETCORE_ENVIRONMENT=Production`. `src/Autheris.Api/scratch` is empty and does not ship.
- Token revocation (HTTP middleware and periodic WebSocket re-check), session expiry, cross-subject and cross-tenant checks between WebSocket upgrade and `connection_init`. Exception handler returns problem+json without details.

## Areas not covered
- Application/Infrastructure/GraphQL/Extensions projects (governed SQL engine, DuckDB engine internals, access-resolver logic, extensibility interceptors, audit repository implementations, plugin loader) were read only where an Api endpoint depends on them for a conclusion.
- Whether `RequestSizeLimitAttribute` endpoint metadata is enforced by the minimal-API pipeline as intended (relied on the previous audit); the doc-bundle upload (`/api/v1/docs/apps/{appId}/bundle`) declares no larger limit, so its 50 MB path is effectively capped by the 2 MB Kestrel default (functional, not exploitable).
- ZIP/bundle extraction (`IUniversalDocStore.ExtractBundleAsync`) path-traversal and zip-bomb handling (Application layer).
- MCP tool registry/guardrail authorization of individual tools (Application layer). `TenantResolutionMiddleware` is not registered in the pipeline (dead code with weaker admin semantics: GatewayAdmin/PlatformAdmin may switch tenant by header); not reachable today, but it should be removed or aligned with `SecurityContextFactory` before anyone re-adds it.
- Deployment manifests (Helm/compose/ingress) and runtime configuration of production environments; no dynamic testing was performed.
