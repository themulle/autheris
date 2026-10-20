namespace Autheris.Application.Identity.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;

public sealed class ScimService : IScimService
{
    private readonly IIdentityStoreRepository _repository;
    private readonly IIdentityDeprovisioningService _deprovisioningService;
    private readonly ScimGroupMembershipValidator? _membershipValidator;
    private readonly IRoleResolverCache? _roleCache;

    public ScimService(
        IIdentityStoreRepository repository,
        IIdentityDeprovisioningService deprovisioningService,
        ScimGroupMembershipValidator? membershipValidator = null,
        IRoleResolverCache? roleCache = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _deprovisioningService = deprovisioningService ?? throw new ArgumentNullException(nameof(deprovisioningService));
        _membershipValidator = membershipValidator;
        _roleCache = roleCache;
    }

    public async Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        return await _repository.GetUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
    }

    public async Task<ScimUser> CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = DateTimeOffset.UtcNow;
        var userKey = string.IsNullOrWhiteSpace(user.Id) ? (user.ExternalId ?? Guid.NewGuid().ToString("N")) : user.Id;

        var entity = user with
        {
            Id = userKey,
            ExternalId = user.ExternalId ?? userKey,
            CreatedAt = user.CreatedAt ?? now,
            UpdatedAt = now
        };

        await _repository.UpsertUserAsync(entity, tenantId, realmId, ct).ConfigureAwait(false);
        return entity;
    }

    public async Task<ScimUser?> UpdateUserAsync(TenantId tenantId, string realmId, string userKey, ScimUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var existing = await _repository.GetUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var updated = user with
        {
            Id = userKey,
            ExternalId = user.ExternalId ?? existing.ExternalId,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now
        };

        if (existing.Active && !updated.Active)
        {
            await _deprovisioningService.DeprovisionAndLockSubjectAsync(
                tenantId, realmId, userKey, "SCIM_DEPROVISION", "SCIM_PUT", ct).ConfigureAwait(false);
        }
        else
        {
            await _repository.UpsertUserAsync(updated, tenantId, realmId, ct).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task<ScimUser?> PatchUserAsync(TenantId tenantId, string realmId, string userKey, ScimPatchRequest patch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var existing = await _repository.GetUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return null;
        }

        bool activeStatus = existing.Active;
        string? displayName = existing.DisplayName;
        string? email = existing.Email;

        foreach (var op in patch.Operations)
        {
            if (string.Equals(op.Op, "replace", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(op.Op, "add", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(op.Path, "active", StringComparison.OrdinalIgnoreCase))
                {
                    if (op.Value is bool b) activeStatus = b;
                    else if (op.Value is JsonElement je && (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False))
                    {
                        activeStatus = je.GetBoolean();
                    }
                    else if (bool.TryParse(op.Value?.ToString(), out var parsed))
                    {
                        activeStatus = parsed;
                    }
                }
                else if (op.Value is JsonElement valElem && valElem.ValueKind == JsonValueKind.Object)
                {
                    if (valElem.TryGetProperty("active", out var actElem))
                    {
                        activeStatus = actElem.GetBoolean();
                    }
                }
            }
        }

        if (existing.Active && !activeStatus)
        {
            // SEC-SC-14: Deprovisioning Lock flow
            await _deprovisioningService.DeprovisionAndLockSubjectAsync(
                tenantId, realmId, userKey, "SCIM_DEPROVISION", "SCIM_PATCH", ct).ConfigureAwait(false);

            return existing with { Active = false, DeactivatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        }

        var updated = existing with
        {
            Active = activeStatus,
            DisplayName = displayName,
            Email = email,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _repository.UpsertUserAsync(updated, tenantId, realmId, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<bool> DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        var existing = await _repository.GetUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return false;
        }

        await _deprovisioningService.DeprovisionAndLockSubjectAsync(
            tenantId, realmId, userKey, "SCIM_DEPROVISION", "SCIM_DELETE", ct).ConfigureAwait(false);

        await _repository.DeleteUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<ScimListResponse<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default)
    {
        var users = await _repository.ListUsersAsync(tenantId, realmId, startIndex, count, filter, ct).ConfigureAwait(false);
        return new ScimListResponse<ScimUser>(users.Count, startIndex, count, users, ["urn:ietf:params:scim:api:messages:2.0:ListResponse"]);
    }

    public async Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default)
    {
        var group = await _repository.GetGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        if (group == null)
        {
            return null;
        }

        var members = await _repository.GetGroupMembersAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        var memberList = members.Select(m => new ScimGroupMember(m)).ToList();
        return group with { Members = memberList };
    }

    public async Task<ScimGroup> CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        var now = DateTimeOffset.UtcNow;
        var groupId = string.IsNullOrWhiteSpace(group.Id) ? (group.ExternalId ?? Guid.NewGuid().ToString("N")) : group.Id;

        var entity = group with
        {
            Id = groupId,
            ExternalId = group.ExternalId ?? groupId,
            CreatedAt = group.CreatedAt ?? now,
            UpdatedAt = now
        };

        await _repository.UpsertGroupAsync(entity, tenantId, realmId, ct).ConfigureAwait(false);

        if (group.Members != null)
        {
            foreach (var m in group.Members)
            {
                await _repository.AddGroupMemberAsync(tenantId, realmId, groupId, m.Value, ct).ConfigureAwait(false);
                _roleCache?.Invalidate(realmId, m.Value);
            }
        }

        return entity;
    }

    public async Task<ScimGroup?> UpdateGroupAsync(TenantId tenantId, string realmId, string groupId, ScimGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        var existing = await _repository.GetGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var updated = group with
        {
            Id = groupId,
            ExternalId = group.ExternalId ?? existing.ExternalId,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now
        };

        await _repository.UpsertGroupAsync(updated, tenantId, realmId, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<ScimGroup?> PatchGroupAsync(TenantId tenantId, string realmId, string groupId, ScimPatchRequest patch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var existing = await _repository.GetGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return null;
        }

        var currentMembers = await _repository.GetGroupMembersAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        var membersToRemove = new List<string>();
        var membersToAdd = new List<string>();

        foreach (var op in patch.Operations)
        {
            if (string.Equals(op.Op, "remove", StringComparison.OrdinalIgnoreCase))
            {
                // Can be path "members[value eq \"xyz\"]" or array in value
                if (!string.IsNullOrWhiteSpace(op.Path) && op.Path.Contains("value eq", StringComparison.OrdinalIgnoreCase))
                {
                    var start = op.Path.IndexOf('"');
                    var end = op.Path.LastIndexOf('"');
                    if (start >= 0 && end > start)
                    {
                        var targetUserKey = op.Path.Substring(start + 1, end - start - 1);
                        membersToRemove.Add(targetUserKey);
                    }
                }
                else if (op.Value is JsonElement arr && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.TryGetProperty("value", out var valProp))
                        {
                            membersToRemove.Add(valProp.GetString()!);
                        }
                    }
                }
                else if (op.Value is IEnumerable<ScimGroupMember> memberList)
                {
                    membersToRemove.AddRange(memberList.Select(m => m.Value));
                }
            }
            else if (string.Equals(op.Op, "add", StringComparison.OrdinalIgnoreCase))
            {
                if (op.Value is JsonElement arr && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.TryGetProperty("value", out var valProp))
                        {
                            membersToAdd.Add(valProp.GetString()!);
                        }
                    }
                }
                else if (op.Value is IEnumerable<ScimGroupMember> memberList)
                {
                    membersToAdd.AddRange(memberList.Select(m => m.Value));
                }
            }
        }

        // SEC-SC-14: Quarantine check on mass member removal
        if (_membershipValidator != null && membersToRemove.Count > 0)
        {
            await _membershipValidator.ValidateMembershipChangeAsync(
                tenantId, realmId, groupId, currentMembers.Count, membersToRemove.Count, ct).ConfigureAwait(false);
        }

        foreach (var toRemove in membersToRemove)
        {
            await _repository.RemoveGroupMemberAsync(tenantId, realmId, groupId, toRemove, ct).ConfigureAwait(false);
            _roleCache?.Invalidate(realmId, toRemove);
        }

        foreach (var toAdd in membersToAdd)
        {
            await _repository.AddGroupMemberAsync(tenantId, realmId, groupId, toAdd, ct).ConfigureAwait(false);
            _roleCache?.Invalidate(realmId, toAdd);
        }

        return await GetGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default)
    {
        var existing = await _repository.GetGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return false;
        }

        await _repository.DeleteGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<ScimListResponse<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default)
    {
        var groups = await _repository.ListGroupsAsync(tenantId, realmId, startIndex, count, filter, ct).ConfigureAwait(false);
        return new ScimListResponse<ScimGroup>(groups.Count, startIndex, count, groups, ["urn:ietf:params:scim:api:messages:2.0:ListResponse"]);
    }

    public async Task EnsureJitUserProvisionedAsync(TenantId tenantId, string realmId, ResolvedIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var existing = await _repository.GetUserAsync(tenantId, realmId, identity.UserKey, ct).ConfigureAwait(false);
        if (existing == null)
        {
            var now = DateTimeOffset.UtcNow;
            var jitUser = new ScimUser(
                Id: identity.UserKey,
                ExternalId: identity.UserKey,
                UserName: identity.Upn ?? identity.UserKey,
                DisplayName: identity.DisplayName,
                Email: identity.Email,
                Active: true,
                DeactivatedAt: null,
                CreatedAt: now,
                UpdatedAt: now
            );

            await _repository.UpsertUserAsync(jitUser, tenantId, realmId, ct).ConfigureAwait(false);
        }
    }
}
