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
    PostgreSql,
    SqlServer
}

internal sealed class IdentityStoreScope(DbConnection connection, Func<ValueTask> dispose) : IAsyncDisposable
{
    public DbConnection Connection { get; } = connection;
    public ValueTask DisposeAsync() => dispose();
}

internal sealed partial class IdentityStore(
    IdentityStoreDialect dialect,
    Func<bool, CancellationToken, Task<IdentityStoreScope>> open)
{
    private const string UserSelectColumns =
        "SELECT user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at, object_sid, immutable_id, upn, entra_object_id ";

    private const string GroupSelectColumns =
        "SELECT group_id, external_id, display_name, created_at, updated_at, object_sid ";

    private SqlDialect Sql => dialect switch
    {
        IdentityStoreDialect.PostgreSql => SqlDialect.PostgreSql,
        _ => SqlDialect.SqlServer
    };

    public Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
        QuerySingleUserAsync(
            UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey",
            tenantId, realmId, "@userKey", userKey, ct);

    public async Task<bool> UserKeyExistsInOtherRealmAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id <> @realmId AND user_key = @userKey";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", userKey);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) != null;
    }

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

    public Task<IReadOnlyList<ScimUser>> GetUsersByEntraObjectIdOrExternalIdAsync(TenantId tenantId, string realmId, string identifier, CancellationToken ct = default) =>
        QueryUsersAsync(
            UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND (entra_object_id = @lookup OR LOWER(external_id) = @lookup)",
            tenantId, realmId, "@lookup", identifier.Trim().ToLowerInvariant(), ct);

    public Task<IReadOnlyList<ScimUser>> GetUsersByUpnOrUserNameAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) =>
        QueryUsersAsync(
            UserSelectColumns + "FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND (upn = @lookup OR LOWER(user_name) = @lookup)",
            tenantId, realmId, "@lookup", userPrincipalName.Trim().ToLowerInvariant(), ct);

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
            if (!normalized.Active)
            {
                await using var tx = await scope.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                await ExecuteUserUpsertAsync(scope.Connection, normalized, tenantId, realmId, userKey, ct, tx).ConfigureAwait(false);
                await ExecuteDeleteUserMembershipsAsync(scope.Connection, tx, tenantId, realmId, userKey, ct).ConfigureAwait(false);
                await ExecuteIncrementEpochAsync(scope.Connection, tx, tenantId, realmId, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await ExecuteUserUpsertAsync(scope.Connection, normalized, tenantId, realmId, userKey, ct).ConfigureAwait(false);
            }
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
        await using var tx = await scope.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await using (var cmd = scope.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE IDENTITY_USERS SET active = @active, deactivated_at = @deactivatedAt, updated_at = @updatedAt WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
            AddParam(cmd, "@tenantId", tenantId.Value);
            AddParam(cmd, "@realmId", realmId);
            AddParam(cmd, "@userKey", userKey);
            AddParam(cmd, "@active", DialectValueCodec.Bool(Sql, false));
            AddParam(cmd, "@deactivatedAt", DialectValueCodec.Ts(Sql, now));
            AddParam(cmd, "@updatedAt", DialectValueCodec.Ts(Sql, now));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await ExecuteDeleteUserMembershipsAsync(scope.Connection, tx, tenantId, realmId, userKey, ct).ConfigureAwait(false);
        await ExecuteIncrementEpochAsync(scope.Connection, tx, tenantId, realmId, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var tx = await scope.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await ExecuteDeleteUserMembershipsAsync(scope.Connection, tx, tenantId, realmId, userKey, ct).ConfigureAwait(false);

        await using (var cmd = scope.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
            AddParam(cmd, "@tenantId", tenantId.Value);
            AddParam(cmd, "@realmId", realmId);
            AddParam(cmd, "@userKey", userKey);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await ExecuteIncrementEpochAsync(scope.Connection, tx, tenantId, realmId, ct).ConfigureAwait(false);
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

    public async Task<string?> GetGroupDisplayNameBySidAsync(TenantId tenant, Sid groupSid, CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenant, "IDENTITY_GROUPS");
        if (!SidFormat.IsGroupSid(groupSid.Value))
        {
            return null;
        }

        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = dialect == IdentityStoreDialect.SqlServer
            ? "SELECT TOP (1) display_name FROM dbo.IDENTITY_GROUPS WHERE tenant_id = @tenantId AND object_sid = @objectSid ORDER BY realm_id ASC"
            : "SELECT display_name FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND object_sid = @objectSid ORDER BY realm_id ASC LIMIT 1";
        AddParam(cmd, "@tenantId", tenant.Value);
        AddParam(cmd, "@objectSid", groupSid.Value);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is string name && !string.IsNullOrWhiteSpace(name) ? name : null;
    }

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
        if (dialect == IdentityStoreDialect.PostgreSql)
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

    public async Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(
        TenantId tenantId,
        string realmId,
        string userKey,
        int maxDepth = 32,
        CancellationToken ct = default)
    {
        if (maxDepth < 0)
        {
            maxDepth = 0;
        }

        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = dialect switch
        {
            IdentityStoreDialect.SqlServer => @"WITH group_tree AS (
  SELECT m.group_id, 1 AS depth, CAST('/' + m.group_id + '/' AS NVARCHAR(MAX)) AS path
  FROM dbo.IDENTITY_GROUP_MEMBERS m
  WHERE m.tenant_id = @tenantId AND m.realm_id = @realmId AND m.user_key = @userKey
  UNION ALL
  SELECT n.parent_group_id, gt.depth + 1, gt.path + CAST(n.parent_group_id AS NVARCHAR(MAX)) + '/'
  FROM dbo.IDENTITY_GROUP_NESTING n
  INNER JOIN group_tree gt ON n.child_group_id = gt.group_id
  WHERE n.tenant_id = @tenantId AND n.realm_id = @realmId
    AND gt.depth <= @maxDepth
    AND CHARINDEX('/' + CAST(n.parent_group_id AS NVARCHAR(MAX)) + '/', gt.path) = 0
)
SELECT g.object_sid, CASE WHEN gt.depth > @maxDepth THEN 1 ELSE 0 END AS over_depth, g.group_id
FROM group_tree gt
LEFT JOIN dbo.IDENTITY_GROUPS g ON g.tenant_id = @tenantId AND g.realm_id = @realmId AND g.group_id = gt.group_id
OPTION (MAXRECURSION 100);",
            IdentityStoreDialect.PostgreSql => @"WITH RECURSIVE group_tree(group_id, depth, path) AS (
  SELECT m.group_id, 1, '/' || m.group_id || '/'
  FROM IDENTITY_GROUP_MEMBERS m
  WHERE m.tenant_id = @tenantId AND m.realm_id = @realmId AND m.user_key = @userKey
  UNION ALL
  SELECT n.parent_group_id, gt.depth + 1, gt.path || n.parent_group_id || '/'
  FROM IDENTITY_GROUP_NESTING n
  INNER JOIN group_tree gt ON n.child_group_id = gt.group_id
  WHERE n.tenant_id = @tenantId AND n.realm_id = @realmId
    AND gt.depth <= @maxDepth
    AND POSITION('/' || n.parent_group_id || '/' IN gt.path) = 0
)
SELECT g.object_sid, CASE WHEN gt.depth > @maxDepth THEN 1 ELSE 0 END AS over_depth, g.group_id
FROM group_tree gt
LEFT JOIN IDENTITY_GROUPS g ON g.tenant_id = @tenantId AND g.realm_id = @realmId AND g.group_id = gt.group_id",
            _ => @"WITH RECURSIVE group_tree(group_id, depth, path) AS (
  SELECT m.group_id, 1, '/' || m.group_id || '/'
  FROM IDENTITY_GROUP_MEMBERS m
  WHERE m.tenant_id = @tenantId AND m.realm_id = @realmId AND m.user_key = @userKey
  UNION ALL
  SELECT n.parent_group_id, gt.depth + 1, gt.path || n.parent_group_id || '/'
  FROM IDENTITY_GROUP_NESTING n
  INNER JOIN group_tree gt ON n.child_group_id = gt.group_id
  WHERE n.tenant_id = @tenantId AND n.realm_id = @realmId
    AND gt.depth <= @maxDepth
    AND INSTR(gt.path, '/' || n.parent_group_id || '/') = 0
)
SELECT g.object_sid, CASE WHEN gt.depth > @maxDepth THEN 1 ELSE 0 END AS over_depth, g.group_id
FROM group_tree gt
LEFT JOIN IDENTITY_GROUPS g ON g.tenant_id = @tenantId AND g.realm_id = @realmId AND g.group_id = gt.group_id"
        };

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", userKey);
        AddParam(cmd, "@maxDepth", maxDepth);

        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool maxDepthExceeded = false;
        bool hasNonCanonicalSid = false;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                maxDepthExceeded = true;
            }

            var sid = reader.IsDBNull(0) ? null : reader.GetString(0);
            var gid = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(gid) || string.IsNullOrWhiteSpace(sid) || !SidFormat.IsGroupSid(sid))
            {
                hasNonCanonicalSid = true;
            }
            else
            {
                sids.Add(sid);
                groupIds.Add(gid);
            }
        }

        return new EffectiveScimGroupsResult(sids, maxDepthExceeded, hasNonCanonicalSid, groupIds);
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
        if (dialect == IdentityStoreDialect.PostgreSql)
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
        if (dialect == IdentityStoreDialect.PostgreSql)
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
        return await ExecuteIncrementEpochAsync(scope.Connection, null, tenantId, realmId, ct).ConfigureAwait(false);
    }

    private async Task ExecuteDeleteUserMembershipsAsync(DbConnection conn, DbTransaction? tx, TenantId tenantId, string realmId, string userKey, CancellationToken ct)
    {
        await using var members = conn.CreateCommand();
        members.Transaction = tx;
        members.CommandText = "DELETE FROM IDENTITY_GROUP_MEMBERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
        AddParam(members, "@tenantId", tenantId.Value);
        AddParam(members, "@realmId", realmId);
        AddParam(members, "@userKey", userKey);
        await members.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<long> ExecuteIncrementEpochAsync(DbConnection conn, DbTransaction? tx, TenantId tenantId, string realmId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        if (dialect == IdentityStoreDialect.PostgreSql)
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

    private async Task ExecuteUserUpsertAsync(DbConnection conn, ScimUser user, TenantId tenantId, string realmId, string userKey, CancellationToken ct, DbTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var now = DateTimeOffset.UtcNow;
        var createdAt = user.CreatedAt ?? now;
        var updatedAt = user.UpdatedAt ?? now;

        if (dialect == IdentityStoreDialect.PostgreSql)
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

        if (dialect == IdentityStoreDialect.PostgreSql)
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

}
