using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;

namespace Autheris.Application.Governance.Requests;

/// <summary>
/// Materializes approved <see cref="ConsentRequestKind.Unmask"/> requests into signed <c>CONSENTS</c> rows with
/// <c>grant_kind = 'UNMASK'</c>. Unmask grants never confer row or column read access.
/// </summary>
public sealed class UnmaskGrantApplier(IGrantWriter grantWriter) : IGrantEffectApplier
{
    private readonly IGrantWriter _grantWriter = grantWriter ?? throw new ArgumentNullException(nameof(grantWriter));

    public ConsentRequestKind RequestKind => ConsentRequestKind.Unmask;

    public async Task<IReadOnlyList<GrantRef>> ApplyAsync(
        IGovernanceTransaction transaction,
        ApprovedPermission approved,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(approved);

        var req = approved.Request;
        if (string.IsNullOrWhiteSpace(req.RequestedGranteeRef))
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Grant target grantee reference is missing.", 422);
        }

        var grantSource = approved.SelfGrant ? ConsentGrantSource.SelfGrant : ConsentGrantSource.ConsentRequest;
        var granteeSid = new Sid(req.RequestedGranteeRef);

        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = req.TableId,
            TableIdentifier = req.TableIdentifier,
            ConsentRequestId = req.Id,
            Effect = ConsentEffect.Allow,
            TenantId = req.TenantId,
            GranteeType = req.RequestedGranteeType,
            GranteeSid = granteeSid,
            CreatedBySid = approved.DecidedBy,
            ValidFrom = approved.ValidFrom,
            ValidTo = approved.ValidTo,
            GrantSource = grantSource,
            GrantKind = PermissionGrantKind.Unmask,
            ApproverCapacity = approved.Capacity,
            SelfGrant = approved.SelfGrant,
            TargetColumns = req.TargetColumns?.ToArray(),
            ColumnRules = Array.Empty<ConsentColumnRule>(),
            WriteRules = []
        };

        var scopeJson = JsonSerializer.Serialize(new
        {
            grantKind = PermissionGrantKind.Unmask.ToSqlString(),
            capacity = approved.Capacity.ToSqlString(),
            selfGrant = approved.SelfGrant,
            targetColumns = req.TargetColumns
        });
        var decisionJson = JsonSerializer.Serialize(new
        {
            approver = approved.DecidedBy.Value,
            capacity = approved.Capacity.ToSqlString(),
            selfGrant = approved.SelfGrant
        });

        var grantInput = new GrantRecordInput(
            req.TenantId.Value,
            scopeJson,
            req.RequesterSid.Value,
            decisionJson,
            approved.DecidedAt,
            ObjectId: !string.IsNullOrWhiteSpace(req.TableIdentifier.TableName) ? req.TableIdentifier.ToString() : req.TableId.ToString("D"),
            GranteeType: req.RequestedGranteeType.ToString(),
            GranteeRef: granteeSid.Value,
            RequestId: req.Id,
            GrantSource: grantSource,
            ValidFrom: approved.ValidFrom,
            ValidUntil: approved.ValidTo,
            RecordType: GrantRecordType.Issued);

        var signed = await _grantWriter.InsertAndGrantAsync(transaction.Transaction, consent, grantInput, ct).ConfigureAwait(false);
        return
        [
            new GrantRef(
                consent.Id,
                PermissionGrantKind.Unmask,
                Table: req.TableIdentifier,
                ValidFrom: consent.ValidFrom,
                ValidTo: consent.ValidTo,
                GrantRecordSeq: signed.Seq)
        ];
    }
}
