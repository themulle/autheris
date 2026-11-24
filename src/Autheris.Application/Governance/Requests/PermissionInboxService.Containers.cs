namespace Autheris.Application.Governance.Requests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// One page of the grants of a container. <see cref="PatternGrants"/> and <see cref="CatalogGrants"/> are separate sections
/// that are returned with the first page only (they are not part of the keyset over <see cref="Items"/>). A section that
/// reached its bound sets its <c>Truncated</c> flag instead of dropping rows silently.
/// </summary>
public sealed record ContainerGrantsPage(
    IReadOnlyList<Consent> Items,
    string? NextCursor,
    IReadOnlyList<Consent>? PatternGrants = null,
    bool PatternGrantsTruncated = false,
    IReadOnlyList<CatalogVisibilityGrant>? CatalogGrants = null,
    bool CatalogGrantsTruncated = false);

public sealed partial class PermissionInboxService
{
    /// <summary>Upper bound of rows in each separate section (pattern and catalog grants) of the container overview.</summary>
    public const int MaxSectionItems = ConsentPageQuery.MaxPageSize;

    /// <summary>Upper bound of container tables the pattern grants are matched against.</summary>
    public const int MaxPatternMatchTables = 1000;

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

        // Degraded group or role resolution never confirms governance or ownership (same guard as the revoke authority).
        if (IsDegraded(caller))
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

        // The tables of the container are joined in the query, so a large container is paged, never truncated.
        var page = await _consents.GetConsentPageAsync(
            new ConsentPageQuery(
                tenant,
                IncludeInactive: includeInactive,
                AfterId: afterId,
                PageSize: Math.Clamp(pageSize, 1, MaxPageSize),
                GrantKind: null,
                DomainId: container.Id),
            ct).ConfigureAwait(false);
        var nextCursor = page.NextAfterId?.ToString("D");
        if (afterId is not null)
        {
            return new ContainerGrantsPage(page.Items, nextCursor);
        }

        var (patternGrants, patternTruncated) = await ListPatternGrantsAsync(tenant, container.Id, includeInactive, ct).ConfigureAwait(false);
        var (catalog, catalogTruncated) = await ListCatalogGrantsAsync(tenant, container, includeInactive, ct).ConfigureAwait(false);
        return new ContainerGrantsPage(page.Items, nextCursor, patternGrants, patternTruncated, catalog, catalogTruncated);
    }

    /// <summary>Pattern grants of the tenant whose pattern matches at least one table of the domain.</summary>
    private async Task<(IReadOnlyList<Consent> Items, bool Truncated)> ListPatternGrantsAsync(
        TenantId tenant, Guid domainId, bool includeInactive, CancellationToken ct)
    {
        var tables = await _domains!.ListDomainTablesAsync(tenant, domainId, MaxPatternMatchTables + 1, ct).ConfigureAwait(false) ?? [];
        var truncated = tables.Count > MaxPatternMatchTables;
        if (tables.Count == 0)
        {
            return ([], truncated);
        }

        var considered = truncated ? tables.Take(MaxPatternMatchTables).ToArray() : tables;
        var candidates = await _consents.GetPatternConsentsWithPendingDeniesAsync(tenant, _time.GetUtcNow(), ct).ConfigureAwait(false) ?? [];
        var matched = new List<Consent>();
        foreach (var grant in candidates)
        {
            if (!grant.IsPatternGrant || grant.TenantId != tenant || (!includeInactive && grant.IsRevoked) ||
                !ObjectPatternSpec.TryParse(grant.ObjectPattern, out var spec, out _) || spec is null ||
                !considered.Any(spec.Matches))
            {
                continue;
            }

            if (matched.Count == MaxSectionItems)
            {
                truncated = true;
                break;
            }

            matched.Add(grant);
        }

        return (matched, truncated);
    }

    private async Task<(IReadOnlyList<CatalogVisibilityGrant> Items, bool Truncated)> ListCatalogGrantsAsync(
        TenantId tenant, ContainerRef container, bool includeInactive, CancellationToken ct)
    {
        if (_catalogGrants is null)
        {
            return ([], false);
        }

        var all = await _catalogGrants.ListByContainerAsync(tenant, container.Kind, container.Id, includeInactive, ct).ConfigureAwait(false) ?? [];
        return all.Count > MaxSectionItems ? (all.Take(MaxSectionItems).ToArray(), true) : (all, false);
    }
}
