using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Dialect-neutral persistence store for tenant access policy settings.</summary>
public sealed class AccessPolicySettingsStore(
    IGovernanceSqlDialect dialect,
    TimeProvider? timeProvider = null,
    IGovernanceChangeNotifier? changeNotifier = null) : IAccessPolicySettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal const string GetSql =
        "SELECT settings_json, version, updated_by, updated_at FROM ACCESS_POLICY_SETTINGS WHERE tenant_id = @tenantId";

    internal const string InsertSql =
        "INSERT INTO ACCESS_POLICY_SETTINGS (tenant_id, settings_json, version, updated_by, updated_at) " +
        "VALUES (@tenantId, @settingsJson, 1, @updatedBy, @updatedAt)";

    internal const string UpdateSql =
        "UPDATE ACCESS_POLICY_SETTINGS SET settings_json = @settingsJson, version = @newVersion, updated_by = @updatedBy, updated_at = @updatedAt " +
        "WHERE tenant_id = @tenantId AND version = @expectedVersion";

    internal const string BumpEpochPgSql =
        "INSERT INTO GOVERNANCE_EPOCHS (tenant_id, epoch, updated_at) VALUES (@tenantId, 1, @updatedAt) " +
        "ON CONFLICT (tenant_id) DO UPDATE SET epoch = GOVERNANCE_EPOCHS.epoch + 1, updated_at = EXCLUDED.updated_at RETURNING epoch";

    internal const string BumpEpochSsSql =
        "MERGE GOVERNANCE_EPOCHS WITH (HOLDLOCK) AS target USING (VALUES (@tenantId)) AS source (tenant_id) " +
        "ON target.tenant_id = source.tenant_id " +
        "WHEN MATCHED THEN UPDATE SET epoch = target.epoch + 1, updated_at = @updatedAt " +
        "WHEN NOT MATCHED THEN INSERT (tenant_id, epoch, updated_at) VALUES (@tenantId, 1, @updatedAt) " +
        "OUTPUT inserted.epoch;";

    internal static IReadOnlyList<string> AllSqlTemplates =>
        [GetSql, InsertSql, UpdateSql, BumpEpochPgSql, BumpEpochSsSql];

    private readonly IGovernanceSqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IGovernanceChangeNotifier? _changeNotifier = changeNotifier;
    private bool IsSqlServer => _dialect is SqlServerGovernanceDialect;

    public async Task<StoredTenantAccessPolicy?> GetAsync(TenantId tenant, CancellationToken ct = default)
    {
        var tenantId = RequireTenant(tenant);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Cmd(conn, null, GetSql, tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var json = reader.GetString(0);
        var version = reader.GetInt32(1);
        var updatedBy = reader.GetString(2);
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(3);
        var settings = JsonSerializer.Deserialize<TenantAccessPolicyUpdate>(json, JsonOptions) ?? new TenantAccessPolicyUpdate();
        return new StoredTenantAccessPolicy(tenant, settings, version, updatedBy, updatedAt);
    }

    public async Task<AccessPolicyWriteResult> UpsertAsync(
        TenantId tenant,
        TenantAccessPolicyUpdate settings,
        int expectedVersion,
        Sid actor,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenant(tenant);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        var actorValue = RequireBounded(actor.Value, 256, nameof(actor));
        var now = _time.GetUtcNow().ToUniversalTime();
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        try
        {
            var outcome = await RunInTxAsync(async (conn, tx) =>
            {
                if (expectedVersion == 0)
                {
                    await using var ins = Cmd(conn, tx, InsertSql, tenantId,
                        ("@settingsJson", json, DbType.String),
                        ("@updatedBy", actorValue, DbType.String),
                        ("@updatedAt", now, DbType.DateTimeOffset));
                    await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    var createdEpoch = await BumpEpochInTxAsync(conn, tx, tenantId, now, ct).ConfigureAwait(false);
                    var created = new StoredTenantAccessPolicy(tenant, settings, 1, actorValue, now);
                    return (true, (Result: new AccessPolicyWriteResult(AccessPolicyWriteStatus.Created, created), Epoch: createdEpoch));
                }

                var newVersion = expectedVersion + 1;
                await using var upd = Cmd(conn, tx, UpdateSql, tenantId,
                    ("@settingsJson", json, DbType.String),
                    ("@newVersion", newVersion, DbType.Int32),
                    ("@updatedBy", actorValue, DbType.String),
                    ("@updatedAt", now, DbType.DateTimeOffset),
                    ("@expectedVersion", expectedVersion, DbType.Int32));
                if (await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
                {
                    var updatedEpoch = await BumpEpochInTxAsync(conn, tx, tenantId, now, ct).ConfigureAwait(false);
                    var updated = new StoredTenantAccessPolicy(tenant, settings, newVersion, actorValue, now);
                    return (true, (Result: new AccessPolicyWriteResult(AccessPolicyWriteStatus.Updated, updated), Epoch: updatedEpoch));
                }

                return (false, (Result: new AccessPolicyWriteResult(AccessPolicyWriteStatus.Conflict), Epoch: 0L));
            }, ct).ConfigureAwait(false);

            if (outcome.Result.Succeeded && _changeNotifier is not null)
            {
                await _changeNotifier.NotifyAsync(new GovernanceChangeEvent(tenantId, outcome.Epoch, null, now), CancellationToken.None).ConfigureAwait(false);
            }

            return outcome.Result;
        }
        catch (DbException ex) when (_dialect.IsUniqueViolation(ex))
        {
            return new AccessPolicyWriteResult(AccessPolicyWriteStatus.Conflict);
        }
    }

    private async Task<long> BumpEpochInTxAsync(DbConnection conn, DbTransaction tx, string tenantId, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, IsSqlServer ? BumpEpochSsSql : BumpEpochPgSql, tenantId, ("@updatedAt", now, DbType.DateTimeOffset));
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull
            ? throw new InvalidOperationException("Governance epoch increment returned no value.")
            : Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = _dialect.CreateConnection();
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<T> RunInTxAsync<T>(Func<DbConnection, DbTransaction, Task<(bool Commit, T Result)>> work, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var (commit, result) = await work(conn, tx).ConfigureAwait(false);
            if (commit)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            throw;
        }
    }

    private static DbCommand Cmd(DbConnection conn, DbTransaction? tx, string sql, string tenantId, params (string Name, object? Value, DbType? Type)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.AddBound("@tenantId", tenantId);
        foreach (var (name, value, type) in parameters)
        {
            cmd.AddBound(name, value, type);
        }

        return cmd;
    }

    private static string RequireTenant(TenantId tenant) =>
        TenantId.TryParse(TenantScope.RequireTenant(tenant, GovernanceTable.AccessPolicySettings.ToSqlName()).Value, out var valid)
            ? valid.Value
            : throw new ArgumentException("A valid non-reserved TenantId is required.", nameof(tenant));

    private static string RequireBounded(string? raw, int maxLen, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw, paramName);
        var trimmed = raw.Trim();
        return trimmed.Length > maxLen || trimmed.Any(char.IsControl)
            ? throw new ArgumentException($"Value for '{paramName}' exceeds {maxLen} characters or contains control characters.", paramName)
            : trimmed;
    }
}
