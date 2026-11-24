namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Caller attribute registry and service sets (129 C-3, C-5): storage shared by the three governance repositories (portable SQL,
/// named parameters). A save is one transaction that upserts the active row; the service checks the writer's capacity first.
/// Each successful write increments the generation once.
/// </summary>
internal static class CallerAttributeStore
{
    private const int Active = (int)FilterApprovalStatus.Active;

    private sealed record DefinitionDto(
        string Name, CallerAttributeType ValueType, CallerAttributeCardinality Cardinality, int MaxValues, int TtlSeconds,
        string ProviderKind, Dictionary<string, string>? ProviderConfig, string? Description);

    public static async Task<CallerAttributeStoreSnapshot> LoadSnapshotAsync(DbConnection connection, CancellationToken ct, DbTransaction? snapshot = null)
    {
        await using var ownedTx = snapshot == null ? await connection.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        var tx = snapshot ?? ownedTx!;
        long generation = await ReadGenerationAsync(connection, tx, ct).ConfigureAwait(false);

        var attributes = new List<CallerAttributeRecord>();
        await using (var cmd = Command(connection, tx, @"
            SELECT id, tenant_id, name, definition_json, status, created_by, approved_by, approved_at, updated_at
            FROM CALLER_ATTRIBUTES ORDER BY tenant_id, name;", []))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = Guid.Parse(reader.GetString(0));
                var tenant = new TenantId(reader.GetString(1));
                var status = (FilterApprovalStatus)reader.GetInt32(4);
                attributes.Add(new CallerAttributeRecord(
                    FromJson(reader.GetString(3), id, tenant, status),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
                    ParseTime(reader.GetString(8))));
            }
        }

        var sets = new List<ServiceAttributeSetRecord>();
        await using (var cmd = Command(connection, tx, @"
            SELECT id, tenant_id, application_id, attribute_name, values_json, status, created_by, approved_by, approved_at, updated_at
            FROM CALLER_ATTRIBUTE_SERVICE_SETS ORDER BY tenant_id, application_id, attribute_name;", []))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                sets.Add(new ServiceAttributeSetRecord(
                    new ServiceAttributeSet(
                        Guid.Parse(reader.GetString(0)), new TenantId(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                        Values(reader.GetString(4)), (FilterApprovalStatus)reader.GetInt32(5)),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : ParseTime(reader.GetString(8)),
                    ParseTime(reader.GetString(9))));
            }
        }

        if (ownedTx != null)
        {
            await ownedTx.CommitAsync(ct).ConfigureAwait(false);
        }

        return new CallerAttributeStoreSnapshot(generation, attributes, sets);
    }

    public static Task<long> GetGenerationAsync(DbConnection connection, CancellationToken ct) => ReadGenerationAsync(connection, null, ct);

    /// <summary>Writes inside <paramref name="tx"/>; the caller commits (together with its audit entry) or rolls back.</summary>
    public static async Task<CallerAttributeChangeResult> SaveDefinitionAsync(DbConnection connection, DbTransaction tx, CallerAttributeDefinition def, string actor, DateTimeOffset at, CancellationToken ct)
    {
        var exists = await ExistsAsync(connection, tx, "SELECT 1 FROM CALLER_ATTRIBUTES WHERE tenant_id = @tenant AND name = @name;", [("@tenant", def.Tenant.Value), ("@name", def.Name)], ct).ConfigureAwait(false);
        var json = ToJson(def);
        var hash = def.ComputeDefinitionHash();
        var stamp = Time(at);
        if (!exists)
        {
            await ExecuteAsync(connection, tx, @"
                INSERT INTO CALLER_ATTRIBUTES (id, tenant_id, name, definition_json, definition_hash, status, created_by, approved_by, approved_at, updated_by, updated_at)
                VALUES (@id, @tenant, @name, @json, @hash, @status, @by, @by, @at, @by, @at);",
                [("@id", def.Id.ToString()), ("@tenant", def.Tenant.Value), ("@name", def.Name), ("@json", json), ("@hash", hash), ("@status", Active), ("@by", actor), ("@at", stamp)], ct).ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTES SET definition_json = @json, definition_hash = @hash, status = @status,
                    approved_by = @by, approved_at = @at, updated_by = @by, updated_at = @at
                WHERE tenant_id = @tenant AND name = @name;",
                [("@json", json), ("@hash", hash), ("@status", Active), ("@by", actor), ("@at", stamp), ("@tenant", def.Tenant.Value), ("@name", def.Name)], ct).ConfigureAwait(false);
        }

        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        return CallerAttributeChangeResult.Applied;
    }

    /// <summary>Writes inside <paramref name="tx"/>; the caller commits (together with its audit entry) or rolls back.</summary>
    public static async Task<CallerAttributeChangeResult> DeleteDefinitionAsync(DbConnection connection, DbTransaction tx, TenantId tenant, string name, CancellationToken ct)
    {
        var affected = await ExecuteAsync(connection, tx, "DELETE FROM CALLER_ATTRIBUTES WHERE tenant_id = @tenant AND name = @name;",
            [("@tenant", tenant.Value), ("@name", name)], ct).ConfigureAwait(false);
        if (affected == 0)
        {
            return CallerAttributeChangeResult.NotFound;
        }

        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        return CallerAttributeChangeResult.Applied;
    }

    // ---- service sets --------------------------------------------------------------------------------------------------------

    /// <summary>Writes inside <paramref name="tx"/>; the caller commits (together with its audit entry) or rolls back.</summary>
    public static async Task<CallerAttributeChangeResult> SaveServiceSetAsync(
        DbConnection connection, DbTransaction tx, TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at, CancellationToken ct)
    {
        var app = CallerAttributeNameRules.CanonicalApplicationId(applicationId);
        var exists = await ExistsAsync(connection, tx,
            "SELECT 1 FROM CALLER_ATTRIBUTE_SERVICE_SETS WHERE tenant_id = @tenant AND application_id = @app AND attribute_name = @name;",
            [("@tenant", tenant.Value), ("@app", app), ("@name", attributeName)], ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(values);
        var stamp = Time(at);
        var set = new ServiceAttributeSet(Guid.NewGuid(), tenant, app, attributeName, values, FilterApprovalStatus.Active);
        if (!exists)
        {
            await ExecuteAsync(connection, tx, @"
                INSERT INTO CALLER_ATTRIBUTE_SERVICE_SETS (id, tenant_id, application_id, attribute_name, values_json, values_hash, status, created_by, approved_by, approved_at, updated_at)
                VALUES (@id, @tenant, @app, @name, @json, @hash, @status, @by, @by, @at, @at);",
                [("@id", set.Id.ToString()), ("@tenant", tenant.Value), ("@app", app), ("@name", attributeName), ("@json", json), ("@hash", set.ComputeValuesHash()), ("@status", Active), ("@by", actor), ("@at", stamp)], ct).ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTE_SERVICE_SETS SET values_json = @json, values_hash = @hash, status = @status, created_by = @by,
                    approved_by = @by, approved_at = @at, updated_at = @at
                WHERE tenant_id = @tenant AND application_id = @app AND attribute_name = @name;",
                [("@json", json), ("@hash", set.ComputeValuesHash()), ("@status", Active), ("@by", actor), ("@at", stamp), ("@tenant", tenant.Value), ("@app", app), ("@name", attributeName)], ct).ConfigureAwait(false);
        }

        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        return CallerAttributeChangeResult.Applied;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------

    private static async Task<bool> ExistsAsync(DbConnection connection, DbTransaction tx, string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false);
    }

    private static string ToJson(CallerAttributeDefinition d) => JsonSerializer.Serialize(new DefinitionDto(
        d.Name, d.ValueType, d.Cardinality, d.MaxValues, d.TtlSeconds, d.Provider.Kind, d.Provider.Config.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), d.Description));

    private static CallerAttributeDefinition FromJson(string json, Guid id, TenantId tenant, FilterApprovalStatus status)
    {
        var dto = JsonSerializer.Deserialize<DefinitionDto>(json) ?? throw new InvalidOperationException("Caller attribute definition is empty.");
        return new CallerAttributeDefinition(
            id, tenant, dto.Name, dto.ValueType, dto.Cardinality, dto.MaxValues, dto.TtlSeconds,
            new CallerAttributeProviderBinding(dto.ProviderKind, dto.ProviderConfig ?? new Dictionary<string, string>()), dto.Description, status);
    }

    private static IReadOnlyList<string> Values(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static string Time(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static Task BumpAsync(DbConnection connection, DbTransaction tx, CancellationToken ct) =>
        ExecuteAsync(connection, tx, "UPDATE CALLER_ATTRIBUTE_GENERATION SET generation = generation + 1 WHERE id = 1;", [], ct);

    private static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction tx, string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> ReadGenerationAsync(DbConnection connection, DbTransaction? tx, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, "SELECT generation FROM CALLER_ATTRIBUTE_GENERATION WHERE id = 1;", []);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? tx, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            SqlCoreBinding.GuardTenantParameter(name, value);
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }
}
