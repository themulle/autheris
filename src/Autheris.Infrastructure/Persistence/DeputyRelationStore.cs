using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Ownership;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL and SQL Server persistence of externally sourced deputy relations (<c>governance_deputy_relations</c>, issue #1126). Every
/// read and write is bound to a validated, non-reserved tenant. A change set is written with its audit entries in one governance
/// transaction (<see cref="GovernanceRepository.WriteWithAuditAsync{T}"/>): updates and deletes are compare-and-set on the "before"
/// state, and any conflict, storage or audit failure rolls the whole set back.
/// </summary>
public sealed class DeputyRelationStore(GovernanceRepository repository, IGovernanceSqlDialect dialect) : IDeputyRelationRepository
{
    private const string PgTable = "governance_deputy_relations";
    private const string SsTable = "dbo.GOVERNANCE_DEPUTY_RELATIONS";

    private const string Columns =
        "id, tenant_id, source, external_id, principal_ref, principal_lookup, principal_alt_lookup, deputy_ref, deputy_lookup, deputy_alt_lookup, " +
        "valid_from, valid_to, created_by, created_at, updated_by, updated_at";

    private const string InsertValues =
        " (" + Columns + ") VALUES (@id, @tenantId, @source, @externalId, @principalRef, @principalKey, @principalAltKey, @deputyRef, " +
        "@deputyKey, @deputyAltKey, @validFrom, @validTo, @createdBy, @createdAt, @updatedBy, @updatedAt)";

    private const string CasWhere =
        " WHERE tenant_id = @tenantId AND id = @id AND principal_lookup = @beforePrincipalKey AND deputy_lookup = @beforeDeputyKey" +
        " AND valid_from = @beforeFrom AND valid_to = @beforeTo";

    private const string UpdateSet =
        " SET principal_ref = @principalRef, principal_lookup = @principalKey, principal_alt_lookup = @principalAltKey, deputy_ref = @deputyRef," +
        " deputy_lookup = @deputyKey, deputy_alt_lookup = @deputyAltKey, valid_from = @validFrom, valid_to = @validTo, updated_by = @updatedBy," +
        " updated_at = @updatedAt" + CasWhere;

    private readonly GovernanceRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IGovernanceSqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

    private bool IsSqlServer => _dialect is SqlServerGovernanceDialect;

    private string Table => IsSqlServer ? SsTable : PgTable;

    public async Task<IReadOnlyList<DeputyRelation>> ListAsync(TenantId tenant, DeputyRelationFilter filter, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var tenantValue = RequireTenant(tenant);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var where = new StringBuilder(" WHERE tenant_id = @tenantId");
        cmd.AddBound("@tenantId", tenantValue);
        if (filter.Source is not null)
        {
            where.Append(" AND source = @source");
            cmd.AddBound("@source", filter.Source);
        }

        if (filter.DeputyKeys is { } deputyKeys)
        {
            var list = InList(cmd, "dk", deputyKeys);
            where.Append(" AND (deputy_lookup IN (").Append(list).Append(") OR deputy_alt_lookup IN (").Append(list).Append("))");
        }

        if (filter.PrincipalKeys is { } principalKeys)
        {
            var list = InList(cmd, "pk", principalKeys);
            where.Append(" AND (principal_lookup IN (").Append(list).Append(") OR principal_alt_lookup IN (").Append(list).Append("))");
        }

        if (filter.SubjectKeys is { } subjectKeys)
        {
            var list = InList(cmd, "sk", subjectKeys);
            where.Append(" AND (deputy_lookup IN (").Append(list).Append(") OR deputy_alt_lookup IN (").Append(list)
                .Append(") OR principal_lookup IN (").Append(list).Append(") OR principal_alt_lookup IN (").Append(list).Append("))");
        }

        if (filter.ActiveAt is { } at)
        {
            where.Append(" AND valid_from <= @activeAt AND valid_to > @activeAt");
            cmd.AddBound("@activeAt", at.ToUniversalTime(), DbType.DateTimeOffset);
        }

        cmd.AddBound("@max", max);
        cmd.CommandText = IsSqlServer
            ? "SELECT TOP (@max) " + Columns + " FROM " + SsTable + where + " ORDER BY source, external_id"
            : "SELECT " + Columns + " FROM " + PgTable + where + " ORDER BY source, external_id LIMIT @max";

        var result = new List<DeputyRelation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadRow(reader));
        }

        return result;
    }

    public Task<bool> ApplyAsync(TenantId tenant, DeputyRelationChanges changes, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(audit);
        var tenantValue = RequireTenant(tenant);
        if (changes.Inserts.Any(r => r.TenantId != tenant) || changes.Updates.Any(u => u.Before.TenantId != tenant || u.After.TenantId != tenant) ||
            changes.Deletes.Any(r => r.TenantId != tenant) || audit.Any(a => a.TenantId != tenant))
        {
            throw new ArgumentException("Every relation and audit entry of a change set must belong to its tenant.", nameof(changes));
        }

        if (changes.IsEmpty)
        {
            return Task.FromResult(true);
        }

        return _repository.WriteWithAuditAsync((conn, tx) => WriteAsync(conn, tx, tenantValue, changes, ct), static applied => applied, audit, ct);
    }

    private async Task<bool> WriteAsync(DbConnection conn, DbTransaction tx, string tenant, DeputyRelationChanges changes, CancellationToken ct)
    {
        foreach (var before in changes.Deletes)
        {
            await using var cmd = Command(conn, tx, "DELETE FROM " + Table + CasWhere);
            BindCas(cmd, tenant, before);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        foreach (var (before, after) in changes.Updates)
        {
            if (before.Id != after.Id || !string.Equals(before.Source, after.Source, StringComparison.Ordinal) ||
                !string.Equals(before.ExternalId, after.ExternalId, StringComparison.Ordinal))
            {
                return false;
            }

            await using var cmd = Command(conn, tx, "UPDATE " + Table + UpdateSet);
            BindValues(cmd, after);
            BindCas(cmd, tenant, before);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        foreach (var relation in changes.Inserts)
        {
            await using (var exists = Command(conn, tx, "SELECT COUNT(*) FROM " + Table + " WHERE tenant_id = @tenantId AND source = @source AND external_id = @externalId"))
            {
                exists.AddBound("@tenantId", tenant);
                exists.AddBound("@source", relation.Source);
                exists.AddBound("@externalId", relation.ExternalId);
                if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
                {
                    return false;
                }
            }

            await using var cmd = Command(conn, tx, "INSERT INTO " + Table + InsertValues);
            cmd.AddBound("@id", relation.Id.ToString("D"));
            cmd.AddBound("@tenantId", tenant);
            cmd.AddBound("@source", relation.Source);
            cmd.AddBound("@externalId", relation.ExternalId);
            cmd.AddBound("@createdBy", relation.CreatedBy.Value);
            cmd.AddBound("@createdAt", relation.CreatedAt.ToUniversalTime(), DbType.DateTimeOffset);
            BindValues(cmd, relation);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return true;
    }

    private static DbCommand Command(DbConnection conn, DbTransaction tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void BindValues(DbCommand cmd, DeputyRelation r)
    {
        cmd.AddBound("@principalRef", r.PrincipalRef);
        cmd.AddBound("@principalKey", r.PrincipalKey);
        cmd.AddBound("@principalAltKey", r.PrincipalAltKey is null ? DBNull.Value : r.PrincipalAltKey);
        cmd.AddBound("@deputyRef", r.DeputyRef);
        cmd.AddBound("@deputyKey", r.DeputyKey);
        cmd.AddBound("@deputyAltKey", r.DeputyAltKey is null ? DBNull.Value : r.DeputyAltKey);
        cmd.AddBound("@validFrom", r.ValidFrom.ToUniversalTime(), DbType.DateTimeOffset);
        cmd.AddBound("@validTo", r.ValidTo.ToUniversalTime(), DbType.DateTimeOffset);
        cmd.AddBound("@updatedBy", r.UpdatedBy is { } by ? by.Value : DBNull.Value);
        cmd.AddBound("@updatedAt", r.UpdatedAt is { } at ? at.ToUniversalTime() : DBNull.Value, DbType.DateTimeOffset);
    }

    private static void BindCas(DbCommand cmd, string tenant, DeputyRelation before)
    {
        cmd.AddBound("@tenantId", tenant);
        cmd.AddBound("@id", before.Id.ToString("D"));
        cmd.AddBound("@beforePrincipalKey", before.PrincipalKey);
        cmd.AddBound("@beforeDeputyKey", before.DeputyKey);
        cmd.AddBound("@beforeFrom", before.ValidFrom.ToUniversalTime(), DbType.DateTimeOffset);
        cmd.AddBound("@beforeTo", before.ValidTo.ToUniversalTime(), DbType.DateTimeOffset);
    }

    private static string InList(DbCommand cmd, string prefix, IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
        {
            // An empty key set matches nothing.
            return "NULL";
        }

        var names = new List<string>(values.Count);
        var i = 0;
        foreach (var value in values)
        {
            var name = "@" + prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            cmd.AddBound(name, value);
            names.Add(name);
            i++;
        }

        return string.Join(", ", names);
    }

    private static DeputyRelation ReadRow(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        TenantId = StoredTenant.Parse(reader.GetString(1)),
        Source = reader.GetString(2),
        ExternalId = reader.GetString(3),
        PrincipalRef = reader.GetString(4),
        PrincipalKey = reader.GetString(5),
        PrincipalAltKey = ConsentWorkflowSql.ReadOptionalString(reader, 6),
        DeputyRef = reader.GetString(7),
        DeputyKey = reader.GetString(8),
        DeputyAltKey = ConsentWorkflowSql.ReadOptionalString(reader, 9),
        // A missing window bound is read as an empty window (fail closed).
        ValidFrom = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 10) ?? DateTimeOffset.MaxValue,
        ValidTo = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 11) ?? DateTimeOffset.UnixEpoch,
        CreatedBy = new Sid(reader.GetString(12)),
        CreatedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 13) ?? DateTimeOffset.UnixEpoch,
        UpdatedBy = ConsentWorkflowSql.ReadOptionalSid(reader, 14),
        UpdatedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 15)
    };

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

    private static string RequireTenant(TenantId tenant) =>
        TenantId.TryParse(TenantScope.RequireTenant(tenant, GovernanceTable.GovernanceDeputyRelations.ToSqlName()).Value, out var valid)
            ? valid.Value
            : throw new ArgumentException("A valid non-reserved TenantId is required.", nameof(tenant));
}
