namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Caller attribute registry and service sets (PLAN-GOV-129 C-3, C-5): storage shared by the three governance repositories (portable SQL,
/// named parameters). Every four-eyes step is one transaction that reads the row, compares the hash the approver reviewed and refuses an
/// approver equal to the creator, so the store itself never activates an unapproved definition (BC-129-10). Each successful write
/// increments the generation once.
/// </summary>
internal static class CallerAttributeStore
{
    private const int Active = (int)FilterApprovalStatus.Active;
    private const int Pending = (int)FilterApprovalStatus.PendingApproval;
    private const int Rejected = (int)FilterApprovalStatus.Rejected;

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
            SELECT id, tenant_id, name, definition_json, status, created_by, approved_by, approved_at,
                   draft_definition_json, draft_created_by, pending_deletion, deletion_requested_by, updated_at
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
                    reader.IsDBNull(8) ? null : FromJson(reader.GetString(8), Guid.NewGuid(), tenant, FilterApprovalStatus.PendingApproval),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetInt32(10) != 0,
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    ParseTime(reader.GetString(12))));
            }
        }

        var sets = new List<ServiceAttributeSetRecord>();
        await using (var cmd = Command(connection, tx, @"
            SELECT id, tenant_id, application_id, attribute_name, values_json, status, created_by, approved_by, approved_at,
                   draft_values_json, draft_created_by, updated_at
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
                    reader.IsDBNull(9) ? null : Values(reader.GetString(9)),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    ParseTime(reader.GetString(11))));
            }
        }

        if (ownedTx != null)
        {
            await ownedTx.CommitAsync(ct).ConfigureAwait(false);
        }

        return new CallerAttributeStoreSnapshot(generation, attributes, sets);
    }

    public static Task<long> GetGenerationAsync(DbConnection connection, CancellationToken ct) => ReadGenerationAsync(connection, null, ct);

    public static async Task<CallerAttributeChangeResult> SubmitDefinitionAsync(DbConnection connection, CallerAttributeDefinition def, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var existing = await ReadRowAsync(connection, tx, def.Tenant.Value, def.Name, ct).ConfigureAwait(false);
        var json = ToJson(def);
        var hash = def.ComputeDefinitionHash();
        var stamp = Time(at);
        if (existing is null)
        {
            await ExecuteAsync(connection, tx, @"
                INSERT INTO CALLER_ATTRIBUTES (id, tenant_id, name, definition_json, definition_hash, status, created_by, updated_by, updated_at)
                VALUES (@id, @tenant, @name, @json, @hash, @status, @by, @by, @at);",
                [("@id", def.Id.ToString()), ("@tenant", def.Tenant.Value), ("@name", def.Name), ("@json", json), ("@hash", hash), ("@status", Pending), ("@by", actor), ("@at", stamp)], ct).ConfigureAwait(false);
        }
        else if (existing.Status == Active)
        {
            await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTES SET draft_definition_json = @json, draft_definition_hash = @hash, draft_created_by = @by,
                    pending_deletion = 0, deletion_requested_by = NULL, updated_by = @by, updated_at = @at
                WHERE tenant_id = @tenant AND name = @name;",
                [("@json", json), ("@hash", hash), ("@by", actor), ("@at", stamp), ("@tenant", def.Tenant.Value), ("@name", def.Name)], ct).ConfigureAwait(false);
        }
        else
        {
            // Never approved (pending or rejected): the first version is replaced; there is no last-known-good to protect.
            await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTES SET definition_json = @json, definition_hash = @hash, status = @status, created_by = @by,
                    approved_by = NULL, approved_at = NULL, updated_by = @by, updated_at = @at
                WHERE tenant_id = @tenant AND name = @name;",
                [("@json", json), ("@hash", hash), ("@status", Pending), ("@by", actor), ("@at", stamp), ("@tenant", def.Tenant.Value), ("@name", def.Name)], ct).ConfigureAwait(false);
        }

        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return CallerAttributeChangeResult.Applied;
    }

    public static async Task<CallerAttributeChangeResult> RequestDeletionAsync(DbConnection connection, TenantId tenant, string name, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var existing = await ReadRowAsync(connection, tx, tenant.Value, name, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return CallerAttributeChangeResult.NotFound;
        }

        if (existing.Status == Active)
        {
            await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTES SET pending_deletion = 1, deletion_requested_by = @by, updated_by = @by, updated_at = @at
                WHERE tenant_id = @tenant AND name = @name;",
                [("@by", actor), ("@at", Time(at)), ("@tenant", tenant.Value), ("@name", name)], ct).ConfigureAwait(false);
        }
        else
        {
            // A never-approved definition grants nothing: removing it needs no second person.
            await ExecuteAsync(connection, tx, "DELETE FROM CALLER_ATTRIBUTES WHERE tenant_id = @tenant AND name = @name;",
                [("@tenant", tenant.Value), ("@name", name)], ct).ConfigureAwait(false);
        }

        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return CallerAttributeChangeResult.Applied;
    }

    public static async Task<CallerAttributeChangeResult> ApproveDefinitionAsync(
        DbConnection connection, TenantId tenant, string name, string approver, string expectedHash, DateTimeOffset at, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await ReadRowAsync(connection, tx, tenant.Value, name, ct).ConfigureAwait(false);
        if (row is null)
        {
            return CallerAttributeChangeResult.NotFound;
        }

        var stamp = Time(at);
        if (row.PendingDeletion)
        {
            var guard = Guard(row.DefinitionHash, row.DeletionRequestedBy, approver, expectedHash);
            if (guard != CallerAttributeChangeResult.Applied)
            {
                return guard;
            }

            await ExecuteAsync(connection, tx, "DELETE FROM CALLER_ATTRIBUTES WHERE tenant_id = @tenant AND name = @name AND definition_hash = @hash;",
                [("@tenant", tenant.Value), ("@name", name), ("@hash", row.DefinitionHash)], ct).ConfigureAwait(false);
        }
        else if (row.DraftJson is not null)
        {
            var guard = Guard(row.DraftHash ?? string.Empty, row.DraftCreatedBy, approver, expectedHash);
            if (guard != CallerAttributeChangeResult.Applied)
            {
                return guard;
            }

            var affected = await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTES SET definition_json = draft_definition_json, definition_hash = draft_definition_hash,
                    created_by = draft_created_by, approved_by = @approver, approved_at = @at,
                    draft_definition_json = NULL, draft_definition_hash = NULL, draft_created_by = NULL,
                    updated_by = @approver, updated_at = @at
                WHERE tenant_id = @tenant AND name = @name AND draft_definition_hash = @hash;",
                [("@approver", approver), ("@at", stamp), ("@tenant", tenant.Value), ("@name", name), ("@hash", row.DraftHash ?? string.Empty)], ct).ConfigureAwait(false);
            if (affected != 1)
            {
                return CallerAttributeChangeResult.HashMismatch;
            }
        }
        else if (row.Status != Active)
        {
            var guard = Guard(row.DefinitionHash, row.CreatedBy, approver, expectedHash);
            if (guard != CallerAttributeChangeResult.Applied)
            {
                return guard;
            }

            var affected = await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTES SET status = @active, approved_by = @approver, approved_at = @at, updated_by = @approver, updated_at = @at
                WHERE tenant_id = @tenant AND name = @name AND definition_hash = @hash AND status <> @active;",
                [("@active", Active), ("@approver", approver), ("@at", stamp), ("@tenant", tenant.Value), ("@name", name), ("@hash", row.DefinitionHash)], ct).ConfigureAwait(false);
            if (affected != 1)
            {
                return CallerAttributeChangeResult.HashMismatch;
            }
        }
        else
        {
            return CallerAttributeChangeResult.NothingPending;
        }

        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return CallerAttributeChangeResult.Applied;
    }

    public static async Task<CallerAttributeChangeResult> RejectDefinitionAsync(DbConnection connection, TenantId tenant, string name, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await ReadRowAsync(connection, tx, tenant.Value, name, ct).ConfigureAwait(false);
        if (row is null)
        {
            return CallerAttributeChangeResult.NotFound;
        }

        string sql;
        if (row.PendingDeletion || row.DraftJson is not null)
        {
            sql = @"UPDATE CALLER_ATTRIBUTES SET pending_deletion = 0, deletion_requested_by = NULL,
                        draft_definition_json = NULL, draft_definition_hash = NULL, draft_created_by = NULL, updated_by = @by, updated_at = @at
                    WHERE tenant_id = @tenant AND name = @name;";
        }
        else if (row.Status == Pending)
        {
            sql = "UPDATE CALLER_ATTRIBUTES SET status = " + Rejected.ToString(CultureInfo.InvariantCulture) + ", updated_by = @by, updated_at = @at WHERE tenant_id = @tenant AND name = @name;";
        }
        else
        {
            return CallerAttributeChangeResult.NothingPending;
        }

        await ExecuteAsync(connection, tx, sql, [("@by", actor), ("@at", Time(at)), ("@tenant", tenant.Value), ("@name", name)], ct).ConfigureAwait(false);
        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return CallerAttributeChangeResult.Applied;
    }

    // ---- service sets --------------------------------------------------------------------------------------------------------

    public static async Task<CallerAttributeChangeResult> SubmitServiceSetAsync(
        DbConnection connection, TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at, CancellationToken ct)
    {
        var app = CallerAttributeNameRules.CanonicalApplicationId(applicationId);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var existing = await ReadSetAsync(connection, tx, tenant.Value, app, attributeName, ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(values);
        var stamp = Time(at);
        if (existing is null)
        {
            var set = new ServiceAttributeSet(Guid.NewGuid(), tenant, app, attributeName, values, FilterApprovalStatus.PendingApproval);
            await ExecuteAsync(connection, tx, @"
                INSERT INTO CALLER_ATTRIBUTE_SERVICE_SETS (id, tenant_id, application_id, attribute_name, values_json, values_hash, status, created_by, updated_at)
                VALUES (@id, @tenant, @app, @name, @json, @hash, @status, @by, @at);",
                [("@id", set.Id.ToString()), ("@tenant", tenant.Value), ("@app", app), ("@name", attributeName), ("@json", json), ("@hash", set.ComputeValuesHash()), ("@status", Pending), ("@by", actor), ("@at", stamp)], ct).ConfigureAwait(false);
        }
        else if (existing.Status == Active)
        {
            await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTE_SERVICE_SETS SET draft_values_json = @json, draft_created_by = @by, updated_at = @at
                WHERE tenant_id = @tenant AND application_id = @app AND attribute_name = @name;",
                [("@json", json), ("@by", actor), ("@at", stamp), ("@tenant", tenant.Value), ("@app", app), ("@name", attributeName)], ct).ConfigureAwait(false);
        }
        else
        {
            var set = new ServiceAttributeSet(Guid.Empty, tenant, app, attributeName, values, FilterApprovalStatus.PendingApproval);
            await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTE_SERVICE_SETS SET values_json = @json, values_hash = @hash, status = @status, created_by = @by,
                    approved_by = NULL, approved_at = NULL, updated_at = @at
                WHERE tenant_id = @tenant AND application_id = @app AND attribute_name = @name;",
                [("@json", json), ("@hash", set.ComputeValuesHash()), ("@status", Pending), ("@by", actor), ("@at", stamp), ("@tenant", tenant.Value), ("@app", app), ("@name", attributeName)], ct).ConfigureAwait(false);
        }

        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return CallerAttributeChangeResult.Applied;
    }

    public static async Task<CallerAttributeChangeResult> ApproveServiceSetAsync(
        DbConnection connection, TenantId tenant, string applicationId, string attributeName, string approver, string expectedHash, DateTimeOffset at, CancellationToken ct)
    {
        var app = CallerAttributeNameRules.CanonicalApplicationId(applicationId);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await ReadSetAsync(connection, tx, tenant.Value, app, attributeName, ct).ConfigureAwait(false);
        if (row is null)
        {
            return CallerAttributeChangeResult.NotFound;
        }

        var stamp = Time(at);
        if (row.DraftValuesJson is not null)
        {
            var draftValues = Values(row.DraftValuesJson);
            var draftHash = new ServiceAttributeSet(Guid.Empty, tenant, app, attributeName, draftValues, FilterApprovalStatus.PendingApproval).ComputeValuesHash();
            var guard = Guard(draftHash, row.DraftCreatedBy, approver, expectedHash);
            if (guard != CallerAttributeChangeResult.Applied)
            {
                return guard;
            }

            await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTE_SERVICE_SETS SET values_json = draft_values_json, values_hash = @hash, created_by = draft_created_by,
                    approved_by = @approver, approved_at = @at, draft_values_json = NULL, draft_created_by = NULL, updated_at = @at
                WHERE tenant_id = @tenant AND application_id = @app AND attribute_name = @name AND draft_values_json = @draft;",
                [("@hash", draftHash), ("@approver", approver), ("@at", stamp), ("@tenant", tenant.Value), ("@app", app), ("@name", attributeName), ("@draft", row.DraftValuesJson)], ct).ConfigureAwait(false);
        }
        else if (row.Status != Active)
        {
            var guard = Guard(row.ValuesHash, row.CreatedBy, approver, expectedHash);
            if (guard != CallerAttributeChangeResult.Applied)
            {
                return guard;
            }

            await ExecuteAsync(connection, tx, @"
                UPDATE CALLER_ATTRIBUTE_SERVICE_SETS SET status = @active, approved_by = @approver, approved_at = @at, updated_at = @at
                WHERE tenant_id = @tenant AND application_id = @app AND attribute_name = @name AND values_hash = @hash;",
                [("@active", Active), ("@approver", approver), ("@at", stamp), ("@tenant", tenant.Value), ("@app", app), ("@name", attributeName), ("@hash", row.ValuesHash)], ct).ConfigureAwait(false);
        }
        else
        {
            return CallerAttributeChangeResult.NothingPending;
        }

        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return CallerAttributeChangeResult.Applied;
    }

    public static async Task<CallerAttributeChangeResult> RejectServiceSetAsync(
        DbConnection connection, TenantId tenant, string applicationId, string attributeName, string actor, DateTimeOffset at, CancellationToken ct)
    {
        var app = CallerAttributeNameRules.CanonicalApplicationId(applicationId);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await ReadSetAsync(connection, tx, tenant.Value, app, attributeName, ct).ConfigureAwait(false);
        if (row is null)
        {
            return CallerAttributeChangeResult.NotFound;
        }

        string sql;
        if (row.DraftValuesJson is not null)
        {
            sql = "UPDATE CALLER_ATTRIBUTE_SERVICE_SETS SET draft_values_json = NULL, draft_created_by = NULL, updated_at = @at WHERE tenant_id = @tenant AND application_id = @app AND attribute_name = @name;";
        }
        else if (row.Status == Pending)
        {
            sql = "UPDATE CALLER_ATTRIBUTE_SERVICE_SETS SET status = " + Rejected.ToString(CultureInfo.InvariantCulture) + ", updated_at = @at WHERE tenant_id = @tenant AND application_id = @app AND attribute_name = @name;";
        }
        else
        {
            return CallerAttributeChangeResult.NothingPending;
        }

        await ExecuteAsync(connection, tx, sql, [("@at", Time(at)), ("@tenant", tenant.Value), ("@app", app), ("@name", attributeName)], ct).ConfigureAwait(false);
        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return CallerAttributeChangeResult.Applied;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------

    /// <summary>Hash the approver saw must equal the stored one; the approver must not be the creator (immutable subject key, case-insensitive like <see cref="Sid"/>).</summary>
    private static CallerAttributeChangeResult Guard(string storedHash, string? creator, string approver, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(approver) || string.IsNullOrWhiteSpace(creator) ||
            string.Equals(approver, creator, StringComparison.OrdinalIgnoreCase))
        {
            return CallerAttributeChangeResult.SameSubject;
        }

        return string.Equals(storedHash, expectedHash, StringComparison.Ordinal) ? CallerAttributeChangeResult.Applied : CallerAttributeChangeResult.HashMismatch;
    }

    private sealed record Row(
        int Status, string CreatedBy, string DefinitionHash, string? DraftJson, string? DraftHash, string? DraftCreatedBy, bool PendingDeletion, string? DeletionRequestedBy);

    private sealed record SetRow(int Status, string CreatedBy, string ValuesHash, string? DraftValuesJson, string? DraftCreatedBy);

    private static async Task<Row?> ReadRowAsync(DbConnection connection, DbTransaction tx, string tenant, string name, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, @"
            SELECT status, created_by, definition_hash, draft_definition_json, draft_definition_hash, draft_created_by, pending_deletion, deletion_requested_by
            FROM CALLER_ATTRIBUTES WHERE tenant_id = @tenant AND name = @name;", [("@tenant", tenant), ("@name", name)]);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Row(
            reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6) != 0, reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private static async Task<SetRow?> ReadSetAsync(DbConnection connection, DbTransaction tx, string tenant, string app, string name, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, @"
            SELECT status, created_by, values_hash, draft_values_json, draft_created_by
            FROM CALLER_ATTRIBUTE_SERVICE_SETS WHERE tenant_id = @tenant AND application_id = @app AND attribute_name = @name;",
            [("@tenant", tenant), ("@app", app), ("@name", name)]);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new SetRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
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
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }
}
