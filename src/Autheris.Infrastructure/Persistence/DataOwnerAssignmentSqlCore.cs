namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Engine-neutral write path of table owners for the dbt governance import (issue #393 part b). All identifier comparisons run in memory
/// (trimmed, FormC, case-insensitive; <see cref="OwnerIdentifier"/>), never through a database collation, so SQLite, PostgreSQL and SQL Server agree
/// (I393B-SC-05). The class gets an open connection and transaction and never commits, rolls back, opens or disposes either; the dialect
/// repository commits only a successful non-dry run. Existing DATA_OWNERS rows are never updated or deleted; every statement binds its values as
/// parameters. Error codes name no identifier value (I393B-SC-01).
/// </summary>
internal static class DataOwnerAssignmentSqlCore
{
    /// <summary>Prefix of TABLE_OWNERS ids created by the import; replace deletes only rows that carry it (I393B-SC-07).</summary>
    public const string ProvenancePrefix = "dbt:";

    public const string SelectOwners = "SELECT id, ad_sid, ad_account, display_name, email, is_active FROM DATA_OWNERS";

    public const string SelectTableOwners = "SELECT id, data_owner_id, owner_role FROM TABLE_OWNERS WHERE table_id = @tid";

    public const string InsertOwner = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                VALUES (@id, @sid, @account, @name, @email, 1)";

    public const string InsertTableOwner = "INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role) VALUES (@id, @tid, @oid, @role)";

    public const string DeleteTableOwner = "DELETE FROM TABLE_OWNERS WHERE id = @id AND table_id = @tid";

    private sealed class OwnerRow
    {
        public required string Id { get; init; }
        public required string Sid { get; init; }
        public required string Account { get; init; }
        public required string DisplayName { get; init; }
        public required string Email { get; init; }
        public required bool IsActive { get; init; }
        public bool Persisted { get; set; } = true;

        public IEnumerable<string> Identifiers() => new[] { Sid, Account, Email }.Where(v => OwnerIdentifier.Normalize(v).Length > 0);
    }

    private sealed record TableOwnerRow(string Id, string OwnerId, string Role);

    public static async Task<DataOwnerAssignmentResult> RunAsync(
        DbConnection conn, DbTransaction tx, string selectTableIdSql, DataOwnerAssignmentRequest request, CancellationToken ct)
    {
        var owners = await ReadOwnersAsync(conn, tx, ct).ConfigureAwait(false);
        var changes = new List<DataOwnerChange>();
        var created = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tableSpec in request.Tables)
        {
            var table = tableSpec.Table;
            var tableName = table.ToString();
            var tableId = await ReadTableIdAsync(conn, tx, selectTableIdSql, table, ct).ConfigureAwait(false);
            if (tableId is null)
            {
                return DataOwnerAssignmentResult.Failure("dbt_owner_table_not_found", tableName);
            }

            var existing = await ReadTableOwnersAsync(conn, tx, tableId, ct).ConfigureAwait(false);
            var desired = new HashSet<string>(StringComparer.Ordinal);
            var linked = existing.Select(e => e.OwnerId).ToHashSet(StringComparer.Ordinal);
            var inserted = 0;

            foreach (var spec in tableSpec.Owners)
            {
                var (row, error) = Resolve(owners, spec, created, request.MaxNewOwners, tableName, changes, table);
                if (error is not null)
                {
                    return DataOwnerAssignmentResult.Failure(error, tableName);
                }

                if (!row!.Persisted)
                {
                    await ExecuteAsync(conn, tx, InsertOwner, ct,
                        ("@id", row.Id), ("@sid", row.Sid), ("@account", row.Account), ("@name", row.DisplayName), ("@email", row.Email)).ConfigureAwait(false);
                    row.Persisted = true;
                }

                desired.Add(row.Id);
                if (!linked.Add(row.Id))
                {
                    continue; // a row for (table, owner) exists whatever its provenance: no duplicate
                }

                var role = spec.Role;
                await ExecuteAsync(conn, tx, InsertTableOwner, ct,
                    ("@id", AssignmentId(tableId, row.Id, role)), ("@tid", tableId), ("@oid", row.Id), ("@role", role)).ConfigureAwait(false);
                changes.Add(new DataOwnerChange(DataOwnerChangeKind.OwnerAssigned, table, row.Id, row.Sid, role));
                inserted++;
            }

            if (request.Replace)
            {
                var stale = existing
                    .Where(e => e.Id.StartsWith(ProvenancePrefix, StringComparison.Ordinal) && !desired.Contains(e.OwnerId))
                    .ToList();
                var removedAll = stale.Count > 0 && existing.Count - stale.Count + inserted == 0;
                foreach (var removal in stale)
                {
                    await ExecuteAsync(conn, tx, DeleteTableOwner, ct, ("@id", removal.Id), ("@tid", tableId)).ConfigureAwait(false);
                    var ownerSid = owners.FirstOrDefault(o => o.Id == removal.OwnerId)?.Sid ?? string.Empty;
                    changes.Add(new DataOwnerChange(DataOwnerChangeKind.OwnerRemoved, table, removal.OwnerId, ownerSid, removal.Role, removedAll));
                }
            }
        }

        return new DataOwnerAssignmentResult(true, changes);
    }

    private static (OwnerRow? Row, string? Error) Resolve(
        List<OwnerRow> owners, DataOwnerSpec spec, HashSet<string> created, int maxNewOwners, string tableName,
        List<DataOwnerChange> changes, TableIdentifier table)
    {
        if (spec.LookupOnly)
        {
            var matches = owners.Where(o => o.Identifiers().Any(i => OwnerIdentifier.AreSame(i, spec.Sid))).ToList();
            if (matches.Count != 1)
            {
                return (null, "dbt_owner_lookup_failed");
            }

            return matches[0].IsActive ? (matches[0], null) : (null, "dbt_owner_inactive");
        }

        var bySid = owners.Where(o => OwnerIdentifier.AreSame(o.Sid, spec.Sid)).ToList();
        if (bySid.Count > 1)
        {
            return (null, "dbt_owner_identifier_collision");
        }

        var specIdentifiers = new List<string> { spec.Sid };
        if (spec.Account is not null)
        {
            specIdentifiers.Add(spec.Account);
        }

        if (spec.Email is not null)
        {
            specIdentifiers.Add(spec.Email);
        }

        if (bySid.Count == 1)
        {
            var row = bySid[0];
            if (!row.IsActive)
            {
                return (null, "dbt_owner_inactive");
            }

            if ((spec.Account is not null && !OwnerIdentifier.AreSame(spec.Account, row.Account)) ||
                (spec.Email is not null && !OwnerIdentifier.AreSame(spec.Email, row.Email)) ||
                (spec.DisplayName is not null && !string.Equals(OwnerIdentifier.Normalize(spec.DisplayName), OwnerIdentifier.Normalize(row.DisplayName), StringComparison.Ordinal)))
            {
                return (null, "dbt_owner_conflict");
            }

            return CollidesWithOther(owners, row, specIdentifiers) ? (null, "dbt_owner_identifier_collision") : (row, null);
        }

        if (string.IsNullOrWhiteSpace(spec.DisplayName))
        {
            return (null, "dbt_owner_display_name_required");
        }

        var account = spec.Account ?? spec.Sid;
        specIdentifiers.Add(account);
        if (CollidesWithOther(owners, null, specIdentifiers))
        {
            return (null, "dbt_owner_identifier_collision");
        }

        var id = OwnerId(spec.Sid);
        if (created.Count >= maxNewOwners)
        {
            return (null, "dbt_owner_cap_exceeded");
        }

        var created_ = new OwnerRow
        {
            Id = id,
            Sid = OwnerIdentifier.Normalize(spec.Sid),
            Account = OwnerIdentifier.Normalize(account),
            DisplayName = OwnerIdentifier.Normalize(spec.DisplayName),
            Email = OwnerIdentifier.Normalize(spec.Email),
            IsActive = true,
            Persisted = false
        };
        owners.Add(created_);
        created.Add(id);
        changes.Add(new DataOwnerChange(DataOwnerChangeKind.OwnerCreated, table, id, created_.Sid, spec.Role));
        return (created_, null);
    }

    private static async Task<List<OwnerRow>> ReadOwnersAsync(DbConnection conn, DbTransaction tx, CancellationToken ct)
    {
        var rows = new List<OwnerRow>();
        await using var cmd = Command(conn, tx, SelectOwners);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new OwnerRow
            {
                Id = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                Sid = reader.GetString(1),
                Account = reader.GetString(2),
                DisplayName = reader.GetString(3),
                Email = reader.GetString(4),
                IsActive = Convert.ToInt32(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture) == 1
            });
        }

        return rows;
    }

    private static async Task<string?> ReadTableIdAsync(DbConnection conn, DbTransaction tx, string sql, TableIdentifier table, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, sql);
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<TableOwnerRow>> ReadTableOwnersAsync(DbConnection conn, DbTransaction tx, string tableId, CancellationToken ct)
    {
        var rows = new List<TableOwnerRow>();
        await using var cmd = Command(conn, tx, SelectTableOwners);
        cmd.AddBound("@tid", tableId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new TableOwnerRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static async Task ExecuteAsync(DbConnection conn, DbTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Command(conn, tx, sql);
        foreach (var (name, value) in parameters)
        {
            cmd.AddBound(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static DbCommand Command(DbConnection conn, DbTransaction tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private static bool CollidesWithOther(List<OwnerRow> owners, OwnerRow? self, IReadOnlyList<string> identifiers) =>
        owners.Any(o => !ReferenceEquals(o, self) &&
                        o.Identifiers().Any(existing => identifiers.Any(candidate => OwnerIdentifier.AreSame(existing, candidate))));

    public static string OwnerId(string sid)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("dbt-data-owner:" + OwnerIdentifier.Normalize(sid).ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    public static string AssignmentId(string tableId, string ownerId, string role)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tableId}|{ownerId}|{role}"));
        return ProvenancePrefix + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
