namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Virtual filters: storage shared by the governance repositories (portable SQL: ON CONFLICT, MERGE on SQL Server,
/// named parameters). Definitions are stored as JSON with their hash; a change set is one transaction and increments
/// the generation once.
/// </summary>
internal static class VirtualFilterStore
{
    private sealed record TableDto(string Domain, string Schema, string Table);
    private sealed record JoinDto(TableDto Table, string Alias, string Left, string Right);
    private sealed record ConditionDto(string Column, FilterConditionOperator Operator, string? Value);
    private sealed record ParameterDto(string Name, RowPolicyParameterType Type, bool IsList = false, int MaxValues = 100);
    private sealed record FilterDto(
        TableDto? From, string? FromAlias, List<JoinDto>? Joins, List<ConditionDto>? Where,
        List<string> KeyColumns, string? ValidFrom, string? ValidTo, List<string> Supersedes,
        string? Sql = null, List<string>? SqlTargetColumns = null,
        FilterApprovalStatus? Status = null, string? CreatedBy = null, string? ApprovedBy = null, DateTimeOffset? ApprovedAt = null,
        FilterDto? Draft = null, bool PendingDeletion = false, string? DeletionRequestedBy = null,
        List<ParameterDto>? Parameters = null, RowPolicyEvaluationMode? EvaluationMode = null, TimeSpan? KeySetMaxAge = null,
        int? KeySetMaxKeys = null, List<RefContainerDto>? ReferencedContainers = null);
    private sealed record RefContainerDto(string Table, string Container);
    private sealed record BindingDto(
        string Filter, string? Target, FilterObjectKinds ObjectKinds, string? TimeColumn, Dictionary<string, string>? ColumnMap,
        VirtualFilterExecutionStrategy? Strategy = null, PushdownParameterFormat? PushdownFormat = null,
        int? MaxPushdownKeys = null);
    private sealed record ProfileDto(
        GranteeType GranteeType, string? GranteeSid, string Scope, UncoveredPolicy? Uncovered, List<BindingDto> Bindings,
        FilterApprovalStatus? Status = null, string? CreatedBy = null, string? ApprovedBy = null, DateTimeOffset? ApprovedAt = null,
        ProfileDto? Draft = null, bool PendingDeletion = false, string? DeletionRequestedBy = null);

    /// <param name="snapshot">A read transaction the caller opened (deferred, so it takes no write lock); the store then neither begins nor commits one.</param>
    public static async Task<VirtualFilterSnapshot> LoadSnapshotAsync(DbConnection connection, CancellationToken ct, DbTransaction? snapshot = null)
    {
        await using var ownedTx = snapshot == null ? await connection.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        var tx = snapshot ?? ownedTx!;
        long generation = await ReadGenerationAsync(connection, tx, ct).ConfigureAwait(false);

        var filters = new List<VirtualFilter>();
        await using (var cmd = Command(connection, tx, @"
            SELECT id, tenant_id, name, source, definition_json, definition_hash, managed_path, managed_commit, updated_by, updated_at
            FROM VIRTUAL_FILTERS ORDER BY tenant_id, name;", []))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                filters.Add(ReadFilter(reader));
            }
        }

        var profiles = new List<VirtualFilterAccessProfile>();
        await using (var cmd = Command(connection, tx, @"
            SELECT id, tenant_id, name, definition_json, definition_hash, managed_path, managed_commit, updated_by, updated_at
            FROM VIRTUAL_FILTER_ACCESS_PROFILES ORDER BY tenant_id, name;", []))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                profiles.Add(ReadProfile(reader));
            }
        }

        if (ownedTx != null)
        {
            await ownedTx.CommitAsync(ct).ConfigureAwait(false);
        }

        return new VirtualFilterSnapshot(generation, filters, profiles);
    }

    public static Task<long> GetGenerationAsync(DbConnection connection, CancellationToken ct) => ReadGenerationAsync(connection, null, ct);

    public static async Task ApplyAsync(DbConnection connection, VirtualFilterChangeSet changes, CancellationToken ct)
    {
        if (changes.IsEmpty)
        {
            return;
        }

        var sqlServer = connection is Microsoft.Data.SqlClient.SqlConnection
            || Autheris.Infrastructure.Persistence.Dialects.SqlServerGovernanceDialect.Unwrap(connection) is Microsoft.Data.SqlClient.SqlConnection;
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // PERS-13: compare-and-set of the generation first; it also takes the write lock before any definition is touched.
        if (changes.ExpectedGeneration is { } expectedGeneration)
        {
            await using var cas = Command(connection, tx,
                "UPDATE VIRTUAL_FILTER_GENERATION SET generation = generation + 1 WHERE id = 1 AND generation = @expected;",
                [("@expected", expectedGeneration)]);
            if (await cas.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw new VirtualFilterConflictException("The virtual filter configuration changed since it was read; reload it and repeat the change.");
            }
        }

        foreach (var filter in changes.SaveFilters)
        {
            string? parametersJson = filter.Parameters.Count == 0 ? null : JsonSerializer.Serialize(filter.Parameters);
            object keySetMaxAgeSeconds = filter.KeySetMaxAge is { } age
                ? (int)Math.Ceiling(age.TotalSeconds)
                : DBNull.Value;
            object keySetMaxKeys = filter.KeySetMaxKeys is { } maxKeys
                ? maxKeys
                : DBNull.Value;
            await ExecuteAsync(connection, tx, sqlServer ? @"
                MERGE VIRTUAL_FILTERS WITH (HOLDLOCK) AS t
                USING (SELECT @tenant AS tenant_id, @name AS name) AS s ON t.tenant_id = s.tenant_id AND t.name = s.name
                WHEN MATCHED THEN UPDATE SET
                    source = @source, definition_json = @json, definition_hash = @hash,
                    managed_path = @path, managed_commit = @commit, updated_by = @by, updated_at = @at,
                    parameters_json = @parametersJson, evaluation_mode = @evalMode, key_set_max_age_seconds = @keySetMaxAgeSec,
                    key_set_max_keys = @keySetMaxKeys, status = @status, approved_by_sid = @approvedBySid, approved_at = @approvedAt
                WHEN NOT MATCHED THEN INSERT (id, tenant_id, name, source, definition_json, definition_hash, managed_path, managed_commit, updated_by, updated_at, parameters_json, evaluation_mode, key_set_max_age_seconds, key_set_max_keys, status, approved_by_sid, approved_at)
                    VALUES (@id, @tenant, @name, @source, @json, @hash, @path, @commit, @by, @at, @parametersJson, @evalMode, @keySetMaxAgeSec, @keySetMaxKeys, @status, @approvedBySid, @approvedAt);" : @"
                INSERT INTO VIRTUAL_FILTERS (id, tenant_id, name, source, definition_json, definition_hash, managed_path, managed_commit, updated_by, updated_at, parameters_json, evaluation_mode, key_set_max_age_seconds, key_set_max_keys, status, approved_by_sid, approved_at)
                VALUES (@id, @tenant, @name, @source, @json, @hash, @path, @commit, @by, @at, @parametersJson, @evalMode, @keySetMaxAgeSec, @keySetMaxKeys, @status, @approvedBySid, @approvedAt)
                ON CONFLICT (tenant_id, name) DO UPDATE SET
                    source = excluded.source, definition_json = excluded.definition_json, definition_hash = excluded.definition_hash,
                    managed_path = excluded.managed_path, managed_commit = excluded.managed_commit,
                    updated_by = excluded.updated_by, updated_at = excluded.updated_at,
                    parameters_json = excluded.parameters_json, evaluation_mode = excluded.evaluation_mode,
                    key_set_max_age_seconds = excluded.key_set_max_age_seconds, key_set_max_keys = excluded.key_set_max_keys,
                    status = excluded.status, approved_by_sid = excluded.approved_by_sid, approved_at = excluded.approved_at;",
                [
                    ("@id", filter.Id.ToString()),
                    ("@tenant", filter.TenantId.Value),
                    ("@name", filter.Name),
                    ("@source", filter.Source),
                    ("@json", JsonSerializer.Serialize(FilterToDto(filter))),
                    ("@hash", filter.ComputeDefinitionHash()),
                    ("@path", filter.ManagedBy?.Path),
                    ("@commit", filter.ManagedBy?.Commit),
                    ("@by", filter.UpdatedBy),
                    ("@at", filter.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)),
                    ("@parametersJson", parametersJson),
                    ("@evalMode", filter.EvaluationMode.ToString()),
                    ("@keySetMaxAgeSec", keySetMaxAgeSeconds),
                    ("@keySetMaxKeys", keySetMaxKeys),
                    ("@status", filter.Status.ToString()),
                    ("@approvedBySid", filter.ApprovedBySid),
                    ("@approvedAt", new Stamp(filter.ApprovedAt))
                ], ct).ConfigureAwait(false);
        }

        foreach (var profile in changes.SaveProfiles)
        {
            await ExecuteAsync(connection, tx, sqlServer ? @"
                MERGE VIRTUAL_FILTER_ACCESS_PROFILES WITH (HOLDLOCK) AS t
                USING (SELECT @tenant AS tenant_id, @name AS name) AS s ON t.tenant_id = s.tenant_id AND t.name = s.name
                WHEN MATCHED THEN UPDATE SET
                    definition_json = @json, definition_hash = @hash,
                    managed_path = @path, managed_commit = @commit, updated_by = @by, updated_at = @at
                WHEN NOT MATCHED THEN INSERT (id, tenant_id, name, definition_json, definition_hash, managed_path, managed_commit, updated_by, updated_at)
                    VALUES (@id, @tenant, @name, @json, @hash, @path, @commit, @by, @at);" : @"
                INSERT INTO VIRTUAL_FILTER_ACCESS_PROFILES (id, tenant_id, name, definition_json, definition_hash, managed_path, managed_commit, updated_by, updated_at)
                VALUES (@id, @tenant, @name, @json, @hash, @path, @commit, @by, @at)
                ON CONFLICT (tenant_id, name) DO UPDATE SET
                    definition_json = excluded.definition_json, definition_hash = excluded.definition_hash,
                    managed_path = excluded.managed_path, managed_commit = excluded.managed_commit,
                    updated_by = excluded.updated_by, updated_at = excluded.updated_at;",
                [
                    ("@id", profile.Id.ToString()),
                    ("@tenant", profile.TenantId.Value),
                    ("@name", profile.Name),
                    ("@json", JsonSerializer.Serialize(ProfileToDto(profile))),
                    ("@hash", profile.ComputeDefinitionHash()),
                    ("@path", profile.ManagedBy?.Path),
                    ("@commit", profile.ManagedBy?.Commit),
                    ("@by", profile.UpdatedBy),
                    ("@at", profile.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))
                ], ct).ConfigureAwait(false);
        }

        foreach (var (tenant, name) in changes.DeleteProfiles)
        {
            await ExecuteAsync(connection, tx, "DELETE FROM VIRTUAL_FILTER_ACCESS_PROFILES WHERE tenant_id = @tenant AND name = @name;",
                [("@tenant", tenant.Value), ("@name", name)], ct).ConfigureAwait(false);
        }

        foreach (var (tenant, name) in changes.DeleteFilters)
        {
            await ExecuteAsync(connection, tx, "DELETE FROM VIRTUAL_FILTERS WHERE tenant_id = @tenant AND name = @name;",
                [("@tenant", tenant.Value), ("@name", name)], ct).ConfigureAwait(false);
        }

        if (changes.ExpectedGeneration is null)
        {
            await ExecuteAsync(connection, tx, "UPDATE VIRTUAL_FILTER_GENERATION SET generation = generation + 1 WHERE id = 1;", [], ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction tx, string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, sql, parameters);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> ReadGenerationAsync(DbConnection connection, DbTransaction? tx, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, "SELECT generation FROM VIRTUAL_FILTER_GENERATION WHERE id = 1;", []);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private readonly record struct Stamp(DateTimeOffset? Value);

    private static DbCommand Command(DbConnection connection, DbTransaction? tx, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            if (value is Stamp stamp)
            {
                cmd.AddBoundTimestamp(name, stamp.Value);
                continue;
            }

            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private static FilterDto FilterToDto(VirtualFilter filter) => new(
        filter.Structured == null ? null : Dto(filter.Structured.From),
        filter.Structured?.FromAlias,
        filter.Structured?.Joins.Select(j => new JoinDto(Dto(j.Table), j.Alias, j.LeftColumn, j.RightColumn)).ToList(),
        filter.Structured?.Where.Select(w => new ConditionDto(w.Column, w.Operator, w.Value)).ToList(),
        filter.KeyColumns.ToList(),
        filter.ValidFromColumn,
        filter.ValidToColumn,
        filter.Supersedes.ToList(),
        filter.Sql,
        filter.Sql == null ? null : filter.SqlTargetColumns.ToList(),
        filter.Status,
        filter.CreatedBy?.Value,
        filter.ApprovedBy?.Value,
        filter.ApprovedAt,
        filter.Draft == null ? null : FilterToDto(filter.Draft),
        filter.PendingDeletion,
        filter.DeletionRequestedBy?.Value,
        filter.Parameters.Count == 0 ? null : filter.Parameters.Select(p => new ParameterDto(p.Name, p.Type, p.IsList, p.MaxValues)).ToList(),
        filter.EvaluationMode == RowPolicyEvaluationMode.Live ? null : filter.EvaluationMode,
        filter.KeySetMaxAge,
        filter.KeySetMaxKeys,
        filter.ReferencedContainers.Count == 0 ? null : filter.ReferencedContainers.Select(r => new RefContainerDto(r.Table, r.Container)).ToList());

    private static ProfileDto ProfileToDto(VirtualFilterAccessProfile profile) => new(
        profile.GranteeType,
        profile.GranteeSid?.Value,
        profile.Scope,
        profile.Uncovered,
        profile.Bindings.Select(b => new BindingDto(
            b.FilterName,
            b.TargetPattern,
            b.ObjectKinds,
            b.TimeColumn,
            b.ColumnMap?.ToDictionary(p => p.Key, p => p.Value),
            b.Strategy,
            b.PushdownFormat,
            b.MaxPushdownKeys)).ToList(),
        profile.Status,
        profile.CreatedBy?.Value,
        profile.ApprovedBy?.Value,
        profile.ApprovedAt,
        profile.Draft == null ? null : ProfileToDto(profile.Draft),
        profile.PendingDeletion,
        profile.DeletionRequestedBy?.Value);

    private static VirtualFilter DtoToFilter(FilterDto dto, Guid id, TenantId tenantId, string name, string source) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = name,
        Source = source,
        Structured = dto.From == null ? null : new StructuredFilterDefinition
        {
            From = Identifier(dto.From),
            FromAlias = dto.FromAlias ?? string.Empty,
            Joins = (dto.Joins ?? []).Select(j => new FilterJoin(Identifier(j.Table), j.Alias, j.Left, j.Right)).ToList(),
            Where = (dto.Where ?? []).Select(w => new FilterCondition(w.Column, w.Operator, w.Value)).ToList()
        },
        Sql = dto.Sql,
        SqlTargetColumns = dto.SqlTargetColumns ?? [],
        KeyColumns = dto.KeyColumns ?? [],
        ValidFromColumn = dto.ValidFrom,
        ValidToColumn = dto.ValidTo,
        Supersedes = dto.Supersedes ?? [],
        Status = dto.Status ?? FilterApprovalStatus.Active,
        CreatedBy = string.IsNullOrWhiteSpace(dto.CreatedBy) ? (Sid?)null : new Sid(dto.CreatedBy!),
        ApprovedBy = string.IsNullOrWhiteSpace(dto.ApprovedBy) ? (Sid?)null : new Sid(dto.ApprovedBy!),
        ApprovedAt = dto.ApprovedAt,
        Draft = dto.Draft == null ? null : DtoToFilter(dto.Draft, Guid.NewGuid(), tenantId, name, source),
        PendingDeletion = dto.PendingDeletion,
        DeletionRequestedBy = string.IsNullOrWhiteSpace(dto.DeletionRequestedBy) ? (Sid?)null : new Sid(dto.DeletionRequestedBy!),
        Parameters = (dto.Parameters ?? []).Select(p => new RowPolicyParameter(p.Name, p.Type, p.IsList, p.MaxValues)).ToList(),
        EvaluationMode = dto.EvaluationMode ?? RowPolicyEvaluationMode.Live,
        KeySetMaxAge = dto.KeySetMaxAge,
        KeySetMaxKeys = dto.KeySetMaxKeys,
        ReferencedContainers = (dto.ReferencedContainers ?? []).Select(r => new ReferencedTableContainer(r.Table, r.Container)).ToList()
    };

    private static VirtualFilterAccessProfile DtoToProfile(ProfileDto dto, Guid id, TenantId tenantId, string name) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = name,
        GranteeType = dto.GranteeType,
        GranteeSid = string.IsNullOrWhiteSpace(dto.GranteeSid) ? (Sid?)null : new Sid(dto.GranteeSid!),
        Scope = dto.Scope,
        Uncovered = dto.Uncovered,
        Bindings = (dto.Bindings ?? []).Select(b => new FilterBinding
        {
            FilterName = b.Filter,
            TargetPattern = b.Target,
            ObjectKinds = b.ObjectKinds,
            TimeColumn = b.TimeColumn,
            ColumnMap = b.ColumnMap,
            Strategy = b.Strategy ?? VirtualFilterExecutionStrategy.Adaptive,
            PushdownFormat = b.PushdownFormat,
            MaxPushdownKeys = b.MaxPushdownKeys ?? 100
        }).ToList(),
        Status = dto.Status ?? FilterApprovalStatus.Active,
        CreatedBy = string.IsNullOrWhiteSpace(dto.CreatedBy) ? (Sid?)null : new Sid(dto.CreatedBy!),
        ApprovedBy = string.IsNullOrWhiteSpace(dto.ApprovedBy) ? (Sid?)null : new Sid(dto.ApprovedBy!),
        ApprovedAt = dto.ApprovedAt,
        Draft = dto.Draft == null ? null : DtoToProfile(dto.Draft, Guid.NewGuid(), tenantId, name),
        PendingDeletion = dto.PendingDeletion,
        DeletionRequestedBy = string.IsNullOrWhiteSpace(dto.DeletionRequestedBy) ? (Sid?)null : new Sid(dto.DeletionRequestedBy!)
    };

    private static VirtualFilter ReadFilter(DbDataReader reader)
    {
        var dto = JsonSerializer.Deserialize<FilterDto>(reader.GetString(4))
            ?? throw new InvalidOperationException("Virtual filter definition is empty.");
        var id = Guid.Parse(reader.GetString(0));
        var tenantId = new TenantId(reader.GetString(1));
        var name = reader.GetString(2);
        var source = reader.GetString(3);
        var filter = DtoToFilter(dto, id, tenantId, name, source);
        return filter with
        {
            StoredDefinitionHash = reader.GetString(5),
            ManagedBy = ReadManagedBy(reader, 6),
            UpdatedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)
        };
    }

    private static VirtualFilterAccessProfile ReadProfile(DbDataReader reader)
    {
        var dto = JsonSerializer.Deserialize<ProfileDto>(reader.GetString(3))
            ?? throw new InvalidOperationException("Access profile definition is empty.");
        if (!Enum.IsDefined(dto.GranteeType) || (dto.Draft is { } draft && !Enum.IsDefined(draft.GranteeType)))
        {
            // Never skipped or mapped to another grantee type: the snapshot load fails and the decision that needs it denies.
            throw new InvalidOperationException("Access profile definition has an unsupported grantee type.");
        }

        var id = Guid.Parse(reader.GetString(0));
        var tenantId = new TenantId(reader.GetString(1));
        var name = reader.GetString(2);
        var profile = DtoToProfile(dto, id, tenantId, name);
        return profile with
        {
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
