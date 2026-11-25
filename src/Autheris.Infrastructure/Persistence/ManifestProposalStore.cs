using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Declarative;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL and SQL Server persistence of declarative manifest proposals (<c>governance_manifest_proposals</c>). Every read and write is
/// bound to a validated, non-reserved tenant; a decision is a conditional status update, so the first decision wins.
/// </summary>
public sealed class ManifestProposalStore(IGovernanceSqlDialect dialect) : IManifestProposalRepository
{
    private const string PgTable = "governance_manifest_proposals";
    private const string SsTable = "dbo.GOVERNANCE_MANIFEST_PROPOSALS";

    private const string Columns =
        "id, tenant_id, scope_name, submitted_by, submitted_at, manifest_json, diff_json, state_hash, status, decided_by, decided_at, reject_reason, expires_at";

    private const string InsertValues =
        " (" + Columns + ") VALUES (@id, @tenantId, @scope, @submittedBy, @submittedAt, @manifest, @diff, @stateHash, @status, NULL, NULL, NULL, @expiresAt)";

    private const string InsertPgSql = "INSERT INTO " + PgTable + InsertValues;
    private const string InsertSsSql = "INSERT INTO " + SsTable + InsertValues;

    private const string SupersedeSet =
        " SET status = @superseded, decided_at = @now WHERE tenant_id = @tenantId AND scope_name = @scope AND status = @pending";

    private const string SupersedeSelectPgSql = "SELECT id FROM " + PgTable + " WHERE tenant_id = @tenantId AND scope_name = @scope AND status = @pending FOR UPDATE";
    private const string SupersedeSelectSsSql = "SELECT id FROM " + SsTable + " WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenantId AND scope_name = @scope AND status = @pending";
    private const string SupersedePgSql = "UPDATE " + PgTable + SupersedeSet;
    private const string SupersedeSsSql = "UPDATE " + SsTable + SupersedeSet;

    private const string GetPgSql = "SELECT " + Columns + " FROM " + PgTable + " WHERE tenant_id = @tenantId AND id = @id";
    private const string GetSsSql = "SELECT " + Columns + " FROM " + SsTable + " WHERE tenant_id = @tenantId AND id = @id";

    private const string TransitionSet =
        " SET status = @to, decided_by = @decidedBy, decided_at = @decidedAt, reject_reason = @reason WHERE tenant_id = @tenantId AND id = @id AND status = @from";

    private const string TransitionPgSql = "UPDATE " + PgTable + TransitionSet;
    private const string TransitionSsSql = "UPDATE " + SsTable + TransitionSet;

    private const string UpdateDiffSet = " SET diff_json = @diff, state_hash = @stateHash WHERE tenant_id = @tenantId AND id = @id AND status = @pending";
    private const string UpdateDiffPgSql = "UPDATE " + PgTable + UpdateDiffSet;
    private const string UpdateDiffSsSql = "UPDATE " + SsTable + UpdateDiffSet;

    private const string ExpiredPgSql =
        "SELECT " + Columns + " FROM " + PgTable + " WHERE status = @pending AND expires_at <= @now ORDER BY expires_at LIMIT @max";

    private const string ExpiredSsSql =
        "SELECT TOP (@max) " + Columns + " FROM " + SsTable + " WHERE status = @pending AND expires_at <= @now ORDER BY expires_at";

    private readonly IGovernanceSqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

    private bool IsSqlServer => _dialect is SqlServerGovernanceDialect;

    public async Task<IReadOnlyList<Guid>> CreateAsync(ManifestProposal proposal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var tenant = RequireTenant(proposal.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.ScopeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.ManifestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.StateHash);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var superseded = new List<Guid>();
            await using (var select = conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = IsSqlServer ? SupersedeSelectSsSql : SupersedeSelectPgSql;
                select.AddBound("@tenantId", tenant);
                select.AddBound("@scope", proposal.ScopeName);
                select.AddBound("@pending", ManifestProposalStatus.Pending.ToString());
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    superseded.Add(Guid.Parse(reader.GetString(0)));
                }
            }

            if (superseded.Count > 0)
            {
                await using var update = conn.CreateCommand();
                update.Transaction = tx;
                update.CommandText = IsSqlServer ? SupersedeSsSql : SupersedePgSql;
                update.AddBound("@superseded", ManifestProposalStatus.Superseded.ToString());
                update.AddBound("@now", proposal.SubmittedAt.ToUniversalTime(), DbType.DateTimeOffset);
                update.AddBound("@tenantId", tenant);
                update.AddBound("@scope", proposal.ScopeName);
                update.AddBound("@pending", ManifestProposalStatus.Pending.ToString());
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = IsSqlServer ? InsertSsSql : InsertPgSql;
                insert.AddBound("@id", proposal.Id.ToString("D"));
                insert.AddBound("@tenantId", tenant);
                insert.AddBound("@scope", proposal.ScopeName);
                insert.AddBound("@submittedBy", proposal.SubmittedBy.Value);
                insert.AddBound("@submittedAt", proposal.SubmittedAt.ToUniversalTime(), DbType.DateTimeOffset);
                insert.AddBound("@manifest", proposal.ManifestJson);
                insert.AddBound("@diff", proposal.DiffJson);
                insert.AddBound("@stateHash", proposal.StateHash);
                insert.AddBound("@status", ManifestProposalStatus.Pending.ToString());
                insert.AddBound("@expiresAt", proposal.ExpiresAt.ToUniversalTime(), DbType.DateTimeOffset);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return superseded;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ManifestProposal?> GetAsync(TenantId tenantId, Guid id, CancellationToken ct = default)
    {
        var tenant = RequireTenant(tenantId);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? GetSsSql : GetPgSql;
        cmd.AddBound("@tenantId", tenant);
        cmd.AddBound("@id", id.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRow(reader) : null;
    }

    public async Task<bool> TryTransitionAsync(
        TenantId tenantId,
        Guid id,
        ManifestProposalStatus from,
        ManifestProposalStatus to,
        Sid? decidedBy,
        DateTimeOffset? decidedAt,
        string? rejectReason,
        CancellationToken ct = default)
    {
        var tenant = RequireTenant(tenantId);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? TransitionSsSql : TransitionPgSql;
        cmd.AddBound("@to", to.ToString());
        cmd.AddBound("@decidedBy", decidedBy.HasValue ? decidedBy.Value.Value : DBNull.Value);
        cmd.AddBound("@decidedAt", decidedAt.HasValue ? decidedAt.Value.ToUniversalTime() : DBNull.Value, DbType.DateTimeOffset);
        cmd.AddBound("@reason", rejectReason is null ? DBNull.Value : rejectReason);
        cmd.AddBound("@tenantId", tenant);
        cmd.AddBound("@id", id.ToString("D"));
        cmd.AddBound("@from", from.ToString());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> UpdateDiffAsync(TenantId tenantId, Guid id, string diffJson, string stateHash, CancellationToken ct = default)
    {
        var tenant = RequireTenant(tenantId);
        ArgumentNullException.ThrowIfNull(diffJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateHash);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? UpdateDiffSsSql : UpdateDiffPgSql;
        cmd.AddBound("@diff", diffJson);
        cmd.AddBound("@stateHash", stateHash);
        cmd.AddBound("@tenantId", tenant);
        cmd.AddBound("@id", id.ToString("D"));
        cmd.AddBound("@pending", ManifestProposalStatus.Pending.ToString());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<ManifestProposal>> ListExpiredPendingAsync(DateTimeOffset now, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var result = new List<ManifestProposal>();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? ExpiredSsSql : ExpiredPgSql;
        cmd.AddBound("@pending", ManifestProposalStatus.Pending.ToString());
        cmd.AddBound("@now", now.ToUniversalTime(), DbType.DateTimeOffset);
        cmd.AddBound("@max", max);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadRow(reader));
        }

        return result;
    }

    private static ManifestProposal ReadRow(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        TenantId = StoredTenant.Parse(reader.GetString(1)),
        ScopeName = reader.GetString(2),
        SubmittedBy = new Sid(reader.GetString(3)),
        SubmittedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 4) ?? DateTimeOffset.UnixEpoch,
        ManifestJson = reader.GetString(5),
        DiffJson = reader.GetString(6),
        StateHash = reader.GetString(7),
        // An unknown stored status is never pending (fail closed).
        Status = Enum.TryParse<ManifestProposalStatus>(reader.GetString(8), ignoreCase: false, out var status) && Enum.IsDefined(status)
            ? status
            : ManifestProposalStatus.Expired,
        DecidedBy = ConsentWorkflowSql.ReadOptionalSid(reader, 9),
        DecidedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 10),
        RejectReason = ConsentWorkflowSql.ReadOptionalString(reader, 11),
        // A missing expiry is treated as already expired (fail closed).
        ExpiresAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 12) ?? DateTimeOffset.UnixEpoch
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
        TenantId.TryParse(TenantScope.RequireTenant(tenant, GovernanceTable.GovernanceManifestProposals.ToSqlName()).Value, out var valid)
            ? valid.Value
            : throw new ArgumentException("A valid non-reserved TenantId is required.", nameof(tenant));
}
