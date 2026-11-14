# Implementation Plan: WP-SIMPL-43 PR 5c & PR 6 — SCIM Inbound HTTP Endpoints, Authentication Hardening & Tenant Provisioning Lifecycle (Step 18)

- **Date:** 2026-11-14
- **Work Package:** WP-SIMPL-43 (Authentication & Authorization Core Simplification)
- **PR Slice:** PR 5c (SCIM Inbound HTTP Endpoints & Authentication Hardening — Step 18) & PR 6 (Tenant Provisioning Lifecycle & Sync Integration)
- **Parent Spec:** `docs/plans/simplification/WP-SIMPL-43-authentication-authorization-core.md` (§4.3a, §5 Step 18, §6, §7, §8, §9 PR 5c)
- **Companion Plans:**
  - `docs/plans/2026-11-14-plan-scim-identity-model-schema.md` (PR 5a — Step 13)
  - `docs/plans/2026-11-14-plan-scim-correlation-effective-groups.md` (PR 5b — Steps 14–17)
- **Owner Decisions:** `OD-21`, `OD-27`, `OD-31`, `OD-37`
- **Security Conditions:** `SEC-AUTH-09`, `SEC-AUTH-10`, `SEC-AUTH-11`, `SEC-AUTH-12`, `SEC-SIMPL-01`, `SEC-SIMPL-02`
- **Target Framework:** .NET 10 / C# 14

---

## 1. Architecture Overview, Preconditions & Ratchet Constraints

### 1.1 Scope & Integration Across PR 5a, PR 5b, PR 5c, and PR 6
- **PR 5a (Step 13):** Domain models (`ScimUser`, `ScimGroup`, `ScimGroupMember`, `ScimGroupNestingEdge`, `ScimRealmSyncState`, `ScimUniquenessConflictException`), extension schemas (`urn:ietf:params:scim:schemas:extension:autheris:2.0:User` and `...:Group`), segregated store interfaces (`IScimUserStore`, `IScimGroupStore`, `IScimProvisioningStore`), and migration step `0012_scim_identity_correlation`.
- **PR 5b (Steps 14–17):** `ScimIdentityCorrelator`, recursive CTE nested group flattening (`GetEffectiveGroupSidsForUserAsync`, depth 32, cycle-safe), token $\cup$ SCIM effective group union in `GroupSidResolver`, `MaxSyncAgeMinutes` staleness check (`Degrade` vs. `DropScimGroups` with `IGroupDenyInspector`), and `SCIM_REALM_SYNC_STATE.deprovision_epoch`.
- **PR 5c & PR 6 (Step 18 + Lifecycle Integration — This Plan):**
  1. **Scheme Isolation (`Step 18f`):** SCIM routes (`/scim/v2/{realmId}/...`) select and accept **only** `GatewayAuthSchemes.Scim` in every `Authentication:Mode` (`Direct`, `Upstream`, `Both`), never `ForwardAuth`, `Bearer`, `Basic`, or `Negotiate`.
  2. **Per-Realm Secret Authentication & Realm Path Binding (`Step 18a`):** Constant-time bearer secret validation (`>= 32` chars, unique across realms) bound strictly to the `{realmId}` route parameter (`Secret_of_realm_a_cannot_write_realm_b`).
  3. **Mandatory IP Allowlist Outside Development (`Step 18b`):** `Authentication:Realms:Scim:AllowedIpRanges` is required outside `Development` whenever any realm has `Provisioning == RealmProvisioningMode.Scim`; missing allowlist fails startup (`Missing_ip_allowlist_fails_startup_outside_development`).
  4. **Per-Realm Rate Limiting (`Step 18c`):** `RequestsPerMinute` (default `600`, range `1..60000`) enforced via `IRateLimiterService.CheckPostAuthSidAsync` keyed by `scim-client:{realmId}`, returning HTTP `429 Too Many Requests` with `Retry-After` header and RFC 7644 error body (`Rate_limit_returns_429_with_retry_after`).
  5. **Synchronous Required Auditing (`Step 18d`):** Replace all `AuditEventTypes.AuditConfigChanged` usages on SCIM routes with `ScimUserProvisioned`, `ScimUserUpdated`, `ScimUserDeprovisioned`, `ScimGroupProvisioned`, `ScimGroupDeleted`, and `ScimGroupMembershipChanged` (one event per added/removed member) written via `IAuditWriter.RecordRequiredAsync` inside the request so an audit failure refuses the write (`Every_membership_change_writes_required_audit`, `Audit_failure_refuses_write`).
  6. **Payload & Patch Bounds (`Step 18e`):** 1 MiB request body limit and maximum `1000` members / operations per `PATCH` (`400 Bad Request` with `scimType: "tooMany"`, `Oversized_patch_is_rejected`).
  7. **End-to-End Tenant Provisioning & Sync Lifecycle (PR 6):** Transactional coordination between `ScimService`, `IdentityStore`, `IdentityDeprovisioningService`, `ScimSyncStateTracker`, `SCIM_REALM_SYNC_STATE.deprovision_epoch`, `BroadcastingRoleResolverCache` (`IdentityGroupsChanged`), and `LockRoleCacheInvalidationHostedService`.

### 1.2 Repository Ratchet Constraints
- **No `WP-`, `SEC-`, or `OD-` strings in `src/Autheris.Api` or `src/Autheris.Application`:** Enforced by `CleanArchitectureTests` (`plan-id-comment-counts.txt` is `0`).
- **Route Count Ratchet (`EndpointSecurityCoverageTests`):** Keep the exact existing route registrations in `ScimDiscoveryEndpoints`, `ScimUsersEndpoints`, and `ScimGroupsEndpoints` (total gateway route count stays `272`).
- **Audit Endpoint Metadata Ratchet (`AuditEndpointCoverageTests`):** Every endpoint in `ScimUsersEndpoints`, `ScimGroupsEndpoints`, and `ScimDiscoveryEndpoints` keeps `.WithAudit(...)`, updated from `AuditEventTypes.AuditConfigChanged` to the new `AuditEventTypes.Scim*` constants.
- **Options & Config Reference Ratchets (`OptionsConsumptionInventoryTests`, `ConfigurationReferenceTests`):** Add `RequestsPerMinute` with an XML `<summary>` comment to `ScimRealmSecretOptions` (or `ScimOptions`) in `IdentityRealmsOptions.cs`, consume it in production code, and regenerate `docs/reference/configuration.md` with `AUTHERIS_UPDATE_CONFIG_REFERENCE=1`.

---

## 2. Authentication, Scheme Isolation & Startup Validation (`SEC-AUTH-12`)

### 2.1 Unconditional SCIM Scheme Selection in `GatewayAuthSchemeSelector`
**File:** `src/Autheris.Api/Security/GatewayAuthSchemeSelector.cs`

Currently, line 28 checks `if (authHeader.StartsWith("Bearer ", ...) && GatewayAuthSchemes.IsScimPath(context.Request.Path))`. In `Upstream` or `Both` mode, a request to `/scim/v2/...` without a `Bearer` header (or carrying ForwardAuth headers) could fall through to `GatewayAuthSchemes.ForwardAuth`, and a request with `Basic` or `Negotiate` could fall through to those handlers.

Update `GatewayAuthSchemeSelector.Select` so the path check is unconditional at the very top of the method:

```csharp
// SCIM provisioning routes (/scim/...) accept only the Scim scheme in every Authentication:Mode (Direct, Upstream, Both).
// ForwardAuth headers, JWTs, Basic, Negotiate, and session cookies are never evaluated on SCIM paths.
if (GatewayAuthSchemes.IsScimPath(context.Request.Path))
{
    return GatewayAuthSchemes.Scim;
}
```

And in `src/Autheris.Api/Endpoints/ScimDiscoveryEndpoints.cs`, add `.RequireAuthorization(GatewayPolicies.Scim)` to the route group so discovery routes (`/ServiceProviderConfig`, `/Schemas`, `/ResourceTypes`) enforce the exact same scheme, role, and realm-matching policy as `/Users` and `/Groups`:

```csharp
var group = app.MapGroup("/scim/v2/{realmId}")
    .RequireAuthorization(GatewayPolicies.Scim);
```

### 2.2 Per-Realm Secret Validation & Tenant Binding in `ScimAuthenticationHandler`
**File:** `src/Autheris.Api/Security/ScimAuthenticationHandler.cs`

1. **IP Allowlist First (No Oracle):**
   - Evaluates `ScimIpAllowlist.IsAllowed(_realmOptions.CurrentValue.Scim?.AllowedIpRanges, peer)` before inspecting the realm or secret; returns the uniform `InvalidCredentialsMessage` on failure.
2. **Route `{realmId}` Extraction & Realm Lookup:**
   - Extracts `realmId` from `/scim/v2/{realmId}/...`.
   - Verifies `options.Realms.TryGetValue(realmId, out var realmConfig)` and `realmConfig.Provisioning == RealmProvisioningMode.Scim`.
3. **Constant-Time Secret Comparison Bound to `realmId`:**
   - Validates the bearer token against `realmConfig.Scim.PrimarySecret` and `realmConfig.Scim.SecondarySecret` using `CryptographicOperations.FixedTimeEquals` on UTF-8 bytes.
   - Because secrets are looked up solely on `options.Realms[realmId]`, the secret of `realm-a` is rejected with `401 Unauthorized` when presented against `/scim/v2/realm-b/...` (`Secret_of_realm_a_cannot_write_realm_b`), and `GatewayPolicies.MatchesRouteRealm` provides a second defense-in-depth check on the `"autheris:realm"` claim.
4. **Authoritative Realm Tenant Stamping:**
   - `IdentityPipeline` resolves the realm by `"autheris:realm"` and stamps `"autheris:tenant"` from the realm's `TenantBindingSpecification` (`FixedTenantId` or configured default tenant).
   - Because the SCIM principal carries only `GatewayPolicies.ScimProvisionerRole` (`SCIM_PROVISIONER`) and never `ClusterAdmin`, `X-Tenant-ID` headers are ignored; all SCIM operations are strictly isolated to the realm's bound `TenantId`.

### 2.3 Mandatory `AllowedIpRanges` Outside Development & `RequestsPerMinute` Validation
**File:** `src/Autheris.Domain/Options/Security/IdentityRealmsOptions.cs`

Add `RequestsPerMinute` to `ScimOptions` and `ScimRealmSecretOptions`:
```csharp
public sealed class ScimOptions
{
    public const int DefaultRequestsPerMinute = 600;
    public const int MinRequestsPerMinute = 1;
    public const int MaxRequestsPerMinute = 60_000;

    /// <summary>CIDR ranges or IP addresses permitted to call SCIM 2.0 endpoints. Mandatory outside Development when any realm uses SCIM provisioning.</summary>
    public List<string> AllowedIpRanges { get; set; } = [];

    /// <summary>Default per-realm rate limit in requests per minute for inbound SCIM calls (1..60000, default 600).</summary>
    public int RequestsPerMinute { get; set; } = DefaultRequestsPerMinute;

    /// <summary>Authentication:Realms:Scim:MassChangeProtection. Throttle on SCIM-originated user deprovisioning (06-FU-02).</summary>
    public ScimMassChangeProtectionOptions MassChangeProtection { get; set; } = new();
}
```
And on `ScimRealmSecretOptions`:
```csharp
    /// <summary>Optional per-realm rate limit override in requests per minute for inbound SCIM calls (1..60000). When null, Authentication:Realms:Scim:RequestsPerMinute applies.</summary>
    public int? RequestsPerMinute { get; set; }
```

**File:** `src/Autheris.Application/Identity/Validators/IdentityRealmsScimOptionsValidator.cs`

Replace the Production-only warning on lines 61–67 with a fail-closed startup error outside Development, and validate `RequestsPerMinute`:

```csharp
bool hasScimRealm = options.Realms.Values.Any(r => r.Provisioning == RealmProvisioningMode.Scim);
bool isDevelopment = _environment?.IsDevelopment() == true;

if (hasScimRealm && !isDevelopment && ranges.Count == 0)
{
    return ValidateOptionsResult.Fail(
        "Authentication:Realms:Scim:AllowedIpRanges must contain at least one CIDR range or IP address outside Development when SCIM provisioning is enabled.");
}

var defaultRpm = options.Scim?.RequestsPerMinute ?? ScimOptions.DefaultRequestsPerMinute;
if (defaultRpm is < ScimOptions.MinRequestsPerMinute or > ScimOptions.MaxRequestsPerMinute)
{
    return ValidateOptionsResult.Fail(
        $"Authentication:Realms:Scim:RequestsPerMinute must be between {ScimOptions.MinRequestsPerMinute} and {ScimOptions.MaxRequestsPerMinute}.");
}

foreach (var (realmId, config) in options.Realms)
{
    if (config.Scim?.RequestsPerMinute is { } realmRpm &&
        (realmRpm < ScimOptions.MinRequestsPerMinute || realmRpm > ScimOptions.MaxRequestsPerMinute))
    {
        return ValidateOptionsResult.Fail(
            $"Authentication:Realms:Realms:{realmId}:Scim:RequestsPerMinute must be between {ScimOptions.MinRequestsPerMinute} and {ScimOptions.MaxRequestsPerMinute}.");
    }
    // ... existing PrimarySecret / SecondarySecret length >= 32 and uniqueness checks ...
}
```

---

## 3. Inbound SCIM 2.0 Protocol Endpoints, Rate Limiting, Bounds & Error Handling (RFC 7644)

### 3.1 Per-Realm Rate Limiting & Sync Contact Filter (`ScimEndpointExecutionFilter`)
Create an endpoint helper/filter `ScimEndpointGuard` in `src/Autheris.Api/Endpoints/ScimEndpointGuard.cs` applied to the `/scim/v2/{realmId}` route groups in `ScimUsersEndpoints`, `ScimGroupsEndpoints`, and `ScimDiscoveryEndpoints`:

1. **Per-Realm Rate Limit (`Step 18c`):**
   - Resolve `realmId` from route values and determine effective `requestsPerMinute = realmConfig?.Scim?.RequestsPerMinute ?? scimOptions.RequestsPerMinute` (default `600`).
   - Call `var limitResult = await rateLimiter.CheckPostAuthSidAsync($"scim-client:{realmId}", new PostAuthSidRateLimitOptions { Enabled = true, RequestsPerMinute = requestsPerMinute, BurstCapacity = requestsPerMinute }, ct)`.
   - If `!limitResult.Allowed`:
     - Set `httpContext.Response.Headers.RetryAfter = Math.Max(1, limitResult.RetryAfterSeconds).ToString(CultureInfo.InvariantCulture)`.
     - Return HTTP `429 Too Many Requests` with `contentType: "application/scim+json"`:
       ```json
       {
         "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"],
         "status": "429",
         "detail": "SCIM rate limit exceeded for realm. Retry after the indicated delay."
       }
       ```
2. **Request Body Size Limit (`1 MiB = 1,048,576 bytes`, `Step 18e`):**
   - Attach `.WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1_048_576))` to the `/Users` and `/Groups` route groups.
   - Before reading the body on `POST`, `PUT`, and `PATCH`, if `httpContext.Request.ContentLength is > 1_048_576`, return HTTP `400 Bad Request` (or `413`) with RFC 7644 error body (`scimType: "tooMany"`, `detail: "SCIM request payload exceeds the 1 MiB limit."`). When reading/deserializing throws `BadHttpRequestException` due to body size limit, map to the same RFC 7644 error response.
3. **Sync Contact Tracking (`Step 17` Integration):**
   - After authentication and rate-limit checks pass, call `await syncStateTracker.RecordAuthenticatedContactAsync(tenantId, realmId, isWrite: !HttpMethods.IsGet(httpContext.Request.Method), ct)` so every authenticated SCIM request (including `GET /Users` and `GET /Groups` reconciliation polls from Entra ID) refreshes `last_contact_at` (throttled to once per 30 s per `(tenantId, realmId)` per node).

### 3.2 Patch & Member Count Bounds (`MaxMembersPerRequest = 1000`)
In `src/Autheris.Application/Identity/Validators/ScimRequestValidator.cs`:
- Define `public const int MaxMembersOrOperationsPerRequest = 1000;`.
- Throw a typed `ScimTooManyException : ValidationException` when:
  1. `ScimGroupRequest.Members` has `Count > 1000`.
  2. `ScimPatchRequest.Operations` has `Count > 1000`.
  3. Across all operations in a `ScimPatchRequest`, the total number of member entries in `op.Value` arrays exceeds `1000`.
- In `ScimUsersEndpoints` and `ScimGroupsEndpoints`, map:
  - `ScimTooManyException` $\to$ HTTP `400 Bad Request` with `scimType: "tooMany"`.
  - `ValidationException` $\to$ HTTP `400 Bad Request` with `scimType: "invalidValue"`.
  - `ScimUniquenessConflictException` $\to$ HTTP `409 Conflict` with `scimType: "uniqueness"`.
  - `ScimMassChangeBlockedException` $\to$ HTTP `429 Too Many Requests` with `Retry-After` header (`ScimMassChangeResult.TooManyRequests`).
  - `MassMembershipChangeQuarantineException` $\to$ HTTP `409 Conflict` (or `403 Forbidden`) with RFC 7644 error body.

### 3.3 RFC 7644 Error Response Matrix

| Condition | HTTP Status | `scimType` | Response `Content-Type` | Headers / Notes |
|---|---|---|---|---|
| Missing/invalid token, wrong realm secret, or disallowed IP | `401 Unauthorized` | *(omitted)* | `application/scim+json` | Constant-time check; uniform detail message |
| Non-SCIM scheme (`Bearer` JWT, `ForwardAuth`, `Basic`) | `401 Unauthorized` | *(omitted)* | `application/scim+json` | Blocked by `GatewayAuthSchemeSelector` + `GatewayPolicies.Scim` |
| Unsupported `filter` expression | `400 Bad Request` | `"invalidFilter"` | `application/scim+json` | Constant detail string (`ScimFilterGuard`) |
| Missing/non-canonical/privileged `objectSid`, invalid GUID/UPN/member `type`, self-nesting | `400 Bad Request` | `"invalidValue"` | `application/scim+json` | Enforced by `ScimRequestValidator` |
| `> 1000` patch operations or `> 1000` members in request, or body `> 1 MiB` | `400 Bad Request` | `"tooMany"` | `application/scim+json` | `Oversized_patch_is_rejected` |
| Duplicate `objectSid`, `onPremisesImmutableId`, `entraObjectId`, `upn`, or `externalId` | `409 Conflict` | `"uniqueness"` | `application/scim+json` | RFC 7644 §3.12 (`ScimUniquenessConflictException`) |
| Per-realm rate limit (`RequestsPerMinute`) or `ScimMassChangeGuard` exceeded | `429 Too Many Requests` | *(omitted)* | `application/scim+json` | Includes `Retry-After: <seconds>` header |
| Required audit failure (`IAuditWriter.RecordRequiredAsync` throws) | `500` / `503` | *(omitted)* | `application/scim+json` | Write is refused / rolled back (`Audit_failure_refuses_write`) |

---

## 4. Synchronous Required Auditing (`Step 18d`, `SEC-AUTH-12`)

### 4.1 Audit Event Constants in `src/Autheris.Domain/Audit/AuditEventTypes.cs`
Add the following constants to `AuditEventTypes`:

```csharp
// SCIM 2.0 Provisioning & Identity Correlation (Tier A — Required Audit)
public const string ScimUserProvisioned = "SCIM_USER_PROVISIONED";
public const string ScimUserUpdated = "SCIM_USER_UPDATED";
public const string ScimUserDeprovisioned = "SCIM_USER_DEPROVISIONED";
public const string ScimGroupProvisioned = "SCIM_GROUP_PROVISIONED";
public const string ScimGroupDeleted = "SCIM_GROUP_DELETED";
public const string ScimGroupMembershipChanged = "SCIM_GROUP_MEMBERSHIP_CHANGED";
public const string IdentityCorrelationAmbiguous = "IDENTITY_CORRELATION_AMBIGUOUS";
```

### 4.2 Removing `AuditEventTypes.AuditConfigChanged` from `Scim*.cs` Endpoints
Update the `.WithAudit(...)` declarations in `src/Autheris.Api/Endpoints/ScimUsersEndpoints.cs` and `src/Autheris.Api/Endpoints/ScimGroupsEndpoints.cs`:
- `ScimUsersEndpoints.cs`:
  - `MapPost("")` $\to$ `.WithAudit(AuditLevel.Full, AuditEventTypes.ScimUserProvisioned)`
  - `MapPut("/{id}")` $\to$ `.WithAudit(AuditLevel.Full, AuditEventTypes.ScimUserUpdated)`
  - `MapPatch("/{id}")` $\to$ `.WithAudit(AuditLevel.Full, AuditEventTypes.ScimUserUpdated)`
  - `MapDelete("/{id}")` $\to$ `.WithAudit(AuditLevel.Full, AuditEventTypes.ScimUserDeprovisioned)`
- `ScimGroupsEndpoints.cs`:
  - `MapPost("")` $\to$ `.WithAudit(AuditLevel.Full, AuditEventTypes.ScimGroupProvisioned)`
  - `MapPut("/{id}")` $\to$ `.WithAudit(AuditLevel.Full, AuditEventTypes.ScimGroupMembershipChanged)`
  - `MapPatch("/{id}")` $\to$ `.WithAudit(AuditLevel.Full, AuditEventTypes.ScimGroupMembershipChanged)`
  - `MapDelete("/{id}")` $\to$ `.WithAudit(AuditLevel.Full, AuditEventTypes.ScimGroupDeleted)`

Verification check:
`git grep -n -E 'AuditEventTypes\.AuditConfigChanged' -- src/Autheris.Api/Endpoints/Scim*.cs` must produce **empty output**.

### 4.3 Transactional / Refuse-Write Auditing in `ScimService` (`IAuditWriter.RecordRequiredAsync`)
Inject `IAuditWriter` into `ScimService` (`src/Autheris.Application/Identity/Services/ScimService.cs`). Every SCIM mutation must call `await _auditWriter.RecordRequiredAsync(entry, ct)` **before** committing/returning the result (or roll back / compensate if `RecordRequiredAsync` throws so no un-audited mutation persists):

1. **`CreateUserAsync`:**
   - Write `AuditEventTypes.ScimUserProvisioned` (`TargetTable = "IDENTITY_USERS"`, `ActorSid = new Sid($"scim-client:{realmId}")`, `DetailsJson = { realm_id, user_key, active }`).
2. **`UpdateUserAsync` & `PatchUserAsync`:**
   - When `Active` transitions to `false` (or user is deactivated): write `AuditEventTypes.ScimUserDeprovisioned` (`TargetTable = "IDENTITY_USERS"`, `DetailsJson = { realm_id, user_key, operation = "deactivate" }`).
   - Otherwise: write `AuditEventTypes.ScimUserUpdated` (`TargetTable = "IDENTITY_USERS"`, `DetailsJson = { realm_id, user_key, active }`).
3. **`DeleteUserAsync`:**
   - Write `AuditEventTypes.ScimUserDeprovisioned` (`TargetTable = "IDENTITY_USERS"`, `DetailsJson = { realm_id, user_key, operation = "delete" }`).
4. **`CreateGroupAsync`:**
   - Write `AuditEventTypes.ScimGroupProvisioned` (`TargetTable = "IDENTITY_GROUPS"`, `DetailsJson = { realm_id, group_id, group_sid = entity.ObjectSid }`).
   - For **each** initial member in `group.Members`: write one `AuditEventTypes.ScimGroupMembershipChanged` event via `RecordRequiredAsync` with:
     ```json
     {
       "realm_id": "<realmId>",
       "group_id": "<groupId>",
       "group_sid": "<groupObjectSid>",
       "member_key": "<member.Value>",
       "member_type": "User|Group",
       "operation": "add"
     }
     ```
5. **`UpdateGroupAsync` & `PatchGroupAsync`:**
   - Compute exact delta of added and removed members (`(Value, Type)`).
   - For **each** removed member and **each** added member, write one `AuditEventTypes.ScimGroupMembershipChanged` event via `RecordRequiredAsync` carrying `{ realm_id, group_id, group_sid, member_key, member_type, operation: "add" | "remove" }` (`Every_membership_change_writes_required_audit`).
6. **`DeleteGroupAsync`:**
   - Write `AuditEventTypes.ScimGroupDeleted` (`TargetTable = "IDENTITY_GROUPS"`, `DetailsJson = { realm_id, group_id, group_sid = existing.ObjectSid }`).
7. **Refuse Write on Audit Failure (`Audit_failure_refuses_write`):**
   - Perform the required audit call (`await _auditWriter.RecordRequiredAsync(...)`) before committing the store mutation (or inside the unit-of-work so if `RecordRequiredAsync` throws, the database write is not committed / is rolled back and the exception propagates). Never catch and ignore an `IAuditWriter` exception in `ScimService`.

---

## 5. PR 6: Tenant Provisioning Lifecycle & Cluster-Wide Sync Integration

### 5.1 End-to-End Provisioning & Deprovisioning Sequence

```
Entra ID / AD SCIM Pusher
   │
   ├─► 1. GatewayAuthSchemeSelector.Select (IsScimPath -> GatewayAuthSchemes.Scim)
   ├─► 2. ScimAuthenticationHandler (AllowedIpRanges -> realmId -> constant-time secret)
   ├─► 3. IdentityPipeline (stamps autheris:realm & fixed TenantId; no X-Tenant-ID override)
   ├─► 4. ScimEndpointGuard (CheckPostAuthSidAsync rate limit -> RecordAuthenticatedContactAsync)
   └─► 5. ScimService Mutation:
          ├─► a. ScimRequestValidator (<=1000 members/ops, canonical non-privileged SIDs, no self-nesting)
          ├─► b. ScimMassChangeGuard (user deprovision) / ScimGroupMembershipValidator (>10% privileged removal)
          ├─► c. IAuditWriter.RecordRequiredAsync (refuses write if audit fails)
          ├─► d. IdentityStore Transaction:
          │       - User delete or active=false: deletes IDENTITY_GROUP_MEMBERS rows in same tx
          │       - Group delete: deletes IDENTITY_GROUP_MEMBERS & IDENTITY_GROUP_NESTING rows in same tx
          │       - Group member add/remove: updates IDENTITY_GROUP_MEMBERS or IDENTITY_GROUP_NESTING
          ├─► e. IncrementRealmDeprovisionEpochAsync (bumps SCIM_REALM_SYNC_STATE.deprovision_epoch)
          ├─► f. IdentityDeprovisioningService (lock entry, token revocation, webhook subscription cleanup)
          ├─► g. BroadcastingRoleResolverCache.Invalidate (publishes IdentityGroupsChanged on IEventBus)
          └─► h. Return HTTP 200 / 201 / 204 response to SCIM pusher
```

### 5.2 Background Subscriber & Multi-Node Cache Coherency Lifecycle
1. **Immediate Correctness Backstop via `SCIM_REALM_SYNC_STATE.deprovision_epoch`:**
   - Every node resolving groups for a SCIM-provisioned realm reads `ScimRealmSyncState` and prefixes its `IRoleResolverCache` lookup key with `e{deprovision_epoch}:{userKey}`.
   - Because Step 5e increments `deprovision_epoch` before Step 5h returns the HTTP response, any subsequent data request on **any** node in the cluster immediately observes `deprovision_epoch + 1` and bypasses pre-deprovisioning cache entries — even if Redis pub/sub is unreachable or drops the message.
2. **Low-Latency L1 Cache Eviction via `IdentityGroupsChangedSubscriber` (`IHostedService`):**
   - Subscribes to `IdentityGroupsChangedChannel.Name` (`"autheris:identity:groups-changed"`).
   - Evicts stale `e{oldEpoch}:{userKey}` entries from `RoleResolverCache` (`ConcurrentLru`) immediately so memory stays bounded.
   - Calls `local.InvalidateAll()` on `eventBus.ConnectionRestored`, `failures.DeliveryFailed`, group deletion, or any batch exceeding `256` user keys.
3. **Subject Lock & Webhook/CDC Subscription Cleanup (`IdentityDeprovisioningService` & `LockRoleCacheInvalidationHostedService`):**
   - When a user is deactivated (`active = false`) or deleted via SCIM:
     - `IdentityDeprovisioningService.DeprovisionAndLockSubjectAsync` writes a `ScimLock` (`IDENTITY_LOCKS`), revokes active tokens (`ITokenRevocationService.RevokeAsync`), removes CloudEvent/webhook subscriptions created by the user (`ICloudEventSubscriptionStore`), and publishes `LockPubSubMessage` on `IRedisLockPubSubChannel`.
     - `LockRoleCacheInvalidationHostedService` on all nodes receives `LockPubSubMessage` and invalidates the user's cached role resolution.

---

## 6. Red-Green-Refactor Test Matrix & Verification Commands

### 6.1 Unit & Integration Test Matrix (`[Trait("Category", "Security")]`)

| Test File | Exact Test Method Names | Requirement Verified |
|---|---|---|
| `tests/Autheris.Tests.Unit/Security/ScimEndpointHardeningTests.cs` (New) | 1. `Missing_ip_allowlist_fails_startup_outside_development`<br>2. `Rate_limit_returns_429_with_retry_after`<br>3. `Every_membership_change_writes_required_audit`<br>4. `Audit_failure_refuses_write`<br>5. `Oversized_patch_is_rejected`<br>6. `Jwt_or_forwardauth_never_authenticates_scim_routes`<br>7. `Secret_of_realm_a_cannot_write_realm_b` | Step 18 (`SEC-AUTH-12`): mandatory `AllowedIpRanges` outside Development, per-realm `RequestsPerMinute` 429 + `Retry-After`, per-member `SCIM_GROUP_MEMBERSHIP_CHANGED` via `RecordRequiredAsync`, write refusal on audit failure, 1000-member/1-MiB bounds (`400 tooMany`), scheme isolation across `Direct`/`Upstream`/`Both`, and cross-realm secret rejection. |
| `tests/Autheris.Tests.Unit/Security/AuthSchemeSelectionTests.cs` (Extend) | `Scim_path_selects_scim_in_every_mode` | Verifies `GatewayAuthSchemeSelector.Select` returns `GatewayAuthSchemes.Scim` for `/scim/v2/...` in `Direct`, `Upstream`, and `Both` modes regardless of headers. |
| `tests/Autheris.Tests.Integration/Security/ScimGroupSupplementEndToEndTests.cs` (New / Extend from PR 5b) | 1. `Entra_token_without_group_plus_scim_membership_grants_access`<br>2. `Scim_group_row_filter_applies_to_writes`<br>3. `Stale_sync_denies_data_access`<br>4. `Ambiguous_correlation_denies_data_access`<br>5. `Deprovisioning_revokes_access_on_second_node_immediately`<br>6. `Usage_is_attributed_to_scim_group`<br>7. `Recursive_cte_matches_on_both_engines` | Full lifecycle integration from inbound SCIM HTTP push (`/scim/v2/{realmId}/Users` & `/Groups`) through `IdentityPipeline`, `GroupSidResolver`, `IAccessDecisionPoint`, row-filter/masking enforcement, and cluster-wide deprovisioning revocation. |

### 6.2 Local Filtered Verification Commands (Resource-Constrained)

```bash
# 1. Run SCIM endpoint hardening and scheme selection unit tests
NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj \
  --filter "FullyQualifiedName~ScimEndpointHardeningTests|FullyQualifiedName~AuthSchemeSelectionTests|FullyQualifiedName~ScimIdentityCorrelatorTests|FullyQualifiedName~ScimEffectiveGroupsTests|FullyQualifiedName~ScimDeprovisioningTests|FullyQualifiedName~ScimSecurityTests" \
  -m:2 -nodeReuse:false

# 2. Regenerate docs/reference/configuration.md and verify architecture ratchets
AUTHERIS_UPDATE_CONFIG_REFERENCE=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj \
  --filter "FullyQualifiedName~ConfigurationReferenceTests|FullyQualifiedName~OptionsConsumptionInventoryTests|FullyQualifiedName~AuditEndpointCoverageTests|FullyQualifiedName~EndpointSecurityCoverageTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~SecurityTestCoverageTests|FullyQualifiedName~DocsIntegrityTests" \
  -m:2 -nodeReuse:false

# 3. Verify zero remaining AuditConfigChanged usages on SCIM endpoints (expect empty output)
git grep -n -E 'AuditEventTypes\.AuditConfigChanged' -- src/Autheris.Api/Endpoints/Scim*.cs

# 4. Compile-only verification of integration tests (never execute integration tests locally)
NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj \
  --no-restore -m:2 -nodeReuse:false
```

---

## 7. Ordered Implementation Checklist for Phase 4 (TDD Developer)

1. **Red — Write Failing Unit Tests:**
   - Create `tests/Autheris.Tests.Unit/Security/ScimEndpointHardeningTests.cs` (`[Trait("Category", "Security")]`) with all 7 mandatory tests from §6.1, and verify `Scim_path_selects_scim_in_every_mode` in `AuthSchemeSelectionTests.cs`.
2. **Green — Scheme Isolation & Startup IP Allowlist Validation:**
   - Update `src/Autheris.Api/Security/GatewayAuthSchemeSelector.cs` so `if (GatewayAuthSchemes.IsScimPath(context.Request.Path)) return GatewayAuthSchemes.Scim;` runs before any mode/header branch.
   - Add `.RequireAuthorization(GatewayPolicies.Scim)` to `ScimDiscoveryEndpoints.cs`.
   - Add `RequestsPerMinute` (with XML `<summary>` doc comments) to `ScimOptions` and `ScimRealmSecretOptions` in `src/Autheris.Domain/Options/Security/IdentityRealmsOptions.cs`.
   - Update `src/Autheris.Application/Identity/Validators/IdentityRealmsScimOptionsValidator.cs` to fail startup when `AllowedIpRanges` is empty outside `Development` and validate `RequestsPerMinute` (`1..60000`).
3. **Green — Rate Limiting, Payload Bounds & Required Auditing:**
   - Add the `ScimUserProvisioned`, `ScimUserUpdated`, `ScimUserDeprovisioned`, `ScimGroupProvisioned`, `ScimGroupDeleted`, and `ScimGroupMembershipChanged` constants to `src/Autheris.Domain/Audit/AuditEventTypes.cs`.
   - Update `ScimService.cs` to write required audit events via `IAuditWriter.RecordRequiredAsync` before committing mutations (including one `ScimGroupMembershipChanged` per added/removed member) so audit failures refuse the write.
   - Create `ScimEndpointGuard.cs` and wire per-realm rate limiting (`IRateLimiterService.CheckPostAuthSidAsync("scim-client:{realmId}")` returning `429` with `Retry-After`), 1 MiB body limit, `1000`-member/operation PATCH cap (`400 scimType: "tooMany"`), and `IScimSyncStateTracker.RecordAuthenticatedContactAsync` into `ScimUsersEndpoints.cs`, `ScimGroupsEndpoints.cs`, and `ScimDiscoveryEndpoints.cs`.
   - Replace all `AuditEventTypes.AuditConfigChanged` references in `ScimUsersEndpoints.cs` and `ScimGroupsEndpoints.cs`.
4. **Green — PR 6 End-to-End Integration Suite & Ratchets:**
   - Complete `tests/Autheris.Tests.Integration/Security/ScimGroupSupplementEndToEndTests.cs` and verify compile-only build.
   - Regenerate `docs/reference/configuration.md` (`AUTHERIS_UPDATE_CONFIG_REFERENCE=1`) and update `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt`.
   - Run all verification commands in §6.2.
