namespace Autheris.Application.Governance.Requests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>A pending request the caller may decide, with the capacity the caller would decide with.</summary>
public sealed record PermissionInboxItem(ConsentRequest Request, ApproverCapacity Capacity);

/// <summary>One page of the decision inbox.</summary>
public sealed record PermissionInboxPage(IReadOnlyList<PermissionInboxItem> Items, string? NextCursor);

/// <summary>One page of the grants of a container.</summary>
public sealed record ContainerGrantsPage(IReadOnlyList<Consent> Items, string? NextCursor);

/// <summary>
/// Read side for deciders: the requests a caller is eligible to decide and the grant overview of a container.
/// A request or container the caller may not see is indistinguishable from a missing one.
/// </summary>
public sealed class PermissionInboxService(
    IConsentApprovalRepository approvals,
    IConsentRepository consents,
    ApproverResolver approverResolver,
    ApproverEligibility approverEligibility,
    ISeparationOfDutiesService sod,
    IGovernanceCapabilities capabilities,
    IGovernanceDomainRepository? domains = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>Default and maximum page size of the inbox and the grant overview.</summary>
    public const int MaxPageSize = ConsentPageQuery.MaxPageSize;

    /// <summary>Repository pages inspected for one inbox page; the filter can skip many rows.</summary>
    public const int MaxScanPages = 10;

    private const int RepositoryPageSize = 100;
    private const string PendingStatus = "PENDING";

    private readonly IConsentApprovalRepository _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
    private readonly IConsentRepository _consents = consents ?? throw new ArgumentNullException(nameof(consents));
    private readonly ApproverResolver _approverResolver = approverResolver ?? throw new ArgumentNullException(nameof(approverResolver));
    private readonly ApproverEligibility _approverEligibility = approverEligibility ?? throw new ArgumentNullException(nameof(approverEligibility));
    private readonly ISeparationOfDutiesService _sod = sod ?? throw new ArgumentNullException(nameof(sod));
    private readonly IGovernanceCapabilities _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    private readonly IGovernanceDomainRepository? _domains = domains;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Requests of <paramref name="tenant"/> the caller is eligible to decide, oldest first.</summary>
    public async Task<PermissionInboxPage> GetInboxAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var size = Math.Clamp(limit, 1, MaxPageSize);
        if (!caller.IsAuthenticated || caller.TenantClaimInvalid || string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            return new PermissionInboxPage([], null);
        }

        var items = new List<PermissionInboxItem>(size);
        var next = cursor;
        string? lastScannedCursor = cursor;
        var exhausted = false;

        for (var scanned = 0; scanned < MaxScanPages && items.Count < size; scanned++)
        {
            var page = await _approvals.QueryTenantConsentRequestsAsync(
                new ConsentRequestPageQuery(tenant, PendingStatus, Cursor: next, Limit: RepositoryPageSize),
                ct).ConfigureAwait(false);

            foreach (var request in page.Items)
            {
                if (items.Count >= size)
                {
                    break;
                }

                var capacity = await EvaluateAsync(caller, request, ct).ConfigureAwait(false);
                if (capacity is { } held)
                {
                    items.Add(new PermissionInboxItem(request, held));
                }
            }

            lastScannedCursor = page.NextCursor;
            if (page.NextCursor is null || page.Items.Count == 0)
            {
                exhausted = true;
                break;
            }

            next = page.NextCursor;
        }

        return new PermissionInboxPage(items, exhausted ? null : lastScannedCursor);
    }

    /// <summary>
    /// Grants (including self-grants) of a container for owner group members and Data Governance;
    /// <see langword="null"/> when the container is not visible to the caller.
    /// </summary>
    public async Task<ContainerGrantsPage?> GetContainerGrantsAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        ContainerRef container,
        Guid? afterId,
        int pageSize,
        bool includeInactive,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.IsAuthenticated || caller.TenantClaimInvalid || string.IsNullOrWhiteSpace(tenant.Value) ||
            TenantId.IsReserved(tenant) || _domains is null || container.Kind != ContainerKind.Domain)
        {
            return null;
        }

        var allowed = _capabilities.IsGovernance(caller.Principal, tenant) ||
                      await _capabilities.IsOwnerMemberAsync(caller, container, ct).ConfigureAwait(false);
        if (!allowed)
        {
            return null;
        }

        var domain = await _domains.GetAsync(tenant, container.Id, ct).ConfigureAwait(false);
        if (domain is null)
        {
            return null;
        }

        var tableIds = await _domains.ListDomainTableIdsAsync(tenant, container.Id, ConsentPageQuery.MaxTableIds, ct).ConfigureAwait(false);
        if (tableIds.Count == 0)
        {
            return new ContainerGrantsPage([], null);
        }

        var page = await _consents.GetConsentPageAsync(
            new ConsentPageQuery(
                tenant,
                IncludeInactive: includeInactive,
                AfterId: afterId,
                PageSize: Math.Clamp(pageSize, 1, MaxPageSize),
                GrantKind: null,
                TableIds: tableIds.Take(ConsentPageQuery.MaxTableIds).ToArray()),
            ct).ConfigureAwait(false);

        return new ContainerGrantsPage(page.Items, page.NextAfterId?.ToString("D"));
    }

    private async Task<ApproverCapacity?> EvaluateAsync(SecurityPrincipalContext caller, ConsentRequest request, CancellationToken ct)
    {
        try
        {
            var target = request.ContainerKind is { } kind && request.ContainerId is { } id
                ? PermissionTarget.ForContainer(kind, id)
                : request.ProfileId is { } profileId && request.RequestKind == ConsentRequestKind.ProfileAssignment
                    ? PermissionTarget.ForProfile(profileId)
                    : PermissionTarget.ForTable(request.TableIdentifier, request.TargetColumns);

            var set = await _approverResolver.ResolveAsync(
                request.TenantId, request.RequestKind, target, request.RequesterSid, request.RequestedGranteeRef, ct).ConfigureAwait(false);
            var capacity = await _approverEligibility.EvaluateAsync(caller, set, ct).ConfigureAwait(false);
            if (!capacity.HasValue)
            {
                return null;
            }

            var verdict = await _sod.CheckPermissionDecisionAsync(request.TenantId, caller, request, ct).ConfigureAwait(false);
            if (verdict.Allowed)
            {
                return capacity;
            }

            // Mirrors the engine: a grantee group member is handled only by IT through the self-grant rules.
            var selfGrantByIt = verdict.Outcome == SodDecisionOutcome.SelfGrant &&
                                capacity.Value == ApproverCapacity.ItAdmin &&
                                request.RequestKind is ConsentRequestKind.Data or ConsentRequestKind.Unmask;
            return selfGrantByIt ? capacity : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A request whose approvers cannot be resolved is never listed as decidable.
            return null;
        }
    }
}
