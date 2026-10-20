namespace Autheris.Application.Identity.Services;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;

public interface IScimService
{
    Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);
    Task<ScimUser> CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default);
    Task<ScimUser?> UpdateUserAsync(TenantId tenantId, string realmId, string userKey, ScimUser user, CancellationToken ct = default);
    Task<ScimUser?> PatchUserAsync(TenantId tenantId, string realmId, string userKey, ScimPatchRequest patch, CancellationToken ct = default);
    Task<bool> DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);
    Task<ScimListResponse<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default);

    Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default);
    Task<ScimGroup> CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default);
    Task<ScimGroup?> UpdateGroupAsync(TenantId tenantId, string realmId, string groupId, ScimGroup group, CancellationToken ct = default);
    Task<ScimGroup?> PatchGroupAsync(TenantId tenantId, string realmId, string groupId, ScimPatchRequest patch, CancellationToken ct = default);
    Task<bool> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default);
    Task<ScimListResponse<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default);

    Task EnsureJitUserProvisionedAsync(TenantId tenantId, string realmId, ResolvedIdentity identity, CancellationToken ct = default);
}
