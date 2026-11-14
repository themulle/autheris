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

internal sealed partial class IdentityStore
{
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

    private async Task<IReadOnlyList<ScimUser>> QueryUsersAsync(string sql, TenantId tenantId, string realmId, string paramName, string paramValue, CancellationToken ct)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, paramName, paramValue);

        var users = new List<ScimUser>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            users.Add(ReadUser(reader));
        }

        return users;
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
