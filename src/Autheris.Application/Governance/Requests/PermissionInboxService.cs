namespace Autheris.Application.Governance.Requests;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>A pending request the caller may decide, with the capacity the caller would decide with.</summary>
public sealed record PermissionInboxItem(ConsentRequest Request, ApproverCapacity Capacity);

/// <summary>One page of the decision inbox.</summary>
public sealed record PermissionInboxPage(IReadOnlyList<PermissionInboxItem> Items, string? NextCursor);

/// <summary>
/// Read side for deciders: the requests a caller is eligible to decide and the grant overview of a container.
/// A request or container the caller may not see is indistinguishable from a missing one.
/// </summary>
public sealed partial class PermissionInboxService(
    IConsentApprovalRepository approvals,
    IConsentRepository consents,
    ApproverResolver approverResolver,
    ApproverEligibility approverEligibility,
    ISeparationOfDutiesService sod,
    IGovernanceCapabilities capabilities,
    IGovernanceDomainRepository? domains = null,
    TimeProvider? timeProvider = null,
    ICatalogVisibilityGrantRepository? catalogGrants = null,
    IDeputyAuthority? deputies = null,
    IDeputyRelationRepository? deputyRelations = null)
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
    private readonly ICatalogVisibilityGrantRepository? _catalogGrants = catalogGrants;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IDeputyAuthority? _deputies = deputies;
    private readonly IDeputyRelationRepository? _deputyRelations = deputyRelations;

    /// <summary>
    /// Requests of <paramref name="tenant"/> the caller is eligible to decide, newest first (by last update, then id), the
    /// order of the request page query. <paramref name="cursor"/> resumes strictly after the last request evaluated for
    /// the previous page, so no request is skipped when a repository page is cut.
    /// </summary>
    /// <exception cref="ArgumentException">The cursor is malformed.</exception>
    public async Task<PermissionInboxPage> GetInboxAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (cursor is not null && !ConsentRequestCursor.TryDecode(cursor, out _, out _))
        {
            throw new ArgumentException("The inbox cursor is malformed.", nameof(cursor));
        }

        var size = Math.Clamp(limit, 1, MaxPageSize);
        if (!caller.IsAuthenticated || caller.TenantClaimInvalid || string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            return new PermissionInboxPage([], null);
        }

        // Bounded scan cost: a caller that can never be an approver neither pays for nor causes a scan.
        if (IsDegraded(caller))
        {
            // ApproverEligibility denies every capacity under degraded resolution.
            return new PermissionInboxPage([], null);
        }

        // The caller's active deputy relations are read once per inbox call (issue #1126) and reused for every request of the page.
        var deputies = _deputies is null ? null : new MemoizedDeputyAuthority(_deputies);
        if (!MayHoldAnyDeciderCapacity(caller, tenant) &&
            (deputies is null || (await deputies.GetActiveRelationsAsync(caller, tenant, ct).ConfigureAwait(false)).Count == 0))
        {
            return new PermissionInboxPage([], null);
        }

        var evaluation = new InboxEvaluation(caller, tenant, _capabilities, deputies);
        var items = new List<PermissionInboxItem>(size);
        var next = cursor;
        var resume = cursor;
        var hasMore = false;

        for (var scanned = 0; ; scanned++)
        {
            var page = await _approvals.QueryTenantConsentRequestsAsync(
                new ConsentRequestPageQuery(tenant, PendingStatus, Cursor: next, Limit: RepositoryPageSize),
                ct).ConfigureAwait(false);

            var index = 0;
            for (; index < page.Items.Count && items.Count < size; index++)
            {
                var request = page.Items[index];
                var capacity = await EvaluateAsync(evaluation, request, ct).ConfigureAwait(false);
                if (capacity is { } held)
                {
                    items.Add(new PermissionInboxItem(request, held));
                }

                // The cursor always points at the last evaluated row, so rows after it are never skipped.
                resume = ConsentRequestCursor.For(request);
            }

            if (index < page.Items.Count)
            {
                hasMore = true;
                break;
            }

            if (page.NextCursor is null || page.Items.Count == 0)
            {
                break;
            }

            next = page.NextCursor;
            if (items.Count >= size || scanned + 1 >= MaxScanPages)
            {
                hasMore = true;
                break;
            }
        }

        return new PermissionInboxPage(items, hasMore ? resume : null);
    }

    private bool MayHoldAnyDeciderCapacity(SecurityPrincipalContext caller, TenantId tenant) =>
        caller.GroupSids is { Count: > 0 } ||
        _capabilities.IsGovernance(caller.Principal, tenant) ||
        _capabilities.IsItAdmin(caller.Principal, tenant);

    private static bool IsDegraded(SecurityPrincipalContext caller) =>
        caller.GroupResolutionStatus != GroupResolutionStatus.Complete ||
        GroupResolutionClaims.HasAnyDegradedClaim(caller.Claims) ||
        SeparationOfDutiesService.IsDegradedGroupOrRoleResolution(caller.Principal);

    /// <summary>The decision target exactly as the engine derives it when the request is decided.</summary>
    private static PermissionTarget TargetOf(ConsentRequest request) =>
        request.RequestKind switch
        {
            ConsentRequestKind.Catalog when request.ContainerKind.HasValue && request.ContainerId.HasValue =>
                PermissionTarget.ForContainer(request.ContainerKind.Value, request.ContainerId.Value),
            ConsentRequestKind.ProfileAssignment when request.ProfileId.HasValue =>
                PermissionTarget.ForProfile(request.ProfileId.Value),
            _ => PermissionTarget.ForTable(request.TableIdentifier, request.TargetColumns)
        };

    private async Task<ApproverCapacity?> EvaluateAsync(InboxEvaluation evaluation, ConsentRequest request, CancellationToken ct)
    {
        try
        {
            var set = await _approverResolver.ResolveAsync(
                request.TenantId, request.RequestKind, TargetOf(request), request.RequesterSid, request.RequestedGranteeRef,
                evaluation.Resolutions, ct).ConfigureAwait(false);
            var capacity = await evaluation.Eligibility.EvaluateAsync(evaluation.Caller, set, ct).ConfigureAwait(false);
            if (!capacity.HasValue)
            {
                return null;
            }

            var verdict = await _sod.CheckPermissionDecisionAsync(request.TenantId, evaluation.Caller, request, ct).ConfigureAwait(false);
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

    /// <summary>Per-call state: one caller, one tenant, memoized container, owner-membership and deputyship lookups.</summary>
    private sealed class InboxEvaluation(
        SecurityPrincipalContext caller, TenantId tenant, IGovernanceCapabilities capabilities, IDeputyAuthority? deputies)
    {
        public SecurityPrincipalContext Caller { get; } = caller;

        public ApproverResolutionCache Resolutions { get; } = new(tenant);

        public ApproverEligibility Eligibility { get; } = new(new OwnerMembershipMemo(capabilities), deputies);
    }

    /// <summary>Caches owner-group membership per container for the single caller of one inbox call.</summary>
    private sealed class OwnerMembershipMemo(IGovernanceCapabilities inner) : IGovernanceCapabilities
    {
        private readonly Dictionary<ContainerRef, bool> _membership = [];

        public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) => inner.IsGovernance(principal, tenant);

        public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => inner.IsItAdmin(principal, tenant);

        public async ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext principal, ContainerRef container, CancellationToken ct)
        {
            if (_membership.TryGetValue(container, out var known))
            {
                return known;
            }

            var member = await inner.IsOwnerMemberAsync(principal, container, ct).ConfigureAwait(false);
            _membership[container] = member;
            return member;
        }

        public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext principal, ContainerRef container, TenantId objectTenant, CancellationToken ct) =>
            inner.CanClassifyAsync(principal, container, objectTenant, ct);

        public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId objectTenant) => inner.CanEditPolicy(principal, objectTenant);
    }
}
