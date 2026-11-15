namespace Autheris.Application.Governance.Grants;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;

public sealed class OwnerGrantException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed record OwnerGrantAssignment(
    Guid TableId,
    TableIdentifier Table,
    string OwnerId,
    Sid OwnerSid,
    string Role,
    TenantId TenantId,
    Sid ActorSid,
    IReadOnlyCollection<string>? CallerIdentifiers = null);

public sealed record OwnerGrantRemoval(
    Guid TableId,
    TableIdentifier Table,
    string OwnerId,
    Sid OwnerSid,
    string Role,
    TenantId TenantId,
    Sid ActorSid,
    string Reason);

public interface IOwnerGrantStore
{
    Task<IReadOnlyList<Consent>> GetActiveOwnerGrantsAsync(
        DbTransaction transaction,
        Guid tableId,
        Sid ownerSid,
        CancellationToken ct = default);

    Task<bool> HasActiveTableOwnershipAsync(
        DbTransaction transaction,
        Guid tableId,
        string ownerId,
        CancellationToken ct = default);

    Task MarkOwnerGrantRevokedAsync(
        DbTransaction transaction,
        Guid consentId,
        Sid actorSid,
        string reason,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task IncrementTableEpochAsync(
        DbTransaction transaction,
        TableIdentifier table,
        CancellationToken ct = default);

    Task RecordAuditInTransactionAsync(
        DbTransaction transaction,
        AuditLogEntry entry,
        CancellationToken ct = default);
}

public interface IOwnerGrantService
{
    Task<Consent> GrantOwnerAsync(
        DbTransaction transaction,
        OwnerGrantAssignment assignment,
        CancellationToken ct = default);

    Task<int> RevokeOwnerAsync(
        DbTransaction transaction,
        OwnerGrantRemoval removal,
        CancellationToken ct = default);
}

/// <summary>
/// Issues and revokes open-ended <see cref="ConsentGrantSource.Owner"/> grant records through <see cref="IGrantWriter"/>
/// exclusively inside control-plane data-owner assignment transactions.
/// </summary>
public sealed class OwnerGrantService(
    IGrantWriter grantWriter,
    IOwnerGrantStore store,
    TimeProvider? timeProvider = null) : IOwnerGrantService
{
    public const string SelfGrantForbiddenCode = "SELF_GRANT_FORBIDDEN";

    private readonly IGrantWriter _grantWriter = grantWriter ?? throw new ArgumentNullException(nameof(grantWriter));
    private readonly IOwnerGrantStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<Consent> GrantOwnerAsync(
        DbTransaction transaction,
        OwnerGrantAssignment assignment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(assignment);
        StoredTenant.RequireWritable(assignment.TenantId);

        if (string.IsNullOrWhiteSpace(assignment.OwnerSid.Value))
        {
            throw new ArgumentException("Owner SID is required for an owner grant.", nameof(assignment));
        }

        if (string.IsNullOrWhiteSpace(assignment.ActorSid.Value))
        {
            throw new ArgumentException("Control-plane actor SID is required for an owner grant.", nameof(assignment));
        }

        EnsureNotSelfGrant(assignment);

        var existing = await _store.GetActiveOwnerGrantsAsync(transaction, assignment.TableId, assignment.OwnerSid, ct)
            .ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return existing[0];
        }

        var assignedAt = _time.GetUtcNow();
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = assignment.TableId,
            TableIdentifier = assignment.Table,
            DataSource = assignment.Table.Domain,
            ColumnSetMode = ColumnSetMode.AllNonHidden,
            Effect = ConsentEffect.Allow,
            TenantId = assignment.TenantId,
            GranteeType = GranteeType.User,
            GranteeSid = assignment.OwnerSid,
            CreatedBySid = assignment.ActorSid,
            ValidFrom = assignedAt,
            ValidTo = DateTimeOffset.MaxValue,
            GrantSource = ConsentGrantSource.Owner
        };
        consent.Validate();

        var scopeJson = JsonSerializer.Serialize(new
        {
            grantSource = ConsentGrantSource.Owner.ToSqlString(),
            columns = "ALL_CLEAR",
            role = assignment.Role
        });
        var decisionJson = JsonSerializer.Serialize(new
        {
            Action = "OwnerAssigned",
            Role = assignment.Role,
            OwnerId = assignment.OwnerId,
            AssignedBy = assignment.ActorSid.Value
        });

        var input = new GrantRecordInput(
            assignment.TenantId.Value,
            scopeJson,
            assignment.ActorSid.Value,
            decisionJson,
            assignedAt,
            ObjectId: assignment.Table.ToString(),
            GranteeType: GranteeType.User.ToString(),
            GranteeRef: assignment.OwnerSid.Value,
            GrantSource: ConsentGrantSource.Owner,
            ValidFrom: assignedAt,
            ValidUntil: null,
            RecordType: GrantRecordType.Issued);

        var record = await _grantWriter.InsertAndGrantAsync(transaction, consent, input, ct).ConfigureAwait(false);
        await _store.IncrementTableEpochAsync(transaction, assignment.Table, ct).ConfigureAwait(false);
        await _store.RecordAuditInTransactionAsync(transaction, new AuditLogEntry
        {
            TenantId = assignment.TenantId,
            OccurredAt = assignedAt,
            EventType = AuditEventTypes.ConsentGranted,
            ActorSid = assignment.ActorSid,
            TargetTable = assignment.Table.ToString(),
            Decision = "ALLOW",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                ConsentId = consent.Id,
                GrantRecordSeq = record.Seq,
                GrantSource = ConsentGrantSource.Owner.ToSqlString(),
                OwnerId = assignment.OwnerId,
                assignment.Role,
                ValidFrom = assignedAt
            })
        }, ct).ConfigureAwait(false);

        return new Consent
        {
            Id = consent.Id,
            TableId = consent.TableId,
            TableIdentifier = consent.TableIdentifier,
            DataSource = consent.DataSource,
            ColumnSetMode = consent.ColumnSetMode,
            Effect = consent.Effect,
            TenantId = consent.TenantId,
            GranteeType = consent.GranteeType,
            GranteeSid = consent.GranteeSid,
            CreatedBySid = consent.CreatedBySid,
            ValidFrom = consent.ValidFrom,
            ValidTo = consent.ValidTo,
            GrantSource = consent.GrantSource,
            GrantRecordSeq = record.Seq
        };
    }

    public async Task<int> RevokeOwnerAsync(
        DbTransaction transaction,
        OwnerGrantRemoval removal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(removal);
        StoredTenant.RequireWritable(removal.TenantId);

        if (string.IsNullOrWhiteSpace(removal.OwnerSid.Value))
        {
            return 0;
        }

        if (await _store.HasActiveTableOwnershipAsync(transaction, removal.TableId, removal.OwnerId, ct).ConfigureAwait(false))
        {
            return 0;
        }

        var activeGrants = await _store.GetActiveOwnerGrantsAsync(transaction, removal.TableId, removal.OwnerSid, ct)
            .ConfigureAwait(false);
        if (activeGrants.Count == 0)
        {
            return 0;
        }

        var now = _time.GetUtcNow();
        var effectiveReason = string.IsNullOrWhiteSpace(removal.Reason) ? "DATA_OWNER_UNASSIGNED" : removal.Reason.Trim();
        var revoked = 0;

        foreach (var grant in activeGrants)
        {
            await _store.MarkOwnerGrantRevokedAsync(transaction, grant.Id, removal.ActorSid, effectiveReason, now, ct)
                .ConfigureAwait(false);

            var revokeInput = new GrantRecordInput(
                removal.TenantId.Value,
                JsonSerializer.Serialize(new { grantSource = ConsentGrantSource.Owner.ToSqlString() }),
                removal.ActorSid.Value,
                JsonSerializer.Serialize(new { Action = "OwnerUnassigned", Reason = effectiveReason, OwnerId = removal.OwnerId }),
                now,
                ObjectId: removal.Table.ToString(),
                GranteeType: grant.GranteeType.ToString(),
                GranteeRef: removal.OwnerSid.Value,
                GrantSource: ConsentGrantSource.Owner,
                ValidFrom: grant.ValidFrom,
                ValidUntil: now,
                RecordType: GrantRecordType.Revoked);

            await _grantWriter.RevokeAsync(transaction, grant, revokeInput, ct).ConfigureAwait(false);
            await _store.RecordAuditInTransactionAsync(transaction, new AuditLogEntry
            {
                TenantId = removal.TenantId,
                OccurredAt = now,
                EventType = AuditEventTypes.ConsentRevoked,
                ActorSid = removal.ActorSid,
                TargetTable = removal.Table.ToString(),
                Decision = "REVOKED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    ConsentId = grant.Id,
                    GrantSource = ConsentGrantSource.Owner.ToSqlString(),
                    OwnerId = removal.OwnerId,
                    Reason = effectiveReason,
                    RevokedAt = now
                })
            }, ct).ConfigureAwait(false);
            revoked++;
        }

        await _store.IncrementTableEpochAsync(transaction, removal.Table, ct).ConfigureAwait(false);
        return revoked;
    }

    private static void EnsureNotSelfGrant(OwnerGrantAssignment assignment)
    {
        if (OwnerIdentifier.AreSame(assignment.ActorSid.Value, assignment.OwnerSid.Value))
        {
            throw new OwnerGrantException(SelfGrantForbiddenCode, "A data owner cannot create an owner grant for themselves.");
        }

        if (assignment.CallerIdentifiers is not { Count: > 0 })
        {
            return;
        }

        foreach (var candidate in assignment.CallerIdentifiers)
        {
            if (OwnerIdentifier.AreSame(candidate, assignment.OwnerSid.Value))
            {
                throw new OwnerGrantException(SelfGrantForbiddenCode, "A data owner cannot create an owner grant for themselves.");
            }
        }
    }
}
