using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

internal enum AccessProfileDialect
{
    Sqlite,
    PostgreSql,
    SqlServer
}

/// <summary>A connection for one store operation; disposing releases it (and, for SQLite, the write gate).</summary>
internal sealed class AccessProfileStoreScope(DbConnection connection, Func<ValueTask> dispose) : IAsyncDisposable
{
    public DbConnection Connection { get; } = connection;

    public ValueTask DisposeAsync() => dispose();
}

/// <summary>
/// WP-SC-03: the persisted access profiles (<c>ACCESS_PROFILES</c>, <c>ACCESS_PROFILE_ASSIGNMENTS</c>) shared by the three governance repositories.
/// The SQL differs only in the upsert statements and the timestamp representation. A store failure always propagates (never an empty result),
/// so a database outage denies. Which assignments are effective, and in what order, is decided by <see cref="AccessProfileAssignmentRules"/>.
/// </summary>
internal sealed class AccessProfileStore(
    AccessProfileDialect dialect,
    Func<bool, CancellationToken, Task<AccessProfileStoreScope>> open,
    TimeProvider? timeProvider = null)
{
    private const int MaxTermsPerStatement = 900;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private const string ProfileColumns = "p.profile_id, p.tenant_id, p.name, p.masking_mode, p.target_tables, p.row_filter_predicate, p.justification, p.created_by, p.created_at, p.valid_to";
    private const string AssignmentColumns = "a.subject, a.subject_type, a.assigned_at, a.expires_at, a.created_by, a.approved_by, a.approved_at, a.justification, a.kind, a.renewal_count";

    // ---------------------------------------------------------------- reads

    public async Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectsAsync(
        TenantId tenantId,
        IReadOnlyCollection<string> users,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> roles,
        CancellationToken ct)
    {
        var terms = new List<(AccessProfileSubjectType Type, string Norm)>();
        var seen = new HashSet<(AccessProfileSubjectType, string)>();
        void Add(AccessProfileSubjectType type, IReadOnlyCollection<string>? values)
        {
            foreach (var v in values ?? [])
            {
                if (!string.IsNullOrWhiteSpace(v) && seen.Add((type, ConsentSubjectFilter.Normalize(v.Trim()))))
                {
                    terms.Add((type, ConsentSubjectFilter.Normalize(v.Trim())));
                }
            }
        }

        Add(AccessProfileSubjectType.User, users);
        Add(AccessProfileSubjectType.Group, groups);
        Add(AccessProfileSubjectType.Role, roles);
        if (terms.Count == 0)
        {
            return [];
        }

        var wanted = terms.Select(t => (t.Type, t.Norm)).ToHashSet();
        var now = _time.GetUtcNow();
        var byId = new Dictionary<string, AccessProfile>(StringComparer.Ordinal);
        await using var scope = await open(false, ct).ConfigureAwait(false);

        for (var offset = 0; offset < terms.Count; offset += MaxTermsPerStatement)
        {
            var chunk = terms.Skip(offset).Take(MaxTermsPerStatement).ToList();
            await using var cmd = scope.Connection.CreateCommand();
            var sql = new StringBuilder($"SELECT {ProfileColumns}, {AssignmentColumns} FROM ACCESS_PROFILES p JOIN ACCESS_PROFILE_ASSIGNMENTS a " +
                                        "ON p.tenant_id = a.tenant_id AND p.profile_id = a.profile_id WHERE p.tenant_id = @tenantId AND (");
            AddParam(cmd, "@tenantId", tenantId.Value);
            var index = 0;
            var first = true;
            foreach (var group in chunk.GroupBy(t => t.Type))
            {
                sql.Append(first ? "" : " OR ").Append($"(a.subject_type = '{group.Key}' AND (a.subject_norm IN (");
                sql.Append(string.Join(",", group.Select(t =>
                {
                    var name = "@k" + index++;
                    AddParam(cmd, name, t.Norm);
                    return name;
                })));
                sql.Append(")))");
                first = false;
            }

            sql.Append(')');
            cmd.CommandText = sql.ToString();

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var profile = ReadProfile(reader, 0);
                var assignment = ReadAssignment(reader, 10);
                if (!wanted.Contains((assignment.SubjectType, ConsentSubjectFilter.Normalize(assignment.Subject.Trim()))) ||
                    !AccessProfileAssignmentRules.IsEffective(profile, assignment, now))
                {
                    continue;
                }

                if (!byId.TryGetValue(profile.ProfileId, out var existing))
                {
                    existing = profile;
                    byId[profile.ProfileId] = existing;
                }

                if (!existing.Assignments.Any(a => a.SubjectType == assignment.SubjectType &&
                                                   string.Equals(a.Subject, assignment.Subject, StringComparison.OrdinalIgnoreCase)))
                {
                    existing.Assignments.Add(assignment);
                }
            }
        }

        return AccessProfileAssignmentRules.Order(byId.Values);
    }

    public async Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        return await LoadProfilesAsync(scope.Connection, tenantId, null, ct).ConfigureAwait(false);
    }

    public async Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        return (await LoadProfilesAsync(scope.Connection, tenantId, profileId, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    private async Task<List<AccessProfile>> LoadProfilesAsync(DbConnection conn, TenantId tenantId, string? profileId, CancellationToken ct, DbTransaction? tx = null)
    {
        var profiles = new List<AccessProfile>();
        var map = new Dictionary<string, AccessProfile>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT {ProfileColumns.Replace("p.", "")} FROM ACCESS_PROFILES WHERE tenant_id = @tenantId" +
                              (profileId != null ? " AND profile_id = @profileId" : "") + " ORDER BY name";
            AddParam(cmd, "@tenantId", tenantId.Value);
            if (profileId != null)
            {
                AddParam(cmd, "@profileId", profileId);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var p = ReadProfile(reader, 0);
                profiles.Add(p);
                map[p.ProfileId] = p;
            }
        }

        if (map.Count == 0)
        {
            return profiles;
        }

        await using var assignCmd = conn.CreateCommand();
        assignCmd.Transaction = tx;
        assignCmd.CommandText = $"SELECT profile_id, {AssignmentColumns.Replace("a.", "")} FROM ACCESS_PROFILE_ASSIGNMENTS WHERE tenant_id = @tenantId" +
                                (profileId != null ? " AND profile_id = @profileId" : "");
        AddParam(assignCmd, "@tenantId", tenantId.Value);
        if (profileId != null)
        {
            AddParam(assignCmd, "@profileId", profileId);
        }

        await using var assignReader = await assignCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await assignReader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (map.TryGetValue(assignReader.GetString(0), out var profile))
            {
                var assignment = ReadAssignment(assignReader, 1);
                profile.Assignments.Add(assignment);
            }
        }

        return profiles;
    }

    // --------------------------------------------------------------- writes

    public async Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);
        if (!Enum.IsDefined(profile.MaskingMode))
        {
            // An undefined mode would be stored as its number ("7") and could never be applied as intended.
            throw new ArgumentOutOfRangeException(nameof(profile), "The access profile masking mode is not a defined value.");
        }

        var now = _time.GetUtcNow();
        await using var scope = await open(true, ct).ConfigureAwait(false);
        var conn = scope.Connection;
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await ExecAsync(conn, tx, ProfileUpsertSql, ct,
            ("@profileId", profile.ProfileId), ("@tenantId", profile.TenantId.Value), ("@name", profile.Name),
            ("@maskingMode", profile.MaskingMode.ToString()), ("@targetTables", JsonSerializer.Serialize(profile.TargetTables ?? [])),
            ("@rowFilterPredicate", profile.RowFilterPredicate), ("@justification", profile.Justification),
            ("@createdBy", profile.CreatedBy ?? "unknown"), ("@createdAt", Ts(profile.CreatedAt)), ("@validTo", Ts(profile.ValidTo))).ConfigureAwait(false);

        // O-5d: the typed assignments are authoritative (validated, period restarted now); an empty list clears every stored assignment.
        var desired = new Dictionary<(AccessProfileSubjectType, string), AccessProfileAssignment>();
        foreach (var explicitAssignment in profile.Assignments)
        {
            var a = explicitAssignment with { AssignedAt = now, ApprovedAt = explicitAssignment.ApprovedBy is null ? null : explicitAssignment.ApprovedAt ?? now };
            AccessProfileAssignmentRules.ValidateForAssignment(profile, a, now);
            desired[(a.SubjectType, ConsentSubjectFilter.Normalize(a.Subject.Trim()))] = a;
        }

        var existing = (await LoadProfilesAsync(conn, profile.TenantId, profile.ProfileId, ct, tx).ConfigureAwait(false))
            .FirstOrDefault()?.Assignments ?? [];
        foreach (var old in existing.Where(e => !desired.ContainsKey((e.SubjectType, ConsentSubjectFilter.Normalize(e.Subject.Trim())))))
        {
            await DeleteAssignmentAsync(conn, tx, profile.TenantId, profile.ProfileId, old.SubjectType, old.Subject, ct).ConfigureAwait(false);
        }

        foreach (var a in desired.Values)
        {
            await UpsertAssignmentAsync(conn, tx, profile.TenantId, profile.ProfileId, a, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var tx = await scope.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await ExecAsync(scope.Connection, tx, "DELETE FROM ACCESS_PROFILE_ASSIGNMENTS WHERE tenant_id = @tenantId AND profile_id = @profileId", ct,
            ("@tenantId", tenantId.Value), ("@profileId", profileId)).ConfigureAwait(false);
        var rows = await ExecAsync(scope.Connection, tx, "DELETE FROM ACCESS_PROFILES WHERE tenant_id = @tenantId AND profile_id = @profileId", ct,
            ("@tenantId", tenantId.Value), ("@profileId", profileId)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<AccessProfileAssignment> AssignProfileAsync(TenantId tenantId, string profileId, AccessProfileAssignment assignment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var now = _time.GetUtcNow();
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var tx = await scope.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (profile, existing) = await LoadAssignmentAsync(scope.Connection, tx, tenantId, profileId, assignment.SubjectType, assignment.Subject, ct).ConfigureAwait(false);
        if (profile is null)
        {
            throw new KeyNotFoundException($"Access profile '{profileId}' does not exist.");
        }

        if (existing is not null && existing.Kind != assignment.Kind)
        {
            throw new AccessProfileAssignmentRejectedException(AccessProfileAssignmentRules.KindConflict, "An assignment of another kind already exists for this subject.");
        }

        var stored = assignment with
        {
            AssignedAt = now, Subject = assignment.Subject.Trim(), RenewalCount = existing?.RenewalCount ?? 0,
            ApprovedAt = assignment.ApprovedBy is null ? null : assignment.ApprovedAt ?? now
        };
        AccessProfileAssignmentRules.ValidateForAssignment(profile, stored, now);
        await UpsertAssignmentAsync(scope.Connection, tx, tenantId, profileId, stored, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return stored;
    }

    public async Task<AccessProfileAssignment> ApproveAssignmentAsync(
        TenantId tenantId, string profileId, AccessProfileSubjectType type, string subject, string approvedBy, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var tx = await scope.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (profile, existing) = await LoadAssignmentAsync(scope.Connection, tx, tenantId, profileId, type, subject, ct).ConfigureAwait(false);
        if (profile is null || existing is null)
        {
            throw new KeyNotFoundException($"Assignment of '{subject}' to access profile '{profileId}' does not exist.");
        }

        AccessProfileAssignmentRules.ValidateApprover(profile, existing, approvedBy);
        if (existing.ApprovedBy != null)
        {
            throw new AccessProfileAssignmentRejectedException("ALREADY_APPROVED", "The assignment already has an approver.");
        }

        var approved = existing with { ApprovedBy = approvedBy.Trim(), ApprovedAt = now };
        await UpsertAssignmentAsync(scope.Connection, tx, tenantId, profileId, approved, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return approved;
    }

    public async Task<AccessProfileAssignment> RenewAssignmentAsync(
        TenantId tenantId, string profileId, AccessProfileSubjectType type, string subject, DateTimeOffset newExpiresAt, string approvedBy, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var tx = await scope.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (profile, existing) = await LoadAssignmentAsync(scope.Connection, tx, tenantId, profileId, type, subject, ct).ConfigureAwait(false);
        if (profile is null || existing is null)
        {
            throw new KeyNotFoundException($"Assignment of '{subject}' to access profile '{profileId}' does not exist.");
        }

        if (existing.Kind == AccessProfileAssignmentKind.BreakGlass)
        {
            throw new AccessProfileAssignmentRejectedException(AccessProfileAssignmentRules.BreakGlassNotRenewable, "A break-glass exception cannot be renewed.");
        }

        AccessProfileAssignmentRules.ValidateApprover(profile, existing, approvedBy);
        var renewed = existing with
        {
            AssignedAt = now, ExpiresAt = newExpiresAt, ApprovedBy = approvedBy.Trim(), ApprovedAt = now, RenewalCount = existing.RenewalCount + 1
        };
        AccessProfileAssignmentRules.ValidateForAssignment(profile, renewed, now);
        await UpsertAssignmentAsync(scope.Connection, tx, tenantId, profileId, renewed, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return renewed;
    }

    public async Task<bool> RemoveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType type, string subject, CancellationToken ct)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var tx = await scope.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var rows = await DeleteAssignmentAsync(scope.Connection, tx, tenantId, profileId, type, subject, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    private async Task<(AccessProfile? Profile, AccessProfileAssignment? Assignment)> LoadAssignmentAsync(
        DbConnection conn, DbTransaction tx, TenantId tenantId, string profileId, AccessProfileSubjectType type, string subject, CancellationToken ct)
    {
        var profile = (await LoadProfilesAsync(conn, tenantId, profileId, ct, tx).ConfigureAwait(false)).FirstOrDefault();
        var normalized = ConsentSubjectFilter.Normalize(subject.Trim());
        var assignment = profile?.Assignments.FirstOrDefault(a => a.SubjectType == type &&
                                                                   ConsentSubjectFilter.Normalize(a.Subject.Trim()) == normalized);
        return (profile, assignment);
    }

    private Task<int> UpsertAssignmentAsync(DbConnection conn, DbTransaction tx, TenantId tenantId, string profileId, AccessProfileAssignment a, CancellationToken ct) =>
        ExecAsync(conn, tx, AssignmentUpsertSql, ct,
            ("@profileId", profileId), ("@tenantId", tenantId.Value), ("@subject", a.Subject.Trim()), ("@subjectType", a.SubjectType.ToString()),
            ("@subjectNorm", ConsentSubjectFilter.Normalize(a.Subject.Trim())), ("@assignedAt", Ts(a.AssignedAt)), ("@expiresAt", Ts(a.ExpiresAt)),
            ("@createdBy", a.CreatedBy), ("@approvedBy", a.ApprovedBy), ("@approvedAt", Ts(a.ApprovedAt)), ("@justification", a.Justification),
            ("@kind", a.Kind.ToString()), ("@renewalCount", a.RenewalCount));

    private Task<int> DeleteAssignmentAsync(DbConnection conn, DbTransaction tx, TenantId tenantId, string profileId, AccessProfileSubjectType type, string subject, CancellationToken ct) =>
        ExecAsync(conn, tx,
            // PERS-06: assignments are keyed by the normalised subject, so a different spelling removes the same row.
            "DELETE FROM ACCESS_PROFILE_ASSIGNMENTS WHERE tenant_id = @tenantId AND profile_id = @profileId AND subject_type = @subjectType AND subject_norm = @subjectNorm", ct,
            ("@tenantId", tenantId.Value), ("@profileId", profileId), ("@subjectType", type.ToString()), ("@subjectNorm", ConsentSubjectFilter.Normalize(subject.Trim())));

    // ------------------------------------------------------------- dialect

    private string ProfileUpsertSql => dialect == AccessProfileDialect.SqlServer
        ? @"MERGE ACCESS_PROFILES WITH (HOLDLOCK) AS t
            USING (SELECT @profileId AS profile_id, @tenantId AS tenant_id) AS s ON t.tenant_id = s.tenant_id AND t.profile_id = s.profile_id
            WHEN MATCHED THEN UPDATE SET name = @name, masking_mode = @maskingMode, target_tables = @targetTables, row_filter_predicate = @rowFilterPredicate,
                justification = @justification, created_by = @createdBy, created_at = @createdAt, valid_to = @validTo
            WHEN NOT MATCHED THEN INSERT (profile_id, tenant_id, name, masking_mode, target_tables, row_filter_predicate, justification, created_by, created_at, valid_to)
                VALUES (@profileId, @tenantId, @name, @maskingMode, @targetTables, @rowFilterPredicate, @justification, @createdBy, @createdAt, @validTo);"
        : @"INSERT INTO ACCESS_PROFILES (profile_id, tenant_id, name, masking_mode, target_tables, row_filter_predicate, justification, created_by, created_at, valid_to)
            VALUES (@profileId, @tenantId, @name, @maskingMode, @targetTables, @rowFilterPredicate, @justification, @createdBy, @createdAt, @validTo)
            ON CONFLICT (tenant_id, profile_id) DO UPDATE SET name = excluded.name, masking_mode = excluded.masking_mode, target_tables = excluded.target_tables,
                row_filter_predicate = excluded.row_filter_predicate, justification = excluded.justification, created_by = excluded.created_by,
                created_at = excluded.created_at, valid_to = excluded.valid_to";

    private string AssignmentUpsertSql => dialect == AccessProfileDialect.SqlServer
        ? @"MERGE ACCESS_PROFILE_ASSIGNMENTS WITH (HOLDLOCK) AS t
            USING (SELECT @tenantId AS tenant_id, @profileId AS profile_id, @subjectType AS subject_type, @subjectNorm AS subject_norm) AS s
              ON t.tenant_id = s.tenant_id AND t.profile_id = s.profile_id AND t.subject_type = s.subject_type AND t.subject_norm = s.subject_norm
            WHEN MATCHED THEN UPDATE SET assigned_at = @assignedAt, expires_at = @expiresAt, created_by = @createdBy,
                approved_by = @approvedBy, approved_at = @approvedAt, justification = @justification, kind = @kind, renewal_count = @renewalCount
            WHEN NOT MATCHED THEN INSERT (profile_id, tenant_id, subject, subject_type, subject_norm, assigned_at, expires_at, created_by, approved_by, approved_at, justification, kind, renewal_count)
                VALUES (@profileId, @tenantId, @subject, @subjectType, @subjectNorm, @assignedAt, @expiresAt, @createdBy, @approvedBy, @approvedAt, @justification, @kind, @renewalCount);"
        : @"INSERT INTO ACCESS_PROFILE_ASSIGNMENTS (profile_id, tenant_id, subject, subject_type, subject_norm, assigned_at, expires_at, created_by, approved_by, approved_at, justification, kind, renewal_count)
            VALUES (@profileId, @tenantId, @subject, @subjectType, @subjectNorm, @assignedAt, @expiresAt, @createdBy, @approvedBy, @approvedAt, @justification, @kind, @renewalCount)
            ON CONFLICT (tenant_id, profile_id, subject_type, subject_norm) DO UPDATE SET assigned_at = excluded.assigned_at,
                expires_at = excluded.expires_at, created_by = excluded.created_by, approved_by = excluded.approved_by, approved_at = excluded.approved_at,
                justification = excluded.justification, kind = excluded.kind, renewal_count = excluded.renewal_count";

    /// <summary>The database value of a timestamp: ISO-8601 text on SQLite, a UTC <see cref="DateTimeOffset"/> elsewhere.</summary>
    private object Ts(DateTimeOffset? value) => value is not { } v
        ? DBNull.Value
        : dialect == AccessProfileDialect.Sqlite ? v.ToString("O", CultureInfo.InvariantCulture) : v.ToUniversalTime();

    private DateTimeOffset? ReadTs(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (dialect == AccessProfileDialect.Sqlite)
        {
            var text = reader.GetString(ordinal);
            return string.IsNullOrWhiteSpace(text) ? null : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        }

        return reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    internal AccessProfile ReadProfile(DbDataReader reader, int o)
    {
        // Unknown or undefined (e.g. a numeric "7", which Enum.TryParse accepts) mode: most restrictive.
        var mode = Enum.TryParse<MaskingPolicyMode>(reader.GetString(o + 3), true, out var m) && Enum.IsDefined(m) ? m : MaskingPolicyMode.Strict;
        var targets = reader.IsDBNull(o + 4) || string.IsNullOrWhiteSpace(reader.GetString(o + 4))
            ? []
            : JsonSerializer.Deserialize<List<string>>(reader.GetString(o + 4)) ?? [];
        return new AccessProfile
        {
            ProfileId = reader.GetString(o),
            // DEF-SC-14: an unparsable stored tenant never becomes the caller's tenant (that would expose a foreign or corrupt row); both loading
            // queries are tenant-filtered, so such a row is an integrity failure: refuse with a governed code and no echo.
            TenantId = TenantId.TryParse(reader.GetString(o + 1), out var t) ? t : throw new AccessProfileTenantIntegrityException(),
            Name = reader.GetString(o + 2),
            MaskingMode = mode,
            TargetTables = targets,
            RowFilterPredicate = reader.IsDBNull(o + 5) ? null : reader.GetString(o + 5),
            Justification = reader.IsDBNull(o + 6) ? null : reader.GetString(o + 6),
            CreatedBy = reader.IsDBNull(o + 7) ? null : reader.GetString(o + 7),
            CreatedAt = ReadTs(reader, o + 8) ?? DateTimeOffset.UtcNow,
            ValidTo = ReadTs(reader, o + 9),
            Assignments = []
        };
    }

    private AccessProfileAssignment ReadAssignment(DbDataReader reader, int o) => new()
    {
        Subject = reader.GetString(o),
        SubjectType = Enum.TryParse<AccessProfileSubjectType>(reader.GetString(o + 1), true, out var st)
            ? st
            : throw new InvalidDataException("Unknown access profile subject type; refusing to interpret the assignment."),
        AssignedAt = ReadTs(reader, o + 2) ?? DateTimeOffset.UtcNow,
        ExpiresAt = ReadTs(reader, o + 3),
        CreatedBy = reader.IsDBNull(o + 4) ? null : reader.GetString(o + 4),
        ApprovedBy = reader.IsDBNull(o + 5) ? null : reader.GetString(o + 5),
        ApprovedAt = ReadTs(reader, o + 6),
        Justification = reader.IsDBNull(o + 7) ? null : reader.GetString(o + 7),
        Kind = Enum.TryParse<AccessProfileAssignmentKind>(reader.GetString(o + 8), true, out var k) ? k : AccessProfileAssignmentKind.BreakGlass,
        RenewalCount = reader.GetInt32(o + 9)
    };

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        SqlCoreBinding.GuardTenantParameter(name, value);
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static async Task<int> ExecAsync(DbConnection conn, DbTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            AddParam(cmd, name, value);
        }

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
