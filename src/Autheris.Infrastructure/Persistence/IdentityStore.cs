namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Microsoft.Data.SqlClient;
using Npgsql;

internal enum IdentityStoreDialect
{
    Sqlite,
    PostgreSql,
    SqlServer
}

internal sealed class IdentityStoreScope(DbConnection connection, Func<ValueTask> dispose) : IAsyncDisposable
{
    public DbConnection Connection { get; } = connection;
    public ValueTask DisposeAsync() => dispose();
}

internal sealed class IdentityStore(
    IdentityStoreDialect dialect,
    Func<bool, CancellationToken, Task<IdentityStoreScope>> open)
{
    private const string UserSelectColumns =
        "SELECT user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at, object_sid, immutable_id, upn, entra_object_id ";

    private const string GroupSelectColumns =
        "SELECT group_id, external_id, display_name, created_at, updated_at, object_sid ";

    private SqlDialect Sql => dialect switch
    {
        IdentityStoreDialect.Sqlite => SqlDialect.Sqlite,
        IdentityStoreDialect.PostgreSql => SqlDialect.PostgreSql,
        _ => SqlDialect.SqlServer
    };

    public Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
        QuerySingleUserAsync(
            UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey",
            tenantId, realmId, "@userKey", userKey, ct);

    public Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) =>
        QuerySingleUserAsync(
            UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND external_id = @externalId",
            tenantId, realmId, "@externalId", externalId, ct);

    public Task<ScimUser?> GetUserByUserNameAsync(TenantId tenantId, string realmId, string userName, CancellationToken ct = default) =>
        QuerySingleUserAsync(
            UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_name = @userName",
            tenantId, realmId, "@userName", userName, ct);

    public Task<ScimUser?> GetUserByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) =>
        QuerySingleUserAsync(
            UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND object_sid = @objectSid",
            tenantId, realmId, "@objectSid", objectSid, ct);

    public Task<ScimUser?> GetUserByImmutableIdAsync(TenantId tenantId, string realmId, string onPremisesImmutableId, CancellationToken ct = default) =>
        QuerySingleUserAsync(
            UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND immutable_id = @immutableId",
            tenantId, realmId, "@immutableId", onPremisesImmutableId, ct);

    public Task<ScimUser?> GetUserByEntraObjectIdAsync(TenantId tenantId, string realmId, string entraObjectId, CancellationToken ct = default) =>
        QuerySingleUserAsync(
            UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND entra_object_id = @entraObjectId",
            tenantId, realmId, "@entraObjectId", entraObjectId, ct);

    public Task<ScimUser?> GetUserByUpnAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) =>
        QuerySingleUserAsync(
            UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND upn = @upn",
            tenantId, realmId, "@upn", userPrincipalName.Trim().ToLowerInvariant(), ct);

    public async Task CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        await using var scope = await open(true, ct).ConfigureAwait(false);
        var userKey = user.Id ?? user.ExternalId ?? Guid.NewGuid().ToString("N");
        var normalized = NormalizeUserForWrite(user, userKey);
        await EnsureUserCorrelationUniqueAsync(scope.Connection, tenantId, realmId, userKey, normalized, ct).ConfigureAwait(false);
        try
        {
            await ExecuteUserUpsertAsync(scope.Connection, normalized, tenantId, realmId, userKey, ct).ConfigureAwait(false);
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            throw new ScimUniquenessConflictException(InferUserConflictAttribute(ex), "A SCIM user with a conflicting unique attribute already exists.", ex);
        }
    }

    public async Task UpdateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        await using var scope = await open(true, ct).ConfigureAwait(false);
        var userKey = user.Id ?? user.ExternalId ?? Guid.NewGuid().ToString("N");
        var normalized = NormalizeUserForWrite(user, userKey);
        await EnsureUserCorrelationUniqueAsync(scope.Connection, tenantId, realmId, userKey, normalized, ct).ConfigureAwait(false);
        try
        {
            await ExecuteUserUpsertAsync(scope.Connection, normalized, tenantId, realmId, userKey, ct).ConfigureAwait(false);
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            throw new ScimUniquenessConflictException(InferUserConflictAttribute(ex), "A SCIM user with a conflicting unique attribute already exists.", ex);
        }
    }

    public async Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        await using var scope = await open(true, ct).ConfigureAwait(false);
        var userKey = user.Id ?? user.ExternalId ?? Guid.NewGuid().ToString("N");
        var normalized = NormalizeUserForWrite(user, userKey);
        await ExecuteUserUpsertAsync(scope.Connection, normalized, tenantId, realmId, userKey, ct).ConfigureAwait(false);
    }

    public async Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        cmd.CommandText = "UPDATE IDENTITY_USERS SET active = @active, deactivated_at = @deactivatedAt, updated_at = @updatedAt WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", userKey);
        AddParam(cmd, "@active", DialectValueCodec.Bool(Sql, false));
        AddParam(cmd, "@deactivatedAt", DialectValueCodec.Ts(Sql, now));
        AddParam(cmd, "@updatedAt", DialectValueCodec.Ts(Sql, now));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var tx = await scope.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var members = scope.Connection.CreateCommand())
        {
            members.Transaction = tx;
            members.CommandText = "DELETE FROM IDENTITY_GROUP_MEMBERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
            AddParam(members, "@tenantId", tenantId.Value);
            AddParam(members, "@realmId", realmId);
            AddParam(members, "@userKey", userKey);
            await members.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = scope.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
            AddParam(cmd, "@tenantId", tenantId.Value);
            AddParam(cmd, "@realmId", realmId);
            AddParam(cmd, "@userKey", userKey);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        int offset = Math.Max(0, startIndex - 1);
        cmd.CommandText = dialect == IdentityStoreDialect.SqlServer
            ? UserSelectColumns + "FROM dbo.IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId ORDER BY user_key OFFSET @offset ROWS FETCH NEXT @count ROWS ONLY"
            : UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId ORDER BY user_key LIMIT @count OFFSET @offset";

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@count", count);
        AddParam(cmd, "@offset", offset);

        var list = new List<ScimUser>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadUser(reader));
        }
        return list;
    }

    public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) =>
        QuerySingleGroupAsync(
            GroupSelectColumns + "FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_id = @groupId",
            tenantId, realmId, "@groupId", groupId, ct);

    public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) =>
        QuerySingleGroupAsync(
            GroupSelectColumns + "FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId AND external_id = @externalId",
            tenantId, realmId, "@externalId", externalId, ct);

    public Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) =>
        QuerySingleGroupAsync(
            GroupSelectColumns + "FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId AND object_sid = @objectSid",
            tenantId, realmId, "@objectSid", objectSid, ct);

    public async Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        await using var scope = await open(true, ct).ConfigureAwait(false);
        var groupId = group.Id ?? group.ExternalId ?? Guid.NewGuid().ToString("N");
        var normalized = NormalizeGroupForWrite(group, groupId);
        await EnsureGroupCorrelationUniqueAsync(scope.Connection, tenantId, realmId, groupId, normalized, ct).ConfigureAwait(false);
        try
        {
            await ExecuteGroupUpsertAsync(scope.Connection, normalized, tenantId, realmId, groupId, ct).ConfigureAwait(false);
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            throw new ScimUniquenessConflictException(InferGroupConflictAttribute(ex), "A SCIM group with a conflicting unique attribute already exists.", ex);
        }
    }

    public async Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        await using var scope = await open(true, ct).ConfigureAwait(false);
        var groupId = group.Id ?? group.ExternalId ?? Guid.NewGuid().ToString("N");
        var normalized = NormalizeGroupForWrite(group, groupId);
        await EnsureGroupCorrelationUniqueAsync(scope.Connection, tenantId, realmId, groupId, normalized, ct).ConfigureAwait(false);
        try
        {
            await ExecuteGroupUpsertAsync(scope.Connection, normalized, tenantId, realmId, groupId, ct).ConfigureAwait(false);
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            throw new ScimUniquenessConflictException(InferGroupConflictAttribute(ex), "A SCIM group with a conflicting unique attribute already exists.", ex);
        }
    }

    public async Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        await using var scope = await open(true, ct).ConfigureAwait(false);
        var groupId = group.Id ?? group.ExternalId ?? Guid.NewGuid().ToString("N");
        var normalized = NormalizeGroupForWrite(group, groupId);
        await ExecuteGroupUpsertAsync(scope.Connection, normalized, tenantId, realmId, groupId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var tx = await scope.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var memberKeys = new List<string>();
        await using (var select = scope.Connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT user_key FROM IDENTITY_GROUP_MEMBERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_id = @groupId";
            AddParam(select, "@tenantId", tenantId.Value);
            AddParam(select, "@realmId", realmId);
            AddParam(select, "@groupId", groupId);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                memberKeys.Add(reader.GetString(0));
            }
        }

        await using (var members = scope.Connection.CreateCommand())
        {
            members.Transaction = tx;
            members.CommandText = "DELETE FROM IDENTITY_GROUP_MEMBERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_id = @groupId";
            AddParam(members, "@tenantId", tenantId.Value);
            AddParam(members, "@realmId", realmId);
            AddParam(members, "@groupId", groupId);
            await members.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var nesting = scope.Connection.CreateCommand())
        {
            nesting.Transaction = tx;
            nesting.CommandText = "DELETE FROM IDENTITY_GROUP_NESTING WHERE tenant_id = @tenantId AND realm_id = @realmId AND (parent_group_id = @groupId OR child_group_id = @groupId)";
            AddParam(nesting, "@tenantId", tenantId.Value);
            AddParam(nesting, "@realmId", realmId);
            AddParam(nesting, "@groupId", groupId);
            await nesting.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = scope.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_id = @groupId";
            AddParam(cmd, "@tenantId", tenantId.Value);
            AddParam(cmd, "@realmId", realmId);
            AddParam(cmd, "@groupId", groupId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return memberKeys;
    }

    public async Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        int offset = Math.Max(0, startIndex - 1);
        cmd.CommandText = dialect == IdentityStoreDialect.SqlServer
            ? GroupSelectColumns + "FROM dbo.IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId ORDER BY group_id OFFSET @offset ROWS FETCH NEXT @count ROWS ONLY"
            : GroupSelectColumns + "FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId ORDER BY group_id LIMIT @count OFFSET @offset";

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@count", count);
        AddParam(cmd, "@offset", offset);

        var list = new List<ScimGroup>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadGroup(reader));
        }
        return list;
    }

    public async Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        var now = DialectValueCodec.Ts(Sql, DateTimeOffset.UtcNow);
        if (dialect == IdentityStoreDialect.Sqlite || dialect == IdentityStoreDialect.PostgreSql)
        {
            cmd.CommandText = "INSERT INTO IDENTITY_GROUP_MEMBERS (tenant_id, realm_id, group_id, user_key, created_at) VALUES (@tenantId, @realmId, @groupId, @userKey, @createdAt) ON CONFLICT DO NOTHING";
        }
        else
        {
            cmd.CommandText = @"IF NOT EXISTS (SELECT 1 FROM dbo.IDENTITY_GROUP_MEMBERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_id = @groupId AND user_key = @userKey)
INSERT INTO dbo.IDENTITY_GROUP_MEMBERS (tenant_id, realm_id, group_id, user_key, created_at) VALUES (@tenantId, @realmId, @groupId, @userKey, @createdAt)";
        }

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupId", groupId);
        AddParam(cmd, "@userKey", userKey);
        AddParam(cmd, "@createdAt", now);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM IDENTITY_GROUP_MEMBERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_id = @groupId AND user_key = @userKey";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupId", groupId);
        AddParam(cmd, "@userKey", userKey);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = dialect == IdentityStoreDialect.SqlServer
            ? @"SELECT m.group_id FROM dbo.IDENTITY_GROUP_MEMBERS m
INNER JOIN dbo.IDENTITY_GROUPS g ON g.tenant_id = m.tenant_id AND g.realm_id = m.realm_id AND g.group_id = m.group_id
WHERE m.tenant_id = @tenantId AND m.realm_id = @realmId AND m.user_key = @userKey"
            : @"SELECT m.group_id FROM IDENTITY_GROUP_MEMBERS m
INNER JOIN IDENTITY_GROUPS g ON g.tenant_id = m.tenant_id AND g.realm_id = m.realm_id AND g.group_id = m.group_id
WHERE m.tenant_id = @tenantId AND m.realm_id = @realmId AND m.user_key = @userKey";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", userKey);

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public async Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT user_key FROM IDENTITY_GROUP_MEMBERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_id = @groupId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupId", groupId);

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public async Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        if (dialect == IdentityStoreDialect.Sqlite || dialect == IdentityStoreDialect.PostgreSql)
        {
            cmd.CommandText = "INSERT INTO IDENTITY_GROUP_NESTING (tenant_id, realm_id, parent_group_id, child_group_id) VALUES (@tenantId, @realmId, @parentGroupId, @childGroupId) ON CONFLICT DO NOTHING";
        }
        else
        {
            cmd.CommandText = @"IF NOT EXISTS (SELECT 1 FROM dbo.IDENTITY_GROUP_NESTING WHERE tenant_id = @tenantId AND realm_id = @realmId AND parent_group_id = @parentGroupId AND child_group_id = @childGroupId)
INSERT INTO dbo.IDENTITY_GROUP_NESTING (tenant_id, realm_id, parent_group_id, child_group_id) VALUES (@tenantId, @realmId, @parentGroupId, @childGroupId)";
        }

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@parentGroupId", parentGroupId);
        AddParam(cmd, "@childGroupId", childGroupId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM IDENTITY_GROUP_NESTING WHERE tenant_id = @tenantId AND realm_id = @realmId AND parent_group_id = @parentGroupId AND child_group_id = @childGroupId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@parentGroupId", parentGroupId);
        AddParam(cmd, "@childGroupId", childGroupId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT child_group_id FROM IDENTITY_GROUP_NESTING WHERE tenant_id = @tenantId AND realm_id = @realmId AND parent_group_id = @parentGroupId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@parentGroupId", parentGroupId);

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public async Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT parent_group_id, child_group_id FROM IDENTITY_GROUP_NESTING WHERE tenant_id = @tenantId AND realm_id = @realmId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);

        var list = new List<ScimGroupNestingEdge>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ScimGroupNestingEdge(reader.GetString(0), reader.GetString(1)));
        }
        return list;
    }

    public async Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = dialect == IdentityStoreDialect.SqlServer
            ? "SELECT last_contact_at, last_write_at, deprovision_epoch FROM dbo.SCIM_REALM_SYNC_STATE WHERE tenant_id = @tenantId AND realm_id = @realmId"
            : "SELECT last_contact_at, last_write_at, deprovision_epoch FROM SCIM_REALM_SYNC_STATE WHERE tenant_id = @tenantId AND realm_id = @realmId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ScimRealmSyncState(
            tenantId,
            realmId,
            DialectValueCodec.ReadTs(Sql, reader, 0),
            DialectValueCodec.ReadTs(Sql, reader, 1),
            reader.GetInt64(2));
    }

    public async Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt, DateTimeOffset? lastWriteAt, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        if (dialect == IdentityStoreDialect.Sqlite || dialect == IdentityStoreDialect.PostgreSql)
        {
            cmd.CommandText = @"INSERT INTO SCIM_REALM_SYNC_STATE (tenant_id, realm_id, last_contact_at, last_write_at, deprovision_epoch)
VALUES (@tenantId, @realmId, @lastContactAt, @lastWriteAt, 0)
ON CONFLICT (tenant_id, realm_id) DO UPDATE SET
  last_contact_at = EXCLUDED.last_contact_at,
  last_write_at = COALESCE(EXCLUDED.last_write_at, SCIM_REALM_SYNC_STATE.last_write_at)";
        }
        else
        {
            cmd.CommandText = @"MERGE INTO dbo.SCIM_REALM_SYNC_STATE AS target
USING (VALUES (@tenantId, @realmId, @lastContactAt, @lastWriteAt))
  AS source (tenant_id, realm_id, last_contact_at, last_write_at)
ON target.tenant_id = source.tenant_id AND target.realm_id = source.realm_id
WHEN MATCHED THEN UPDATE SET
  target.last_contact_at = source.last_contact_at,
  target.last_write_at = COALESCE(source.last_write_at, target.last_write_at)
WHEN NOT MATCHED THEN INSERT (tenant_id, realm_id, last_contact_at, last_write_at, deprovision_epoch)
  VALUES (source.tenant_id, source.realm_id, source.last_contact_at, source.last_write_at, 0);";
        }

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@lastContactAt", DialectValueCodec.Ts(Sql, lastContactAt));
        AddParam(cmd, "@lastWriteAt", DialectValueCodec.Ts(Sql, lastWriteAt));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        if (dialect == IdentityStoreDialect.Sqlite || dialect == IdentityStoreDialect.PostgreSql)
        {
            cmd.CommandText = @"INSERT INTO SCIM_REALM_SYNC_STATE (tenant_id, realm_id, deprovision_epoch)
VALUES (@tenantId, @realmId, 1)
ON CONFLICT (tenant_id, realm_id) DO UPDATE SET
  deprovision_epoch = SCIM_REALM_SYNC_STATE.deprovision_epoch + 1
RETURNING deprovision_epoch";
        }
        else
        {
            cmd.CommandText = @"MERGE INTO dbo.SCIM_REALM_SYNC_STATE AS target
USING (VALUES (@tenantId, @realmId)) AS source (tenant_id, realm_id)
ON target.tenant_id = source.tenant_id AND target.realm_id = source.realm_id
WHEN MATCHED THEN UPDATE SET target.deprovision_epoch = target.deprovision_epoch + 1
WHEN NOT MATCHED THEN INSERT (tenant_id, realm_id, deprovision_epoch) VALUES (source.tenant_id, source.realm_id, 1)
OUTPUT INSERTED.deprovision_epoch;";
        }

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        if (dialect == IdentityStoreDialect.Sqlite || dialect == IdentityStoreDialect.PostgreSql)
        {
            cmd.CommandText = @"INSERT INTO IDENTITY_LOCKS (tenant_id, realm_id, user_key, reason, source, locked_at)
VALUES (@tenantId, @realmId, @userKey, @reason, @source, @lockedAt)
ON CONFLICT (tenant_id, realm_id, user_key) DO UPDATE SET
  reason = EXCLUDED.reason,
  source = EXCLUDED.source,
  locked_at = EXCLUDED.locked_at";
        }
        else
        {
            cmd.CommandText = @"MERGE INTO dbo.IDENTITY_LOCKS AS target
USING (VALUES (@tenantId, @realmId, @userKey, @reason, @source, @lockedAt))
  AS source (tenant_id, realm_id, user_key, reason, source, locked_at)
ON target.tenant_id = source.tenant_id AND target.realm_id = source.realm_id AND target.user_key = source.user_key
WHEN MATCHED THEN UPDATE SET
  target.reason = source.reason,
  target.source = source.source,
  target.locked_at = source.locked_at
WHEN NOT MATCHED THEN INSERT (tenant_id, realm_id, user_key, reason, source, locked_at)
  VALUES (source.tenant_id, source.realm_id, source.user_key, source.reason, source.source, source.locked_at);";
        }

        AddParam(cmd, "@tenantId", lockEntry.TenantId.Value);
        AddParam(cmd, "@realmId", lockEntry.RealmId);
        AddParam(cmd, "@userKey", lockEntry.UserKey);
        AddParam(cmd, "@reason", lockEntry.Reason);
        AddParam(cmd, "@source", lockEntry.Source);
        AddParam(cmd, "@lockedAt", DialectValueCodec.Ts(Sql, lockEntry.LockedAt));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM IDENTITY_LOCKS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", userKey);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result != null;
    }

    public async Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, realm_id, user_key, reason, source, locked_at FROM IDENTITY_LOCKS WHERE tenant_id = @tenantId AND realm_id = @realmId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);

        var list = new List<ScimLock>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ScimLock(
                new TenantId(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DialectValueCodec.ReadRequiredTs(Sql, reader, 5)
            ));
        }
        return list;
    }

    public async Task<ScimLockWindow> CountLocksSinceAsync(
        TenantId tenantId,
        string realmId,
        IReadOnlyList<string> sources,
        DateTimeOffset sinceUtc,
        int cap,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || cap < 1)
        {
            return new ScimLockWindow(0, null);
        }

        var names = new string[sources.Count];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = "@source" + i;
        }

        var where = "tenant_id = @tenantId AND realm_id = @realmId AND source IN (" + string.Join(", ", names) + ") AND locked_at >= @since";
        var sqlServer = dialect == IdentityStoreDialect.SqlServer;
        var table = sqlServer ? "dbo.IDENTITY_LOCKS" : "IDENTITY_LOCKS";

        var countSql = sqlServer
            ? "SELECT COUNT(*) FROM (SELECT TOP (@cap) 1 AS one FROM " + table + " WHERE " + where + ") AS capped"
            : "SELECT COUNT(*) FROM (SELECT 1 AS one FROM " + table + " WHERE " + where + " LIMIT @cap) AS capped";
        var oldestSql = sqlServer
            ? "SELECT TOP (1) locked_at FROM " + table + " WHERE " + where + " ORDER BY locked_at ASC"
            : "SELECT locked_at FROM " + table + " WHERE " + where + " ORDER BY locked_at ASC LIMIT 1";

        await using var scope = await open(false, ct).ConfigureAwait(false);

        void Bind(DbCommand cmd, bool withCap)
        {
            AddParam(cmd, "@tenantId", tenantId.Value);
            AddParam(cmd, "@realmId", realmId);
            for (var i = 0; i < names.Length; i++)
            {
                AddParam(cmd, names[i], sources[i]);
            }

            AddParam(cmd, "@since", DialectValueCodec.Ts(Sql, sinceUtc));
            if (withCap)
            {
                AddParam(cmd, "@cap", cap);
            }
        }

        int count;
        await using (var countCmd = scope.Connection.CreateCommand())
        {
            countCmd.CommandText = countSql;
            Bind(countCmd, true);
            var scalar = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            count = Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (count == 0)
        {
            return new ScimLockWindow(0, null);
        }

        await using var oldestCmd = scope.Connection.CreateCommand();
        oldestCmd.CommandText = oldestSql;
        Bind(oldestCmd, false);
        await using var reader = await oldestCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        DateTimeOffset? oldest = await reader.ReadAsync(ct).ConfigureAwait(false)
            ? DialectValueCodec.ReadRequiredTs(Sql, reader, 0)
            : null;
        return new ScimLockWindow(count, oldest);
    }

    public async Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM IDENTITY_LOCKS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", userKey);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<ScimUser?> QuerySingleUserAsync(string sql, TenantId tenantId, string realmId, string paramName, string paramValue, CancellationToken ct)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, paramName, paramValue);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadUser(reader) : null;
    }

    private async Task<ScimGroup?> QuerySingleGroupAsync(string sql, TenantId tenantId, string realmId, string paramName, string paramValue, CancellationToken ct)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, paramName, paramValue);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadGroup(reader) : null;
    }

    private static ScimUser NormalizeUserForWrite(ScimUser user, string userKey) =>
        user with
        {
            Id = userKey,
            ExternalId = user.ExternalId ?? userKey,
            ObjectSid = user.ResolvedObjectSid,
            OnPremisesImmutableId = user.ResolvedOnPremisesImmutableId,
            UserPrincipalName = user.ResolvedUserPrincipalName?.Trim().ToLowerInvariant(),
            EntraObjectId = user.ResolvedEntraObjectId?.Trim().ToLowerInvariant()
        };

    private static ScimGroup NormalizeGroupForWrite(ScimGroup group, string groupId) =>
        group with
        {
            Id = groupId,
            ExternalId = group.ExternalId ?? groupId,
            ObjectSid = group.ResolvedObjectSid
        };

    private async Task ExecuteUserUpsertAsync(DbConnection conn, ScimUser user, TenantId tenantId, string realmId, string userKey, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        var createdAt = user.CreatedAt ?? now;
        var updatedAt = user.UpdatedAt ?? now;

        if (dialect == IdentityStoreDialect.Sqlite || dialect == IdentityStoreDialect.PostgreSql)
        {
            cmd.CommandText = @"INSERT INTO IDENTITY_USERS (tenant_id, realm_id, user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at, object_sid, immutable_id, upn, entra_object_id)
VALUES (@tenantId, @realmId, @userKey, @externalId, @userName, @email, @displayName, @active, @deactivatedAt, @createdAt, @updatedAt, @objectSid, @immutableId, @upn, @entraObjectId)
ON CONFLICT (tenant_id, realm_id, user_key) DO UPDATE SET
  external_id = EXCLUDED.external_id,
  user_name = EXCLUDED.user_name,
  email = EXCLUDED.email,
  display_name = EXCLUDED.display_name,
  active = EXCLUDED.active,
  deactivated_at = EXCLUDED.deactivated_at,
  updated_at = EXCLUDED.updated_at,
  object_sid = EXCLUDED.object_sid,
  immutable_id = EXCLUDED.immutable_id,
  upn = EXCLUDED.upn,
  entra_object_id = EXCLUDED.entra_object_id";
        }
        else
        {
            cmd.CommandText = @"MERGE INTO dbo.IDENTITY_USERS AS target
USING (VALUES (@tenantId, @realmId, @userKey, @externalId, @userName, @email, @displayName, @active, @deactivatedAt, @createdAt, @updatedAt, @objectSid, @immutableId, @upn, @entraObjectId))
  AS source (tenant_id, realm_id, user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at, object_sid, immutable_id, upn, entra_object_id)
ON target.tenant_id = source.tenant_id AND target.realm_id = source.realm_id AND target.user_key = source.user_key
WHEN MATCHED THEN UPDATE SET
  target.external_id = source.external_id,
  target.user_name = source.user_name,
  target.email = source.email,
  target.display_name = source.display_name,
  target.active = source.active,
  target.deactivated_at = source.deactivated_at,
  target.updated_at = source.updated_at,
  target.object_sid = source.object_sid,
  target.immutable_id = source.immutable_id,
  target.upn = source.upn,
  target.entra_object_id = source.entra_object_id
WHEN NOT MATCHED THEN INSERT (tenant_id, realm_id, user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at, object_sid, immutable_id, upn, entra_object_id)
  VALUES (source.tenant_id, source.realm_id, source.user_key, source.external_id, source.user_name, source.email, source.display_name, source.active, source.deactivated_at, source.created_at, source.updated_at, source.object_sid, source.immutable_id, source.upn, source.entra_object_id);";
        }

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", userKey);
        AddParam(cmd, "@externalId", user.ExternalId ?? userKey);
        AddParam(cmd, "@userName", user.UserName);
        AddParam(cmd, "@email", (object?)user.Email ?? DBNull.Value);
        AddParam(cmd, "@displayName", (object?)user.DisplayName ?? DBNull.Value);
        AddParam(cmd, "@active", DialectValueCodec.Bool(Sql, user.Active));
        AddParam(cmd, "@deactivatedAt", DialectValueCodec.Ts(Sql, user.DeactivatedAt));
        AddParam(cmd, "@createdAt", DialectValueCodec.Ts(Sql, createdAt));
        AddParam(cmd, "@updatedAt", DialectValueCodec.Ts(Sql, updatedAt));
        AddParam(cmd, "@objectSid", (object?)user.ObjectSid ?? DBNull.Value);
        AddParam(cmd, "@immutableId", (object?)user.OnPremisesImmutableId ?? DBNull.Value);
        AddParam(cmd, "@upn", (object?)user.UserPrincipalName ?? DBNull.Value);
        AddParam(cmd, "@entraObjectId", (object?)user.EntraObjectId ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task ExecuteGroupUpsertAsync(DbConnection conn, ScimGroup group, TenantId tenantId, string realmId, string groupId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var createdAt = group.CreatedAt ?? DateTimeOffset.UtcNow;
        var updatedAt = group.UpdatedAt ?? createdAt;

        if (dialect == IdentityStoreDialect.Sqlite || dialect == IdentityStoreDialect.PostgreSql)
        {
            cmd.CommandText = @"INSERT INTO IDENTITY_GROUPS (tenant_id, realm_id, group_id, external_id, display_name, created_at, updated_at, object_sid)
VALUES (@tenantId, @realmId, @groupId, @externalId, @displayName, @createdAt, @updatedAt, @objectSid)
ON CONFLICT (tenant_id, realm_id, group_id) DO UPDATE SET
  external_id = EXCLUDED.external_id,
  display_name = EXCLUDED.display_name,
  updated_at = EXCLUDED.updated_at,
  object_sid = EXCLUDED.object_sid";
        }
        else
        {
            cmd.CommandText = @"MERGE INTO dbo.IDENTITY_GROUPS AS target
USING (VALUES (@tenantId, @realmId, @groupId, @externalId, @displayName, @createdAt, @updatedAt, @objectSid))
  AS source (tenant_id, realm_id, group_id, external_id, display_name, created_at, updated_at, object_sid)
ON target.tenant_id = source.tenant_id AND target.realm_id = source.realm_id AND target.group_id = source.group_id
WHEN MATCHED THEN UPDATE SET
  target.external_id = source.external_id,
  target.display_name = source.display_name,
  target.updated_at = source.updated_at,
  target.object_sid = source.object_sid
WHEN NOT MATCHED THEN INSERT (tenant_id, realm_id, group_id, external_id, display_name, created_at, updated_at, object_sid)
  VALUES (source.tenant_id, source.realm_id, source.group_id, source.external_id, source.display_name, source.created_at, source.updated_at, source.object_sid);";
        }

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupId", groupId);
        AddParam(cmd, "@externalId", group.ExternalId ?? groupId);
        AddParam(cmd, "@displayName", group.DisplayName);
        AddParam(cmd, "@createdAt", DialectValueCodec.Ts(Sql, createdAt));
        AddParam(cmd, "@updatedAt", DialectValueCodec.Ts(Sql, updatedAt));
        AddParam(cmd, "@objectSid", (object?)group.ObjectSid ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task EnsureUserCorrelationUniqueAsync(DbConnection conn, TenantId tenantId, string realmId, string userKey, ScimUser user, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(user.ObjectSid))
        {
            await CheckUserUniqueColumnAsync(conn, "SELECT user_key FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND object_sid = @val AND user_key <> @userKey", tenantId, realmId, userKey, user.ObjectSid, "objectSid", ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(user.OnPremisesImmutableId))
        {
            await CheckUserUniqueColumnAsync(conn, "SELECT user_key FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND immutable_id = @val AND user_key <> @userKey", tenantId, realmId, userKey, user.OnPremisesImmutableId, "onPremisesImmutableId", ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(user.EntraObjectId))
        {
            await CheckUserUniqueColumnAsync(conn, "SELECT user_key FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND entra_object_id = @val AND user_key <> @userKey", tenantId, realmId, userKey, user.EntraObjectId, "entraObjectId", ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(user.UserPrincipalName))
        {
            await CheckUserUniqueColumnAsync(conn, "SELECT user_key FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND upn = @val AND user_key <> @userKey", tenantId, realmId, userKey, user.UserPrincipalName, "userPrincipalName", ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(user.ExternalId))
        {
            await CheckUserUniqueColumnAsync(conn, "SELECT user_key FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND external_id = @val AND user_key <> @userKey", tenantId, realmId, userKey, user.ExternalId, "externalId", ct).ConfigureAwait(false);
        }
    }

    private async Task EnsureGroupCorrelationUniqueAsync(DbConnection conn, TenantId tenantId, string realmId, string groupId, ScimGroup group, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(group.ObjectSid))
        {
            await CheckGroupUniqueColumnAsync(conn, "SELECT group_id FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId AND object_sid = @val AND group_id <> @groupId", tenantId, realmId, groupId, group.ObjectSid, "objectSid", ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(group.ExternalId))
        {
            await CheckGroupUniqueColumnAsync(conn, "SELECT group_id FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId AND external_id = @val AND group_id <> @groupId", tenantId, realmId, groupId, group.ExternalId, "externalId", ct).ConfigureAwait(false);
        }
    }

    private async Task CheckUserUniqueColumnAsync(DbConnection conn, string sql, TenantId tenantId, string realmId, string userKey, string value, string attributeName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", userKey);
        AddParam(cmd, "@val", value);
        var conflict = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (conflict != null && conflict != DBNull.Value)
        {
            throw new ScimUniquenessConflictException(attributeName, $"A SCIM user with the same {attributeName} already exists.");
        }
    }

    private async Task CheckGroupUniqueColumnAsync(DbConnection conn, string sql, TenantId tenantId, string realmId, string groupId, string value, string attributeName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupId", groupId);
        AddParam(cmd, "@val", value);
        var conflict = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (conflict != null && conflict != DBNull.Value)
        {
            throw new ScimUniquenessConflictException(attributeName, $"A SCIM group with the same {attributeName} already exists.");
        }
    }

    private static bool IsUniqueViolation(DbException ex) =>
        ex.SqlState == "23505"
        || ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
        || ex is SqlException { Number: 2601 or 2627 };

    private static string InferUserConflictAttribute(DbException ex)
    {
        var msg = ex.Message;
        if (msg.Contains("object_sid", StringComparison.OrdinalIgnoreCase)) return "objectSid";
        if (msg.Contains("immutable_id", StringComparison.OrdinalIgnoreCase)) return "onPremisesImmutableId";
        if (msg.Contains("entra_oid", StringComparison.OrdinalIgnoreCase) || msg.Contains("entra_object_id", StringComparison.OrdinalIgnoreCase)) return "entraObjectId";
        if (msg.Contains("upn", StringComparison.OrdinalIgnoreCase)) return "userPrincipalName";
        if (msg.Contains("external_id", StringComparison.OrdinalIgnoreCase) || msg.Contains("_ext", StringComparison.OrdinalIgnoreCase)) return "externalId";
        return "externalId";
    }

    private static string InferGroupConflictAttribute(DbException ex) =>
        ex.Message.Contains("object_sid", StringComparison.OrdinalIgnoreCase) ? "objectSid" : "externalId";

    private ScimUser ReadUser(DbDataReader reader)
    {
        var userKey = reader.GetString(0);
        var externalId = reader.GetString(1);
        var userName = reader.GetString(2);
        var email = reader.IsDBNull(3) ? null : reader.GetString(3);
        var displayName = reader.IsDBNull(4) ? null : reader.GetString(4);
        var active = DialectValueCodec.ReadBool(Sql, reader, 5);
        var deactivatedAt = DialectValueCodec.ReadTs(Sql, reader, 6);
        var createdAt = DialectValueCodec.ReadRequiredTs(Sql, reader, 7);
        var updatedAt = DialectValueCodec.ReadRequiredTs(Sql, reader, 8);
        var objectSid = reader.IsDBNull(9) ? null : reader.GetString(9);
        var immutableId = reader.IsDBNull(10) ? null : reader.GetString(10);
        var upn = reader.IsDBNull(11) ? null : reader.GetString(11);
        var entraOid = reader.IsDBNull(12) ? null : reader.GetString(12);

        return new ScimUser(userKey, externalId, userName, displayName, email, active, deactivatedAt, createdAt, updatedAt, objectSid, immutableId, upn, entraOid);
    }

    private ScimGroup ReadGroup(DbDataReader reader)
    {
        var groupId = reader.GetString(0);
        var externalId = reader.GetString(1);
        var displayName = reader.GetString(2);
        var createdAt = DialectValueCodec.ReadRequiredTs(Sql, reader, 3);
        var updatedAt = DialectValueCodec.ReadRequiredTs(Sql, reader, 4);
        var objectSid = reader.IsDBNull(5) ? null : reader.GetString(5);

        return new ScimGroup(groupId, externalId, displayName, null, createdAt, updatedAt, objectSid);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        SqlCoreBinding.GuardTenantParameter(name, value);
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
