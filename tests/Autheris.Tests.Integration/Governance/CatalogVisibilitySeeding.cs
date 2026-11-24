namespace Autheris.Tests.Integration.Governance;

using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Shared seeding for catalog visibility integration tests. A table is visible to a caller without a data grant when its container is
/// <c>Open</c> or the caller holds a catalog grant on the container; tests that expect visible tables seed one of the two here instead of
/// building their own domain rows.
/// </summary>
internal static class CatalogVisibilitySeeding
{
    private static readonly DateTimeOffset Seeded = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Creates an <c>Open</c> domain in <paramref name="tenant"/> and assigns <paramref name="tableIds"/> to it.</summary>
    public static Task<GovernanceDomain> SeedOpenDomainAsync(
        IGovernanceDomainRepository domains, TenantId tenant, Sid actor, params Guid[] tableIds) =>
        SeedDomainAsync(domains, tenant, actor, CatalogVisibilityMode.Open, tableIds);

    /// <summary>Creates an <c>OnRequest</c> domain in <paramref name="tenant"/> and assigns <paramref name="tableIds"/> to it.</summary>
    public static Task<GovernanceDomain> SeedOnRequestDomainAsync(
        IGovernanceDomainRepository domains, TenantId tenant, Sid actor, params Guid[] tableIds) =>
        SeedDomainAsync(domains, tenant, actor, CatalogVisibilityMode.OnRequest, tableIds);

    /// <summary>Gives <paramref name="grantee"/> a catalog grant (metadata only, never data access) on <paramref name="container"/>.</summary>
    public static Task<CatalogVisibilityGrant> SeedCatalogGrantAsync(
        ICatalogVisibilityGrantRepository grants, TenantId tenant, ContainerRef container, Sid grantee, Sid grantedBy) =>
        grants.CreateAsync(new CatalogVisibilityGrant
        {
            TenantId = tenant,
            ContainerKind = container.Kind,
            ContainerId = container.Id,
            GranteeType = GranteeType.User,
            GranteeSid = grantee,
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            GrantedBySid = grantedBy,
            ApproverCapacity = ApproverCapacity.Governance,
            CreatedAt = DateTimeOffset.UtcNow
        });

    private static async Task<GovernanceDomain> SeedDomainAsync(
        IGovernanceDomainRepository domains, TenantId tenant, Sid actor, CatalogVisibilityMode visibility, Guid[] tableIds)
    {
        var result = await domains.UpsertAsync(
            new GovernanceDomain(
                Guid.NewGuid(),
                tenant,
                $"SeededDomain-{Guid.NewGuid():N}",
                Description: null,
                Contact: null,
                OwnerGroupSid: actor,
                ProtectionClass.Internal,
                visibility,
                IsSystemUnassigned: false,
                Version: 0,
                Seeded,
                Seeded),
            expectedVersion: 0,
            actor).ConfigureAwait(false);
        var domain = result.Domain ?? throw new InvalidOperationException("The domain could not be seeded.");
        if (tableIds.Length > 0)
        {
            await domains.AssignTablesAsync(tenant, domain.Id, tableIds, actor).ConfigureAwait(false);
        }

        return domain;
    }
}
