namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;

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
    private SqlDialect Sql => dialect switch
    {
        IdentityStoreDialect.Sqlite => SqlDialect.Sqlite,
        IdentityStoreDialect.PostgreSql => SqlDialect.PostgreSql,
        _ => SqlDialect.SqlServer
    };

    public async Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", userKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadUser(reader);
        }
        return null;
    }

    public async Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND external_id = @externalId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@externalId", externalId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadUser(reader);
        }
        return null;
    }

    public async Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        var createdAt = user.CreatedAt ?? now;
        var updatedAt = user.UpdatedAt ?? now;

        if (dialect == IdentityStoreDialect.Sqlite)
        {
            cmd.CommandText = @"INSERT INTO IDENTITY_USERS (tenant_id, realm_id, user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at)
VALUES (@tenantId, @realmId, @userKey, @externalId, @userName, @email, @displayName, @active, @deactivatedAt, @createdAt, @updatedAt)
ON CONFLICT (tenant_id, realm_id, user_key) DO UPDATE SET
  external_id = excluded.external_id,
  user_name = excluded.user_name,
  email = excluded.email,
  display_name = excluded.display_name,
  active = excluded.active,
  deactivated_at = excluded.deactivated_at,
  updated_at = excluded.updated_at";
        }
        else if (dialect == IdentityStoreDialect.PostgreSql)
        {
            cmd.CommandText = @"INSERT INTO IDENTITY_USERS (tenant_id, realm_id, user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at)
VALUES (@tenantId, @realmId, @userKey, @externalId, @userName, @email, @displayName, @active, @deactivatedAt, @createdAt, @updatedAt)
ON CONFLICT (tenant_id, realm_id, user_key) DO UPDATE SET
  external_id = EXCLUDED.external_id,
  user_name = EXCLUDED.user_name,
  email = EXCLUDED.email,
  display_name = EXCLUDED.display_name,
  active = EXCLUDED.active,
  deactivated_at = EXCLUDED.deactivated_at,
  updated_at = EXCLUDED.updated_at";
        }
        else // SqlServer
        {
            cmd.CommandText = @"MERGE INTO dbo.IDENTITY_USERS AS target
USING (VALUES (@tenantId, @realmId, @userKey, @externalId, @userName, @email, @displayName, @active, @deactivatedAt, @createdAt, @updatedAt))
  AS source (tenant_id, realm_id, user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at)
ON target.tenant_id = source.tenant_id AND target.realm_id = source.realm_id AND target.user_key = source.user_key
WHEN MATCHED THEN UPDATE SET
  target.external_id = source.external_id,
  target.user_name = source.user_name,
  target.email = source.email,
  target.display_name = source.display_name,
  target.active = source.active,
  target.deactivated_at = source.deactivated_at,
  target.updated_at = source.updated_at
WHEN NOT MATCHED THEN INSERT (tenant_id, realm_id, user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at)
  VALUES (source.tenant_id, source.realm_id, source.user_key, source.external_id, source.user_name, source.email, source.display_name, source.active, source.deactivated_at, source.created_at, source.updated_at);";
        }

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", user.Id ?? user.ExternalId ?? Guid.NewGuid().ToString("N"));
        AddParam(cmd, "@externalId", user.ExternalId ?? user.Id ?? "");
        AddParam(cmd, "@userName", user.UserName);
        AddParam(cmd, "@email", (object?)user.Email ?? DBNull.Value);
        AddParam(cmd, "@displayName", (object?)user.DisplayName ?? DBNull.Value);
        AddParam(cmd, "@active", DialectValueCodec.Bool(Sql, user.Active));
        AddParam(cmd, "@deactivatedAt", DialectValueCodec.Ts(Sql, user.DeactivatedAt));
        AddParam(cmd, "@createdAt", DialectValueCodec.Ts(Sql, createdAt));
        AddParam(cmd, "@updatedAt", DialectValueCodec.Ts(Sql, updatedAt));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

        // PERS-07: the memberships go with the user in one transaction (a locked, deleted user keeps its IDENTITY_LOCKS row).
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
        cmd.CommandText = "SELECT user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId ORDER BY user_key LIMIT @count OFFSET @offset";
        if (dialect == IdentityStoreDialect.SqlServer)
        {
            cmd.CommandText = "SELECT user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at FROM dbo.IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId ORDER BY user_key OFFSET @offset ROWS FETCH NEXT @count ROWS ONLY";
        }

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

    public async Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT group_id, external_id, display_name, created_at, updated_at FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_id = @groupId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupId", groupId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadGroup(reader);
        }
        return null;
    }

    public async Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT group_id, external_id, display_name, created_at, updated_at FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId AND external_id = @externalId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@externalId", externalId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadGroup(reader);
        }
        return null;
    }

    public async Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        var createdAt = group.CreatedAt ?? now;
        var updatedAt = group.UpdatedAt ?? now;

        if (dialect == IdentityStoreDialect.Sqlite)
        {
            cmd.CommandText = @"INSERT INTO IDENTITY_GROUPS (tenant_id, realm_id, group_id, external_id, display_name, created_at, updated_at)
VALUES (@tenantId, @realmId, @groupId, @externalId, @displayName, @createdAt, @updatedAt)
ON CONFLICT (tenant_id, realm_id, group_id) DO UPDATE SET
  external_id = excluded.external_id,
  display_name = excluded.display_name,
  updated_at = excluded.updated_at";
        }
        else if (dialect == IdentityStoreDialect.PostgreSql)
        {
            cmd.CommandText = @"INSERT INTO IDENTITY_GROUPS (tenant_id, realm_id, group_id, external_id, display_name, created_at, updated_at)
VALUES (@tenantId, @realmId, @groupId, @externalId, @displayName, @createdAt, @updatedAt)
ON CONFLICT (tenant_id, realm_id, group_id) DO UPDATE SET
  external_id = EXCLUDED.external_id,
  display_name = EXCLUDED.display_name,
  updated_at = EXCLUDED.updated_at";
        }
        else
        {
            cmd.CommandText = @"MERGE INTO dbo.IDENTITY_GROUPS AS target
USING (VALUES (@tenantId, @realmId, @groupId, @externalId, @displayName, @createdAt, @updatedAt))
  AS source (tenant_id, realm_id, group_id, external_id, display_name, created_at, updated_at)
ON target.tenant_id = source.tenant_id AND target.realm_id = source.realm_id AND target.group_id = source.group_id
WHEN MATCHED THEN UPDATE SET
  target.external_id = source.external_id,
  target.display_name = source.display_name,
  target.updated_at = source.updated_at
WHEN NOT MATCHED THEN INSERT (tenant_id, realm_id, group_id, external_id, display_name, created_at, updated_at)
  VALUES (source.tenant_id, source.realm_id, source.group_id, source.external_id, source.display_name, source.created_at, source.updated_at);";
        }

        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupId", group.Id ?? group.ExternalId ?? Guid.NewGuid().ToString("N"));
        AddParam(cmd, "@externalId", group.ExternalId ?? group.Id ?? "");
        AddParam(cmd, "@displayName", group.DisplayName);
        AddParam(cmd, "@createdAt", DialectValueCodec.Ts(Sql, createdAt));
        AddParam(cmd, "@updatedAt", DialectValueCodec.Ts(Sql, updatedAt));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// PERS-07: deletes the group and its memberships in one transaction and returns the user keys that were members, so the caller can
    /// invalidate their cached role resolution.
    /// </summary>
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
        cmd.CommandText = "SELECT group_id, external_id, display_name, created_at, updated_at FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId ORDER BY group_id LIMIT @count OFFSET @offset";
        if (dialect == IdentityStoreDialect.SqlServer)
        {
            cmd.CommandText = "SELECT group_id, external_id, display_name, created_at, updated_at FROM dbo.IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId ORDER BY group_id OFFSET @offset ROWS FETCH NEXT @count ROWS ONLY";
        }

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
        // PERS-07: an orphan membership (its group no longer exists) never resolves to a group.
        cmd.CommandText = @"SELECT m.group_id FROM IDENTITY_GROUP_MEMBERS m
INNER JOIN IDENTITY_GROUPS g ON g.tenant_id = m.tenant_id AND g.realm_id = m.realm_id AND g.group_id = m.group_id
WHERE m.tenant_id = @tenantId AND m.realm_id = @realmId AND m.user_key = @userKey";
        if (dialect == IdentityStoreDialect.SqlServer)
        {
            cmd.CommandText = cmd.CommandText.Replace("IDENTITY_GROUP_MEMBERS m", "dbo.IDENTITY_GROUP_MEMBERS m").Replace("IDENTITY_GROUPS g", "dbo.IDENTITY_GROUPS g");
        }
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

        // Bounded scan over the (tenant_id, realm_id, user_key) primary-key prefix: at most `cap` rows are counted.
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

        return new ScimUser(userKey, externalId, userName, displayName, email, active, deactivatedAt, createdAt, updatedAt);
    }

    private ScimGroup ReadGroup(DbDataReader reader)
    {
        var groupId = reader.GetString(0);
        var externalId = reader.GetString(1);
        var displayName = reader.GetString(2);
        var createdAt = DialectValueCodec.ReadRequiredTs(Sql, reader, 3);
        var updatedAt = DialectValueCodec.ReadRequiredTs(Sql, reader, 4);

        return new ScimGroup(groupId, externalId, displayName, null, createdAt, updatedAt);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
