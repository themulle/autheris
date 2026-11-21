namespace Autheris.Application.ConsentRequests.ProfileAssignments;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed partial class ProfileAssignmentRequestService
{
    private async Task<ConsentDecisionResult> DecideExternalAsync(
        ConsentDecisionCommand command,
        ConsentRequest req,
        ProfileAssignmentRequestContext snapshot,
        IReadOnlyList<TableMetadata> tenantTables,
        Sid actorSid,
        IReadOnlyList<Sid> externalApproverSids,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (command.Decision == ConsentDecisionKind.Reject)
        {
            var reason = ValidateRejectionReason(command.Reason);
            var rejected = await _approvalRepository.RejectExternalConsentRequestAsync(
                req.Id, command.ExternalAttribution!, externalApproverSids[^1], reason, ct).ConfigureAwait(false);
            if (_grantWriter is not null)
            {
                await using var rtx = await _store.BeginTransactionAsync(ct).ConfigureAwait(false);
                await _grantWriter.RecordRejectionAsync(rtx.Transaction, BuildRejectionInput(req, externalApproverSids[^1], reason, now), ct).ConfigureAwait(false);
                await rtx.CommitAsync(ct).ConfigureAwait(false);
            }

            await EmitEventAsync(rejected, ConsentEventTypes.RequestDecided, now, ct).ConfigureAwait(false);
            return new ConsentDecisionResult(rejected);
        }

        var approvedValidTo = ResolveApprovedValidToOrThrow(req, command.ApprovedValidTo, now);
        ProfileGrantMaterializer.ValidateExternalNarrowing(snapshot, tenantTables, command.ApprovedProfileItems, command.ApprovedClearColumns);
        if (req.RequestedGranteeType == GranteeType.Group &&
            SeparationOfDutiesService.IsDegradedGroupOrRoleResolution(command.Principal))
        {
            throw new ConsentDecisionException(ConsentDecisionErrorCodes.SelfApprovalForbidden, "Separation of duties violated: group or role resolution is degraded; denied fail-closed.", statusCode: 403);
        }

        foreach (var extSid in externalApproverSids)
        {
            await EnsureSodAsync(req, null, extSid, checkPriorApprovers: true, ct).ConfigureAwait(false);
        }

        var combinedApprovers = req.ApprovalSteps
            .Where(s => string.Equals(s.Decision, "APPROVED", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ApproverSid.Value)
            .Concat(externalApproverSids.Select(s => s.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requiresFourEyes = RequiresFourEyes(snapshot, tenantTables, req, approvedValidTo, now, combinedApprovers);
        if (requiresFourEyes && CountDistinctApprovers(req, combinedApprovers) < 2)
        {
            await using (var stepTx = await _store.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                await _store.ApplyRequestStepAndStatusInTransactionAsync(
                    stepTx.Transaction, req, "PENDING_EXTERNAL_APPROVAL", externalApproverSids[^1], "APPROVED", command.ExternalAttribution?.ExternalReference, now, ct).ConfigureAwait(false);
                await stepTx.CommitAsync(ct).ConfigureAwait(false);
            }

            var pendingExtReq = CloneWithStatus(req, "PENDING_EXTERNAL_APPROVAL", now);
            await EmitEventAsync(pendingExtReq, ConsentEventTypes.RequestUpdated, now, ct).ConfigureAwait(false);
            return new ConsentDecisionResult(pendingExtReq);
        }

        var allApprovingSids = combinedApprovers.Select(s => new Sid(s)).ToList();
        IReadOnlyList<Consent> materialized;
        await using (var tx = await _store.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            try
            {
                for (int i = 0; i < externalApproverSids.Count - 1; i++)
                {
                    await _store.ApplyRequestStepAndStatusInTransactionAsync(
                        tx.Transaction, req, newStatus: null, externalApproverSids[i], "APPROVED", command.ExternalAttribution?.ExternalReference, now, ct)
                        .ConfigureAwait(false);
                }

                await _store.ApplyRequestStepAndStatusInTransactionAsync(
                    tx.Transaction, req, "APPROVED", externalApproverSids[^1], "APPROVED", command.ExternalAttribution?.ExternalReference, now, ct)
                    .ConfigureAwait(false);

                materialized = await _materializer.MaterializeApprovedAsync(
                    tx.Transaction, req, snapshot, [], tenantTables, externalApproverSids[^1], allApprovingSids,
                    approvedValidTo, now, command.Description, command.WriteRules, command.ApprovedProfileItems, command.ApprovedClearColumns, ct)
                    .ConfigureAwait(false);

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        var concreteTables = materialized.Where(c => !c.IsPatternGrant).Select(c => c.TableIdentifier).Distinct().ToList();
        if (concreteTables.Count > 0)
        {
            await _store.InvalidateTablesAfterCommitAsync(concreteTables, ct).ConfigureAwait(false);
        }

        var approvedReq = CloneWithStatus(req, "APPROVED", now);
        await EmitEventAsync(approvedReq, ConsentEventTypes.RequestDecided, now, ct).ConfigureAwait(false);
        return new ConsentDecisionResult(approvedReq, materialized.FirstOrDefault());
    }
}
