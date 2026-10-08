namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Virtual filters: storage shared by the SQLite and PostgreSQL governance repositories (portable SQL: ON CONFLICT,
/// named parameters). Definitions are stored as JSON with their hash; every write increments the generation in the
/// same transaction.
/// </summary>
internal static class VirtualFilterStore
{
    private sealed record TableDto(string Domain, string Schema, string Table);
    private sealed record JoinDto(TableDto Table, string Alias, string Left, string Right);
    private sealed record ConditionDto(string Column, FilterConditionOperator Operator, string? Value);
    private sealed record FilterDto(
        TableDto From, string FromAlias, List<JoinDto> Joins, List<ConditionDto> Where,
        List<string> KeyColumns, string? ValidFrom, string? ValidTo, List<string> Supersedes);
    private sealed record BindingDto(
        string TargetPattern, GranteeType GranteeType, string? GranteeSid, string? RoleName, FilterObjectKinds ObjectKinds,
        string? TimeColumn, Dictionary<string, string>? ColumnMap, OnUnmatched? OnUnmatched);

    public static async Task<VirtualFilterSnapshot> LoadSnapshotAsync(DbConnection connection, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        long generation = await ReadGenerationAsync(connection, tx, ct).ConfigureAwait(false);

        var filters = new List<VirtualFilter>();
        await using (var cmd = Command(connection, tx, @"
            SELECT id, tenant_id, name, source, definition_json, definition_hash, managed_path, managed_commit, updated_by, updated_at
            FROM VIRTUAL_FILTERS ORDER BY tenant_id, name;"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                filters.Add(ReadFilter(reader));
            }
        }

        var bindings = new List<FilterBinding>();
        await using (var cmd = Command(connection, tx, @"
            SELECT id, tenant_id, filter_name, definition_json, definition_hash, managed_path, managed_commit, updated_by, updated_at
            FROM FILTER_BINDINGS ORDER BY tenant_id, filter_name, id;"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                bindings.Add(ReadBinding(reader));
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new VirtualFilterSnapshot(generation, filters, bindings);
    }

    public static async Task<long> GetGenerationAsync(DbConnection connection, CancellationToken ct) =>
        await ReadGenerationAsync(connection, null, ct).ConfigureAwait(false);

    public static async Task SaveFilterAsync(DbConnection connection, VirtualFilter filter, CancellationToken ct)
    {
        var definition = new FilterDto(
            Dto(filter.Structured!.From),
            filter.Structured.FromAlias,
            filter.Structured.Joins.Select(j => new JoinDto(Dto(j.Table), j.Alias, j.LeftColumn, j.RightColumn)).ToList(),
            filter.Structured.Where.Select(w => new ConditionDto(w.Column, w.Operator, w.Value)).ToList(),
            filter.KeyColumns.ToList(),
            filter.ValidFromColumn,
            filter.ValidToColumn,
            filter.Supersedes.ToList());

        await WriteAsync(connection, @"
            INSERT INTO VIRTUAL_FILTERS (id, tenant_id, name, source, definition_json, definition_hash, managed_path, managed_commit, updated_by, updated_at)
            VALUES (@id, @tenant, @name, @source, @json, @hash, @path, @commit, @by, @at)
            ON CONFLICT (tenant_id, name) DO UPDATE SET
                source = excluded.source, definition_json = excluded.definition_json, definition_hash = excluded.definition_hash,
                managed_path = excluded.managed_path, managed_commit = excluded.managed_commit,
                updated_by = excluded.updated_by, updated_at = excluded.updated_at;",
            [
                ("@id", filter.Id.ToString()),
                ("@tenant", filter.TenantId.Value),
                ("@name", filter.Name),
                ("@source", filter.Source),
                ("@json", JsonSerializer.Serialize(definition)),
                ("@hash", filter.ComputeDefinitionHash()),
                ("@path", filter.ManagedBy?.Path),
                ("@commit", filter.ManagedBy?.Commit),
                ("@by", filter.UpdatedBy),
                ("@at", filter.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))
            ], ct).ConfigureAwait(false);
    }

    public static Task<bool> DeleteFilterAsync(DbConnection connection, TenantId tenantId, string name, CancellationToken ct) =>
        DeleteAsync(connection, "DELETE FROM VIRTUAL_FILTERS WHERE tenant_id = @tenant AND name = @key;", tenantId, name, ct);

    public static async Task SaveBindingAsync(DbConnection connection, FilterBinding binding, CancellationToken ct)
    {
        var definition = new BindingDto(
            binding.TargetPattern, binding.GranteeType, binding.GranteeSid?.Value, binding.RoleName, binding.ObjectKinds,
            binding.TimeColumn, binding.ColumnMap?.ToDictionary(p => p.Key, p => p.Value), binding.OnUnmatched);

        await WriteAsync(connection, @"
            INSERT INTO FILTER_BINDINGS (id, tenant_id, filter_name, definition_json, definition_hash, managed_path, managed_commit, updated_by, updated_at)
            VALUES (@id, @tenant, @filter, @json, @hash, @path, @commit, @by, @at)
            ON CONFLICT (id) DO UPDATE SET
                tenant_id = excluded.tenant_id, filter_name = excluded.filter_name, definition_json = excluded.definition_json,
                definition_hash = excluded.definition_hash, managed_path = excluded.managed_path, managed_commit = excluded.managed_commit,
                updated_by = excluded.updated_by, updated_at = excluded.updated_at;",
            [
                ("@id", binding.Id.ToString()),
                ("@tenant", binding.TenantId.Value),
                ("@filter", binding.FilterName),
                ("@json", JsonSerializer.Serialize(definition)),
                ("@hash", binding.ComputeDefinitionHash()),
                ("@path", binding.ManagedBy?.Path),
                ("@commit", binding.ManagedBy?.Commit),
                ("@by", binding.UpdatedBy),
                ("@at", binding.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))
            ], ct).ConfigureAwait(false);
    }

    public static Task<bool> DeleteBindingAsync(DbConnection connection, TenantId tenantId, Guid id, CancellationToken ct) =>
        DeleteAsync(connection, "DELETE FROM FILTER_BINDINGS WHERE tenant_id = @tenant AND id = @key;", tenantId, id.ToString(), ct);

    private static async Task WriteAsync(DbConnection connection, string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = Command(connection, tx, sql, parameters))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await IncrementGenerationAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> DeleteAsync(DbConnection connection, string sql, TenantId tenantId, string key, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        int affected;
        await using (var cmd = Command(connection, tx, sql, ("@tenant", tenantId.Value), ("@key", key)))
        {
            affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (affected == 0)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await IncrementGenerationAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static async Task IncrementGenerationAsync(DbConnection connection, DbTransaction tx, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, "UPDATE VIRTUAL_FILTER_GENERATION SET generation = generation + 1 WHERE id = 1;");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> ReadGenerationAsync(DbConnection connection, DbTransaction? tx, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, "SELECT generation FROM VIRTUAL_FILTER_GENERATION WHERE id = 1;");
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private static VirtualFilter ReadFilter(DbDataReader reader)
    {
        var dto = JsonSerializer.Deserialize<FilterDto>(reader.GetString(4))
            ?? throw new InvalidOperationException("Virtual filter definition is empty.");
        return new VirtualFilter
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = new TenantId(reader.GetString(1)),
            Name = reader.GetString(2),
            Source = reader.GetString(3),
            Structured = new StructuredFilterDefinition
            {
                From = Identifier(dto.From),
                FromAlias = dto.FromAlias,
                Joins = dto.Joins.Select(j => new FilterJoin(Identifier(j.Table), j.Alias, j.Left, j.Right)).ToList(),
                Where = dto.Where.Select(w => new FilterCondition(w.Column, w.Operator, w.Value)).ToList()
            },
            KeyColumns = dto.KeyColumns,
            ValidFromColumn = dto.ValidFrom,
            ValidToColumn = dto.ValidTo,
            Supersedes = dto.Supersedes,
            StoredDefinitionHash = reader.GetString(5),
            ManagedBy = ReadManagedBy(reader, 6),
            UpdatedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)
        };
    }

    private static FilterBinding ReadBinding(DbDataReader reader)
    {
        var dto = JsonSerializer.Deserialize<BindingDto>(reader.GetString(3))
            ?? throw new InvalidOperationException("Filter binding definition is empty.");
        return new FilterBinding
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = new TenantId(reader.GetString(1)),
            FilterName = reader.GetString(2),
            TargetPattern = dto.TargetPattern,
            GranteeType = dto.GranteeType,
            GranteeSid = dto.GranteeSid == null ? (Sid?)null : new Sid(dto.GranteeSid),
            RoleName = dto.RoleName,
            ObjectKinds = dto.ObjectKinds,
            TimeColumn = dto.TimeColumn,
            ColumnMap = dto.ColumnMap,
            OnUnmatched = dto.OnUnmatched,
            StoredDefinitionHash = reader.GetString(4),
            ManagedBy = ReadManagedBy(reader, 5),
            UpdatedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)
        };
    }

    private static ManagedBy? ReadManagedBy(DbDataReader reader, int pathOrdinal) =>
        reader.IsDBNull(pathOrdinal) ? (ManagedBy?)null : new ManagedBy(reader.GetString(pathOrdinal), reader.IsDBNull(pathOrdinal + 1) ? string.Empty : reader.GetString(pathOrdinal + 1));

    private static TableDto Dto(TableIdentifier id) => new(id.Domain, id.Schema, id.TableName);

    private static TableIdentifier Identifier(TableDto dto) => new(dto.Domain, dto.Schema, dto.Table);
}
