using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Governance.Requests;

/// <summary>
/// Materializes approved <see cref="ConsentRequestKind.ProfileAssignment"/> requests into individual signed grants.
/// Profile assignments require Data Governance or Data Owner capacity and a finite expiration date.
/// </summary>
public sealed class ProfileAssignmentApplier(
    IAccessProfileRepository profileRepository,
    ITableMetadataRepository metadataRepository,
    IGrantWriter grantWriter,
    IConsentExtrasWriter? extrasWriter = null,
    IProfileAssignmentStore? assignmentStore = null,
    IProfileGrantMaterializer? materializer = null) : IGrantEffectApplier
{
    private readonly IAccessProfileRepository _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
    private readonly ITableMetadataRepository _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
    private readonly IGrantWriter _grantWriter = grantWriter ?? throw new ArgumentNullException(nameof(grantWriter));
    private readonly IConsentExtrasWriter? _extrasWriter = extrasWriter;
    private readonly IProfileAssignmentStore? _assignmentStore = assignmentStore;
    private readonly IProfileGrantMaterializer? _materializer = materializer;

    public ConsentRequestKind RequestKind => ConsentRequestKind.ProfileAssignment;

    public async Task<IReadOnlyList<GrantRef>> ApplyAsync(
        IGovernanceTransaction transaction,
        ApprovedPermission approved,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(approved);

        if (approved.SelfGrant || approved.Capacity is ApproverCapacity.ItAdmin or ApproverCapacity.SelfGrant)
        {
            throw new ConsentDecisionException("FORBIDDEN", "IT administrators cannot approve profile assignment grants.", 403);
        }

        if (approved.ValidTo <= approved.ValidFrom || approved.ValidTo >= DateTimeOffset.MaxValue)
        {
            throw new ConsentDecisionException(GrantErrorCodes.EndDateRequired, "Profile assignment grants require a finite expiration date.", 422);
        }

        var req = approved.Request;
        if (ProfileAssignmentSnapshots.Parse(req).ExtendingConsentId is { } extendingConsentId)
        {
            return await ExtendPatternCoverageAsync(transaction, approved, extendingConsentId, ct).ConfigureAwait(false);
        }

        if (!req.ProfileId.HasValue || req.ProfileId.Value == Guid.Empty)
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Profile assignment requests require a valid profileId.", 422);
        }

        if (string.IsNullOrWhiteSpace(req.RequestedGranteeRef))
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Grant target grantee reference is missing.", 422);
        }

        var profile = await _profileRepository.GetProfileAsync(req.TenantId, req.ProfileId.Value.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new ConsentDecisionException("NOT_FOUND", $"Access profile '{req.ProfileId.Value:D}' was not found.", 404);

        var targets = profile.TargetTables ?? [];
        if (targets.Count == 0)
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Access profile contains no target tables or patterns.", 422);
        }

        var granteeSid = new Sid(req.RequestedGranteeRef);
        var created = new List<GrantRef>(targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var rawTarget = targets[i]?.Trim();
            if (string.IsNullOrWhiteSpace(rawTarget))
            {
                continue;
            }

            var isPattern = rawTarget.Contains('*', StringComparison.Ordinal) || rawTarget.Contains('?', StringComparison.Ordinal);
            Guid tableId = Guid.Empty;
            TableIdentifier tableIdentifier;
            string? objectPattern = null;
            string? dataSource = null;

            if (isPattern)
            {
                var spec = ObjectPatternSpec.Parse(rawTarget);
                objectPattern = spec.NormalizedPattern;
                dataSource = spec.DataSource;
                tableIdentifier = default;
            }
            else
            {
                if (!TableIdentifier.TryParse(rawTarget, out var parsedTable))
                {
                    throw new ConsentDecisionException("VALIDATION_ERROR", $"Profile target table '{rawTarget}' is invalid.", 422);
                }

                var matched = await _metadataRepository.GetTableMetadataAsync(parsedTable, ct).ConfigureAwait(false);
                if (!CatalogTenantOwnership.IsEligibleTargetInTenant(matched, req.TenantId))
                {
                    throw new ConsentDecisionException("NOT_FOUND", $"Profile target table '{rawTarget}' was not found.", 404);
                }

                tableId = matched!.Table.Id;
                tableIdentifier = matched.Identifier;
            }

            var consent = new Consent
            {
                Id = Guid.NewGuid(),
                TableId = tableId,
                TableIdentifier = tableIdentifier,
                ObjectPattern = objectPattern,
                DataSource = dataSource,
                ConsentRequestId = req.Id,
                ProfileAssignmentId = req.Id,
                ProfileItemNo = i,
                Effect = ConsentEffect.Allow,
                TenantId = req.TenantId,
                GranteeType = req.RequestedGranteeType,
                GranteeSid = granteeSid,
                CreatedBySid = approved.DecidedBy,
                ValidFrom = approved.ValidFrom,
                ValidTo = approved.ValidTo,
                GrantSource = ConsentGrantSource.AccessProfile,
                GrantKind = PermissionGrantKind.Data,
                ApproverCapacity = approved.Capacity,
                SelfGrant = false,
                RowScope = profile.RowScope ?? [],
                WriteRules = MapWriteRules(profile.WriteRules, isPattern, tableIdentifier)
            };

            var scopeJson = JsonSerializer.Serialize(new
            {
                grantKind = PermissionGrantKind.Data.ToSqlString(),
                profileId = profile.ProfileId,
                itemNo = i,
                capacity = approved.Capacity.ToSqlString(),
                target = rawTarget
            });
            var decisionJson = JsonSerializer.Serialize(new
            {
                approver = approved.DecidedBy.Value,
                capacity = approved.Capacity.ToSqlString(),
                profileId = profile.ProfileId
            });

            var input = new GrantRecordInput(
                req.TenantId.Value,
                scopeJson,
                req.RequesterSid.Value,
                decisionJson,
                approved.DecidedAt,
                ObjectId: isPattern ? objectPattern : tableIdentifier.ToString(),
                GranteeType: req.RequestedGranteeType.ToString(),
                GranteeRef: granteeSid.Value,
                RequestId: req.Id,
                GrantSource: ConsentGrantSource.AccessProfile,
                ProfileAssignmentId: req.Id,
                ValidFrom: approved.ValidFrom,
                ValidUntil: approved.ValidTo,
                RecordType: GrantRecordType.Issued);

            var signed = await _grantWriter.InsertAndGrantAsync(transaction.Transaction, consent, input, ct).ConfigureAwait(false);
            if (_assignmentStore is not null)
            {
                // A pattern grant has no table: the store advances the catalog version instead of a table epoch (issue #1044).
                await _assignmentStore.IncrementTableEpochInTransactionAsync(transaction.Transaction, tableIdentifier, ct).ConfigureAwait(false);
            }

            if (_extrasWriter is not null && consent.WriteRules.Count > 0)
            {
                await _extrasWriter.PersistDataGrantExtrasAsync(transaction, consent, ct).ConfigureAwait(false);
            }

            created.Add(new GrantRef(
                consent.Id,
                PermissionGrantKind.Data,
                Table: isPattern ? null : tableIdentifier,
                ValidFrom: consent.ValidFrom,
                ValidTo: consent.ValidTo,
                GrantRecordSeq: signed.Seq));
        }

        if (created.Count == 0)
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Access profile contains no valid targets.", 422);
        }

        return created;
    }

    /// <summary>
    /// A system-initiated pattern coverage request extends the approving owners of the existing pattern grant with the
    /// owners of the newly matched object (with a new signed grant record) instead of creating another profile grant.
    /// </summary>
    private async Task<IReadOnlyList<GrantRef>> ExtendPatternCoverageAsync(
        IGovernanceTransaction transaction,
        ApprovedPermission approved,
        Guid extendingConsentId,
        CancellationToken ct)
    {
        if (_assignmentStore is null || _materializer is null)
        {
            throw new ConsentDecisionException("SERVICE_UNAVAILABLE", "Pattern coverage extension is not available.", 503);
        }

        var req = approved.Request;
        if (req.RequesterSid != ProfileAssignmentMaintenanceService.PatternCoverageRequester)
        {
            // Only the pattern coverage monitor creates extension requests; a caller-made request never extends a grant.
            throw new ConsentDecisionException("VALIDATION_ERROR", "Only system pattern coverage requests can extend a pattern grant.", 422);
        }

        var existing = await _assignmentStore.GetConsentByIdInTransactionAsync(transaction.Transaction, extendingConsentId, ct).ConfigureAwait(false);
        if (existing is null ||
            existing.TenantId != req.TenantId ||
            !existing.IsPatternGrant ||
            existing.ProfileAssignmentId is null ||
            existing.IsRevoked ||
            existing.ValidTo <= approved.DecidedAt ||
            existing.GranteeSid is not { } grantee ||
            !string.Equals(grantee.Value, req.RequestedGranteeRef, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConsentDecisionException("CONFLICT", "The pattern grant this request extends is no longer active.", 409);
        }

        var owners = (await _assignmentStore.GetAssignmentItemsAsync(transaction.Transaction, req.Id, ct).ConfigureAwait(false))
            .Where(i => i.TenantId == req.TenantId && !string.IsNullOrWhiteSpace(i.OwnerSid))
            .Select(i => i.OwnerSid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (owners.Count == 0)
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Pattern coverage request names no owners to add.", 422);
        }

        var extended = await _materializer.ExtendApprovingOwnersAsync(
            transaction.Transaction, existing, req.Id, owners, approved.DecidedBy, approved.DecidedAt, ct).ConfigureAwait(false);

        // New approving owners can make the pattern grant apply to more tables (issue #1044).
        await _assignmentStore.IncrementTableEpochInTransactionAsync(transaction.Transaction, existing.TableIdentifier, ct).ConfigureAwait(false);

        return
        [
            new GrantRef(
                extended.Id,
                PermissionGrantKind.Data,
                Table: null,
                ValidFrom: extended.ValidFrom,
                ValidTo: extended.ValidTo,
                GrantRecordSeq: extended.GrantRecordSeq)
        ];
    }

    private static IReadOnlyList<WriteRule> MapWriteRules(
        IReadOnlyList<AccessProfileWriteRule>? rules,
        bool isPattern,
        TableIdentifier tableIdentifier) =>
        !isPattern && rules is { Count: > 0 }
            ? rules
                .Where(r => r.TableIdentifier.Equals(tableIdentifier))
                .Select(r => new WriteRule(r.Id, r.Operation, r.Columns, r.RowScope, r.RowFilter, r.MaxRowsPerStatement))
                .ToArray()
            : [];
}
