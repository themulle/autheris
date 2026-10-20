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
        AddParam(cmd, "@active", user.Active ? 1 : 0);
        AddParam(cmd, "@deactivatedAt", (object?)user.DeactivatedAt?.ToString("O") ?? DBNull.Value);
        AddParam(cmd, "@createdAt", createdAt.ToString("O"));
        AddParam(cmd, "@updatedAt", updatedAt.ToString("O"));

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
        AddParam(cmd, "@active", 0);
        AddParam(cmd, "@deactivatedAt", now.ToString("O"));
        AddParam(cmd, "@updatedAt", now.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@userKey", userKey);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        int offset = Math.Max(0, startIndex - 1);
        cmd.CommandText = "SELECT user_key, external_id, user_name, email, display_name, active, deactivated_at, created_at, updated_at FROM IDENTITY_USERS WHERE tenant_id = @tenantId AND realm_id = @realmId LIMIT @count OFFSET @offset";
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
        AddParam(cmd, "@createdAt", createdAt.ToString("O"));
        AddParam(cmd, "@updatedAt", updatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_id = @groupId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupId", groupId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        int offset = Math.Max(0, startIndex - 1);
        cmd.CommandText = "SELECT group_id, external_id, display_name, created_at, updated_at FROM IDENTITY_GROUPS WHERE tenant_id = @tenantId AND realm_id = @realmId LIMIT @count OFFSET @offset";
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
        var now = DateTimeOffset.UtcNow.ToString("O");
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
        cmd.CommandText = "SELECT group_id FROM IDENTITY_GROUP_MEMBERS WHERE tenant_id = @tenantId AND realm_id = @realmId AND user_key = @userKey";
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
        AddParam(cmd, "@lockedAt", lockEntry.LockedAt.ToString("O"));

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
                DateTimeOffset.Parse(reader.GetString(5))
            ));
        }
        return list;
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

    private static ScimUser ReadUser(DbDataReader reader)
    {
        var userKey = reader.GetString(0);
        var externalId = reader.GetString(1);
        var userName = reader.GetString(2);
        var email = reader.IsDBNull(3) ? null : reader.GetString(3);
        var displayName = reader.IsDBNull(4) ? null : reader.GetString(4);
        var active = reader.GetInt32(5) != 0;
        var deactivatedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(6));
        var createdAt = DateTimeOffset.Parse(reader.GetString(7));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(8));

        return new ScimUser(userKey, externalId, userName, displayName, email, active, deactivatedAt, createdAt, updatedAt);
    }

    private static ScimGroup ReadGroup(DbDataReader reader)
    {
        var groupId = reader.GetString(0);
        var externalId = reader.GetString(1);
        var displayName = reader.GetString(2);
        var createdAt = DateTimeOffset.Parse(reader.GetString(3));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(4));

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
