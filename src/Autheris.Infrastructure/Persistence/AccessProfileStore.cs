using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

internal enum AccessProfileDialect
{
    PostgreSql,
    SqlServer
}

/// <summary>A connection for one store operation; disposing releases it .</summary>
internal sealed class AccessProfileStoreScope(DbConnection connection, Func<ValueTask> dispose) : IAsyncDisposable
{
    public DbConnection Connection { get; } = connection;

    public ValueTask DisposeAsync() => dispose();
}

/// <summary>
/// the persisted access profiles (<c>ACCESS_PROFILES</c>, <c>ACCESS_PROFILE_ITEMS</c>, <c>ACCESS_PROFILE_CLASSIFICATION_ACCESS</c>, <c>ACCESS_PROFILE_ASSIGNMENTS</c>) shared by the three governance repositories.
/// The SQL differs only in the upsert statements and the timestamp representation. A store failure always propagates (never an empty result),
/// so a database outage denies. Which assignments are effective, and in what order, is decided by <see cref="AccessProfileAssignmentRules"/>.
/// </summary>
internal sealed class AccessProfileStore(
    AccessProfileDialect dialect,
    Func<bool, CancellationToken, Task<AccessProfileStoreScope>> open,
    TimeProvider? timeProvider = null,
    ILogger? logger = null)
{
    private const int MaxTermsPerStatement = 900;
    private const AccessProfileAssignmentKind UnknownAssignmentKind = (AccessProfileAssignmentKind)(-1);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private const string ProfileColumns = "p.profile_id, p.tenant_id, p.name, p.row_scope_json, p.justification, p.created_by, p.created_at, p.valid_to";
    private const int ProfileColumnCount = 8;

    /// <summary>The one classification code a profile carries: its profile-wide masking mode (Strict 0, Default 1, Unmasked 2).</summary>
    internal const string PiiCode = "PII";

    /// <summary>The longest stored table entry (the <c>object_ref</c> column is 512 characters).</summary>
    internal const int MaxTableEntryLength = 512;

    internal const string ItemsSelectSql = "SELECT profile_id, item_no, object_ref FROM ACCESS_PROFILE_ITEMS WHERE tenant_id = @tenantId AND profile_id IN ({0}) ORDER BY profile_id, item_no";
    internal const string LevelsSelectSql = "SELECT profile_id, code, access_level FROM ACCESS_PROFILE_CLASSIFICATION_ACCESS WHERE tenant_id = @tenantId AND profile_id IN ({0})";
    internal const string ItemsDeleteSql = "DELETE FROM ACCESS_PROFILE_ITEMS WHERE tenant_id = @tenantId AND profile_id = @profileId";
    internal const string LevelsDeleteSql = "DELETE FROM ACCESS_PROFILE_CLASSIFICATION_ACCESS WHERE tenant_id = @tenantId AND profile_id = @profileId";
    internal const string ItemInsertSql = "INSERT INTO ACCESS_PROFILE_ITEMS (tenant_id, profile_id, item_no, object_ref) VALUES (@tenantId, @profileId, @itemNo, @objectRef)";
    internal const string LevelInsertSql = "INSERT INTO ACCESS_PROFILE_CLASSIFICATION_ACCESS (tenant_id, profile_id, code, access_level) VALUES (@tenantId, @profileId, @code, @accessLevel)";
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
        var rows = new Dictionary<string, ProfileRow>(StringComparer.Ordinal);
        var pairs = new List<(string ProfileId, AccessProfileAssignment Assignment)>();
        await using var scope = await open(false, ct).ConfigureAwait(false);

        // One snapshot for the profile rows and the item rows, so a concurrent upsert is never seen half applied (old mode with new tables).
        await using var readTx = await scope.Connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);

        for (var offset = 0; offset < terms.Count; offset += MaxTermsPerStatement)
        {
            var chunk = terms.Skip(offset).Take(MaxTermsPerStatement).ToList();
            await using var cmd = scope.Connection.CreateCommand();
            cmd.Transaction = readTx;
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

            sql.Append(") AND a.deactivated_reason IS NULL");
            cmd.CommandText = sql.ToString();

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = ReadProfileRow(reader, 0);
                var assignment = ReadAssignment(reader, ProfileColumnCount);
                if (!wanted.Contains((assignment.SubjectType, ConsentSubjectFilter.Normalize(assignment.Subject.Trim()))))
                {
                    continue;
                }

                rows.TryAdd(row.ProfileId, row);
                pairs.Add((row.ProfileId, assignment));
            }
        }

        var byId = new Dictionary<string, AccessProfile>(StringComparer.Ordinal);
        if (rows.Count > 0)
        {
            // The mode and the table entries come from the item tables (one batched, tenant-filtered query each); a failure throws, so no profile is built from a partial read.
            var details = await LoadProfileDetailsAsync(scope.Connection, tenantId, rows.Keys.ToList(), readTx, ct).ConfigureAwait(false);
            var built = new Dictionary<string, AccessProfile>(StringComparer.Ordinal);
            foreach (var (profileId, assignment) in pairs)
            {
                if (!built.TryGetValue(profileId, out var profile))
                {
                    profile = ToProfile(rows[profileId], details);
                    built[profileId] = profile;
                }

                if (!AccessProfileAssignmentRules.IsEffective(profile, assignment, now))
                {
                    continue;
                }

                byId.TryAdd(profileId, profile);
                if (!profile.Assignments.Any(a => a.SubjectType == assignment.SubjectType &&
                                                  string.Equals(a.Subject, assignment.Subject, StringComparison.OrdinalIgnoreCase)))
                {
                    profile.Assignments.Add(assignment);
                }
            }
        }

        await readTx.CommitAsync(ct).ConfigureAwait(false);
        return AccessProfileAssignmentRules.Order(byId.Values);
    }

    public async Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        return await LoadProfilesSnapshotAsync(scope.Connection, tenantId, null, ct).ConfigureAwait(false);
    }

    public async Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        return (await LoadProfilesSnapshotAsync(scope.Connection, tenantId, profileId, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    private async Task<List<AccessProfile>> LoadProfilesSnapshotAsync(DbConnection conn, TenantId tenantId, string? profileId, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        var profiles = await LoadProfilesAsync(conn, tenantId, profileId, ct, tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return profiles;
    }

    private async Task<List<AccessProfile>> LoadProfilesAsync(DbConnection conn, TenantId tenantId, string? profileId, CancellationToken ct, DbTransaction? tx = null)
    {
        var profiles = new List<AccessProfile>();
        var map = new Dictionary<string, AccessProfile>(StringComparer.Ordinal);
        var rows = new List<ProfileRow>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT {ProfileColumns.Replace("p.", "", StringComparison.Ordinal)} FROM ACCESS_PROFILES WHERE tenant_id = @tenantId" +
                              (profileId != null ? " AND profile_id = @profileId" : "") + " ORDER BY name";
            AddParam(cmd, "@tenantId", tenantId.Value);
            if (profileId != null)
            {
                AddParam(cmd, "@profileId", profileId);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(ReadProfileRow(reader, 0));
            }
        }

        if (rows.Count > 0)
        {
            var details = await LoadProfileDetailsAsync(conn, tenantId, rows.Select(r => r.ProfileId).ToList(), tx, ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                var p = ToProfile(row, details);
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
        assignCmd.CommandText = $"SELECT profile_id, {AssignmentColumns.Replace("a.", "", StringComparison.Ordinal)} FROM ACCESS_PROFILE_ASSIGNMENTS WHERE tenant_id = @tenantId AND deactivated_reason IS NULL" +
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

        var items = DistinctItems(profile.TargetTables);
        var level = ToAccessLevel(profile.MaskingMode);

        var now = _time.GetUtcNow();
        await using var scope = await open(true, ct).ConfigureAwait(false);
        var conn = scope.Connection;
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await ExecAsync(conn, tx, ProfileUpsertSql, ct,
            ("@profileId", profile.ProfileId), ("@tenantId", profile.TenantId.Value), ("@name", profile.Name),
            ("@rowScopeJson", profile.RowScope.Count == 0 ? null : JsonSerializer.Serialize(profile.RowScope)), ("@justification", profile.Justification),
            ("@createdBy", profile.CreatedBy ?? "unknown"), ("@createdAt", Ts(profile.CreatedAt)), ("@validTo", Ts(profile.ValidTo))).ConfigureAwait(false);

        // The table entries and the profile-wide masking level replace the stored rows in this same transaction (the profile row above holds the lock).
        await ReplaceItemsAsync(conn, tx, profile, items, level, ct).ConfigureAwait(false);

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
        await ExecAsync(scope.Connection, tx, ItemsDeleteSql, ct, ("@tenantId", tenantId.Value), ("@profileId", profileId)).ConfigureAwait(false);
        await ExecAsync(scope.Connection, tx, LevelsDeleteSql, ct, ("@tenantId", tenantId.Value), ("@profileId", profileId)).ConfigureAwait(false);
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
            WHEN MATCHED THEN UPDATE SET name = @name, row_scope_json = @rowScopeJson,
                justification = @justification, created_by = @createdBy, created_at = @createdAt, valid_to = @validTo
            WHEN NOT MATCHED THEN INSERT (profile_id, tenant_id, name, row_scope_json, justification, created_by, created_at, valid_to)
                VALUES (@profileId, @tenantId, @name, @rowScopeJson, @justification, @createdBy, @createdAt, @validTo);"
        : @"INSERT INTO ACCESS_PROFILES (profile_id, tenant_id, name, row_scope_json, justification, created_by, created_at, valid_to)
            VALUES (@profileId, @tenantId, @name, @rowScopeJson, @justification, @createdBy, @createdAt, @validTo)
            ON CONFLICT (tenant_id, profile_id) DO UPDATE SET name = excluded.name,
                row_scope_json = excluded.row_scope_json, justification = excluded.justification, created_by = excluded.created_by,
                created_at = excluded.created_at, valid_to = excluded.valid_to";

    private string AssignmentUpsertSql => dialect == AccessProfileDialect.SqlServer
        ? @"MERGE ACCESS_PROFILE_ASSIGNMENTS WITH (HOLDLOCK) AS t
            USING (SELECT @tenantId AS tenant_id, @profileId AS profile_id, @subjectType AS subject_type, @subjectNorm AS subject_norm) AS s
              ON t.tenant_id = s.tenant_id AND t.profile_id = s.profile_id AND t.subject_type = s.subject_type AND t.subject_norm = s.subject_norm
            WHEN MATCHED THEN UPDATE SET assigned_at = @assignedAt, expires_at = @expiresAt, created_by = @createdBy,
                approved_by = @approvedBy, approved_at = @approvedAt, justification = @justification, kind = @kind, renewal_count = @renewalCount, deactivated_reason = NULL
            WHEN NOT MATCHED THEN INSERT (profile_id, tenant_id, subject, subject_type, subject_norm, assigned_at, expires_at, created_by, approved_by, approved_at, justification, kind, renewal_count)
                VALUES (@profileId, @tenantId, @subject, @subjectType, @subjectNorm, @assignedAt, @expiresAt, @createdBy, @approvedBy, @approvedAt, @justification, @kind, @renewalCount);"
        : @"INSERT INTO ACCESS_PROFILE_ASSIGNMENTS (profile_id, tenant_id, subject, subject_type, subject_norm, assigned_at, expires_at, created_by, approved_by, approved_at, justification, kind, renewal_count)
            VALUES (@profileId, @tenantId, @subject, @subjectType, @subjectNorm, @assignedAt, @expiresAt, @createdBy, @approvedBy, @approvedAt, @justification, @kind, @renewalCount)
            ON CONFLICT (tenant_id, profile_id, subject_type, subject_norm) DO UPDATE SET assigned_at = excluded.assigned_at,
                expires_at = excluded.expires_at, created_by = excluded.created_by, approved_by = excluded.approved_by, approved_at = excluded.approved_at,
                justification = excluded.justification, kind = excluded.kind, renewal_count = excluded.renewal_count, deactivated_reason = NULL";

    /// <summary>The database value of a timestamp: a UTC <see cref="DateTimeOffset"/>.</summary>
    private object Ts(DateTimeOffset? value) => value is not { } v
        ? DBNull.Value
        : v.ToUniversalTime();

    private DateTimeOffset? ReadTs(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    /// <summary>The profile columns of a row; the masking mode and table entries come from the item tables (see <see cref="LoadProfileDetailsAsync"/>).</summary>
    internal sealed record ProfileRow(
        string ProfileId, TenantId TenantId, string Name, List<GrantRowScopeEntry> RowScope, string? Justification, string? CreatedBy,
        DateTimeOffset CreatedAt, DateTimeOffset? ValidTo);

    internal ProfileRow ReadProfileRow(DbDataReader reader, int o) => new(
        reader.GetString(o),
        // DEF-SC-14: an unparsable stored tenant never becomes the caller's tenant (that would expose a foreign or corrupt row); both loading
        // queries are tenant-filtered, so such a row is an integrity failure: refuse with a governed code and no echo.
        TenantId.TryParse(reader.GetString(o + 1), out var t) ? t : throw new AccessProfileTenantIntegrityException(),
        reader.GetString(o + 2),
        reader.IsDBNull(o + 3) || string.IsNullOrWhiteSpace(reader.GetString(o + 3))
            ? []
            : JsonSerializer.Deserialize<List<GrantRowScopeEntry>>(reader.GetString(o + 3)) ?? [],
        reader.IsDBNull(o + 4) ? null : reader.GetString(o + 4),
        reader.IsDBNull(o + 5) ? null : reader.GetString(o + 5),
        ReadTs(reader, o + 6) ?? DateTimeOffset.UtcNow,
        ReadTs(reader, o + 7));

    private static AccessProfile ToProfile(ProfileRow row, Dictionary<string, (MaskingPolicyMode Mode, List<string> Tables)> details)
    {
        // A profile without detail rows reads as the most restrictive mode with no tables (it matches nothing and is refused at assignment).
        var (mode, tables) = details.TryGetValue(row.ProfileId, out var d) ? d : (MaskingPolicyMode.Strict, new List<string>());
        return new AccessProfile
        {
            ProfileId = row.ProfileId,
            TenantId = row.TenantId,
            Name = row.Name,
            MaskingMode = mode,
            TargetTables = tables,
            RowScope = row.RowScope,
            Justification = row.Justification,
            CreatedBy = row.CreatedBy,
            CreatedAt = row.CreatedAt,
            ValidTo = row.ValidTo,
            Assignments = []
        };
    }

    /// <summary>Strict 0 (DENY), Default 1 (MASK), Unmasked 2 (CLEAR). An undefined mode is never stored.</summary>
    internal static short ToAccessLevel(MaskingPolicyMode mode) => mode switch
    {
        MaskingPolicyMode.Strict => 0,
        MaskingPolicyMode.Default => 1,
        MaskingPolicyMode.Unmasked => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), "The access profile masking mode is not a defined value.")
    };

    /// <summary>
    /// The masking mode of the stored classification rows of one profile, fail closed: exactly one row with the <c>PII</c> code and a level 0..2 maps to its mode;
    /// a missing row, extra or other rows, duplicates and a level outside 0..2 all resolve to <see cref="MaskingPolicyMode.Strict"/> (never a less restrictive mode).
    /// </summary>
    internal static MaskingPolicyMode ToMaskingMode(IReadOnlyList<(string Code, short Level)> rows)
    {
        if (rows.Count != 1 || !string.Equals(rows[0].Code, PiiCode, StringComparison.Ordinal))
        {
            return MaskingPolicyMode.Strict;
        }

        return rows[0].Level switch
        {
            1 => MaskingPolicyMode.Default,
            2 => MaskingPolicyMode.Unmasked,
            _ => MaskingPolicyMode.Strict
        };
    }

    /// <summary>The table entries stored verbatim in first-occurrence order, duplicates collapsed (set semantics); a null, blank or over-long entry is refused without echoing it.</summary>
    internal static List<string> DistinctItems(IReadOnlyList<string>? tables)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in tables ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry) || entry.Length > MaxTableEntryLength)
            {
                throw new ArgumentException("A table entry of the access profile is blank or longer than 512 characters.", nameof(tables));
            }

            // SQL Server compares without trailing blanks, so the collapse key ignores them too (the stored text stays verbatim).
            if (seen.Add(entry.TrimEnd(' ')))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private async Task ReplaceItemsAsync(DbConnection conn, DbTransaction tx, AccessProfile profile, List<string> items, short level, CancellationToken ct)
    {
        var tenant = profile.TenantId.Value;
        await ExecAsync(conn, tx, ItemsDeleteSql, ct, ("@tenantId", tenant), ("@profileId", profile.ProfileId)).ConfigureAwait(false);
        await ExecAsync(conn, tx, LevelsDeleteSql, ct, ("@tenantId", tenant), ("@profileId", profile.ProfileId)).ConfigureAwait(false);
        await ExecAsync(conn, tx, LevelInsertSql, ct,
            ("@tenantId", tenant), ("@profileId", profile.ProfileId), ("@code", PiiCode), ("@accessLevel", level)).ConfigureAwait(false);
        for (var i = 0; i < items.Count; i++)
        {
            await ExecAsync(conn, tx, ItemInsertSql, ct,
                ("@tenantId", tenant), ("@profileId", profile.ProfileId), ("@itemNo", i + 1), ("@objectRef", items[i])).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The masking mode and table entries of the given profiles: one tenant-filtered query per table and id chunk (no N+1), grouped by profile id.
    /// Any failure throws, so a profile is never built from a partial read.
    /// </summary>
    private async Task<Dictionary<string, (MaskingPolicyMode Mode, List<string> Tables)>> LoadProfileDetailsAsync(
        DbConnection conn, TenantId tenantId, IReadOnlyList<string> profileIds, DbTransaction? tx, CancellationToken ct)
    {
        var levels = new Dictionary<string, List<(string Code, short Level)>>(StringComparer.Ordinal);
        var tables = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var offset = 0; offset < profileIds.Count; offset += MaxTermsPerStatement)
        {
            var chunk = profileIds.Skip(offset).Take(MaxTermsPerStatement).ToList();
            await ReadDetailRowsAsync(conn, tx, tenantId, chunk, LevelsSelectSql, r =>
            {
                var id = r.GetString(0);
                if (!levels.TryGetValue(id, out var list))
                {
                    levels[id] = list = [];
                }

                list.Add((r.GetString(1), r.GetInt16(2)));
            }, ct).ConfigureAwait(false);
            await ReadDetailRowsAsync(conn, tx, tenantId, chunk, ItemsSelectSql, r =>
            {
                var id = r.GetString(0);
                if (!tables.TryGetValue(id, out var list))
                {
                    tables[id] = list = [];
                }

                list.Add(r.GetString(2));
            }, ct).ConfigureAwait(false);
        }

        var result = new Dictionary<string, (MaskingPolicyMode, List<string>)>(StringComparer.Ordinal);
        foreach (var id in profileIds)
        {
            result[id] = (ToMaskingMode(levels.TryGetValue(id, out var l) ? l : []), tables.TryGetValue(id, out var t) ? t : []);
        }

        return result;
    }

    private static async Task ReadDetailRowsAsync(
        DbConnection conn, DbTransaction? tx, TenantId tenantId, List<string> ids, string sqlTemplate, Action<DbDataReader> onRow, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        AddParam(cmd, "@tenantId", tenantId.Value);
        var names = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            names[i] = "@i" + i;
            AddParam(cmd, names[i], ids[i]);
        }

        cmd.CommandText = string.Format(CultureInfo.InvariantCulture, sqlTemplate, string.Join(",", names));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            onRow(reader);
        }
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
        Kind = ReadKind(reader.GetString(o + 8)),
        RenewalCount = reader.GetInt32(o + 9)
    };

    internal AccessProfileAssignmentKind ReadKind(string rawKind)
    {
        if (Enum.TryParse<AccessProfileAssignmentKind>(rawKind, true, out var k) && Enum.IsDefined(k))
        {
            return k;
        }

        logger?.LogWarning("Access profile assignment with an unknown kind was read from storage and ignored (fail closed).");
        return UnknownAssignmentKind;
    }

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
