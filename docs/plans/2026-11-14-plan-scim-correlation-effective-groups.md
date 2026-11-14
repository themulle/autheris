# Implementation Plan: WP-SIMPL-43 PR 5b — SCIM Correlation, Effective Groups, Staleness & Deprovisioning (Steps 14–17)

- **Date:** 2026-11-14
- **Work Package:** WP-SIMPL-43 (Authentication & Authorization Core Simplification)
- **PR Slice:** PR 5b — SCIM Correlation, Effective Groups, Staleness & Deprovisioning (Steps 14–17)
- **Parent Spec:** `docs/plans/simplification/WP-SIMPL-43-authentication-authorization-core.md` (§4.3a, §5 Steps 14–17, §6, §9 PR 5b)
- **Prerequisite Plans:** `docs/plans/2026-11-14-plan-scim-identity-model-schema.md` (PR 5a — Step 13), PR 3a & PR 3b
- **Owner Decisions:** `OD-27`, `OD-31`, `OD-37`
- **Security Conditions:** `SEC-AUTH-04`, `SEC-AUTH-09`, `SEC-AUTH-10`, `SEC-AUTH-11`, `SEC-AZ-09`, `SEC-AZ-14`, `SEC-SIMPL-01`, `SEC-SIMPL-02`
- **Target Framework:** .NET 10 / C# 14

---

## 1. Preconditions, Dependencies & Architectural Guardrails

### 1.1 Upstream Preconditions
1. **PR 3a & PR 3b (Merged on `main`):**
   - `SidFormat` (`IsCanonical`, `TryFromEntraObjectId`, `TryFromBinary`, `IsPrivileged`) in `src/Autheris.Domain/Common/SidFormat.cs`.
   - `GroupSidResolver` (`src/Autheris.Application/Identity/GroupSidResolver.cs`) and `IdentityPipeline` (`src/Autheris.Api/Security/IdentityPipeline.cs`) stamping `ClaimTypes.GroupSid` with `IdentityClaimIssuers.Pipeline`.
   - `RoleResolverCache` and `BroadcastingRoleResolverCache` (`src/Autheris.Application/Identity/Caching/`).
2. **PR 5a (Step 13 — `docs/plans/2026-11-14-plan-scim-identity-model-schema.md`):**
   - Migration step `0012_scim_identity_correlation` adding:
     - `IDENTITY_USERS`: `object_sid`, `immutable_id`, `upn`, `entra_object_id` with filtered unique indexes per `(tenant_id, realm_id, <col>)`.
     - `IDENTITY_GROUPS`: `object_sid` with filtered unique index per `(tenant_id, realm_id, object_sid)`.
     - `IDENTITY_GROUP_NESTING(tenant_id, realm_id, parent_group_id, child_group_id)`.
     - `SCIM_REALM_SYNC_STATE(tenant_id, realm_id, last_contact_at, last_write_at, deprovision_epoch)`.
   - Segregated store contracts `IScimUserStore`, `IScimGroupStore`, `IScimProvisioningStore` composed into `IIdentityStoreRepository`.

### 1.2 Architectural & Ratchet Guardrails
- **No `WP-`, `SEC-`, or `OD-` strings in `src/Autheris.Api` or `src/Autheris.Application`:** Enforced by `CleanArchitectureTests` (`plan-id-comment-counts.txt` is `0` for both projects).
- **No Raw Group Claim Reads Outside Pipeline:** `IdentityPipelineArchitectureTests.No_code_reads_raw_groups_claims_outside_pipeline` scans for `"groups"` and `"groupsid"` string literals. `ScimIdentityCorrelator` reads only subject/correlation claims (`onprem_sid`, `primarysid`, `onPremisesImmutableId`, `oid`, `upn`, `preferred_username`) and never reads group claims.
- **Options Inventory & Configuration Reference Ratchets:**
  - Every new property added to `ScimRealmSecretOptions` in `src/Autheris.Domain/Options/Security/IdentityRealmsOptions.cs` must have an XML `<summary>` doc comment (`ConfigurationReferenceTests.Every_option_has_a_description`), must be read in production code (`OptionsConsumptionInventoryTests.UnconsumedOptionProperties_MustBeEmpty`), and `docs/reference/configuration.md` must be regenerated with `AUTHERIS_UPDATE_CONFIG_REFERENCE=1`.
  - Do not add any new `IOptions<GatewayOptions>` constructor injections (`GatewayOptionsConsumptionRatchetTests` ceiling is `210`).
- **Security Test Ratchet:** All new unit test classes must carry `[Trait("Category", "Security")]` and update `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt`.

---

## 2. Step 15: SCIM Identity Correlation Engine (`SEC-AUTH-10`, `OD-27`)

### 2.1 Correlation Precedence & Ambiguity Rules
Create `IScimIdentityCorrelator` and `ScimIdentityCorrelator` in `src/Autheris.Application/Identity/ScimIdentityCorrelator.cs`.

#### Precedence Order of Token Claims
All correlation keys present on the normalized `ClaimsPrincipal` are extracted and evaluated in strict precedence order (from strongest to weakest):

| Rank | Key Name | Inbound Token Claim Types Checked (in order) | Store Lookup on `IScimUserStore` (`(tenantId, realmId)`) | Enabled Condition |
|---|---|---|---|---|
| **1 (Strongest)** | `objectSid` | 1. `"onprem_sid"`<br>2. `"primarysid"`<br>3. `ClaimTypes.PrimarySid` (`"http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid"`) | `GetUserByObjectSidAsync(tenantId, realmId, claimValue, ct)` (must pass `SidFormat.IsCanonical`) | Always enabled when claim is present |
| **2** | `onPremisesImmutableId` | 1. `"onPremisesImmutableId"`<br>2. `"immutable_id"` | `GetUserByImmutableIdAsync(tenantId, realmId, claimValue, ct)` | Always enabled when claim is present |
| **3** | `entraObjectId` / `externalId` | 1. `"oid"`<br>2. `"http://schemas.microsoft.com/identity/claims/objectidentifier"` | `GetUsersByEntraObjectIdOrExternalIdAsync(tenantId, realmId, oidValue, ct)`: queries `WHERE tenant_id = @tid AND realm_id = @rid AND (entra_object_id = @oid OR external_id = @oid)` | Always enabled when claim is present |
| **4 (Weakest)** | `userPrincipalName` | 1. `"upn"`<br>2. `ClaimTypes.Upn`<br>3. `"preferred_username"` | `GetUsersByUpnOrUserNameAsync(tenantId, realmId, normalizedUpn, ct)`: queries `WHERE tenant_id = @tid AND realm_id = @rid AND (upn = @upn OR LOWER(user_name) = @upn)` (ordinal ignore-case) | **Only** when `realm.Scim.AllowUpnCorrelation == true` (default `false`; ignored completely when `false`) |

> **Note on Precedence Order:** Both `entraObjectId` (`oid`) and `userPrincipalName` (`upn`, when `AllowUpnCorrelation = true`) are evaluated together with `objectSid` and `onPremisesImmutableId` under the strict agreement and anti-downgrade rules below. Thus, whether a deployment considers `upn` above or below `oid`, any disagreement between present keys or any unmatched stronger key when a weaker key matches is rejected as `Ambiguous`. Ordering `objectSid` (1) > `onPremisesImmutableId` (2) > `oid`/`entraObjectId` (3) > `upn` (4, opt-in) ensures that when `AllowUpnCorrelation = false` (default), `oid` correlates without being blocked by an unprovisioned `upn` claim, and when `AllowUpnCorrelation = true`, an unmatched immutable hardware/directory key (`objectSid`, `onPremisesImmutableId`, `oid`) always blocks fallback to a mutable/reusable `upn`. If `oid` is absent and `AllowUpnCorrelation = true`, `upn` correlates only if no higher-rank key (`objectSid`, `onPremisesImmutableId`) was present and unmatched. (Furthermore, if `entra_object_id` and `upn` are both present when `AllowUpnCorrelation = true`, both must resolve to the exact same SCIM user or correlation is `Ambiguous`).

#### Strict Fail-Closed Evaluation Rules (`SEC-AUTH-10`)
1. **Evaluate all present enabled keys:** Do not short-circuit on the first match. Every enabled key present in the token must be looked up in `IScimUserStore`.
2. **Single-key multi-user ambiguity (`Key_matching_two_users_is_ambiguous`):**
   - If any single key matches more than one distinct SCIM user row (for example, `oid` matches User A's `entra_object_id` and User B's `external_id`, or `upn` matches User A's `upn` and User B's `user_name`), return `CorrelationStatus.Ambiguous` (`Reason = "key_matched_multiple_users"`).
3. **Cross-key disagreement ambiguity (`Keys_matching_different_users_are_ambiguous`):**
   - Every present enabled key that matches must resolve to the **exact same** `ScimUser.Id`. If Key $i$ matches User A and Key $j$ matches User B ($A \neq B$), return `CorrelationStatus.Ambiguous` (`Reason = "keys_matched_different_users"`).
4. **Anti-downgrade / unmatched stronger key rule (`Unmatched_stronger_key_with_matching_weaker_key_is_ambiguous`):**
   - If a higher-precedence key (e.g. Rank 1 `objectSid` or Rank 2 `onPremisesImmutableId`) is present in the token and matches **0** SCIM users, while a lower-precedence key (e.g. Rank 3 `oid` or Rank 4 `upn`) matches a SCIM user, return `CorrelationStatus.Ambiguous` (`Reason = "unmatched_stronger_key"`). This prevents a token belonging to a newly created or different on-premises identity from correlating to another user who happens to share a recycled UPN or externalId.
   - Similarly, if a higher-precedence key in the token has a malformed format (e.g. `onprem_sid` is present but fails `SidFormat.IsCanonical`), treat it as an invalid/ambiguous key and return `CorrelationStatus.Ambiguous` (`Reason = "malformed_correlation_claim"`).
5. **Required Audit on Ambiguity:**
   - Whenever correlation yields `CorrelationStatus.Ambiguous`, write a required audit entry via `IAuditWriter.RecordRequiredAsync` with `EventType = "IDENTITY_CORRELATION_AMBIGUOUS"` (`AuditEventTypes.IdentityCorrelationAmbiguous`), `Decision = "DENY"`, `TargetTable = "IDENTITY_USERS"`, and `DetailsJson` containing only `{ "realm_id": realm.Id, "reason": reason, "present_keys": ["onprem_sid", ...] }` (never logging raw claim values or PII).
6. **No Match (`NotProvisioned`):**
   - When no enabled key matches any SCIM user:
     - Return `CorrelationStatus.NotProvisioned`.
     - In `GroupSidResolver`: if `realm.Scim.RequireCorrelation == true` (default `true` for realms with `Provisioning == RealmProvisioningMode.Scim`), resolution is `GroupResolutionStatus.Degraded` (`"scim_not_provisioned"`); if `RequireCorrelation == false`, resolution uses token groups only (`GroupResolutionStatus.Complete`).
7. **Inactive or Locked User (`Inactive_user_is_forbidden`):**
   - When all matched keys agree on a single `ScimUser`, check:
     - `!user.Active || user.DeactivatedAt.HasValue || await _store.IsIdentityLockedAsync(tenantId, realm.Id, user.Id, ct)`
     - If inactive or locked, return `CorrelationStatus.Deprovisioned` (`ScimUserKey = user.Id`, `Reason = "user_inactive_or_locked"`).
     - `IdentityPipeline` marks the principal with a deprovisioned/degraded claim so the request is denied with `403 Forbidden`.

### 2.2 C# Contract for `ScimIdentityCorrelator`
**File:** `src/Autheris.Application/Identity/ScimIdentityCorrelator.cs`

```csharp
namespace Autheris.Application.Identity;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;

public enum CorrelationStatus
{
    Matched = 1,
    NotProvisioned = 2,
    Ambiguous = 3,
    Deprovisioned = 4
}

public sealed record CorrelationResult(
    CorrelationStatus Status,
    string? ScimUserKey = null,
    string? MatchedByKey = null,
    string? FailureReason = null)
{
    public static CorrelationResult Matched(string scimUserKey, string matchedByKey) =>
        new(CorrelationStatus.Matched, ScimUserKey: scimUserKey, MatchedByKey: matchedByKey);

    public static CorrelationResult NotProvisioned(string reason = "no_matching_scim_user") =>
        new(CorrelationStatus.NotProvisioned, FailureReason: reason);

    public static CorrelationResult Ambiguous(string reason) =>
        new(CorrelationStatus.Ambiguous, FailureReason: reason);

    public static CorrelationResult Deprovisioned(string scimUserKey, string reason = "user_inactive_or_locked") =>
        new(CorrelationStatus.Deprovisioned, ScimUserKey: scimUserKey, FailureReason: reason);
}

public interface IScimIdentityCorrelator
{
    ValueTask<CorrelationResult> CorrelateAsync(
        ClaimsPrincipal normalized,
        IdentityRealm realm,
        TenantId tenant,
        CancellationToken ct = default);
}
```

---

## 3. Step 14: Nested Group Resolution Algorithm (`OD-27`, `SEC-AUTH-09`)

### 3.1 Edge Semantics & Direction
In SCIM 2.0 (`POST/PUT/PATCH /scim/v2/{realmId}/Groups`), when parent group $P$ lists child group $C$ in `P.members` (`type: "Group", value: C.id`):
- PR 5a stores the row `(tenant_id, realm_id, parent_group_id = P.id, child_group_id = C.id)` in `IDENTITY_GROUP_NESTING`.
- If User $U$ is a member of child group $C$ (`IDENTITY_GROUP_MEMBERS(group_id = C.id, user_key = U.id)`), then User $U$ is transitively a member of parent group $P$.
- Therefore, transitive membership resolution starts at User $U$'s direct groups (`IDENTITY_GROUP_MEMBERS.group_id`) at `depth = 0`, and at each recursive step $d \to d + 1$ traverses `IDENTITY_GROUP_NESTING` from `child_group_id` to `parent_group_id`.

### 3.2 Store Contract (`IScimGroupStore` / `IIdentityStoreRepository`)
Add to `IScimGroupStore` in `src/Autheris.Domain/Interfaces/IIdentityStoreRepository.cs`:

```csharp
public sealed record EffectiveScimGroupsResult(
    IReadOnlySet<string> GroupObjectSids,
    bool MaxDepthExceeded,
    bool HasNonCanonicalSid = false);

// Inside IScimGroupStore:
Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(
    TenantId tenantId,
    string realmId,
    string userKey,
    int maxDepth = 32,
    CancellationToken ct = default);
```

### 3.3 Cycle-Safe Recursive CTE (PostgreSQL & SQL Server) with Max Depth 32
In `src/Autheris.Infrastructure/Persistence/IdentityStore.cs`:
- **`MaxNestingDepth = 32`:**
  - Direct group memberships in `IDENTITY_GROUP_MEMBERS` start at `depth = 0`.
  - Each hop through `IDENTITY_GROUP_NESTING` increments `depth` by `1`.
  - The recursive member continues while `gt.depth <= @maxDepth` (allowing up to `depth = maxDepth + 1 = 33` to be detected).
  - Cycle detection is enforced inside the recursive `WHERE` clause using the delimited visited path `|<group_id>|` so a cycle (`G1 -> G2 -> G1`) terminates immediately without spinning to `maxDepth + 1`.
  - If any row produced by the CTE has `depth > maxDepth` (i.e. a non-cyclic chain of 33+ nesting hops), `GetEffectiveGroupSidsForUserAsync` returns `new EffectiveScimGroupsResult( EmptySet, MaxDepthExceeded: true )` -> `GroupSidResolver` marks the resolution `Degraded` (`"scim_nesting_too_deep"`).
  - If any reached group in `IDENTITY_GROUPS` has a null or non-canonical `object_sid` (`!SidFormat.IsCanonical(sid)`), `GetEffectiveGroupSidsForUserAsync` returns `HasNonCanonicalSid: true` -> `GroupSidResolver` marks the resolution `Degraded`.

#### PostgreSQL Recursive CTE (`DatabaseDialect.PostgreSql`)
```sql
WITH RECURSIVE group_tree (group_id, depth, path) AS (
    SELECT m.group_id, 0 AS depth, '|' || m.group_id || '|' AS path
    FROM IDENTITY_GROUP_MEMBERS m
    WHERE m.tenant_id = @tid AND m.realm_id = @rid AND m.user_key = @uid
    UNION ALL
    SELECT n.parent_group_id, gt.depth + 1, gt.path || n.parent_group_id || '|'
    FROM group_tree gt
    INNER JOIN IDENTITY_GROUP_NESTING n
        ON n.tenant_id = @tid
       AND n.realm_id = @rid
       AND n.child_group_id = gt.group_id
    WHERE gt.depth <= @maxDepth
      AND POSITION('|' || n.parent_group_id || '|' IN gt.path) = 0
)
SELECT g.object_sid, MAX(gt.depth) AS max_depth
FROM group_tree gt
INNER JOIN IDENTITY_GROUPS g
    ON g.tenant_id = @tid
   AND g.realm_id = @rid
   AND g.id = gt.group_id
GROUP BY g.id, g.object_sid;
```

#### SQL Server Recursive CTE (`DatabaseDialect.SqlServer`)
```sql
WITH group_tree (group_id, depth, path) AS (
    SELECT m.group_id, 0 AS depth, CAST('|' + m.group_id + '|' AS NVARCHAR(MAX)) AS path
    FROM IDENTITY_GROUP_MEMBERS m
    WHERE m.tenant_id = @tid AND m.realm_id = @rid AND m.user_key = @uid
    UNION ALL
    SELECT n.parent_group_id, gt.depth + 1, CAST(gt.path + n.parent_group_id + '|' AS NVARCHAR(MAX))
    FROM group_tree gt
    INNER JOIN IDENTITY_GROUP_NESTING n
        ON n.tenant_id = @tid
       AND n.realm_id = @rid
       AND n.child_group_id = gt.group_id
    WHERE gt.depth <= @maxDepth
      AND CHARINDEX('|' + n.parent_group_id + '|', gt.path) = 0
)
SELECT g.object_sid, MAX(gt.depth) AS max_depth
FROM group_tree gt
INNER JOIN IDENTITY_GROUPS g
    ON g.tenant_id = @tid
   AND g.realm_id = @rid
   AND g.id = gt.group_id
GROUP BY g.id, g.object_sid
OPTION (MAXRECURSION 100);
```

### 3.4 Updating `ScimStoreRoleProvider` (`src/Autheris.Application/Identity/Providers/ScimStoreRoleProvider.cs`)
Update `ScimStoreRoleProvider.GetRolesAsync`:
- Call `_repository.GetEffectiveGroupSidsForUserAsync(identity.TenantId, realm.Id, identity.UserKey, maxDepth: 32, cancellationToken)`.
- If `result.MaxDepthExceeded || result.HasNonCanonicalSid`, return `ProviderRoleResult.FailedResult` (which causes `RoleResolver` to mark the resolution `Degraded`, fail-closed).
- Otherwise return `result.GroupObjectSids` (canonical SIDs only, never internal group IDs or display names).

---

## 4. Step 16: Effective Groups Union Logic (`SEC-AUTH-11`, `OD-27`, `OD-31`)

### 4.1 Union Formula & Source Composition in `GroupSidResolver`
In `src/Autheris.Application/Identity/GroupSidResolver.cs`:
1. **Primary Resolution (`tokenOutcome`):**
   - Resolves groups from the authentication scheme or resolver chain as in PR 3a/3b:
     - `RealmGroupSource.Token` (e.g., Entra JWT `groups` + Graph overage, AD FS `groupsid`, Windows Negotiate PAC, Basic configured `GroupSids`, ForwardAuth `Header`).
     - `RealmGroupSource.Resolver` (realm `ResolverChain`).
2. **SCIM Supplement Resolution (when `realm.Provisioning == RealmProvisioningMode.Scim`):**
   - For every realm with `Provisioning == RealmProvisioningMode.Scim`:
     - Run SCIM correlation (`IScimIdentityCorrelator.CorrelateAsync`), staleness evaluation, and effective SCIM group lookup (`GetEffectiveGroupSidsForUserAsync` for the correlated `ScimUserKey`).
     - Note: Even if `realm.GroupSource == RealmGroupSource.Resolver` with `ResolverChain = ["scim-store"]`, routing SCIM-provisioned realms through `ScimIdentityCorrelator` + staleness check ensures that `onprem_sid` / `immutable_id` / `oid` correlation, `MaxSyncAgeMinutes` staleness, and `deprovision_epoch` apply uniformly whether `GroupSource` is `Token` or `Resolver`!
   - **Union Rule:**
     $$\text{GroupResolution.GroupSids} = \text{tokenGroupSids} \cup \text{scimEffectiveGroupSids}$$
   - **Degraded Propagation Rule:**
     - If either the token/scheme resolution OR the SCIM supplement resolution is `Degraded`, the combined `GroupResolution.Status` is `GroupResolutionStatus.Degraded` and `GroupResolution.GroupSids` is empty (`new HashSet<Sid>()`).
   - **Cap Enforcement:**
     - If `combinedSids.Count > _maxGroups` (`Authentication:MaxGroupSidsPerCaller`, default `1024`), clear `combinedSids` and return `GroupResolutionStatus.Degraded`.
   - **Source Label:**
     - Fixed metric/resolution source label: e.g. `"entra+scim"`, `"adfs+scim"`, `"resolver+scim"`, or `"scim"`.
3. **Strict Exclusion of Plugin-Enriched SIDs (`SEC-AUTH-11`, `OD-31`, `SEC-AZ-14`):**
   - `GroupSidResolver` never accepts or reads `SecurityContext.ControlPlaneEnrichment` (`IAuthRoleEnricher.EnrichRolesAsync`).
   - All downstream data-plane consumers (the PDP `IAccessDecisionPoint`, `ConsentResolutionService`, row filters, column masking, column deny, write grants, usage accounting, catalog discovery, and Envoy `ext_authz`) read only `SecurityContext.GroupSids` (populated exclusively from `IdentityClaimIssuers.Pipeline` claims emitted by `IdentityPipeline`).

---

## 5. Step 17: Staleness Check & Cluster-Wide Deprovisioning (`SEC-AUTH-04`, `SEC-AUTH-09`, `OD-27`, `OD-37`)

### 5.1 Realm SCIM Configuration Options
In `src/Autheris.Domain/Identity/IdentityRealm.cs`:
```csharp
public enum ScimStaleSyncBehavior
{
    Degrade = 1,
    DropScimGroups = 2
}

public sealed record RealmScimSettings(
    bool AllowUpnCorrelation = false,
    bool RequireCorrelation = true,
    int MaxSyncAgeMinutes = 180,
    ScimStaleSyncBehavior StaleSyncBehavior = ScimStaleSyncBehavior.Degrade);
```
Add `RealmScimSettings ScimSettings` (with default `new RealmScimSettings()`) to `IdentityRealm` (preserving backward-compatible constructor overloads).

In `src/Autheris.Domain/Options/Security/IdentityRealmsOptions.cs`, extend `ScimRealmSecretOptions`:
```csharp
public sealed class ScimRealmSecretOptions
{
    public const int MinMaxSyncAgeMinutes = 10;
    public const int MaxMaxSyncAgeMinutes = 10080;
    public const int DefaultMaxSyncAgeMinutes = 180;

    /// <summary>Primary bearer secret for inbound SCIM 2.0 provisioning requests to this realm (minimum 32 characters).</summary>
    public string? PrimarySecret { get; set; }

    /// <summary>Optional secondary bearer secret accepted during zero-downtime SCIM token rotation.</summary>
    public string? SecondarySecret { get; set; }

    /// <summary>Whether SCIM identity correlation may fall back to matching UPN or userName when stronger keys are absent. Default false.</summary>
    public bool AllowUpnCorrelation { get; set; } = false;

    /// <summary>Whether an authenticated caller in a SCIM-provisioned realm must match a provisioned SCIM user. Default true.</summary>
    public bool RequireCorrelation { get; set; } = true;

    /// <summary>Maximum age in minutes since the last authenticated SCIM contact before the realm's SCIM sync is considered stale (10..10080, default 180).</summary>
    public int MaxSyncAgeMinutes { get; set; } = DefaultMaxSyncAgeMinutes;

    /// <summary>Behavior when SCIM sync is older than MaxSyncAgeMinutes: Degrade (default, denies data access) or DropScimGroups (uses token groups only when no SCIM-only group carries a deny).</summary>
    public ScimStaleSyncBehavior StaleSyncBehavior { get; set; } = ScimStaleSyncBehavior.Degrade;
}
```

#### Startup Validation (`src/Autheris.Application/Identity/Validators/IdentityRealmOptionsValidator.cs`)
- Validate for every realm in `IdentityRealmsOptions.Realms`:
  - If `config.Scim is not null`:
    - `config.Scim.MaxSyncAgeMinutes` must be `>= 10 and <= 10080`; otherwise throw `ValidationException("Authentication:Realms:Realms:{realmId}:Scim:MaxSyncAgeMinutes must be between 10 and 10080.")`.
    - `Enum.IsDefined(config.Scim.StaleSyncBehavior)` must be `true`.
    - When `config.Scim.StaleSyncBehavior == ScimStaleSyncBehavior.DropScimGroups`, emit a startup `ILogger.LogWarning` in `IdentityRealmRegistry.Initialize` (or `AuthenticationStartupChecks`) warning that grants assigned to SCIM-only groups will not be applied while the SCIM sync is stale, whereas denies on SCIM-only groups still degrade and deny access.

### 5.2 Throttled Contact Refresh & Sync State Tracker
Create `IScimSyncStateTracker` / `ScimSyncStateTracker` in `src/Autheris.Application/Identity/Services/ScimSyncStateTracker.cs`:
1. **Contact & Write Recording (Throttled to once per 30 s per `(tenantId, realmId)` per node):**
   - `RecordAuthenticatedContactAsync(TenantId tenantId, string realmId, bool isWrite, CancellationToken ct)`:
     - Invoked on every successfully authenticated SCIM request in `ScimUsersEndpoints`, `ScimGroupsEndpoints`, and `ScimDiscoveryEndpoints` (or centrally in `ScimAuthenticationHandler` / endpoint filter after `GatewayPolicies.Scim` succeeds).
     - `GET` requests (`Get_requests_refresh_last_contact`) call `RecordAuthenticatedContactAsync(tenantId, realmId, isWrite: false, ct)`.
     - If `isWrite == false` and the node has already flushed `last_contact_at` for `(tenantId, realmId)` within the last 30 seconds (`TimeProvider.GetUtcNow()`), skip the DB round-trip.
     - Otherwise call `_store.UpsertRealmSyncStateAsync(tenantId, realmId, lastContactAt: now, lastWriteAt: isWrite ? now : null, ct)` and update the node-local throttle timestamp.
2. **Staleness Evaluation (`MaxSyncAgeMinutes`):**
   - When resolving SCIM groups for a realm with `Provisioning == RealmProvisioningMode.Scim`:
     - Read `ScimRealmSyncState? state = await _store.GetRealmSyncStateAsync(tenant, realm.Id, ct)`.
     - The sync is **stale** if `state?.LastContactAt is null` OR `(_time.GetUtcNow() - state.LastContactAt.Value) > TimeSpan.FromMinutes(realm.ScimSettings.MaxSyncAgeMinutes)`.
3. **Stale Sync Behavior (`Degrade` vs. `DropScimGroups` with `IGroupDenyInspector`, `OD-37`, `SEC-AZ-09`, `SEC-AUTH-04`):**
   - If `realm.ScimSettings.StaleSyncBehavior == ScimStaleSyncBehavior.Degrade` (default):
     - Return `GroupResolutionStatus.Degraded` (`Source = "scim-stale"`). Data access is denied (`403 identity.groups_unresolved`).
   - If `realm.ScimSettings.StaleSyncBehavior == ScimStaleSyncBehavior.DropScimGroups`:
     - Compute the user's last-known SCIM-only groups: `scimOnlySids = scimGroupSids.Except(tokenGroupSids)`.
     - If `scimOnlySids.Count == 0`: return `tokenGroupSids` (`GroupResolutionStatus.Complete`).
     - If `scimOnlySids.Count > 0`: inspect whether any group in `scimOnlySids` carries a deny via `IGroupDenyInspector.AnyGroupCarriesDenyAsync(tenant, scimOnlySids, ct)`:
       - Create `IGroupDenyInspector` and `GovernanceGroupDenyInspector` in `src/Autheris.Application/Identity/GroupDenyInspector.cs`:
         ```csharp
         public interface IGroupDenyInspector
         {
             ValueTask<bool> AnyGroupCarriesDenyAsync(
                 TenantId tenantId,
                 IReadOnlySet<Sid> groupSids,
                 CancellationToken ct = default);
         }
         ```
       - `GovernanceGroupDenyInspector` checks active consents (`IConsentRepository` / `IGovernanceRepository`) and active virtual-filter access profiles (`IVirtualFilterRepository`) in `tenantId`:
         1. Any active non-revoked `Consent` with `GranteeType == GranteeType.Group && GranteeSid is { } sid && groupSids.Contains(sid)` where:
            - `Effect == ConsentEffect.Deny` (table-level deny grant), OR
            - `ColumnRules.Any(r => r.AccessLevel == ColumnAccessLevel.Deny)` (column deny rule).
         2. Any active `VirtualFilterAccessProfile` (`Status == FilterApprovalStatus.Active && !PendingDeletion`) with `GranteeType == GranteeType.Group && GranteeSid is { } vfSid && groupSids.Contains(vfSid)` where `Uncovered == UncoveredPolicy.Deny` or `Bindings.Count > 0` (virtual-filter deny/restriction assigned to that group).
       - If `AnyGroupCarriesDenyAsync` returns `true` (or throws an exception — fail closed!):
         - Return `GroupResolutionStatus.Degraded` (`Source = "scim-stale-deny"`). **A SCIM-only group carrying a deny is never dropped.**
       - If `AnyGroupCarriesDenyAsync` returns `false`:
         - Drop `scimOnlySids` and return `tokenGroupSids` only (`GroupResolutionStatus.Complete`).

### 5.3 Cluster-Wide Deprovisioning & Store Epoch (`SCIM_REALM_SYNC_STATE.deprovision_epoch`)
1. **Atomic Membership Removal on Deactivation & Deletion (`IdentityStore.cs`):**
   - `DeleteUserAsync(tenantId, realmId, userId, ct)`: already deletes from `IDENTITY_GROUP_MEMBERS` and `IDENTITY_USERS` in one transaction; also increment `deprovision_epoch` inside or immediately after the transaction before returning.
   - `DeactivateUserAsync(tenantId, realmId, userId, ct)` AND `UpdateUserAsync(tenantId, realmId, user, ct)` when `!user.Active`:
     - Execute inside a single database transaction:
       1. `DELETE FROM IDENTITY_GROUP_MEMBERS WHERE tenant_id = @tid AND realm_id = @rid AND user_key = @id;`
       2. `UPDATE IDENTITY_USERS SET active = @active, deactivated_at = @deact, updated_at = @upd ... WHERE tenant_id = @tid AND realm_id = @rid AND id = @id;`
2. **Incrementing `deprovision_epoch` on All Deprovisioning Operations:**
   - Implement `IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct)` in `IdentityStore.cs`:
     - **PostgreSQL:**
       ```sql
       INSERT INTO SCIM_REALM_SYNC_STATE (tenant_id, realm_id, last_contact_at, last_write_at, deprovision_epoch)
       VALUES (@tid, @rid, @now, @now, 1)
       ON CONFLICT (tenant_id, realm_id) DO UPDATE SET
           last_contact_at = EXCLUDED.last_contact_at,
           last_write_at = EXCLUDED.last_write_at,
           deprovision_epoch = SCIM_REALM_SYNC_STATE.deprovision_epoch + 1
       RETURNING deprovision_epoch;
       ```
     - **SQL Server:**
       ```sql
       UPDATE SCIM_REALM_SYNC_STATE
       SET last_contact_at = @now,
           last_write_at = @now,
           deprovision_epoch = deprovision_epoch + 1
       OUTPUT INSERTED.deprovision_epoch
       WHERE tenant_id = @tid AND realm_id = @rid;
       IF @@ROWCOUNT = 0
       BEGIN
           INSERT INTO SCIM_REALM_SYNC_STATE (tenant_id, realm_id, last_contact_at, last_write_at, deprovision_epoch)
           OUTPUT INSERTED.deprovision_epoch
           VALUES (@tid, @rid, @now, @now, 1);
       END
       ```
   - `ScimService` calls `IncrementRealmDeprovisionEpochAsync` and `_roleResolverCache.Invalidate(...)` / `_roleResolverCache.InvalidateAll()` **before** returning from:
     - `DeleteUserAsync`
     - `ReplaceUserAsync` / `PatchUserAsync` when `Active` is `false`
     - `DeleteGroupAsync`
     - `ReplaceGroupAsync` / `PatchGroupAsync` when any user or group member is removed
3. **Epoch-Keyed Cache Lookup (`RoleResolver` & `GroupSidResolver`):**
   - When resolving SCIM correlation and effective groups for `(realm.Id, subjectUserKey)`:
     - Read `syncState = await _store.GetRealmSyncStateAsync(tenant, realm.Id, ct)`.
     - Let `epoch = syncState?.DeprovisionEpoch ?? 0`.
     - Construct the epoch-qualified cache key: `epochUserKey = $"e{epoch}:{subjectUserKey}"` (bounded to `<= 256` chars).
     - Lookup `_cache.GetAsync(realm.Id, epochUserKey, ct)`.
     - When Node 1 performs a deprovisioning write (user delete, `active=false`, group delete, or member removal), `deprovision_epoch` increments in `SCIM_REALM_SYNC_STATE` (`epoch` $\to$ `epoch + 1`) before the SCIM HTTP response is sent.
     - Even if the `IdentityGroupsChanged` event on `IEventBus` is delayed or dropped, Node 2's very next request reads `epoch + 1`, forms key `e{epoch+1}:{subjectUserKey}`, misses the pre-deprovisioning `e{epoch}:{subjectUserKey}` entry in its local `RoleResolverCache`, and re-evaluates against the database immediately.

---

## 6. Red-Green-Refactor Test Matrix & Filtered Verification Commands

### 6.1 Unit Test Matrix (`[Trait("Category", "Security")]`)

| Test File | Exact Test Method Names | Requirement Verified |
|---|---|---|
| `tests/Autheris.Tests.Unit/Security/ScimIdentityCorrelatorTests.cs` (New) | 1. `Onprem_sid_correlates`<br>2. `Immutable_id_correlates`<br>3. `Oid_correlates_via_externalId`<br>4. `Upn_ignored_unless_enabled`<br>5. `Keys_matching_different_users_are_ambiguous`<br>6. `Key_matching_two_users_is_ambiguous`<br>7. `Unmatched_stronger_key_with_matching_weaker_key_is_ambiguous`<br>8. `Not_provisioned_is_degraded_when_required`<br>9. `Inactive_user_is_forbidden` | Step 15 (`SEC-AUTH-10`): correlation precedence, all-key agreement, anti-downgrade rule, required `IDENTITY_CORRELATION_AMBIGUOUS` audit, `RequireCorrelation` handling, and inactive/locked user `403` rejection. |
| `tests/Autheris.Tests.Unit/Security/ScimEffectiveGroupsTests.cs` (New) | 1. `Effective_groups_are_union_of_token_and_scim`<br>2. `Nested_groups_are_flattened`<br>3. `Nesting_cycle_terminates`<br>4. `Nesting_depth_over_limit_degrades`<br>5. `Group_without_canonical_sid_is_rejected`<br>6. `Display_name_is_never_matched`<br>7. `Stale_sync_degrades_by_default`<br>8. `Stale_sync_drop_mode_uses_token_groups_only`<br>9. `DropScimGroups_never_drops_group_with_deny`<br>10. `Get_requests_refresh_last_contact` | Steps 14, 16, 17 (`SEC-AUTH-04`, `SEC-AUTH-09`, `SEC-AUTH-11`, `OD-27`, `OD-37`, `SEC-AZ-09`): token $\cup$ SCIM group union, recursive flattening, cycle termination, depth > 32 degradation, staleness `Degrade` vs `DropScimGroups`, deny preservation, and contact timestamp refresh on GET. |
| `tests/Autheris.Tests.Unit/Security/ScimDeprovisioningTests.cs` (New) | 1. `Delete_user_removes_memberships_and_bumps_epoch`<br>2. `Active_false_removes_memberships`<br>3. `Member_remove_invalidates_other_node`<br>4. `Response_after_commit_and_epoch` | Step 17 (`SEC-AUTH-09`, `OD-27`): transactional membership deletion on `DELETE` and `active=false`, cluster-wide cache invalidation via `deprovision_epoch` when event is dropped, and commit-before-response ordering. |
| `tests/Autheris.Tests.Unit/Security/ScimSecurityTests.cs` (New or Extend) | 1. `Deprovision_invalidates_cluster_wide` | Mandatory Phase 3 security test (§6 line 146): end-to-end two-node cache + shared store epoch verification across user deactivation, user deletion, group deletion, and member removal. |

### 6.2 Filtered Local CLI Verification Commands (Resource-Constrained)
Execute only filtered unit and architecture tests locally with `-m:2 -nodeReuse:false`:

```bash
# 1. Run PR 5b SCIM correlation, effective groups, staleness, and deprovisioning unit tests
NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj \
  --filter "FullyQualifiedName~ScimIdentityCorrelatorTests|FullyQualifiedName~ScimEffectiveGroupsTests|FullyQualifiedName~ScimDeprovisioningTests|FullyQualifiedName~ScimSecurityTests|FullyQualifiedName~GroupSidResolverTests|FullyQualifiedName~GroupResolutionCacheTests" \
  -m:2 -nodeReuse:false

# 2. Regenerate docs/reference/configuration.md and verify architecture ratchets
AUTHERIS_UPDATE_CONFIG_REFERENCE=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj \
  --filter "FullyQualifiedName~ConfigurationReferenceTests|FullyQualifiedName~OptionsConsumptionInventoryTests|FullyQualifiedName~GatewayOptionsConsumptionRatchetTests|FullyQualifiedName~IdentityPipelineArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~SecurityTestCoverageTests|FullyQualifiedName~DocsIntegrityTests" \
  -m:2 -nodeReuse:false
```

### 6.3 Integration Test Planning & Compile-Only Local Check
1. **Integration Test Suite (`tests/Autheris.Tests.Integration/Security/ScimGroupSupplementEndToEndTests.cs`):**
   - Implement the 7 integration tests specified in `WP-SIMPL-43` §6 (executed in CI on PostgreSQL and SQL Server Testcontainers; compiled locally):
     1. `Entra_token_without_group_plus_scim_membership_grants_access`
     2. `Scim_group_row_filter_applies_to_writes`
     3. `Stale_sync_denies_data_access`
     4. `Ambiguous_correlation_denies_data_access`
     5. `Deprovisioning_revokes_access_on_second_node_immediately`
     6. `Usage_is_attributed_to_scim_group`
     7. `Recursive_cte_matches_on_both_engines`
2. **Local Compile-Only Verification Command:**
   ```bash
   NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj \
     --no-restore -m:2 -nodeReuse:false
   ```

---

## 7. Ordered Implementation Checklist for Phase 4 (TDD Developer)

1. **Red — Write Failing Unit Tests:**
   - Create `ScimIdentityCorrelatorTests.cs`, `ScimEffectiveGroupsTests.cs`, `ScimDeprovisioningTests.cs`, and `ScimSecurityTests.cs` under `tests/Autheris.Tests.Unit/Security/` with `[Trait("Category", "Security")]`.
2. **Green — Options & Domain Realm Settings:**
   - Add `ScimStaleSyncBehavior` and `RealmScimSettings` to `src/Autheris.Domain/Identity/IdentityRealm.cs`.
   - Add `AllowUpnCorrelation`, `RequireCorrelation`, `MaxSyncAgeMinutes`, and `StaleSyncBehavior` (with XML `<summary>` descriptions) to `ScimRealmSecretOptions` in `src/Autheris.Domain/Options/Security/IdentityRealmsOptions.cs`.
   - Wire `RealmScimSettings` in `IdentityRealmRegistry.Initialize` and validate bounds (`MaxSyncAgeMinutes` `10..10080`) + startup warning for `DropScimGroups` in `IdentityRealmOptionsValidator.cs`.
3. **Green — Nested Group Recursive CTE & Store Epoch (`IdentityStore.cs`):**
   - Implement `GetEffectiveGroupSidsForUserAsync` (PostgreSQL & SQL Server recursive CTEs, depth 32, path cycle check), `GetUsersByEntraObjectIdOrExternalIdAsync`, `GetUsersByUpnOrUserNameAsync`, and `IncrementRealmDeprovisionEpochAsync` in `src/Autheris.Infrastructure/Persistence/IdentityStore.cs` and `GovernanceRepository.Identity.cs`.
   - Update `ScimStoreRoleProvider` to call `GetEffectiveGroupSidsForUserAsync`.
   - Update `IdentityStore.DeactivateUserAsync` and `UpdateUserAsync` (when `!user.Active`) to remove `IDENTITY_GROUP_MEMBERS` rows in the same transaction.
4. **Green — Correlation Engine, Deny Inspector & Sync Tracker:**
   - Implement `ScimIdentityCorrelator` (`src/Autheris.Application/Identity/ScimIdentityCorrelator.cs`) with required `IDENTITY_CORRELATION_AMBIGUOUS` auditing.
   - Implement `IGroupDenyInspector` / `GovernanceGroupDenyInspector` (`src/Autheris.Application/Identity/GroupDenyInspector.cs`).
   - Implement `IScimSyncStateTracker` / `ScimSyncStateTracker` (`src/Autheris.Application/Identity/Services/ScimSyncStateTracker.cs`) and wire contact/write updates + `IncrementRealmDeprovisionEpochAsync` into `ScimService` and SCIM endpoints.
5. **Green — Effective Groups Union in `GroupSidResolver` & `IdentityPipeline`:**
   - Update `GroupSidResolver.ResolveAsync` to union token groups with SCIM effective groups for `Provisioning == RealmProvisioningMode.Scim`, enforce `MaxSyncAgeMinutes` staleness (`Degrade` vs `DropScimGroups` + `IGroupDenyInspector`), and key `IRoleResolverCache` lookups with `e{deprovisionEpoch}:{userKey}`.
   - Register `IScimIdentityCorrelator`, `IGroupDenyInspector`, and `IScimSyncStateTracker` in DI (`GatewaySecurityServiceExtensions.cs`) and update DI composition snapshots if needed.
6. **Refactor & Ratchet Alignment:**
   - Add `ScimGroupSupplementEndToEndTests.cs` in `tests/Autheris.Tests.Integration/Security/` and verify compile-only build.
   - Regenerate `docs/reference/configuration.md` (`AUTHERIS_UPDATE_CONFIG_REFERENCE=1`) and update `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt`.
   - Run the filtered unit and architecture test commands from §6.2.
