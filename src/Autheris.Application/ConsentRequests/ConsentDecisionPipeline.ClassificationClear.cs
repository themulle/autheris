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

        if (classificationRepository is null || _metadataRepository is null)
        {
            throw new ConsentDecisionException(
                ClearRequestErrorCodes.Unavailable,
                "Classification Clear cannot be verified or persisted by this deployment.",
                statusCode: 409);
        }

        // The stored request is never trusted: re-validate the approved items against the live vocabulary and the table
        // (columns exist and carry exactly that classification, grantable, never CREDENTIALS).
        var meta = await _metadataRepository.GetTableMetadataAsync(req.TableIdentifier, ct).ConfigureAwait(false)
            ?? throw new ConsentDecisionException(ClearRequestErrorCodes.Unavailable, "The table metadata was not found.", statusCode: 409);
        try
        {
            approved = ClassificationClearPolicy.ValidateRequest(approved, catalog, meta, tenant);
        }
        catch (ClearRequestException ex)
        {
            throw new ConsentDecisionException(ex.Code, ex.Message, statusCode: 409);
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

    /// <summary>
    /// Every steward group of the approved classifications must be represented by an approver other than the first one (the owner
    /// step): an owner who is also a steward never satisfies both roles, and at least two distinct people must approve.
    /// </summary>
    private async Task EnsureStewardCoverageAsync(
        ConsentRequest req, ClearApprovalPlan plan, IReadOnlyList<string> orderedApproverSids, CancellationToken ct)
    {
        var distinct = orderedApproverSids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var approverSids = distinct.Skip(1).ToList();
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

        if (distinct.Count < 2 || plan.StewardGroupSids.Any(g => !covered.Contains(g)))
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

    private static List<ConsentClassificationAccess> BuildClearAccess(
        ConsentRequest req, Guid consentId, ClearApprovalPlan plan, Sid approver, DateTimeOffset now, DateTimeOffset grantValidTo, int defaultMaxClearDays) =>
        plan.Items.Select(item => new ConsentClassificationAccess
        {
            TenantId = req.TenantId.Value,
            ConsentId = consentId,
            Code = item.Classification,
            AccessLevel = ColumnAccessLevel.Clear,
            ValidUntil = ClassificationClearPolicy.CapValidity(grantValidTo, now, plan.Catalog.Definitions[item.Classification], defaultMaxClearDays),
            ApprovedBySid = approver.Value,
            ApprovedAt = now
        }).ToList();
}
