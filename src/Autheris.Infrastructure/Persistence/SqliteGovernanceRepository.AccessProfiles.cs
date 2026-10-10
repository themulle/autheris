namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;

public partial class SqliteGovernanceRepository : IAccessProfileRepository
{
    public async Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectAsync(TenantId tenantId, string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var profiles = new Dictionary<string, AccessProfile>(StringComparer.OrdinalIgnoreCase);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT p.profile_id, p.tenant_id, p.name, p.masking_mode, p.target_tables,
                       p.row_filter_predicate, p.justification, p.created_by, p.created_at, p.valid_to,
                       a.expires_at
                FROM ACCESS_PROFILES p
                INNER JOIN ACCESS_PROFILE_ASSIGNMENTS a
                    ON p.tenant_id = a.tenant_id AND p.profile_id = a.profile_id
                WHERE p.tenant_id = @tenantId AND a.subject = @subject;
            ";
            cmd.Parameters.AddWithValue("@tenantId", tenantId.Value);
            cmd.Parameters.AddWithValue("@subject", subject);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var validToStr = reader.IsDBNull(9) ? null : reader.GetString(9);
                DateTimeOffset? validTo = !string.IsNullOrWhiteSpace(validToStr)
                    ? DateTimeOffset.Parse(validToStr, CultureInfo.InvariantCulture)
                    : null;

                if (validTo != null && validTo.Value <= now)
                {
                    continue;
                }

                var expiresToStr = reader.IsDBNull(10) ? null : reader.GetString(10);
                DateTimeOffset? expiresAt = !string.IsNullOrWhiteSpace(expiresToStr)
                    ? DateTimeOffset.Parse(expiresToStr, CultureInfo.InvariantCulture)
                    : null;

                if (expiresAt != null && expiresAt.Value <= now)
                {
                    continue;
                }

                var profileId = reader.GetString(0);
                if (!profiles.ContainsKey(profileId))
                {
                    var p = ReadAccessProfileRow(reader, tenantId);
                    p.AssignedSubjects.Add(subject);
                    profiles[profileId] = p;
                }
            }

            return [.. profiles.Values];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profiles = new List<AccessProfile>();
            var profileMap = new Dictionary<string, AccessProfile>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT profile_id, tenant_id, name, masking_mode, target_tables,
                           row_filter_predicate, justification, created_by, created_at, valid_to
                    FROM ACCESS_PROFILES
                    WHERE tenant_id = @tenantId
                    ORDER BY name;
                ";
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value);

                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var p = ReadAccessProfileRow(reader, tenantId);
                    profiles.Add(p);
                    profileMap[p.ProfileId] = p;
                }
            }

            if (profileMap.Count > 0)
            {
                using var assignCmd = _connection.CreateCommand();
                assignCmd.CommandText = @"
                    SELECT profile_id, subject
                    FROM ACCESS_PROFILE_ASSIGNMENTS
                    WHERE tenant_id = @tenantId;
                ";
                assignCmd.Parameters.AddWithValue("@tenantId", tenantId.Value);

                using var assignReader = await assignCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await assignReader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var pId = assignReader.GetString(0);
                    var subj = assignReader.GetString(1);
                    if (profileMap.TryGetValue(pId, out var profile) && !profile.AssignedSubjects.Contains(subj))
                    {
                        profile.AssignedSubjects.Add(subj);
                    }
                }
            }

            return profiles;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AccessProfile? profile = null;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT profile_id, tenant_id, name, masking_mode, target_tables,
                           row_filter_predicate, justification, created_by, created_at, valid_to
                    FROM ACCESS_PROFILES
                    WHERE tenant_id = @tenantId AND profile_id = @profileId;
                ";
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value);
                cmd.Parameters.AddWithValue("@profileId", profileId);

                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    profile = ReadAccessProfileRow(reader, tenantId);
                }
            }

            if (profile != null)
            {
                using var assignCmd = _connection.CreateCommand();
                assignCmd.CommandText = @"
                    SELECT subject
                    FROM ACCESS_PROFILE_ASSIGNMENTS
                    WHERE tenant_id = @tenantId AND profile_id = @profileId;
                ";
                assignCmd.Parameters.AddWithValue("@tenantId", tenantId.Value);
                assignCmd.Parameters.AddWithValue("@profileId", profileId);

                using var assignReader = await assignCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await assignReader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var subj = assignReader.GetString(0);
                    if (!profile.AssignedSubjects.Contains(subj))
                    {
                        profile.AssignedSubjects.Add(subj);
                    }
                }
            }

            return profile;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO ACCESS_PROFILES (
                        profile_id, tenant_id, name, masking_mode, target_tables,
                        row_filter_predicate, justification, created_by, created_at, valid_to
                    ) VALUES (
                        @profileId, @tenantId, @name, @maskingMode, @targetTables,
                        @rowFilterPredicate, @justification, @createdBy, @createdAt, @validTo
                    )
                    ON CONFLICT (tenant_id, profile_id) DO UPDATE SET
                        name = excluded.name,
                        masking_mode = excluded.masking_mode,
                        target_tables = excluded.target_tables,
                        row_filter_predicate = excluded.row_filter_predicate,
                        justification = excluded.justification,
                        created_by = excluded.created_by,
                        created_at = excluded.created_at,
                        valid_to = excluded.valid_to;
                ";
                cmd.Parameters.AddWithValue("@profileId", profile.ProfileId);
                cmd.Parameters.AddWithValue("@tenantId", profile.TenantId.Value);
                cmd.Parameters.AddWithValue("@name", profile.Name);
                cmd.Parameters.AddWithValue("@maskingMode", profile.MaskingMode.ToString());
                cmd.Parameters.AddWithValue("@targetTables", JsonSerializer.Serialize(profile.TargetTables ?? []));
                cmd.Parameters.AddWithValue("@rowFilterPredicate", (object?)profile.RowFilterPredicate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@justification", (object?)profile.Justification ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@createdBy", (object?)profile.CreatedBy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@createdAt", profile.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@validTo", profile.ValidTo.HasValue
                    ? profile.ValidTo.Value.ToString("O", CultureInfo.InvariantCulture)
                    : DBNull.Value);

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Replace assignments
            using (var delCmd = _connection.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = @"
                    DELETE FROM ACCESS_PROFILE_ASSIGNMENTS
                    WHERE tenant_id = @tenantId AND profile_id = @profileId;
                ";
                delCmd.Parameters.AddWithValue("@tenantId", profile.TenantId.Value);
                delCmd.Parameters.AddWithValue("@profileId", profile.ProfileId);
                await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (profile.AssignedSubjects != null && profile.AssignedSubjects.Count > 0)
            {
                foreach (var subject in profile.AssignedSubjects)
                {
                    if (string.IsNullOrWhiteSpace(subject)) continue;

                    using var insCmd = _connection.CreateCommand();
                    insCmd.Transaction = tx;
                    insCmd.CommandText = @"
                        INSERT INTO ACCESS_PROFILE_ASSIGNMENTS (
                            profile_id, tenant_id, subject, subject_type, assigned_at, expires_at
                        ) VALUES (
                            @profileId, @tenantId, @subject, 'User', @assignedAt, @expiresAt
                        );
                    ";
                    insCmd.Parameters.AddWithValue("@profileId", profile.ProfileId);
                    insCmd.Parameters.AddWithValue("@tenantId", profile.TenantId.Value);
                    insCmd.Parameters.AddWithValue("@subject", subject.Trim());
                    insCmd.Parameters.AddWithValue("@assignedAt", profile.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                    insCmd.Parameters.AddWithValue("@expiresAt", profile.ValidTo.HasValue
                        ? profile.ValidTo.Value.ToString("O", CultureInfo.InvariantCulture)
                        : DBNull.Value);

                    await insCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            tx.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();

            using var delAssignCmd = _connection.CreateCommand();
            delAssignCmd.Transaction = tx;
            delAssignCmd.CommandText = @"
                DELETE FROM ACCESS_PROFILE_ASSIGNMENTS
                WHERE tenant_id = @tenantId AND profile_id = @profileId;
            ";
            delAssignCmd.Parameters.AddWithValue("@tenantId", tenantId.Value);
            delAssignCmd.Parameters.AddWithValue("@profileId", profileId);
            await delAssignCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var delProfileCmd = _connection.CreateCommand();
            delProfileCmd.Transaction = tx;
            delProfileCmd.CommandText = @"
                DELETE FROM ACCESS_PROFILES
                WHERE tenant_id = @tenantId AND profile_id = @profileId;
            ";
            delProfileCmd.Parameters.AddWithValue("@tenantId", tenantId.Value);
            delProfileCmd.Parameters.AddWithValue("@profileId", profileId);
            var rows = await delProfileCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            tx.Commit();
            return rows > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static AccessProfile ReadAccessProfileRow(SqliteDataReader reader, TenantId tenantId)
    {
        var profileId = reader.GetString(0);
        var tenantStr = reader.GetString(1);
        var name = reader.GetString(2);
        var maskingModeStr = reader.GetString(3);
        var targetTablesJson = reader.GetString(4);
        var rowFilter = reader.IsDBNull(5) ? null : reader.GetString(5);
        var justification = reader.IsDBNull(6) ? null : reader.GetString(6);
        var createdBy = reader.IsDBNull(7) ? null : reader.GetString(7);
        var createdAtStr = reader.GetString(8);
        var validToStr = reader.IsDBNull(9) ? null : reader.GetString(9);

        var mode = Enum.TryParse<MaskingPolicyMode>(maskingModeStr, true, out var m) ? m : MaskingPolicyMode.Default;
        var tables = !string.IsNullOrWhiteSpace(targetTablesJson)
            ? JsonSerializer.Deserialize<List<string>>(targetTablesJson) ?? []
            : [];
        var createdAt = DateTimeOffset.Parse(createdAtStr, CultureInfo.InvariantCulture);
        DateTimeOffset? validTo = !string.IsNullOrWhiteSpace(validToStr)
            ? DateTimeOffset.Parse(validToStr, CultureInfo.InvariantCulture)
            : null;

        return new AccessProfile
        {
            ProfileId = profileId,
            TenantId = TenantId.TryParse(tenantStr, out var t) ? t : tenantId,
            Name = name,
            MaskingMode = mode,
            TargetTables = tables,
            RowFilterPredicate = rowFilter,
            Justification = justification,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            ValidTo = validTo,
            AssignedSubjects = []
        };
    }
}
