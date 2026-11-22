using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;

namespace Autheris.Application.Governance.Requests;

/// <summary>
/// Materializes approved <see cref="ConsentRequestKind.Data"/> requests into signed <c>CONSENTS</c> rows with <c>grant_kind = 'DATA'</c>.
/// </summary>
public sealed class DataGrantApplier(
    IGrantWriter grantWriter,
    ITableMetadataRepository? metadataRepository = null,
    IConsentExtrasWriter? extrasWriter = null) : IGrantEffectApplier
{
    private readonly IGrantWriter _grantWriter = grantWriter ?? throw new ArgumentNullException(nameof(grantWriter));
    private readonly ITableMetadataRepository? _metadataRepository = metadataRepository;
    private readonly IConsentExtrasWriter? _extrasWriter = extrasWriter;

    public ConsentRequestKind RequestKind => ConsentRequestKind.Data;

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

        var consentId = Guid.NewGuid();
        var columnRules = await BuildTargetColumnRulesAsync(consentId, req, ct).ConfigureAwait(false);
        var grantSource = approved.SelfGrant ? ConsentGrantSource.SelfGrant : ConsentGrantSource.ConsentRequest;
        var granteeSid = new Sid(req.RequestedGranteeRef);

        var consent = new Consent
        {
            Id = consentId,
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
            GrantKind = PermissionGrantKind.Data,
            ApproverCapacity = approved.Capacity,
            SelfGrant = approved.SelfGrant,
            TargetColumns = req.TargetColumns?.ToArray(),
            ColumnRules = columnRules,
            WriteRules = req.RequestedWriteRules ?? []
        };

        var scopeJson = JsonSerializer.Serialize(new
        {
            grantKind = PermissionGrantKind.Data.ToSqlString(),
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
        if (_extrasWriter is not null && (consent.ColumnRules.Count > 0 || consent.WriteRules.Count > 0))
        {
            await _extrasWriter.PersistDataGrantExtrasAsync(transaction, consent, ct).ConfigureAwait(false);
        }

        return
        [
            new GrantRef(
                consent.Id,
                PermissionGrantKind.Data,
                Table: req.TableIdentifier,
                ValidFrom: consent.ValidFrom,
                ValidTo: consent.ValidTo,
                GrantRecordSeq: signed.Seq)
        ];
    }

    private async Task<IReadOnlyList<ConsentColumnRule>> BuildTargetColumnRulesAsync(
        Guid consentId,
        ConsentRequest req,
        CancellationToken ct)
    {
        if (req.TargetColumns is not { Count: > 0 } || _metadataRepository is null || string.IsNullOrWhiteSpace(req.TableIdentifier.TableName))
        {
            return Array.Empty<ConsentColumnRule>();
        }

        var metadata = await _metadataRepository.GetTableMetadataAsync(req.TableIdentifier, ct).ConfigureAwait(false);
        if (metadata is null || metadata.Columns.Count == 0)
        {
            return Array.Empty<ConsentColumnRule>();
        }

        var requestedSet = new HashSet<string>(req.TargetColumns, StringComparer.OrdinalIgnoreCase);
        var rules = new List<ConsentColumnRule>();
        foreach (var col in metadata.Columns)
        {
            if (!requestedSet.Contains(col.ColumnName))
            {
                rules.Add(new ConsentColumnRule
                {
                    Id = Guid.NewGuid(),
                    ConsentId = consentId,
                    TableColumnId = col.Id,
                    ColumnName = col.ColumnName,
                    AccessLevel = ColumnAccessLevel.Deny
                });
            }
        }

        return rules;
    }
}
