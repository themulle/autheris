namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.ClassificationClear;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>Per-classification Clear routing, steward four-eyes and Clear persistence of the decision pipeline.</summary>
public sealed partial class ConsentDecisionPipeline
{
    private int DefaultMaxClearDays => grantOptions?.Value?.DefaultMaxClearDays is > 0 ? grantOptions.Value.DefaultMaxClearDays : 90;

    /// <summary>
    /// Resolves the Clear items this approval would grant (requested items narrowed by the approver), or <c>null</c> when the
    /// request carries none. An approver can only narrow: selecting a classification that was not requested is refused.
    /// </summary>
    private async Task<ClearApprovalPlan?> ResolveClearPlanAsync(ConsentRequest req, ConsentDecisionCommand command, CancellationToken ct)
    {
        if (command.Decision != ConsentDecisionKind.Approve || req.RequestKind != ConsentRequestKind.Consent)
        {
            return null;
        }

        var requested = RequestedClearCodec.Parse(req.ContextJson);
        if (requested.Count == 0)
        {
            if (command.ApprovedClearClassifications is { Count: > 0 })
            {
                throw new ConsentDecisionException(
                    ConsentDecisionErrorCodes.DecisionExceedsRequest,
                    "The decision selects classification Clear that was not requested.",
                    statusCode: 400);
            }

            return null;
        }

        var approved = ClassificationClearPolicy.Narrow(requested, command.ApprovedClearClassifications, out var exceeds);
        if (exceeds || approved is null)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.DecisionExceedsRequest,
                "The decision selects classification Clear that was not requested.",
                statusCode: 400);
        }

        if (approved.Count == 0)
        {
            return null;
        }

        var tenant = req.TenantId.Value;
        var definitions = classificationRepository is null
            ? []
            : await classificationRepository.GetClassificationsAsync(tenant, includeInactive: true, ct).ConfigureAwait(false);
        var catalog = ClassificationClearPolicy.ResolveCatalog(tenant, definitions);

        foreach (var item in approved)
        {
            // Re-check the live vocabulary: a classification deactivated or made non-grantable since the request is never cleared.
            if (string.Equals(item.Classification, ClassificationScopes.CredentialsCode, StringComparison.Ordinal) ||
                !catalog.Definitions.TryGetValue(item.Classification, out var def) ||
                !def.IsActive || !def.ClearGrantable || !def.SeparateApproval)
            {
                throw new ConsentDecisionException(
                    ClearRequestErrorCodes.NotGrantable,
                    "Clear access can no longer be granted for a requested classification.",
                    statusCode: 409);
            }
        }

        if (classificationRepository is null)
        {
            throw new ConsentDecisionException(
                ClearRequestErrorCodes.Unavailable,
                "Classification Clear cannot be persisted by this deployment.",
                statusCode: 409);
        }

        return ClassificationClearPolicy.BuildPlan(approved, catalog);
    }

    private async Task<bool> IsStewardOfAsync(ConsentRequest req, ClearApprovalPlan plan, string approverSid, CancellationToken ct)
    {
        if (groupStore is null)
        {
            return false;
        }

        var realmId = realmRegistry?.GetDefaultRealm()?.Id ?? "default";
        var effective = await groupStore.GetEffectiveGroupSidsForUserAsync(req.TenantId, realmId, approverSid, ct: ct).ConfigureAwait(false);
        return !effective.MaxDepthExceeded && plan.StewardGroupSids.Any(effective.GroupObjectSids.Contains);
    }

    /// <summary>
    /// A non-owner may only approve as steward, and only after the data owner (the first step is always owner capable).
    /// </summary>
    private async Task EnsureStewardMayApproveAsync(ConsentRequest req, ClearApprovalPlan? plan, Sid approverSid, CancellationToken ct)
    {
        if (plan is not { NeedsSteward: true } || !await IsStewardOfAsync(req, plan, approverSid.Value, ct).ConfigureAwait(false))
        {
            throw new ConsentDecisionException(
                "FORBIDDEN",
                $"User '{approverSid}' is neither a data owner nor a delegated approver for '{req.TableIdentifier}' and has no approver role.",
                statusCode: 403);
        }

        if (!(req.ApprovalSteps ?? []).Any(static s => string.Equals(s.Decision, "APPROVED", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConsentDecisionException(
                ClearRequestErrorCodes.ApproverOrder,
                "A steward approves classification Clear only after the data owner.",
                statusCode: 409);
        }
    }

    /// <summary>Every steward group of the approved classifications must be represented by one of the approvers.</summary>
    private async Task EnsureStewardCoverageAsync(
        ConsentRequest req, ClearApprovalPlan plan, IReadOnlyList<string> approverSids, CancellationToken ct)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (groupStore is not null)
        {
            var realmId = realmRegistry?.GetDefaultRealm()?.Id ?? "default";
            foreach (var sid in approverSids)
            {
                var effective = await groupStore.GetEffectiveGroupSidsForUserAsync(req.TenantId, realmId, sid, ct: ct).ConfigureAwait(false);
                if (effective.MaxDepthExceeded)
                {
                    continue;
                }

                foreach (var group in plan.StewardGroupSids.Where(effective.GroupObjectSids.Contains))
                {
                    covered.Add(group);
                }
            }
        }

        if (plan.StewardGroupSids.Any(g => !covered.Contains(g)))
        {
            throw new ConsentDecisionException(
                ClearRequestErrorCodes.StewardRequired,
                "A steward approval is still required for the requested classification Clear.",
                statusCode: 409);
        }
    }

    private static IReadOnlyList<ConsentColumnRule> ApplyClearColumns(
        IReadOnlyList<ConsentColumnRule> columnRules, ClearApprovalPlan plan)
    {
        var clearNames = plan.Items.SelectMany(static i => i.Columns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return columnRules
            .Select(r => clearNames.Contains(r.ColumnName) && r.AccessLevel != ColumnAccessLevel.Deny
                ? new ConsentColumnRule
                {
                    Id = r.Id,
                    ConsentId = r.ConsentId,
                    TableColumnId = r.TableColumnId,
                    ColumnName = r.ColumnName,
                    AccessLevel = ColumnAccessLevel.Clear,
                    ApplicableScopes = r.ApplicableScopes
                }
                : r)
            .ToList();
    }

    private async Task WriteClearAccessAsync(
        Consent activated, ConsentRequest req, ClearApprovalPlan plan, Sid approver, DateTimeOffset now, DateTimeOffset grantValidTo, CancellationToken ct)
    {
        foreach (var item in plan.Items)
        {
            var def = plan.Catalog.Definitions[item.Classification];
            await classificationRepository!.UpsertConsentClassificationAccessAsync(new ConsentClassificationAccess
            {
                TenantId = req.TenantId.Value,
                ConsentId = activated.Id,
                Code = item.Classification,
                AccessLevel = ColumnAccessLevel.Clear,
                ValidUntil = ClassificationClearPolicy.CapValidity(grantValidTo, now, def, DefaultMaxClearDays),
                ApprovedBySid = approver.Value,
                ApprovedAt = now
            }, ct).ConfigureAwait(false);
        }
    }
}
