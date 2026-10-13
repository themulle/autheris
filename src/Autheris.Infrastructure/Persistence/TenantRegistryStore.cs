namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

/// <summary>
/// CR-ADG-31: portable storage of the tenant registry (SQLite, PostgreSQL, SQL Server; plain SELECT/INSERT/UPDATE, no
/// dialect-specific upsert). The primary key is the normalized key (<see cref="TenantRegistryKey"/>), so two spellings of one
/// group can never both be recorded: the insert of the loser fails on the key, and the loser marks the group as collided.
/// A collided group denies every spelling, including the first one, until an operator resolves it. Statements run in
/// autocommit, so a failed insert never poisons a transaction (PostgreSQL).
/// </summary>
internal static class TenantRegistryStore
{
    public static async Task<TenantRegistration> RegisterOrGetAsync(DbConnection connection, TenantId tenant, CancellationToken ct)
    {
        var key = TenantRegistryKey.Normalize(tenant.Value);

        var row = await ReadAsync(connection, key, ct).ConfigureAwait(false);
        if (row is null)
        {
            if (await TryInsertAsync(connection, key, tenant.Value, collided: false, ct).ConfigureAwait(false))
            {
                return TenantRegistration.Registered;
            }

            // Lost a race: the winner's row exists now; a vanished row means the insert failed for another reason.
            row = await ReadAsync(connection, key, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("Tenant registry insert failed and no row exists.");
        }

        return await EvaluateAsync(connection, key, tenant.Value, row.Value, ct).ConfigureAwait(false);
    }

    /// <summary>Read-only lookup (Phase 5 L6): never inserts and never marks a collision.</summary>
    public static async Task<TenantRegistration?> LookupAsync(DbConnection connection, TenantId tenant, CancellationToken ct)
    {
        var row = await ReadAsync(connection, TenantRegistryKey.Normalize(tenant.Value), ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        if (row.Value.Collided)
        {
            return TenantRegistration.Collision;
        }

        return string.Equals(row.Value.TenantId, tenant.Value, StringComparison.Ordinal) ? TenantRegistration.Existing : null;
    }

    /// <summary>
    /// M-A2: clears the collision mark of the group of <paramref name="keep"/> and makes that exact spelling the group's tenant. Only a marked
    /// group changes (the UPDATE is conditional on the mark), so this is never a way to rename a healthy tenant.
    /// </summary>
    public static async Task<bool> ResolveCollisionAsync(DbConnection connection, TenantId keep, CancellationToken ct)
    {
        await using var cmd = Command(connection,
            "UPDATE TENANT_REGISTRY SET tenant_id = @t, collided = 0 WHERE normalized_key = @k AND collided = 1",
            ("@t", keep.Value), ("@k", TenantRegistryKey.Normalize(keep.Value)));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Records configured ids. Spellings that are not valid tenant ids are skipped (they can never authenticate).</summary>
    public static async Task PreseedAsync(DbConnection connection, IEnumerable<string> tenantIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        foreach (var raw in tenantIds.Select(id => id?.Trim()).Distinct(StringComparer.Ordinal))
        {
            if (!TenantId.TryParse(raw, out var tenant))
            {
                continue;
            }

            await RegisterOrGetAsync(connection, tenant, ct).ConfigureAwait(false);
        }
    }

    private static async Task<TenantRegistration> EvaluateAsync(DbConnection connection, string key, string tenant, (string TenantId, bool Collided) row, CancellationToken ct)
    {
        if (row.Collided)
        {
            return TenantRegistration.Collision;
        }

        if (string.Equals(row.TenantId, tenant, StringComparison.Ordinal))
        {
            return TenantRegistration.Existing;
        }

        await using var cmd = Command(connection, "UPDATE TENANT_REGISTRY SET collided = 1 WHERE normalized_key = @k", ("@k", key));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return TenantRegistration.Collision;
    }

    private static async Task<(string TenantId, bool Collided)?> ReadAsync(DbConnection connection, string key, CancellationToken ct)
    {
        await using var cmd = Command(connection, "SELECT tenant_id, collided FROM TENANT_REGISTRY WHERE normalized_key = @k", ("@k", key));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return (reader.GetString(0), Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) != 0);
    }

    private static async Task<bool> TryInsertAsync(DbConnection connection, string key, string tenant, bool collided, CancellationToken ct)
    {
        await using var cmd = Command(connection,
            "INSERT INTO TENANT_REGISTRY (normalized_key, tenant_id, collided, first_seen_at) VALUES (@k, @t, @c, @at)",
            ("@k", key), ("@t", tenant), ("@c", collided ? 1 : 0), ("@at", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbException)
        {
            return false; // primary key violation (lost the race); the caller re-reads and rethrows if no row exists
        }
    }

    private static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }
}
