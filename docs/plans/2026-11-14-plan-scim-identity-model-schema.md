# Implementation Plan: WP-SIMPL-43 PR 5a — SCIM Identity Model and Schema (Step 13)

- **Date:** 2026-11-14
- **Work Package:** WP-SIMPL-43 (Authentication & Authorization Core Simplification)
- **PR Slice:** PR 5a — SCIM Identity Model and Schema (Step 13)
- **Parent Spec:** `docs/plans/simplification/WP-SIMPL-43-authentication-authorization-core.md` (§4.3a, §5 Step 13, §6, §9 PR 5a)
- **Owner Decisions:** `OD-21`, `OD-22`, `OD-27`, `OD-30`, `OD-31`, `OD-37`
- **Security Conditions:** `SEC-AUTH-03a`, `SEC-AUTH-09`, `SEC-AUTH-10`, `SEC-AUTH-11`, `SEC-AUTH-12`, `SEC-SIMPL-01`, `SEC-SIMPL-02`
- **Target Framework:** .NET 10 / C# 14

---

## 1. Preconditions, Dependencies & Scope Boundaries

### 1.1 Preconditions & Upstream Dependencies
PR 5a builds directly on the primitives merged in PR 1 through PR 4:
1. **PR 2 (Value Types & Canonical SID Primitives):**
   - `Sid` (`src/Autheris.Domain/ValueObjects/Sid.cs`) and `SidFormat.IsCanonical(ReadOnlySpan<char>)` (`src/Autheris.Domain/Common/SidFormat.cs`) enforcing strict regular grammar (`^S-1-(5-21(-\d+){3,4}|12-1(-\d+){4})$`, every sub-authority parsed as `uint`, no leading zeros except `"0"`).
2. **PR 3a (Claims Normalization & Group SID Resolution):**
   - `SidFormat.TryFromEntraObjectId(ReadOnlySpan<char>, out Sid)`, `SidFormat.TryFromBinary(ReadOnlySpan<byte>, out Sid)`, and `SidFormat.IsPrivileged(Sid)` (`src/Autheris.Domain/Common/SidFormat.cs`) rejecting privileged/system/built-in SIDs (RID `< 1000`, domain/enterprise/schema admins `512`/`516`/`518`/`519`, Krbtgt `502`, Domain Controllers `516`/`521`, `S-1-5-18`, `S-1-5-19`, `S-1-5-20`, `S-1-5-32-*`, `S-1-1-0`, `S-1-5-11`, `S-1-16-*`, `S-1-5-80-*`).
   - `GroupSidResolver` (`src/Autheris.Application/Identity/Resolution/GroupSidResolver.cs`) and `BroadcastingRoleResolverCache` (`src/Autheris.Infrastructure/Caching/BroadcastingRoleResolverCache.cs`).
3. **PR 3b (Unified IdentityPipeline) & PR 4 (Auth Simplification & Break-Glass Retirement):**
   - Merged on `main`; latest forward-only governance migration step is `0011_role_grantee_and_profile_assignment_retirement`.
   - SQLite governance persistence is retired (`WP-SIMPL-11`); only PostgreSQL and SQL Server engines exist.

### 1.2 Scope of PR 5a vs. Subsequent PR 5b / PR 5c
- **In Scope (PR 5a — Step 13):**
  1. SCIM 2.0 extension schema constants and discovery advertisement (`/scim/v2/{realmId}/Schemas` and `/scim/v2/{realmId}/ResourceTypes`):
     - `urn:ietf:params:scim:schemas:extension:autheris:2.0:User` (`objectSid`, `onPremisesImmutableId`, `userPrincipalName`, `entraObjectId` — all optional; validated when present).
     - `urn:ietf:params:scim:schemas:extension:autheris:2.0:Group` (`objectSid` — **required**, canonical non-privileged SID; `displayName` stored for display only).
  2. Domain models (`ScimUser`, `ScimGroup`, `ScimGroupMember` with `Type = "User" | "Group"`, `ScimGroupNestingEdge`, `ScimRealmSyncState`, `ScimUniquenessConflictException`).
  3. Validation rules in `ScimRequestValidator` and RFC 7644 §3.12 HTTP `400 Bad Request` (`scimType: "invalidValue"`) and `409 Conflict` (`scimType: "uniqueness"`) error mapping in `ScimUsersEndpoints` and `ScimGroupsEndpoints`.
  4. Segregated repository interfaces (`IScimUserStore`, `IScimGroupStore`, `IScimProvisioningStore`, composed into `IIdentityStoreRepository`) and dual-engine implementation in `IdentityStore` and `GovernanceRepository.Identity`.
  5. Forward-only migration step `0012_scim_identity_correlation` (PostgreSQL + SQL Server `.sql` and `.shape.json`), baseline schema updates (`PostgreSqlBaselineSchema.cs`, `SqlServerBaselineSchema.cs`, `governance-shape.{postgresql,sqlserver}.v0001.json`), and step checksum golden entries.
- **Deferred to PR 5b (Step 14) & PR 5c (Steps 15–16):**
  - Transitive group graph expansion, cycle detection, and correlation hierarchy resolution in `ScimStoreRoleProvider` (PR 5b).
  - Realm sync freshness check (`MaxSyncAge`), `GroupSidResolver` cache invalidation wiring on SCIM mutations, and per-realm token rotation (PR 5c).

### 1.3 Architecture & Governance Guardrails
- **No `WP-`, `SEC-`, or `OD-` identifiers in `src/Autheris.Api` or `src/Autheris.Application`:** Enforced by `CleanArchitectureTests`. Do not place work-package or owner-decision IDs in comments or strings inside those projects.
- **Endpoint Ratchet (`EndpointSecurityCoverageTests`):** Route count is frozen at `272`. PR 5a modifies existing SCIM endpoint handlers in `ScimDiscoveryEndpoints`, `ScimUsersEndpoints`, and `ScimGroupsEndpoints` without adding or removing route registrations.
- **Options Ratchet (`OptionsSprawlGuardTests`):** PR 5a introduces **0** new `*Options` classes (`ScimSyncOptions` belongs to PR 5c).
- **Security Test Ratchet (`SecurityTestCoverageTests`):** Every new security-relevant unit test class must carry `[Trait("Category", "Security")]`, and `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt` must be incremented to match the new total.

---

## 2. Component & Domain Entity Architecture

### 2.1 Domain Layer — `src/Autheris.Domain/Identity/ScimModels.cs`
Extend `ScimModels.cs` with the SCIM extension URNs, correlation value models, nesting/sync records, and uniqueness conflict exception while preserving source compatibility for existing positional `ScimUser` and `ScimGroup` callers:

```csharp
using System.Text.Json.Serialization;
using Autheris.Domain.ValueObjects;

namespace Autheris.Domain.Identity;

public static class ScimSchemaUris
{
    public const string CoreUser = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string CoreGroup = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string AutherisUserExtension = "urn:ietf:params:scim:schemas:extension:autheris:2.0:User";
    public const string AutherisGroupExtension = "urn:ietf:params:scim:schemas:extension:autheris:2.0:Group";
    public const string PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";
    public const string ServiceProviderConfig = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";
    public const string ResourceType = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
    public const string Schema = "urn:ietf:params:scim:schemas:core:2.0:Schema";
}

public static class ScimMemberTypes
{
    public const string User = "User";
    public const string Group = "Group";
}

public sealed record ScimUserAutherisExtension(
    [property: JsonPropertyName("objectSid")] string? ObjectSid = null,
    [property: JsonPropertyName("onPremisesImmutableId")] string? OnPremisesImmutableId = null,
    [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName = null,
    [property: JsonPropertyName("entraObjectId")] string? EntraObjectId = null);

public sealed record ScimGroupAutherisExtension(
    [property: JsonPropertyName("objectSid")] string? ObjectSid = null);

public record ScimUser(
    string Id,
    string? ExternalId,
    string UserName,
    string? DisplayName,
    string? Email,
    bool Active,
    DateTimeOffset? DeactivatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ObjectSid = null,
    string? OnPremisesImmutableId = null,
    string? UserPrincipalName = null,
    string? EntraObjectId = null)
{
    [JsonPropertyName(ScimSchemaUris.AutherisUserExtension)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScimUserAutherisExtension? AutherisExtension =>
        ObjectSid is null && OnPremisesImmutableId is null && UserPrincipalName is null && EntraObjectId is null
            ? null
            : new ScimUserAutherisExtension(ObjectSid, OnPremisesImmutableId, UserPrincipalName, EntraObjectId);
}

public record ScimGroup(
    string Id,
    string? ExternalId,
    string DisplayName,
    IReadOnlyList<ScimGroupMember> Members,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ObjectSid = null)
{
    [JsonPropertyName(ScimSchemaUris.AutherisGroupExtension)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScimGroupAutherisExtension? AutherisExtension =>
        ObjectSid is null ? null : new ScimGroupAutherisExtension(ObjectSid);
}

public record ScimGroupMember(
    string Value,
    string? Display = null,
    [property: JsonPropertyName("type")] string? Type = ScimMemberTypes.User);

public sealed record ScimGroupNestingEdge(
    string ParentGroupId,
    string ChildGroupId);

public sealed record ScimRealmSyncState(
    TenantId TenantId,
    string RealmId,
    DateTimeOffset? LastContactAt,
    DateTimeOffset? LastWriteAt,
    long DeprovisionEpoch = 0);

public sealed class ScimUniquenessConflictException : Exception
{
    public string AttributeName { get; }

    public ScimUniquenessConflictException(string attributeName, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        AttributeName = attributeName;
    }
}
```

### 2.2 Domain Layer — Request & Patch Payload Binding (`ScimModels.cs`)
Update `ScimUserRequest` and `ScimGroupRequest` so callers can supply the extension attributes via the standard SCIM 2.0 extension URN container `"urn:ietf:params:scim:schemas:extension:autheris:2.0:User"` / `"urn:ietf:params:scim:schemas:extension:autheris:2.0:Group"` (as well as top-level convenience properties resolved via helper methods):

```csharp
public record ScimUserRequest(
    IReadOnlyList<string>? Schemas,
    string? ExternalId,
    string UserName,
    string? DisplayName,
    ScimName? Name,
    IReadOnlyList<ScimEmail>? Emails,
    bool Active = true,
    [property: JsonPropertyName(ScimSchemaUris.AutherisUserExtension)]
    ScimUserAutherisExtension? AutherisExtension = null,
    string? ObjectSid = null,
    string? OnPremisesImmutableId = null,
    string? UserPrincipalName = null,
    string? EntraObjectId = null)
{
    public string? ResolvedObjectSid => AutherisExtension?.ObjectSid ?? ObjectSid;
    public string? ResolvedOnPremisesImmutableId => AutherisExtension?.OnPremisesImmutableId ?? OnPremisesImmutableId;
    public string? ResolvedUserPrincipalName => AutherisExtension?.UserPrincipalName ?? UserPrincipalName;
    public string? ResolvedEntraObjectId => AutherisExtension?.EntraObjectId ?? EntraObjectId;
}

public record ScimGroupRequest(
    IReadOnlyList<string>? Schemas,
    string? ExternalId,
    string DisplayName,
    IReadOnlyList<ScimGroupMember>? Members,
    [property: JsonPropertyName(ScimSchemaUris.AutherisGroupExtension)]
    ScimGroupAutherisExtension? AutherisExtension = null,
    string? ObjectSid = null)
{
    public string? ResolvedObjectSid => AutherisExtension?.ObjectSid ?? ObjectSid;
}
```

### 2.3 Application Layer — `src/Autheris.Application/Identity/Validators/ScimRequestValidator.cs`
Extend `ScimRequestValidator` with strict correlation and group-SID validation:
1. **`ValidateUserRequest(ScimUserRequest request)`:**
   - Existing `userName`, `externalId`, `displayName`, and `emails` checks remain intact.
   - `objectSid` (`request.ResolvedObjectSid`): optional. When non-null:
     - Must be non-empty, length `<= 184`, pass `SidFormat.IsCanonical(objectSid.AsSpan())`, and pass `!SidFormat.IsPrivileged(new Sid(objectSid))`. Otherwise throw `ValidationException("User objectSid must be a canonical, non-privileged SID.")`.
   - `onPremisesImmutableId` (`request.ResolvedOnPremisesImmutableId`): optional. When non-null:
     - Must be non-whitespace, length `<= 128`, printable ASCII (`c >= 0x20 && c <= 0x7E`, no control chars). Otherwise throw `ValidationException`.
   - `userPrincipalName` (`request.ResolvedUserPrincipalName`): optional. When non-null:
     - Must be non-whitespace, length `<= 256`, no control characters (`!char.IsControl(c)`). Normalized via `.Trim().ToLowerInvariant()` before persistence so `(tenant_id, realm_id, upn)` is case-insensitively unique across both PostgreSQL and SQL Server.
   - `entraObjectId` (`request.ResolvedEntraObjectId`): optional. When non-null:
     - Must parse via `Guid.TryParseExact(entraObjectId.AsSpan(), "D", out var parsedGuid)` with `parsedGuid != Guid.Empty` (stored normalized as `parsedGuid.ToString("D")`, length `36 <= 64`). Otherwise throw `ValidationException`.
2. **`ValidateGroupRequest(ScimGroupRequest request)`:**
   - `displayName`: required, non-whitespace, length `<= 256` (stored for display only; never matched for authorization).
   - `objectSid` (`request.ResolvedObjectSid`): **required** on every group create and replace.
     - If `string.IsNullOrWhiteSpace(objectSid)` -> throw `ValidationException("Group objectSid is required in urn:ietf:params:scim:schemas:extension:autheris:2.0:Group.")`.
     - Must pass `SidFormat.IsCanonical(objectSid.AsSpan())` (accepts canonical AD `S-1-5-21-...` and cloud `S-1-12-1-...` SIDs) and `!SidFormat.IsPrivileged(new Sid(objectSid))`. Otherwise throw `ValidationException("Group objectSid must be a canonical, non-privileged SID.")`.
   - `members`: when present, count `<= MaxGroupMembersPerRequest` (`1000`). For each member:
     - `Value`: required, non-whitespace, length `<= 128`, no control chars.
     - `Type`: when null or empty, defaults to `"User"`. Otherwise must equal `"User"` or `"Group"` (`StringComparison.OrdinalIgnoreCase`, canonicalized to `"User"` or `"Group"`). Any other value throws `ValidationException("Group member type must be 'User' or 'Group'.")`.
3. **`ValidatePatchRequest(ScimPatchRequest request)`:**
   - Also validate PATCH operations targeting `objectSid`, `onPremisesImmutableId`, `userPrincipalName`, `entraObjectId` (or their fully-qualified extension paths `urn:ietf:params:scim:schemas:extension:autheris:2.0:User:objectSid`, `urn:ietf:params:scim:schemas:extension:autheris:2.0:Group:objectSid`, and `members` entries with `type`). Removing `objectSid` from a Group via PATCH (`op: "remove"`) is rejected with `ValidationException`.

### 2.4 Application Layer — `src/Autheris.Application/Identity/Services/ScimService.cs`
Update `ScimService` (`IScimService`):
1. **User CRUD & Patch:**
   - Map `ResolvedObjectSid`, `ResolvedOnPremisesImmutableId`, normalized `ResolvedUserPrincipalName` (`.Trim().ToLowerInvariant()`), and normalized `ResolvedEntraObjectId` (`Guid.ToString("D")`) onto `ScimUser` in `CreateUserAsync`, `ReplaceUserAsync`, and `PatchUserAsync`.
   - Support extension-prefixed and short attribute paths in `ApplyUserPatchOperation`:
     - `objectsid` / `urn:ietf:params:scim:schemas:extension:autheris:2.0:user:objectsid`
     - `onpremisesimmutableid` / `urn:ietf:params:scim:schemas:extension:autheris:2.0:user:onpremisesimmutableid`
     - `userprincipalname` / `urn:ietf:params:scim:schemas:extension:autheris:2.0:user:userprincipalname`
     - `entraobjectid` / `urn:ietf:params:scim:schemas:extension:autheris:2.0:user:entraobjectid`
   - Re-validate updated correlation fields via `ScimRequestValidator`.
2. **Group CRUD & Patch (with Group-in-Group Nesting Split):**
   - Validate `ResolvedObjectSid` via `ScimRequestValidator.ValidateGroupRequest`.
   - Partition `request.Members` by normalized `Type`:
     - `Type == "User"` -> stored in `IDENTITY_GROUP_MEMBERS` via `AddUserToGroupAsync`.
     - `Type == "Group"` -> validate `member.Value != groupId` (reject self-nesting with `ValidationException("A group cannot be nested as a member of itself.")`), then store in `IDENTITY_GROUP_NESTING` via `AddGroupNestingEdgeAsync(tenantId, realmId, groupId, member.Value, ct)`.
   - On `GetGroupAsync` / `ListGroupsAsync`: return combined `Members` list containing both user members (`Type: "User"`) and child group edges (`Type: "Group"`).
   - On `ReplaceGroupAsync` and `PatchGroupAsync`: support adding/removing both `"User"` and `"Group"` members, and updating `objectSid` (only to another valid canonical non-privileged SID).
3. **Realm Sync State Touch:**
   - Record write timestamp via `UpsertRealmSyncStateAsync(tenantId, realmId, lastContactAt: now, lastWriteAt: now, ct)` on user/group mutations and `lastContactAt: now` on read operations.

### 2.5 API Layer — `ScimDiscoveryEndpoints.cs`, `ScimUsersEndpoints.cs`, `ScimGroupsEndpoints.cs`
1. **`src/Autheris.Api/Endpoints/ScimDiscoveryEndpoints.cs`:**
   - `/ResourceTypes`:
     - `"User"` resource type includes `schemaExtensions = new[] { new { schema = ScimSchemaUris.AutherisUserExtension, required = false } }`.
     - `"Group"` resource type includes `schemaExtensions = new[] { new { schema = ScimSchemaUris.AutherisGroupExtension, required = true } }`.
   - `/Schemas`:
     - Advertises 4 schemas in `Resources`:
       1. `urn:ietf:params:scim:schemas:core:2.0:User`
       2. `urn:ietf:params:scim:schemas:core:2.0:Group` (including `members.type` sub-attribute accepting `"User"` or `"Group"`)
       3. `urn:ietf:params:scim:schemas:extension:autheris:2.0:User` with attributes `objectSid`, `onPremisesImmutableId`, `userPrincipalName`, `entraObjectId` (`required = false`, `uniqueness = "server"`).
       4. `urn:ietf:params:scim:schemas:extension:autheris:2.0:Group` with attribute `objectSid` (`required = true`, `uniqueness = "server"`).
2. **`src/Autheris.Api/Endpoints/ScimUsersEndpoints.cs` & `ScimGroupsEndpoints.cs`:**
   - Catch `ScimUniquenessConflictException` on `POST`, `PUT`, and `PATCH` and return HTTP `409 Conflict` with `application/scim+json`:
     ```csharp
     catch (ScimUniquenessConflictException ex)
     {
         return ScimResult(
             new ScimError(
                 ScimErrorSchemas,
                 ex.Message,
                 StatusCodes.Status409Conflict.ToString(CultureInfo.InvariantCulture),
                 ScimType: "uniqueness"),
             StatusCodes.Status409Conflict);
     }
     ```
   - Existing `catch (ValidationException ex)` continues to return HTTP `400 Bad Request` with `ScimType: "invalidValue"`.

---

## 3. Boundary Contracts (`IScimUserStore`, `IScimGroupStore`, `IScimProvisioningStore`, `IIdentityStoreRepository`)

### 3.1 Interface Segregation in `src/Autheris.Domain/Interfaces/IIdentityStoreRepository.cs`
Segregate the store contracts by domain responsibility while keeping `IIdentityStoreRepository : IScimUserStore, IScimGroupStore, IScimProvisioningStore` so all existing DI registrations and consumers remain 100% source-compatible:

```csharp
using Autheris.Domain.Identity;
using Autheris.Domain.ValueObjects;

namespace Autheris.Domain.Interfaces;

public interface IScimUserStore
{
    Task<ScimUser?> GetUserByIdAsync(TenantId tenantId, string realmId, string userId, CancellationToken ct = default);
    Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default);
    Task<ScimUser?> GetUserByUserNameAsync(TenantId tenantId, string realmId, string userName, CancellationToken ct = default);
    Task<ScimUser?> GetUserByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default);
    Task<ScimUser?> GetUserByImmutableIdAsync(TenantId tenantId, string realmId, string onPremisesImmutableId, CancellationToken ct = default);
    Task<ScimUser?> GetUserByEntraObjectIdAsync(TenantId tenantId, string realmId, string entraObjectId, CancellationToken ct = default);
    Task<ScimUser?> GetUserByUpnAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default);
    Task<(IReadOnlyList<ScimUser> Users, int TotalCount)> ListUsersAsync(TenantId tenantId, string realmId, string? filter, int startIndex, int count, CancellationToken ct = default);
    Task CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default);
    Task UpdateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default);
    Task DeleteUserAsync(TenantId tenantId, string realmId, string userId, CancellationToken ct = default);
}

public interface IScimGroupStore
{
    Task<ScimGroup?> GetGroupByIdAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default);
    Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default);
    Task<(IReadOnlyList<ScimGroup> Groups, int TotalCount)> ListGroupsAsync(TenantId tenantId, string realmId, string? filter, int startIndex, int count, CancellationToken ct = default);
    Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default);
    Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default);
    Task DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default);

    Task AddUserToGroupAsync(TenantId tenantId, string realmId, string groupId, string userId, CancellationToken ct = default);
    Task RemoveUserFromGroupAsync(TenantId tenantId, string realmId, string groupId, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetGroupsForUserAsync(TenantId tenantId, string realmId, string userId, CancellationToken ct = default);

    Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default);
    Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default);
    Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default);
}

public interface IScimProvisioningStore
{
    Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default);
    Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt, DateTimeOffset? lastWriteAt, CancellationToken ct = default);
    Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default);

    Task LockIdentityAsync(TenantId tenantId, string realmId, string subjectId, DateTimeOffset expiresAt, string reason, CancellationToken ct = default);
    Task<bool> IsIdentityLockedAsync(TenantId tenantId, string realmId, string subjectId, CancellationToken ct = default);
    Task UnlockIdentityAsync(TenantId tenantId, string realmId, string subjectId, CancellationToken ct = default);
}

public interface IIdentityStoreRepository : IScimUserStore, IScimGroupStore, IScimProvisioningStore
{
}
```

### 3.2 Persistence Implementation (`IdentityStore.cs` & `GovernanceRepository.Identity.cs`)
1. **`src/Autheris.Infrastructure/Persistence/IdentityStore.cs`:**
   - **Tenant + Realm Parameterization:** Every query and mutation continues to bind `@tid = tenantId.Value` and `@rid = realmId`.
   - **`IDENTITY_USERS` Select & Write Columns:**
     - `SELECT id, external_id, user_name, display_name, email, active, deactivated_at, created_at, updated_at, object_sid, immutable_id, upn, entra_object_id FROM IDENTITY_USERS WHERE tenant_id = @tid AND realm_id = @rid ...`
     - `GetUserByObjectSidAsync`, `GetUserByImmutableIdAsync`, `GetUserByEntraObjectIdAsync`, `GetUserByUpnAsync` (normalizing `userPrincipalName.Trim().ToLowerInvariant()` before binding `@upn`).
     - `ListUsersAsync` `ParseScimFilter` extended to support `objectSid eq "..."`, `onPremisesImmutableId eq "..."`, `userPrincipalName eq "..."`, `entraObjectId eq "..."` in addition to `userName`, `externalId`, `email`, and `active`.
   - **`IDENTITY_GROUPS` Select & Write Columns:**
     - `SELECT id, external_id, display_name, created_at, updated_at, object_sid FROM IDENTITY_GROUPS WHERE tenant_id = @tid AND realm_id = @rid ...`
     - `GetGroupByObjectSidAsync(tenantId, realmId, objectSid, ct)`.
     - `ListGroupsAsync` `ParseGroupScimFilter` extended to support `objectSid eq "..."` in addition to `displayName` and `externalId`.
     - `GetGroupMembersAsync`: reads user members from `IDENTITY_GROUP_MEMBERS` (`Type = "User"`) AND child group members from `IDENTITY_GROUP_NESTING` (`SELECT g.id, g.display_name FROM IDENTITY_GROUPS g INNER JOIN IDENTITY_GROUP_NESTING n ON g.tenant_id = n.tenant_id AND g.realm_id = n.realm_id AND g.id = n.child_group_id WHERE n.tenant_id = @tid AND n.realm_id = @rid AND n.parent_group_id = @gid`, `Type = "Group"`).
     - `DeleteGroupAsync`: inside the existing transaction, delete from `IDENTITY_GROUP_NESTING WHERE tenant_id = @tid AND realm_id = @rid AND (parent_group_id = @id OR child_group_id = @id)` in addition to `IDENTITY_GROUP_MEMBERS` and `IDENTITY_GROUPS`.
     - `DeleteUserAsync`: unchanged (deletes from `IDENTITY_GROUP_MEMBERS` and `IDENTITY_USERS`).
   - **`IDENTITY_GROUP_NESTING` Operations:**
     - `AddGroupNestingEdgeAsync`: idempotent insert (`ON CONFLICT DO NOTHING` on PostgreSQL; `IF NOT EXISTS (...) INSERT INTO IDENTITY_GROUP_NESTING ...` on SQL Server).
     - `RemoveGroupNestingEdgeAsync`: `DELETE FROM IDENTITY_GROUP_NESTING WHERE tenant_id = @tid AND realm_id = @rid AND parent_group_id = @pid AND child_group_id = @cid`.
     - `GetChildGroupIdsAsync` and `ListGroupNestingEdgesAsync`.
   - **`SCIM_REALM_SYNC_STATE` Operations:**
     - `GetRealmSyncStateAsync` & `UpsertRealmSyncStateAsync` using `DialectValueCodec.Ts(dialect, timestamp)` for `last_contact_at` and `last_write_at` (matching `IDENTITY_USERS` timestamp encoding: `TIMESTAMPTZ` on PostgreSQL, ISO-8601 `NVARCHAR(64)` on SQL Server).
   - **Uniqueness Conflict Detection & Translation:**
     - Before/during `CreateUserAsync`, `UpdateUserAsync`, `CreateGroupAsync`, `UpdateGroupAsync`:
       - Catch database unique constraint violations (PostgreSQL `PostgresException` with `SqlState == "23505"`; SQL Server `SqlException` with `Number is 2601 or 2627`) and translate into `ScimUniquenessConflictException` identifying the conflicting attribute (`objectSid`, `onPremisesImmutableId`, `entraObjectId`, `userPrincipalName`, or `externalId`), AND perform pre-write uniqueness checks inside the connection so deterministic attribute-specific `ScimUniquenessConflictException` is thrown across both engines.
2. **`src/Autheris.Infrastructure/Persistence/GovernanceRepository.Identity.cs`:**
   - Delegate all new `IScimUserStore`, `IScimGroupStore`, and `IScimProvisioningStore` methods to `_identityStore`.

---

## 4. Forward-Only Migration DDL (`0012_scim_identity_correlation`) & `.shape.json` Deltas

### 4.1 Design Notes for Schema Catalog Reader & Step Linter Compatibility
1. **`StepSqlLint.Validate` Compliance (`src/Autheris.Infrastructure/Persistence/Migrations/StepSqlLint.cs`):**
   - No `BEGIN TRAN` / `COMMIT` / `ROLLBACK` (the runner wraps each step in a transaction).
   - No `DO $$` blocks in PostgreSQL and no `EXEC(...)` / `sp_executesql` in SQL Server.
   - Multi-statement SQL Server DDL separates batches with `-- batch` on its own line.
2. **`PostgreSqlSchemaCatalogReader` & `SqlServerSchemaCatalogReader` Parity (`GovernanceMigrationStepParityTests`):**
   - `PostgreSqlSchemaCatalogReader` maps expression index columns (`a.attnum = 0`) to `"(expression)"`. To ensure clean, identical column-based index shapes (`["tenant_id", "realm_id", "upn"]`, etc.) across both PostgreSQL and SQL Server without `"(expression)"` discrepancies, all 6 filtered unique indexes are pure column indexes (`WHERE <col> IS NOT NULL`), and `upn` is normalized to lowercase on write and lookup in `ScimRequestValidator` / `IdentityStore`.
   - In `IDENTITY_GROUPS`, `object_sid` is `VARCHAR(184) NULL` in DDL (with filtered unique index `WHERE object_sid IS NOT NULL`) so existing rows in upgraded databases do not fail `ALTER TABLE ADD` without a synthetic default, while `ScimRequestValidator.ValidateGroupRequest` and `IdentityStore.CreateGroupAsync` / `UpdateGroupAsync` strictly enforce `object_sid` non-null and canonical on every write.
   - Timestamp columns on `SCIM_REALM_SYNC_STATE` follow the exact `IDENTITY_USERS` convention in `PostgreSqlBaselineSchema.cs` (`TIMESTAMPTZ` -> `"type": "TIMESTAMP WITH TIME ZONE"`, `"family": "datetime"`) and `SqlServerBaselineSchema.cs` (`NVARCHAR(64)` -> `"type": "NVARCHAR"`, `"family": "string"`).

### 4.2 PostgreSQL Step DDL
**File:** `src/Autheris.Infrastructure/Persistence/Migrations/Steps/postgresql/0012_scim_identity_correlation.sql`

```sql
-- 0012_scim_identity_correlation (PostgreSQL)
-- Forward-only migration: add hybrid AD / Entra correlation columns to IDENTITY_USERS
-- and IDENTITY_GROUPS, create IDENTITY_GROUP_NESTING and SCIM_REALM_SYNC_STATE tables,
-- and create per-(tenant_id, realm_id) filtered unique correlation indexes.

ALTER TABLE IDENTITY_USERS ADD COLUMN IF NOT EXISTS object_sid VARCHAR(184) NULL;
ALTER TABLE IDENTITY_USERS ADD COLUMN IF NOT EXISTS immutable_id VARCHAR(128) NULL;
ALTER TABLE IDENTITY_USERS ADD COLUMN IF NOT EXISTS upn VARCHAR(256) NULL;
ALTER TABLE IDENTITY_USERS ADD COLUMN IF NOT EXISTS entra_object_id VARCHAR(64) NULL;

ALTER TABLE IDENTITY_GROUPS ADD COLUMN IF NOT EXISTS object_sid VARCHAR(184) NULL;

CREATE TABLE IF NOT EXISTS IDENTITY_GROUP_NESTING (
    tenant_id VARCHAR(128) NOT NULL DEFAULT 'default',
    realm_id VARCHAR(128) NOT NULL,
    parent_group_id VARCHAR(128) NOT NULL,
    child_group_id VARCHAR(128) NOT NULL,
    PRIMARY KEY (tenant_id, realm_id, parent_group_id, child_group_id)
);

CREATE TABLE IF NOT EXISTS SCIM_REALM_SYNC_STATE (
    tenant_id VARCHAR(128) NOT NULL DEFAULT 'default',
    realm_id VARCHAR(128) NOT NULL,
    last_contact_at TIMESTAMPTZ NULL,
    last_write_at TIMESTAMPTZ NULL,
    deprovision_epoch BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, realm_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_users_tenant_realm_object_sid
    ON IDENTITY_USERS (tenant_id, realm_id, object_sid)
    WHERE object_sid IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_users_tenant_realm_immutable_id
    ON IDENTITY_USERS (tenant_id, realm_id, immutable_id)
    WHERE immutable_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_users_tenant_realm_entra_oid
    ON IDENTITY_USERS (tenant_id, realm_id, entra_object_id)
    WHERE entra_object_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_users_tenant_realm_upn
    ON IDENTITY_USERS (tenant_id, realm_id, upn)
    WHERE upn IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_users_tenant_realm_external_id
    ON IDENTITY_USERS (tenant_id, realm_id, external_id)
    WHERE external_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_groups_tenant_realm_object_sid
    ON IDENTITY_GROUPS (tenant_id, realm_id, object_sid)
    WHERE object_sid IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_identity_group_nesting_child
    ON IDENTITY_GROUP_NESTING (tenant_id, realm_id, child_group_id);
```

### 4.3 PostgreSQL Step Shape Sidecar
**File:** `src/Autheris.Infrastructure/Persistence/Migrations/Steps/postgresql/0012_scim_identity_correlation.shape.json`

```json
{
  "stepVersion": 12,
  "stepName": "scim_identity_correlation",
  "engine": "postgresql",
  "addedTables": [
    {
      "name": "identity_group_nesting",
      "columns": [
        { "name": "child_group_id", "type": "CHARACTER VARYING(128)", "family": "string", "nullable": false, "hasDefault": false },
        { "name": "parent_group_id", "type": "CHARACTER VARYING(128)", "family": "string", "nullable": false, "hasDefault": false },
        { "name": "realm_id", "type": "CHARACTER VARYING(128)", "family": "string", "nullable": false, "hasDefault": false },
        { "name": "tenant_id", "type": "CHARACTER VARYING(128)", "family": "string", "nullable": false, "hasDefault": true }
      ],
      "primaryKey": [
        "tenant_id",
        "realm_id",
        "parent_group_id",
        "child_group_id"
      ],
      "foreignKeys": [],
      "indexes": [
        {
          "name": "ix_identity_group_nesting_child",
          "columns": [ "tenant_id", "realm_id", "child_group_id" ],
          "unique": false
        }
      ]
    },
    {
      "name": "scim_realm_sync_state",
      "columns": [
        { "name": "deprovision_epoch", "type": "BIGINT", "family": "integer", "nullable": false, "hasDefault": true },
        { "name": "last_contact_at", "type": "TIMESTAMP WITH TIME ZONE", "family": "datetime", "nullable": true, "hasDefault": false },
        { "name": "last_write_at", "type": "TIMESTAMP WITH TIME ZONE", "family": "datetime", "nullable": true, "hasDefault": false },
        { "name": "realm_id", "type": "CHARACTER VARYING(128)", "family": "string", "nullable": false, "hasDefault": false },
        { "name": "tenant_id", "type": "CHARACTER VARYING(128)", "family": "string", "nullable": false, "hasDefault": true }
      ],
      "primaryKey": [
        "tenant_id",
        "realm_id"
      ],
      "foreignKeys": [],
      "indexes": []
    }
  ],
  "droppedTables": [],
  "alteredTables": [
    {
      "name": "identity_groups",
      "addedColumns": [
        { "name": "object_sid", "type": "CHARACTER VARYING(184)", "family": "string", "nullable": true, "hasDefault": false }
      ],
      "droppedColumns": [],
      "alteredColumns": [],
      "addedForeignKeys": [],
      "droppedForeignKeys": [],
      "addedIndexes": [
        {
          "name": "ix_identity_groups_tenant_realm_object_sid",
          "columns": [ "tenant_id", "realm_id", "object_sid" ],
          "unique": true
        }
      ],
      "droppedIndexes": []
    },
    {
      "name": "identity_users",
      "addedColumns": [
        { "name": "entra_object_id", "type": "CHARACTER VARYING(64)", "family": "string", "nullable": true, "hasDefault": false },
        { "name": "immutable_id", "type": "CHARACTER VARYING(128)", "family": "string", "nullable": true, "hasDefault": false },
        { "name": "object_sid", "type": "CHARACTER VARYING(184)", "family": "string", "nullable": true, "hasDefault": false },
        { "name": "upn", "type": "CHARACTER VARYING(256)", "family": "string", "nullable": true, "hasDefault": false }
      ],
      "droppedColumns": [],
      "alteredColumns": [],
      "addedForeignKeys": [],
      "droppedForeignKeys": [],
      "addedIndexes": [
        {
          "name": "ix_identity_users_tenant_realm_entra_oid",
          "columns": [ "tenant_id", "realm_id", "entra_object_id" ],
          "unique": true
        },
        {
          "name": "ix_identity_users_tenant_realm_external_id",
          "columns": [ "tenant_id", "realm_id", "external_id" ],
          "unique": true
        },
        {
          "name": "ix_identity_users_tenant_realm_immutable_id",
          "columns": [ "tenant_id", "realm_id", "immutable_id" ],
          "unique": true
        },
        {
          "name": "ix_identity_users_tenant_realm_object_sid",
          "columns": [ "tenant_id", "realm_id", "object_sid" ],
          "unique": true
        },
        {
          "name": "ix_identity_users_tenant_realm_upn",
          "columns": [ "tenant_id", "realm_id", "upn" ],
          "unique": true
        }
      ],
      "droppedIndexes": []
    }
  ]
}
```

### 4.4 SQL Server Step DDL
**File:** `src/Autheris.Infrastructure/Persistence/Migrations/Steps/sqlserver/0012_scim_identity_correlation.sql`

```sql
-- 0012_scim_identity_correlation (SQL Server)
-- Forward-only migration: add hybrid AD / Entra correlation columns to IDENTITY_USERS
-- and IDENTITY_GROUPS, create IDENTITY_GROUP_NESTING and SCIM_REALM_SYNC_STATE tables,
-- and create per-(tenant_id, realm_id) filtered unique correlation indexes.

IF COL_LENGTH('IDENTITY_USERS', 'object_sid') IS NULL
    ALTER TABLE IDENTITY_USERS ADD object_sid NVARCHAR(184) NULL;
-- batch
IF COL_LENGTH('IDENTITY_USERS', 'immutable_id') IS NULL
    ALTER TABLE IDENTITY_USERS ADD immutable_id NVARCHAR(128) NULL;
-- batch
IF COL_LENGTH('IDENTITY_USERS', 'upn') IS NULL
    ALTER TABLE IDENTITY_USERS ADD upn NVARCHAR(256) NULL;
-- batch
IF COL_LENGTH('IDENTITY_USERS', 'entra_object_id') IS NULL
    ALTER TABLE IDENTITY_USERS ADD entra_object_id NVARCHAR(64) NULL;
-- batch
IF COL_LENGTH('IDENTITY_GROUPS', 'object_sid') IS NULL
    ALTER TABLE IDENTITY_GROUPS ADD object_sid NVARCHAR(184) NULL;
-- batch
IF OBJECT_ID('IDENTITY_GROUP_NESTING', 'U') IS NULL
CREATE TABLE IDENTITY_GROUP_NESTING (
    tenant_id NVARCHAR(128) NOT NULL DEFAULT 'default',
    realm_id NVARCHAR(128) NOT NULL,
    parent_group_id NVARCHAR(128) NOT NULL,
    child_group_id NVARCHAR(128) NOT NULL,
    PRIMARY KEY (tenant_id, realm_id, parent_group_id, child_group_id)
);
-- batch
IF OBJECT_ID('SCIM_REALM_SYNC_STATE', 'U') IS NULL
CREATE TABLE SCIM_REALM_SYNC_STATE (
    tenant_id NVARCHAR(128) NOT NULL DEFAULT 'default',
    realm_id NVARCHAR(128) NOT NULL,
    last_contact_at NVARCHAR(64) NULL,
    last_write_at NVARCHAR(64) NULL,
    deprovision_epoch BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, realm_id)
);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_identity_users_tenant_realm_object_sid' AND object_id = OBJECT_ID('IDENTITY_USERS'))
    CREATE UNIQUE INDEX ix_identity_users_tenant_realm_object_sid
        ON IDENTITY_USERS (tenant_id, realm_id, object_sid)
        WHERE object_sid IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_identity_users_tenant_realm_immutable_id' AND object_id = OBJECT_ID('IDENTITY_USERS'))
    CREATE UNIQUE INDEX ix_identity_users_tenant_realm_immutable_id
        ON IDENTITY_USERS (tenant_id, realm_id, immutable_id)
        WHERE immutable_id IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_identity_users_tenant_realm_entra_oid' AND object_id = OBJECT_ID('IDENTITY_USERS'))
    CREATE UNIQUE INDEX ix_identity_users_tenant_realm_entra_oid
        ON IDENTITY_USERS (tenant_id, realm_id, entra_object_id)
        WHERE entra_object_id IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_identity_users_tenant_realm_upn' AND object_id = OBJECT_ID('IDENTITY_USERS'))
    CREATE UNIQUE INDEX ix_identity_users_tenant_realm_upn
        ON IDENTITY_USERS (tenant_id, realm_id, upn)
        WHERE upn IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_identity_users_tenant_realm_external_id' AND object_id = OBJECT_ID('IDENTITY_USERS'))
    CREATE UNIQUE INDEX ix_identity_users_tenant_realm_external_id
        ON IDENTITY_USERS (tenant_id, realm_id, external_id)
        WHERE external_id IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_identity_groups_tenant_realm_object_sid' AND object_id = OBJECT_ID('IDENTITY_GROUPS'))
    CREATE UNIQUE INDEX ix_identity_groups_tenant_realm_object_sid
        ON IDENTITY_GROUPS (tenant_id, realm_id, object_sid)
        WHERE object_sid IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_identity_group_nesting_child' AND object_id = OBJECT_ID('IDENTITY_GROUP_NESTING'))
    CREATE INDEX ix_identity_group_nesting_child
        ON IDENTITY_GROUP_NESTING (tenant_id, realm_id, child_group_id);
```

### 4.5 SQL Server Step Shape Sidecar
**File:** `src/Autheris.Infrastructure/Persistence/Migrations/Steps/sqlserver/0012_scim_identity_correlation.shape.json`

```json
{
  "stepVersion": 12,
  "stepName": "scim_identity_correlation",
  "engine": "sqlserver",
  "addedTables": [
    {
      "name": "IDENTITY_GROUP_NESTING",
      "columns": [
        { "name": "child_group_id", "type": "NVARCHAR", "family": "string", "nullable": false, "hasDefault": false },
        { "name": "parent_group_id", "type": "NVARCHAR", "family": "string", "nullable": false, "hasDefault": false },
        { "name": "realm_id", "type": "NVARCHAR", "family": "string", "nullable": false, "hasDefault": false },
        { "name": "tenant_id", "type": "NVARCHAR", "family": "string", "nullable": false, "hasDefault": true }
      ],
      "primaryKey": [
        "tenant_id",
        "realm_id",
        "parent_group_id",
        "child_group_id"
      ],
      "foreignKeys": [],
      "indexes": [
        {
          "name": "ix_identity_group_nesting_child",
          "columns": [ "tenant_id", "realm_id", "child_group_id" ],
          "unique": false
        }
      ]
    },
    {
      "name": "SCIM_REALM_SYNC_STATE",
      "columns": [
        { "name": "deprovision_epoch", "type": "BIGINT", "family": "integer", "nullable": false, "hasDefault": true },
        { "name": "last_contact_at", "type": "NVARCHAR", "family": "string", "nullable": true, "hasDefault": false },
        { "name": "last_write_at", "type": "NVARCHAR", "family": "string", "nullable": true, "hasDefault": false },
        { "name": "realm_id", "type": "NVARCHAR", "family": "string", "nullable": false, "hasDefault": false },
        { "name": "tenant_id", "type": "NVARCHAR", "family": "string", "nullable": false, "hasDefault": true }
      ],
      "primaryKey": [
        "tenant_id",
        "realm_id"
      ],
      "foreignKeys": [],
      "indexes": []
    }
  ],
  "droppedTables": [],
  "alteredTables": [
    {
      "name": "IDENTITY_GROUPS",
      "addedColumns": [
        { "name": "object_sid", "type": "NVARCHAR", "family": "string", "nullable": true, "hasDefault": false }
      ],
      "droppedColumns": [],
      "alteredColumns": [],
      "addedForeignKeys": [],
      "droppedForeignKeys": [],
      "addedIndexes": [
        {
          "name": "ix_identity_groups_tenant_realm_object_sid",
          "columns": [ "tenant_id", "realm_id", "object_sid" ],
          "unique": true
        }
      ],
      "droppedIndexes": []
    },
    {
      "name": "IDENTITY_USERS",
      "addedColumns": [
        { "name": "entra_object_id", "type": "NVARCHAR", "family": "string", "nullable": true, "hasDefault": false },
        { "name": "immutable_id", "type": "NVARCHAR", "family": "string", "nullable": true, "hasDefault": false },
        { "name": "object_sid", "type": "NVARCHAR", "family": "string", "nullable": true, "hasDefault": false },
        { "name": "upn", "type": "NVARCHAR", "family": "string", "nullable": true, "hasDefault": false }
      ],
      "droppedColumns": [],
      "alteredColumns": [],
      "addedForeignKeys": [],
      "droppedForeignKeys": [],
      "addedIndexes": [
        {
          "name": "ix_identity_users_tenant_realm_entra_oid",
          "columns": [ "tenant_id", "realm_id", "entra_object_id" ],
          "unique": true
        },
        {
          "name": "ix_identity_users_tenant_realm_external_id",
          "columns": [ "tenant_id", "realm_id", "external_id" ],
          "unique": true
        },
        {
          "name": "ix_identity_users_tenant_realm_immutable_id",
          "columns": [ "tenant_id", "realm_id", "immutable_id" ],
          "unique": true
        },
        {
          "name": "ix_identity_users_tenant_realm_object_sid",
          "columns": [ "tenant_id", "realm_id", "object_sid" ],
          "unique": true
        },
        {
          "name": "ix_identity_users_tenant_realm_upn",
          "columns": [ "tenant_id", "realm_id", "upn" ],
          "unique": true
        }
      ],
      "droppedIndexes": []
    }
  ]
}
```

### 4.6 Baseline Schema & Golden Checksum Synchronization
Note: In Autheris's migration model, `0001_baseline` (`PostgreSqlBaselineSchema.cs`, `SqlServerBaselineSchema.cs`, `governance-shape.postgresql.v0001.json`, `governance-shape.sqlserver.v0001.json`) is frozen at version 1 (`GovernanceMigrationFreezeTests`), and steps `0002`..`0012` apply deltas on top of `v0001` via `GovernanceSchemaShapeDelta.Apply(v0001, stepDelta)`.
Therefore:
- **Do NOT modify `governance-shape.postgresql.v0001.json` or `governance-shape.sqlserver.v0001.json`!** (`GovernanceMigrationStepParityTests.Step_sidecars_compose_cleanly_from_baseline` starts from `v0001` and applies steps `0002`..`0012` sequentially; adding `0012` columns/tables to `v0001` would cause `Delta.Apply` for step 12 to throw `already exists`!).
- **Do update `PostgreSqlBaselineSchema.cs` and `SqlServerBaselineSchema.cs`** ONLY if `0001_baseline` is executed prior to step runner, OR keep `PostgreSqlBaselineSchema.cs` and `SqlServerBaselineSchema.cs` aligned with `v0001` because `DatabaseMigrator` runs `0001_baseline` followed by all embedded step scripts `0002`..`0012` in order! (Verify: in `0011`, `PostgreSqlBaselineSchema.cs` still defines `ROLE_DEFINITIONS` with `role_name`, and `0011` drops `role_name` via step SQL. Thus `PostgreSqlBaselineSchema.cs` and `SqlServerBaselineSchema.cs` represent `v0001` and must NOT be modified when adding step `0012`).
- **Golden Checksums (`GovernanceMigrationFreezeTests`):**
  - Append `0012_scim_identity_correlation.sql\t<sha256>` to:
    - `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.postgresql.txt`
    - `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.sqlserver.txt`
  - Formula in `GovernanceMigrationFreezeTests.ComputeStepChecksum`:
    `SHA256("<engine>:0012:scim_identity_correlation:" + sqlTextWithLfLineEndings)`.

---

## 5. Red-Green-Refactor Test Matrix & Verification Commands

### 5.1 Unit & Architecture Test Matrix

| Test File | Test Cases (`[Trait("Category", "Security")]`) | Requirement Verified |
|---|---|---|
| `tests/Autheris.Tests.Unit/Identity/ScimCorrelationSchemaAndValidationTests.cs` (New) | 1. `ValidateGroupRequest_MissingObjectSid_ThrowsValidationException`<br>2. `ValidateGroupRequest_NonCanonicalOrPrivilegedSid_ThrowsValidationException` (`"admin"`, `"S-1-5-32-544"`, `"S-1-5-21-100-200-300-512"`)<br>3. `ValidateGroupRequest_CanonicalAdAndCloudSid_Succeeds` (`"S-1-5-21-111-222-333-1001"`, `"S-1-12-1-1-2-3-4"`)<br>4. `ValidateUserRequest_OptionalCorrelationFields_ValidatesFormatAndRejectsPrivilegedSid`<br>5. `ValidateGroupRequest_MemberType_AcceptsUserAndGroup_RejectsInvalidType`<br>6. `ScimUserAndGroup_JsonSerialization_RoundTripsAutherisExtensions` | Step 13 extension schema, required Group `objectSid`, `SidFormat.IsCanonical` & `!SidFormat.IsPrivileged`, `ScimGroupMember.Type` (`"User"` \| `"Group"`). |
| `tests/Autheris.Tests.Unit/Identity/ScimServiceGroupNestingAndCorrelationTests.cs` (New) | 1. `CreateGroupAsync_SplitsUserAndGroupMembersBetweenTables`<br>2. `CreateGroupAsync_SelfNestingEdge_ThrowsValidationException`<br>3. `PatchGroupAsync_AddAndRemoveGroupMember_UpdatesNestingStore`<br>4. `CreateUserAsync_NormalizesUpnToLowercaseAndEntraObjectIdToCanonicalGuid`<br>5. `CreateUserOrGroup_DuplicateCorrelationAttribute_PropagatesScimUniquenessConflictException`<br>6. `Mutations_TouchRealmSyncStateLastContactAndLastWrite` | Step 13 `ScimService` nesting split (`IDENTITY_GROUP_MEMBERS` vs `IDENTITY_GROUP_NESTING`), `SCIM_REALM_SYNC_STATE` updates, 409 uniqueness conflict propagation. |
| `tests/Autheris.Tests.Unit/Identity/ScimDiscoverySchemaTests.cs` (New) | 1. `GetSchemas_IncludesAutherisUserAndGroupExtensions`<br>2. `GetResourceTypes_MarksGroupExtensionRequiredAndUserExtensionOptional` | `/scim/v2/{realmId}/Schemas` and `/ResourceTypes` advertise `urn:ietf:params:scim:schemas:extension:autheris:2.0:User` and `...:Group`. |
| `tests/Autheris.Tests.Architecture/GovernanceMigrationFreezeTests.cs` (Existing) | `Step_files_match_frozen_checksums_and_contiguous_numbering` | Step `0012` SQL lint, contiguous numbering, and frozen checksums on PostgreSQL and SQL Server. |
| `tests/Autheris.Tests.Architecture/GovernanceMigrationStepParityTests.cs` (Existing) | `Step_sidecars_have_identical_structural_deltas_across_engines` & `Step_sidecars_compose_cleanly_from_baseline` | Step `0012` `.shape.json` structural parity across PostgreSQL and SQL Server and clean composition from `v0001`. |

### 5.2 Local Filtered Unit & Architecture Test Commands (Resource-Constrained)
Per repository execution rules, run only filtered unit/architecture tests with `-m:2 -nodeReuse:false`:

```bash
# 1. Run new SCIM correlation, validation, nesting, and discovery unit tests
NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj \
  --filter "FullyQualifiedName~ScimCorrelationSchemaAndValidationTests|FullyQualifiedName~ScimServiceGroupNestingAndCorrelationTests|FullyQualifiedName~ScimDiscoverySchemaTests|FullyQualifiedName~ScimDataOwnerIdentityVerifierTests|FullyQualifiedName~ScimGroupDeleteCacheInvalidationTests" \
  -m:2 -nodeReuse:false

# 2. Run migration freeze, step parity, security test ratchet, and clean architecture tests
NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj \
  --filter "FullyQualifiedName~GovernanceMigrationFreezeTests|FullyQualifiedName~GovernanceMigrationStepParityTests|FullyQualifiedName~SecurityTestCoverageTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests" \
  -m:2 -nodeReuse:false
```

### 5.3 Proactive Integration Test Inspection & Compile-Only Verification
Inspecting `tests/Autheris.Tests.Integration` for callers affected by PR 5a:
1. **`tests/Autheris.Tests.Integration/EnterpriseFeaturesIntegrationTests.cs` & `tests/Autheris.Tests.Integration/MultiTenantIsolationTests.cs`:**
   - Any integration test that creates a SCIM Group via `POST /scim/v2/{realmId}/Groups` or `ScimGroupRequest` must include a canonical `objectSid` (e.g., `"S-1-5-21-1000000001-1000000002-1000000003-2001"`) in `"urn:ietf:params:scim:schemas:extension:autheris:2.0:Group"` (or `ObjectSid` on `ScimGroupRequest`) so it satisfies the new required group `objectSid` validation rule.
   - Any mock or fake implementation of `IIdentityStoreRepository` in `tests/Autheris.Tests.Unit` or `tests/Autheris.Tests.Integration` must implement the new `IScimUserStore`, `IScimGroupStore`, and `IScimProvisioningStore` methods.
2. **Compile-Only Verification Command (Never execute integration tests locally):**
   ```bash
   NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj \
     --no-restore -m:2 -nodeReuse:false
   ```

---

## 6. Ordered Implementation Checklist for Phase 4 (TDD Developer)

1. **Red — Write Failing Unit Tests:**
   - Create `ScimCorrelationSchemaAndValidationTests.cs`, `ScimServiceGroupNestingAndCorrelationTests.cs`, and `ScimDiscoverySchemaTests.cs` in `tests/Autheris.Tests.Unit/Identity/` with `[Trait("Category", "Security")]`.
2. **Green — Domain & Interfaces:**
   - Update `src/Autheris.Domain/Identity/ScimModels.cs` (`ScimSchemaUris`, `ScimMemberTypes`, `ScimUserAutherisExtension`, `ScimGroupAutherisExtension`, `ScimUser`, `ScimGroup`, `ScimGroupMember`, `ScimGroupNestingEdge`, `ScimRealmSyncState`, `ScimUniquenessConflictException`, `ScimUserRequest`, `ScimGroupRequest`).
   - Update `src/Autheris.Domain/Interfaces/IIdentityStoreRepository.cs` (`IScimUserStore`, `IScimGroupStore`, `IScimProvisioningStore`, `IIdentityStoreRepository`).
3. **Green — Application Validation & Service:**
   - Update `src/Autheris.Application/Identity/Validators/ScimRequestValidator.cs` (required canonical non-privileged Group `objectSid`, optional validated User correlation fields, `ScimGroupMember.Type` `"User"` | `"Group"` validation).
   - Update `src/Autheris.Application/Identity/Services/ScimService.cs` (user correlation attributes, group `objectSid`, group member split between `IDENTITY_GROUP_MEMBERS` and `IDENTITY_GROUP_NESTING`, self-nesting rejection, `SCIM_REALM_SYNC_STATE` touch).
4. **Green — Infrastructure Persistence & Migration Step `0012`:**
   - Update `src/Autheris.Infrastructure/Persistence/IdentityStore.cs` and `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Identity.cs`.
   - Create `0012_scim_identity_correlation.sql` and `0012_scim_identity_correlation.shape.json` under `src/Autheris.Infrastructure/Persistence/Migrations/Steps/postgresql/` and `sqlserver/`.
   - Append SHA-256 step checksums to `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.postgresql.txt` and `checksums.sqlserver.txt`.
5. **Green — API Endpoints:**
   - Update `src/Autheris.Api/Endpoints/ScimDiscoveryEndpoints.cs` to advertise `urn:ietf:params:scim:schemas:extension:autheris:2.0:User` and `urn:ietf:params:scim:schemas:extension:autheris:2.0:Group`.
   - Update `src/Autheris.Api/Endpoints/ScimUsersEndpoints.cs` and `src/Autheris.Api/Endpoints/ScimGroupsEndpoints.cs` to map `ScimUniquenessConflictException` to HTTP `409 Conflict` (`scimType: "uniqueness"`).
6. **Refactor & Ratchet Alignment:**
   - Update any existing SCIM group creation test fixtures in `tests/Autheris.Tests.Unit` and `tests/Autheris.Tests.Integration` to supply a canonical `objectSid`.
   - Update `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt` to reflect the new `[Trait("Category", "Security")]` test count.
   - Run the filtered unit and architecture tests plus `dotnet build` on `Autheris.Tests.Integration`.
