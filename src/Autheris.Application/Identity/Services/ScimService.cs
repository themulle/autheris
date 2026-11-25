namespace Autheris.Application.Identity.Services;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Validators;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed partial class ScimService : IScimService
{
    private readonly IIdentityStoreRepository _repository;
    private readonly IIdentityDeprovisioningService _deprovisioningService;
    private readonly ScimGroupMembershipValidator? _membershipValidator;
    private readonly IRoleResolverCache? _roleCache;
    private readonly ScimMassChangeGuard? _massChangeGuard;
    private readonly IScimSyncStateTracker? _syncStateTracker;
    private readonly IAuditWriter? _auditWriter;
    private readonly IIdentityRealmRegistry? _realmRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly IGovernanceChangeNotifier? _governanceChangeNotifier;
    private readonly PrivilegedGroupChangeAuditor? _privilegedGroupAuditor;

    public ScimService(
        IIdentityStoreRepository repository,
        IIdentityDeprovisioningService deprovisioningService,
        ScimGroupMembershipValidator? membershipValidator = null,
        IRoleResolverCache? roleCache = null,
        ScimMassChangeGuard? massChangeGuard = null,
        TimeProvider? timeProvider = null,
        IScimSyncStateTracker? syncStateTracker = null,
        IAuditWriter? auditWriter = null,
        IIdentityRealmRegistry? realmRegistry = null,
        IGovernanceChangeNotifier? governanceChangeNotifier = null,
        PrivilegedGroupChangeAuditor? privilegedGroupAuditor = null)
    {
        _massChangeGuard = massChangeGuard;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _deprovisioningService = deprovisioningService ?? throw new ArgumentNullException(nameof(deprovisioningService));
        _membershipValidator = membershipValidator;
        _roleCache = roleCache;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _syncStateTracker = syncStateTracker;
        _auditWriter = auditWriter;
        _realmRegistry = realmRegistry;
        _governanceChangeNotifier = governanceChangeNotifier;
        _privilegedGroupAuditor = privilegedGroupAuditor;
    }

    public async Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        var user = await _repository.GetUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
        await RecordReadContactAsync(tenantId, realmId, ct).ConfigureAwait(false);
        return user;
    }

    public async Task<ScimUser> CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ScimRequestValidator.ValidateUserCreate(user);

        var now = _timeProvider.GetUtcNow();
        var userKey = string.IsNullOrWhiteSpace(user.Id) ? (user.ExternalId ?? Guid.NewGuid().ToString("N")) : user.Id;

        var entity = new ScimUser(
            Id: userKey,
            ExternalId: user.ExternalId ?? userKey,
            UserName: user.UserName,
            DisplayName: user.DisplayName,
            Email: user.Email,
            Active: user.Active,
            DeactivatedAt: user.DeactivatedAt,
            CreatedAt: user.CreatedAt ?? now,
            UpdatedAt: now,
            ObjectSid: user.ResolvedObjectSid,
            OnPremisesImmutableId: user.ResolvedOnPremisesImmutableId,
            UserPrincipalName: ScimRequestValidator.NormalizeUpn(user.ResolvedUserPrincipalName),
            EntraObjectId: ScimRequestValidator.NormalizeEntraObjectId(user.ResolvedEntraObjectId));

        await RecordRequiredScimAuditAsync(
            tenantId,
            realmId,
            AuditEventTypes.ScimUserProvisioned,
            "IDENTITY_USERS",
            now,
            new { realm_id = realmId, user_key = userKey, active = entity.Active },
            ct).ConfigureAwait(false);

        await _repository.CreateUserAsync(tenantId, realmId, entity, ct).ConfigureAwait(false);
        await RecordWriteContactAsync(tenantId, realmId, now, ct).ConfigureAwait(false);
        return entity;
    }

    public async Task<ScimUser?> UpdateUserAsync(TenantId tenantId, string realmId, string userKey, ScimUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ScimRequestValidator.ValidateUserUpdate(user);

        var existing = await _repository.GetUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var updated = new ScimUser(
            Id: userKey,
            ExternalId: user.ExternalId ?? existing.ExternalId,
            UserName: user.UserName,
            DisplayName: user.DisplayName,
            Email: user.Email,
            Active: user.Active,
            DeactivatedAt: user.DeactivatedAt,
            CreatedAt: existing.CreatedAt,
            UpdatedAt: now,
            ObjectSid: user.ResolvedObjectSid,
            OnPremisesImmutableId: user.ResolvedOnPremisesImmutableId,
            UserPrincipalName: ScimRequestValidator.NormalizeUpn(user.ResolvedUserPrincipalName),
            EntraObjectId: ScimRequestValidator.NormalizeEntraObjectId(user.ResolvedEntraObjectId));

        if (!updated.Active)
        {
            await EnsureMassChangeAllowedAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
            await RecordRequiredScimAuditAsync(
                tenantId,
                realmId,
                AuditEventTypes.ScimUserDeprovisioned,
                "IDENTITY_USERS",
                now,
                new { realm_id = realmId, user_key = userKey, operation = "deactivate" },
                ct).ConfigureAwait(false);
            await _deprovisioningService.DeprovisionAndLockSubjectAsync(
                tenantId, realmId, userKey, "SCIM_DEPROVISION", "SCIM_PUT", ct).ConfigureAwait(false);
            await _repository.IncrementRealmDeprovisionEpochAsync(tenantId, realmId, ct).ConfigureAwait(false);
            _roleCache?.Invalidate(realmId, userKey);
            await NotifyIdentityGovernanceChangeAsync(tenantId, realmId, userKey, groupId: null, now, ct).ConfigureAwait(false);
        }
        else
        {
            await RecordRequiredScimAuditAsync(
                tenantId,
                realmId,
                AuditEventTypes.ScimUserUpdated,
                "IDENTITY_USERS",
                now,
                new { realm_id = realmId, user_key = userKey, active = updated.Active },
                ct).ConfigureAwait(false);
            await _repository.UpdateUserAsync(tenantId, realmId, updated, ct).ConfigureAwait(false);
        }

        await RecordWriteContactAsync(tenantId, realmId, now, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<ScimUser?> PatchUserAsync(TenantId tenantId, string realmId, string userKey, ScimPatchRequest patch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ScimRequestValidator.ValidatePatchRequest(patch, isGroup: false);

        var existing = await _repository.GetUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        bool activeStatus = existing.Active;
        string? displayName = existing.DisplayName;
        string? email = existing.Email;
        string? objectSid = existing.ObjectSid;
        string? immutableId = existing.OnPremisesImmutableId;
        string? upn = existing.UserPrincipalName;
        string? entraObjectId = existing.EntraObjectId;

        foreach (var op in patch.Operations)
        {
            ApplyUserPatchOperation(
                op,
                ref activeStatus,
                ref displayName,
                ref email,
                ref objectSid,
                ref immutableId,
                ref upn,
                ref entraObjectId);
        }

        ScimRequestValidator.ValidateUserCorrelationFields(objectSid, immutableId, upn, entraObjectId);

        if (!activeStatus)
        {
            await EnsureMassChangeAllowedAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
            await RecordRequiredScimAuditAsync(
                tenantId,
                realmId,
                AuditEventTypes.ScimUserDeprovisioned,
                "IDENTITY_USERS",
                now,
                new { realm_id = realmId, user_key = userKey, operation = "deactivate" },
                ct).ConfigureAwait(false);
            await _deprovisioningService.DeprovisionAndLockSubjectAsync(
                tenantId, realmId, userKey, "SCIM_DEPROVISION", "SCIM_PATCH", ct).ConfigureAwait(false);
            await _repository.IncrementRealmDeprovisionEpochAsync(tenantId, realmId, ct).ConfigureAwait(false);
            _roleCache?.Invalidate(realmId, userKey);
            await NotifyIdentityGovernanceChangeAsync(tenantId, realmId, userKey, groupId: null, now, ct).ConfigureAwait(false);
            await RecordWriteContactAsync(tenantId, realmId, now, ct).ConfigureAwait(false);

            return existing with { Active = false, DeactivatedAt = now, UpdatedAt = now };
        }

        var updated = existing with
        {
            Active = activeStatus,
            DisplayName = displayName,
            Email = email,
            ObjectSid = objectSid,
            OnPremisesImmutableId = immutableId,
            UserPrincipalName = ScimRequestValidator.NormalizeUpn(upn),
            EntraObjectId = ScimRequestValidator.NormalizeEntraObjectId(entraObjectId),
            UpdatedAt = now
        };

        await RecordRequiredScimAuditAsync(
            tenantId,
            realmId,
            AuditEventTypes.ScimUserUpdated,
            "IDENTITY_USERS",
            now,
            new { realm_id = realmId, user_key = userKey, active = updated.Active },
            ct).ConfigureAwait(false);
        await _repository.UpdateUserAsync(tenantId, realmId, updated, ct).ConfigureAwait(false);
        await RecordWriteContactAsync(tenantId, realmId, now, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<bool> DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        var existing = await _repository.GetUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        await EnsureMassChangeAllowedAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
        await RecordRequiredScimAuditAsync(
            tenantId,
            realmId,
            AuditEventTypes.ScimUserDeprovisioned,
            "IDENTITY_USERS",
            now,
            new { realm_id = realmId, user_key = userKey, operation = "delete" },
            ct).ConfigureAwait(false);
        await _deprovisioningService.DeprovisionAndLockSubjectAsync(
            tenantId, realmId, userKey, "SCIM_DEPROVISION", "SCIM_DELETE", ct).ConfigureAwait(false);

        await _repository.DeleteUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);
        await _repository.IncrementRealmDeprovisionEpochAsync(tenantId, realmId, ct).ConfigureAwait(false);
        _roleCache?.Invalidate(realmId, userKey);
        await NotifyIdentityGovernanceChangeAsync(tenantId, realmId, userKey, groupId: null, now, ct).ConfigureAwait(false);
        await RecordWriteContactAsync(tenantId, realmId, now, ct).ConfigureAwait(false);
        return true;
    }

    private Task EnsureMassChangeAllowedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct) =>
        _massChangeGuard?.EnsureAllowedAsync(tenantId, realmId, userKey, ct) ?? Task.CompletedTask;

    private Task RecordReadContactAsync(TenantId tenantId, string realmId, CancellationToken ct) =>
        _syncStateTracker?.RecordAuthenticatedContactAsync(tenantId, realmId, isWrite: false, ct) ?? Task.CompletedTask;

    private Task RecordWriteContactAsync(TenantId tenantId, string realmId, DateTimeOffset now, CancellationToken ct) =>
        _syncStateTracker is not null
            ? _syncStateTracker.RecordAuthenticatedContactAsync(tenantId, realmId, isWrite: true, ct)
            : _repository.UpsertRealmSyncStateAsync(tenantId, realmId, now, now, ct);

    public async Task<ScimListResponse<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default)
    {
        var users = await _repository.ListUsersAsync(tenantId, realmId, startIndex, count, filter, ct).ConfigureAwait(false);
        await RecordReadContactAsync(tenantId, realmId, ct).ConfigureAwait(false);
        return new ScimListResponse<ScimUser>(users.Count, startIndex, count, users, [ScimSchemaUris.ListResponse]);
    }

    public async Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default)
    {
        var group = await GetGroupInternalAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        await RecordReadContactAsync(tenantId, realmId, ct).ConfigureAwait(false);
        return group;
    }

    private async Task<ScimGroup?> GetGroupInternalAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct)
    {
        var group = await _repository.GetGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        if (group == null)
        {
            return null;
        }

        var userMembers = await _repository.GetGroupMembersAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        var childGroups = await _repository.GetChildGroupIdsAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        var memberList = new List<ScimGroupMember>(userMembers.Count + childGroups.Count);
        foreach (var u in userMembers)
        {
            memberList.Add(new ScimGroupMember(u, Type: ScimMemberTypes.User));
        }

        foreach (var g in childGroups)
        {
            memberList.Add(new ScimGroupMember(g, Type: ScimMemberTypes.Group));
        }

        return group with { Members = memberList };
    }

    public async Task<ScimGroup> CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        var realm = _realmRegistry?.GetRealm(realmId);
        var groupId = string.IsNullOrWhiteSpace(group.Id) ? (group.ExternalId ?? Guid.NewGuid().ToString("N")) : group.Id;
        var resolvedObjectSid = ScimRequestValidator.ResolveAndValidateGroupObjectSid(realmId, groupId, group.ResolvedObjectSid, realm);

        var now = _timeProvider.GetUtcNow();
        ValidateNoSelfNesting(groupId, group.Members);

        var entity = new ScimGroup(
            Id: groupId,
            ExternalId: group.ExternalId ?? groupId,
            DisplayName: group.DisplayName,
            Members: group.Members,
            CreatedAt: group.CreatedAt ?? now,
            UpdatedAt: now,
            ObjectSid: resolvedObjectSid);
        ScimRequestValidator.ValidateGroupCreate(entity, ScimRequestValidator.UsesRealmBoundGroupKey(realm));

        var initialChanges = (group.Members ?? [])
            .Select(m => new ScimMembershipAuditItem(m.Value, ScimRequestValidator.NormalizeMemberType(m.Type), "add"))
            .ToList();

        var privilegedClassification = initialChanges.Count > 0
            ? await PreparePrivilegedGroupAuditAsync(tenantId, realmId, groupId, entity.ObjectSid, null, initialChanges, "add", now, ct).ConfigureAwait(false)
            : null;

        await RecordRequiredScimAuditAsync(
            tenantId,
            realmId,
            AuditEventTypes.ScimGroupProvisioned,
            "IDENTITY_GROUPS",
            now,
            new { realm_id = realmId, group_id = groupId, group_sid = entity.ObjectSid },
            ct).ConfigureAwait(false);

        if (group.Members != null)
        {
            foreach (var m in group.Members)
            {
                var memberType = ScimRequestValidator.NormalizeMemberType(m.Type);
                await RecordMembershipChangeAuditAsync(
                    tenantId, realmId, groupId, entity.ObjectSid, m.Value, memberType, "add", now, ct).ConfigureAwait(false);
            }
        }

        await _repository.CreateGroupAsync(tenantId, realmId, entity, ct).ConfigureAwait(false);

        if (group.Members is { Count: > 0 })
        {
            bool hasNestedGroup = false;
            foreach (var m in group.Members)
            {
                var memberType = ScimRequestValidator.NormalizeMemberType(m.Type);
                if (memberType == ScimMemberTypes.Group)
                {
                    hasNestedGroup = true;
                    await _repository.AddGroupNestingEdgeAsync(tenantId, realmId, groupId, m.Value, ct).ConfigureAwait(false);
                }
                else
                {
                    await _repository.AddGroupMemberAsync(tenantId, realmId, groupId, m.Value, ct).ConfigureAwait(false);
                    _roleCache?.Invalidate(realmId, m.Value);
                }
            }

            if (hasNestedGroup)
            {
                _roleCache?.InvalidateAll();
            }

            await _repository.IncrementRealmDeprovisionEpochAsync(tenantId, realmId, ct).ConfigureAwait(false);
            await CompleteGroupChangeNotificationsAsync(tenantId, realmId, groupId, "add", privilegedClassification, now, ct).ConfigureAwait(false);
        }

        await RecordWriteContactAsync(tenantId, realmId, now, ct).ConfigureAwait(false);
        return entity;
    }

    public async Task<ScimGroup?> UpdateGroupAsync(TenantId tenantId, string realmId, string groupId, ScimGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        var realm = _realmRegistry?.GetRealm(realmId);
        var resolvedObjectSid = ScimRequestValidator.ResolveAndValidateGroupObjectSid(realmId, groupId, group.ResolvedObjectSid, realm);
        ScimRequestValidator.ValidateGroupUpdate(group with { ObjectSid = resolvedObjectSid }, ScimRequestValidator.UsesRealmBoundGroupKey(realm));
        ValidateNoSelfNesting(groupId, group.Members);

        var existing = await _repository.GetGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var updated = new ScimGroup(
            Id: groupId,
            ExternalId: group.ExternalId ?? existing.ExternalId,
            DisplayName: group.DisplayName,
            Members: group.Members,
            CreatedAt: existing.CreatedAt,
            UpdatedAt: now,
            ObjectSid: resolvedObjectSid);

        List<string> usersToRemove = [];
        List<string> addedUsers = [];
        List<string> removedChildren = [];
        List<string> addedChildren = [];

        if (group.Members != null)
        {
            var currentUsers = (await _repository.GetGroupMembersAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            var currentChildren = (await _repository.GetChildGroupIdsAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

            var targetUsers = group.Members
                .Where(m => ScimRequestValidator.NormalizeMemberType(m.Type) == ScimMemberTypes.User)
                .Select(m => m.Value)
                .ToHashSet(StringComparer.Ordinal);
            var targetChildren = group.Members
                .Where(m => ScimRequestValidator.NormalizeMemberType(m.Type) == ScimMemberTypes.Group)
                .Select(m => m.Value)
                .ToHashSet(StringComparer.Ordinal);

            usersToRemove = currentUsers.Except(targetUsers, StringComparer.Ordinal).ToList();
            addedUsers = targetUsers.Except(currentUsers, StringComparer.Ordinal).ToList();
            removedChildren = currentChildren.Except(targetChildren, StringComparer.Ordinal).ToList();
            addedChildren = targetChildren.Except(currentChildren, StringComparer.Ordinal).ToList();

            if (_membershipValidator != null && usersToRemove.Count > 0)
            {
                await _membershipValidator.ValidateMembershipChangeAsync(
                    tenantId, realmId, groupId, currentUsers.Count, usersToRemove.Count, updated.ObjectSid, existing.ObjectSid, ct).ConfigureAwait(false);
            }
        }

        var auditChanges = new List<ScimMembershipAuditItem>(usersToRemove.Count + removedChildren.Count + addedUsers.Count + addedChildren.Count);
        auditChanges.AddRange(usersToRemove.Select(u => new ScimMembershipAuditItem(u, ScimMemberTypes.User, "remove")));
        auditChanges.AddRange(removedChildren.Select(g => new ScimMembershipAuditItem(g, ScimMemberTypes.Group, "remove")));
        auditChanges.AddRange(addedUsers.Select(u => new ScimMembershipAuditItem(u, ScimMemberTypes.User, "add")));
        auditChanges.AddRange(addedChildren.Select(g => new ScimMembershipAuditItem(g, ScimMemberTypes.Group, "add")));

        var sidChanged = !string.Equals(existing.ObjectSid, updated.ObjectSid, StringComparison.OrdinalIgnoreCase);
        var privilegedClassification = (auditChanges.Count > 0 || sidChanged)
            ? await PreparePrivilegedGroupAuditAsync(
                tenantId,
                realmId,
                groupId,
                updated.ObjectSid,
                existing.ObjectSid,
                auditChanges,
                auditChanges.Count == 0 ? "update" : "membership_change",
                now,
                ct).ConfigureAwait(false)
            : null;

        if (auditChanges.Count == 0)
        {
            await RecordRequiredScimAuditAsync(
                tenantId,
                realmId,
                AuditEventTypes.ScimGroupMembershipChanged,
                "IDENTITY_GROUPS",
                now,
                new { realm_id = realmId, group_id = groupId, group_sid = updated.ObjectSid, operation = "update" },
                ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var removedUser in usersToRemove)
            {
                await RecordMembershipChangeAuditAsync(
                    tenantId, realmId, groupId, updated.ObjectSid, removedUser, ScimMemberTypes.User, "remove", now, ct).ConfigureAwait(false);
            }
            foreach (var removedChild in removedChildren)
            {
                await RecordMembershipChangeAuditAsync(
                    tenantId, realmId, groupId, updated.ObjectSid, removedChild, ScimMemberTypes.Group, "remove", now, ct).ConfigureAwait(false);
            }
            foreach (var addedUser in addedUsers)
            {
                await RecordMembershipChangeAuditAsync(
                    tenantId, realmId, groupId, updated.ObjectSid, addedUser, ScimMemberTypes.User, "add", now, ct).ConfigureAwait(false);
            }
            foreach (var addedChild in addedChildren)
            {
                await RecordMembershipChangeAuditAsync(
                    tenantId, realmId, groupId, updated.ObjectSid, addedChild, ScimMemberTypes.Group, "add", now, ct).ConfigureAwait(false);
            }
        }

        await _repository.UpdateGroupAsync(tenantId, realmId, updated, ct).ConfigureAwait(false);

        if (group.Members != null)
        {
            foreach (var removedUser in usersToRemove)
            {
                await _repository.RemoveGroupMemberAsync(tenantId, realmId, groupId, removedUser, ct).ConfigureAwait(false);
                _roleCache?.Invalidate(realmId, removedUser);
            }

            foreach (var addedUser in addedUsers)
            {
                await _repository.AddGroupMemberAsync(tenantId, realmId, groupId, addedUser, ct).ConfigureAwait(false);
                _roleCache?.Invalidate(realmId, addedUser);
            }

            foreach (var removedChild in removedChildren)
            {
                await _repository.RemoveGroupNestingEdgeAsync(tenantId, realmId, groupId, removedChild, ct).ConfigureAwait(false);
            }

            foreach (var addedChild in addedChildren)
            {
                await _repository.AddGroupNestingEdgeAsync(tenantId, realmId, groupId, addedChild, ct).ConfigureAwait(false);
            }

            if (removedChildren.Count > 0 || addedChildren.Count > 0 || sidChanged)
            {
                _roleCache?.InvalidateAll();
            }

            if (usersToRemove.Count > 0 || removedChildren.Count > 0 || addedUsers.Count > 0 || addedChildren.Count > 0)
            {
                await _repository.IncrementRealmDeprovisionEpochAsync(tenantId, realmId, ct).ConfigureAwait(false);
            }
        }
        else if (sidChanged)
        {
            _roleCache?.InvalidateAll();
        }

        if (auditChanges.Count > 0 || sidChanged)
        {
            await CompleteGroupChangeNotificationsAsync(
                tenantId,
                realmId,
                groupId,
                auditChanges.Count == 0 ? "update" : "membership_change",
                privilegedClassification,
                now,
                ct).ConfigureAwait(false);
        }

        await RecordWriteContactAsync(tenantId, realmId, now, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<ScimGroup?> PatchGroupAsync(TenantId tenantId, string realmId, string groupId, ScimPatchRequest patch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var realm = _realmRegistry?.GetRealm(realmId);
        var allowRealmGroupKey = ScimRequestValidator.UsesRealmBoundGroupKey(realm);
        ScimRequestValidator.ValidatePatchRequest(patch, isGroup: true, allowRealmGroupKey: allowRealmGroupKey);

        var existing = await _repository.GetGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var currentUserMembers = await _repository.GetGroupMembersAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        var membersToRemove = new List<(string Value, string? ExplicitType)>();
        var membersToAdd = new List<(string Value, string Type)>();
        string? newObjectSid = existing.ObjectSid;
        string newDisplayName = existing.DisplayName;
        bool metadataChanged = false;

        foreach (var op in patch.Operations)
        {
            if (string.Equals(op.Op, "remove", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(op.Path) && op.Path.Contains("value eq", StringComparison.OrdinalIgnoreCase))
                {
                    var start = op.Path.IndexOf('"', StringComparison.Ordinal);
                    var end = op.Path.LastIndexOf('"', StringComparison.Ordinal);
                    if (start >= 0 && end > start)
                    {
                        var targetValue = op.Path.Substring(start + 1, end - start - 1);
                        membersToRemove.Add((targetValue, null));
                    }
                }
                else
                {
                    ExtractMembers(op.Value, defaultType: null, membersToRemove);
                }
            }
            else if (string.Equals(op.Op, "add", StringComparison.OrdinalIgnoreCase))
            {
                if (IsGroupObjectSidPath(op.Path))
                {
                    newObjectSid = ExtractStringValue(op.Value);
                    metadataChanged = true;
                }
                else
                {
                    ExtractMembersToAdd(op.Value, membersToAdd);
                }
            }
            else if (string.Equals(op.Op, "replace", StringComparison.OrdinalIgnoreCase))
            {
                if (IsGroupObjectSidPath(op.Path))
                {
                    newObjectSid = ExtractStringValue(op.Value);
                    metadataChanged = true;
                }
                else if (string.Equals(op.Path?.Trim(), "displayName", StringComparison.OrdinalIgnoreCase))
                {
                    var d = ExtractStringValue(op.Value);
                    if (!string.IsNullOrWhiteSpace(d))
                    {
                        newDisplayName = d;
                        metadataChanged = true;
                    }
                }
            }
        }

        foreach (var added in membersToAdd)
        {
            if (added.Type == ScimMemberTypes.Group && string.Equals(added.Value, groupId, StringComparison.Ordinal))
            {
                throw new ValidationException("A group cannot be nested as a member of itself.");
            }
        }

        ScimGroup? updatedGroup = null;
        if (metadataChanged)
        {
            var resolvedSid = ScimRequestValidator.ResolveAndValidateGroupObjectSid(realmId, groupId, newObjectSid, realm);
            updatedGroup = existing with { DisplayName = newDisplayName, ObjectSid = resolvedSid, UpdatedAt = now };
            ScimRequestValidator.ValidateGroupUpdate(updatedGroup, allowRealmGroupKey);
        }

        var effectiveGroupSid = updatedGroup?.ObjectSid ?? existing.ObjectSid;
        var userRemovals = membersToRemove
            .Where(m => m.ExplicitType is null || m.ExplicitType == ScimMemberTypes.User)
            .Select(m => m.Value)
            .ToList();

        if (_membershipValidator != null && userRemovals.Count > 0)
        {
            await _membershipValidator.ValidateMembershipChangeAsync(
                tenantId, realmId, groupId, currentUserMembers.Count, userRemovals.Count, effectiveGroupSid, existing.ObjectSid, ct).ConfigureAwait(false);
        }

        var currentUserSet = currentUserMembers.ToHashSet(StringComparer.Ordinal);
        var patchAuditChanges = new List<ScimMembershipAuditItem>(membersToRemove.Count + membersToAdd.Count);
        foreach (var (toRemove, explicitType) in membersToRemove)
        {
            var memberType = explicitType ?? (currentUserSet.Contains(toRemove) ? ScimMemberTypes.User : ScimMemberTypes.Group);
            patchAuditChanges.Add(new ScimMembershipAuditItem(toRemove, memberType, "remove"));
        }

        foreach (var (toAdd, memberType) in membersToAdd)
        {
            patchAuditChanges.Add(new ScimMembershipAuditItem(toAdd, memberType, "add"));
        }

        var hasChange = patchAuditChanges.Count > 0 || metadataChanged;
        var privilegedPatchClassification = hasChange
            ? await PreparePrivilegedGroupAuditAsync(
                tenantId,
                realmId,
                groupId,
                effectiveGroupSid,
                existing.ObjectSid,
                patchAuditChanges,
                patchAuditChanges.Count == 0 ? "update" : "membership_change",
                now,
                ct).ConfigureAwait(false)
            : null;

        if (membersToRemove.Count == 0 && membersToAdd.Count == 0 && metadataChanged)
        {
            await RecordRequiredScimAuditAsync(
                tenantId,
                realmId,
                AuditEventTypes.ScimGroupMembershipChanged,
                "IDENTITY_GROUPS",
                now,
                new { realm_id = realmId, group_id = groupId, group_sid = effectiveGroupSid, operation = "update" },
                ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var change in patchAuditChanges)
            {
                await RecordMembershipChangeAuditAsync(
                    tenantId, realmId, groupId, effectiveGroupSid, change.MemberKey, change.MemberType, change.Operation, now, ct).ConfigureAwait(false);
            }
        }

        if (updatedGroup is not null)
        {
            await _repository.UpdateGroupAsync(tenantId, realmId, updatedGroup, ct).ConfigureAwait(false);
        }

        var invalidateAllRoles = updatedGroup is not null && !string.Equals(existing.ObjectSid, updatedGroup.ObjectSid, StringComparison.OrdinalIgnoreCase);
        foreach (var (toRemove, explicitType) in membersToRemove)
        {
            if (explicitType == ScimMemberTypes.Group)
            {
                invalidateAllRoles = true;
                await _repository.RemoveGroupNestingEdgeAsync(tenantId, realmId, groupId, toRemove, ct).ConfigureAwait(false);
            }
            else if (explicitType == ScimMemberTypes.User)
            {
                await _repository.RemoveGroupMemberAsync(tenantId, realmId, groupId, toRemove, ct).ConfigureAwait(false);
                _roleCache?.Invalidate(realmId, toRemove);
            }
            else
            {
                invalidateAllRoles = true;
                await _repository.RemoveGroupMemberAsync(tenantId, realmId, groupId, toRemove, ct).ConfigureAwait(false);
                await _repository.RemoveGroupNestingEdgeAsync(tenantId, realmId, groupId, toRemove, ct).ConfigureAwait(false);
                _roleCache?.Invalidate(realmId, toRemove);
            }
        }

        foreach (var (toAdd, memberType) in membersToAdd)
        {
            if (memberType == ScimMemberTypes.Group)
            {
                invalidateAllRoles = true;
                await _repository.AddGroupNestingEdgeAsync(tenantId, realmId, groupId, toAdd, ct).ConfigureAwait(false);
            }
            else
            {
                await _repository.AddGroupMemberAsync(tenantId, realmId, groupId, toAdd, ct).ConfigureAwait(false);
                _roleCache?.Invalidate(realmId, toAdd);
            }
        }

        if (invalidateAllRoles)
        {
            _roleCache?.InvalidateAll();
        }

        if (membersToRemove.Count > 0 || membersToAdd.Count > 0)
        {
            await _repository.IncrementRealmDeprovisionEpochAsync(tenantId, realmId, ct).ConfigureAwait(false);
        }

        if (hasChange)
        {
            await CompleteGroupChangeNotificationsAsync(
                tenantId,
                realmId,
                groupId,
                patchAuditChanges.Count == 0 ? "update" : "membership_change",
                privilegedPatchClassification,
                now,
                ct).ConfigureAwait(false);
        }

        await RecordWriteContactAsync(tenantId, realmId, now, ct).ConfigureAwait(false);
        return await GetGroupInternalAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default)
    {
        var existing = await _repository.GetGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var privilegedDeleteClassification = await PreparePrivilegedGroupAuditAsync(
            tenantId, realmId, groupId, existing.ObjectSid, null, [], "delete", now, ct).ConfigureAwait(false);

        await RecordRequiredScimAuditAsync(
            tenantId,
            realmId,
            AuditEventTypes.ScimGroupDeleted,
            "IDENTITY_GROUPS",
            now,
            new { realm_id = realmId, group_id = groupId, group_sid = existing.ObjectSid },
            ct).ConfigureAwait(false);

        var formerMembers = await _repository.DeleteGroupAsync(tenantId, realmId, groupId, ct).ConfigureAwait(false);
        foreach (var memberKey in formerMembers)
        {
            _roleCache?.Invalidate(realmId, memberKey);
        }
        _roleCache?.InvalidateAll();

        await _repository.IncrementRealmDeprovisionEpochAsync(tenantId, realmId, ct).ConfigureAwait(false);
        await CompleteGroupChangeNotificationsAsync(tenantId, realmId, groupId, "delete", privilegedDeleteClassification, now, ct).ConfigureAwait(false);
        await RecordWriteContactAsync(tenantId, realmId, now, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<ScimListResponse<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default)
    {
        var groups = await _repository.ListGroupsAsync(tenantId, realmId, startIndex, count, filter, ct).ConfigureAwait(false);
        await RecordReadContactAsync(tenantId, realmId, ct).ConfigureAwait(false);
        return new ScimListResponse<ScimGroup>(groups.Count, startIndex, count, groups, [ScimSchemaUris.ListResponse]);
    }

    public async Task EnsureJitUserProvisionedAsync(TenantId tenantId, string realmId, ResolvedIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var existing = await _repository.GetUserAsync(tenantId, realmId, identity.UserKey, ct).ConfigureAwait(false);
        if (existing == null)
        {
            var now = _timeProvider.GetUtcNow();
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

    private Task RecordMembershipChangeAuditAsync(
        TenantId tenantId,
        string realmId,
        string groupId,
        string? groupSid,
        string memberKey,
        string memberType,
        string operation,
        DateTimeOffset occurredAt,
        CancellationToken ct) =>
        RecordRequiredScimAuditAsync(
            tenantId,
            realmId,
            AuditEventTypes.ScimGroupMembershipChanged,
            "IDENTITY_GROUP_MEMBERS",
            occurredAt,
            new
            {
                realm_id = realmId,
                group_id = groupId,
                group_sid = groupSid,
                member_key = memberKey,
                member_type = memberType,
                operation
            },
            ct);

    private Task RecordRequiredScimAuditAsync(
        TenantId tenantId,
        string realmId,
        string eventType,
        string targetTable,
        DateTimeOffset occurredAt,
        object details,
        CancellationToken ct)
    {
        if (_auditWriter is null)
        {
            return Task.CompletedTask;
        }

        return _auditWriter.RecordRequiredAsync(
            new AuditLogEntry
            {
                TenantId = tenantId,
                OccurredAt = occurredAt,
                EventType = eventType,
                ActorSid = new Sid($"scim-client:{realmId}"),
                TargetTable = targetTable,
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(details)
            },
            ct);
    }
}
