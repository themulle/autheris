namespace Autheris.Application.ConsentRequests.ProfileAssignments;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Non-blocking adapter that projects a pending <see cref="ConsentRequest"/> (standard consent or
/// access-profile assignment) into <see cref="IGrantEffectPreview"/> and returns grant-ID / flag / count
/// summaries only (no foreign identities, SQL predicates, or parameter values).
/// </summary>
public static class ApprovalEffectPreviewHelper
{
    public static bool IsPendingStatus(string? status) =>
        string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase);

    public static async Task<ApprovalEffectPreviewDto?> TryBuildAsync(
        IGrantEffectPreview? effectPreview,
        ConsentRequest request,
        Sid actorSid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (effectPreview is null || !IsPendingStatus(request.Status))
        {
            return null;
        }

        try
        {
            var requestedItems = BuildRequestedItems(request);
            if (requestedItems.Count == 0)
            {
                return null;
            }

            var actorContext = BuildContext(actorSid, request.TenantId, GranteeType.User);
            var subjectContext = BuildSubjectContext(request);
            var preview = await effectPreview
                .PreviewAsync(actorContext, subjectContext, requestedItems, ct)
                .ConfigureAwait(false);

            return ToDto(preview);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Approval preview is strictly non-blocking: return null rather than failing request retrieval.
            return null;
        }
    }

    public static async Task<IReadOnlyDictionary<Guid, ApprovalEffectPreviewDto?>> TryBuildBatchAsync(
        IGrantEffectPreview? effectPreview,
        IReadOnlyList<ConsentRequest> requests,
        Sid actorSid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var dtos = new Dictionary<Guid, ApprovalEffectPreviewDto?>(requests.Count);
        if (effectPreview is null || requests.Count == 0)
        {
            return dtos;
        }

        var groups = new Dictionary<TenantId, List<GrantEffectPreviewBatchEntry>>();
        foreach (var request in requests)
        {
            if (request is null || !IsPendingStatus(request.Status)) continue;
            try
            {
                var requestedItems = BuildRequestedItems(request);
                if (requestedItems.Count == 0) continue;
                if (!groups.TryGetValue(request.TenantId, out var list))
                {
                    list = new List<GrantEffectPreviewBatchEntry>();
                    groups[request.TenantId] = list;
                }
                list.Add(new GrantEffectPreviewBatchEntry(request.Id, BuildSubjectContext(request), requestedItems));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Approval preview is strictly non-blocking: skip malformed requests rather than aborting batch.
            }
        }

        foreach (var (actorTenant, entries) in groups)
        {
            try
            {
                var actorContext = BuildContext(actorSid, actorTenant, GranteeType.User);
                var batch = await effectPreview.PreviewBatchAsync(actorContext, entries, ct).ConfigureAwait(false);
                foreach (var (id, preview) in batch)
                {
                    dtos[id] = preview is null ? null : ToDto(preview);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Approval preview is strictly non-blocking: return partial map rather than failing request retrieval.
            }
        }

        return dtos;
    }

    private static SecurityPrincipalContext BuildSubjectContext(ConsentRequest request)
    {
        var subjectSid = !string.IsNullOrWhiteSpace(request.RequestedGranteeRef)
            ? new Sid(request.RequestedGranteeRef)
            : request.RequesterSid;
        return BuildContext(subjectSid, request.TenantId, request.RequestedGranteeType);
    }

    private static ApprovalEffectPreviewDto ToDto(GrantEffectPreviewResult preview)
    {
        var flags = new List<string>(3);
        if (preview.RedundantGrants.Count > 0) flags.Add("REDUNDANT_GRANT");
        if (preview.EmptyHardIntersection) flags.Add("EMPTY_HARD_INTERSECTION");
        if (preview.IncludesFutureObjects) flags.Add("INCLUDES_FUTURE_OBJECTS");

        var redundantIds = preview.RedundantGrants
            .Select(static f => f.CoveredBy != Guid.Empty ? f.CoveredBy : f.GrantId)
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToList();

        return new ApprovalEffectPreviewDto(
            preview.MatchedObjectCount,
            preview.IncludesFutureObjects,
            preview.RedundantGrants.Count > 0,
            preview.EmptyHardIntersection,
            redundantIds,
            flags);
    }

    private static IReadOnlyList<GrantPreviewRequestedItem> BuildRequestedItems(ConsentRequest request)
    {
        if (request.RequestKind == ConsentRequestKind.ProfileAssignment)
        {
            var context = ProfileAssignmentSnapshots.Parse(request);
            if (context.Items.Count == 0)
            {
                return Array.Empty<GrantPreviewRequestedItem>();
            }

            var result = new List<GrantPreviewRequestedItem>(context.Items.Count);
            foreach (var item in context.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Target))
                {
                    continue;
                }

                result.Add(new GrantPreviewRequestedItem(
                    item.Target,
                    ColumnRules: null,
                    RowScope: context.RowScope,
                    WriteRules: item.WriteRules,
                    ColumnSetMode: item.IsPattern ? ColumnSetMode.AllNonHidden : ColumnSetMode.Explicit,
                    Effect: ConsentEffect.Allow,
                    ItemId: request.Id));
            }

            return result;
        }

        if (string.IsNullOrWhiteSpace(request.TableIdentifier.TableName))
        {
            return Array.Empty<GrantPreviewRequestedItem>();
        }

        return
        [
            new GrantPreviewRequestedItem(
                request.TableIdentifier.ToString(),
                ColumnRules: null,
                RowScope: null,
                WriteRules: request.RequestedWriteRules,
                ColumnSetMode: ColumnSetMode.Explicit,
                Effect: ConsentEffect.Allow,
                ItemId: request.Id)
        ];
    }

    private static SecurityPrincipalContext BuildContext(Sid sid, TenantId tenantId, GranteeType granteeType)
    {
        var groupSids = granteeType == GranteeType.Group
            ? new HashSet<Sid> { sid }
            : new HashSet<Sid>();

        return new SecurityPrincipalContext
        {
            UserSid = sid,
            ResolvedTenant = tenantId,
            GroupSids = groupSids,
            TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AuthenticationScheme = "InternalPreview",
            IsAuthenticated = true
        };
    }
}
