namespace Autheris.Tests.Unit.Api;

using Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

/// <summary>
/// IT capacity (TenantAdmin and canonical ClusterAdmin) is refused by every classification, mask-category, owner-group,
/// policy and dataset tag/privacy write. Policy-protected routes are checked through the real gateway policies;
/// handler-guarded routes through the capability check their handlers call.
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernanceWriteAuthorizationTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    public static IEnumerable<object[]> PolicyProtectedWriteRoutes()
    {
        yield return ["PUT /api/v1/governance/classifications/{code}", GatewayPolicies.GovernanceAdmin];
        yield return ["PUT /api/v1/governance/classifications/{code}/mask-rule", GatewayPolicies.GovernanceAdmin];
        yield return ["POST /api/governance/classification/classify-table", GatewayPolicies.GovernanceAdmin];
        yield return ["POST /api/governance/classification/assign-owner", GatewayPolicies.GovernanceAdmin];
    }

    public static IEnumerable<object[]> CapabilityGuardedWriteRoutes()
    {
        yield return ["POST domains"];
        yield return ["PUT domains/{id}"];
        yield return ["DELETE domains/{id}"];
        yield return ["PUT domains/{id}/owner-group"];
        yield return ["PUT domains/{id}/tables"];
        yield return ["PUT objects/{id}/protection"];
        yield return ["PUT objects/{id}/columns/{c}/protection"];
        yield return ["DELETE objects/{id}/import-samples"];
        yield return ["PUT access-policy"];
        yield return ["PUT products/{schema}/{name}/owner-group"];
        yield return ["POST products/{schema}/{name}/columns/{c}/classification"];
        yield return ["PUT columns/{id}/classifications/{code}"];
        yield return ["PUT columns/{id}/mask-overrides/{code}"];
    }

    private static ClaimsPrincipal TenantAdmin() => Wp0.User("S-1-5-21-it", Tenant.Value, ["TenantAdmin"]);

    private static ClaimsPrincipal ClusterAdmin() => Wp0.User("S-1-5-21-root", null, ["ClusterAdmin"]);

    private static async Task<bool> PolicyAllowsAsync(ClaimsPrincipal user, string policy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(GatewayPolicies.Configure);
        await using var provider = services.BuildServiceProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        return (await authz.AuthorizeAsync(user, null, policy)).Succeeded;
    }

    [Theory]
    [MemberData(nameof(PolicyProtectedWriteRoutes))]
    public async Task TenantAdmin_refused_on_each_write_route(string route, string policy)
    {
        (await PolicyAllowsAsync(TenantAdmin(), policy)).ShouldBeFalse($"{route} must refuse TenantAdmin.");
    }

    [Theory]
    [MemberData(nameof(PolicyProtectedWriteRoutes))]
    public async Task ClusterAdmin_refused_on_each_write_route(string route, string policy)
    {
        (await PolicyAllowsAsync(ClusterAdmin(), policy)).ShouldBeFalse($"{route} must refuse ClusterAdmin.");
    }

    [Theory]
    [MemberData(nameof(CapabilityGuardedWriteRoutes))]
    public async Task It_principals_fail_every_capability_the_write_handlers_call(string route)
    {
        var domains = NullFake.Create<IGovernanceDomainRepository>();
        var caps = new GovernanceCapabilities(domainRepository: domains);

        foreach (var user in new[] { TenantAdmin(), ClusterAdmin() })
        {
            caps.IsGovernance(user, Tenant).ShouldBeFalse($"{route}: IT is not governance");
            caps.CanEditPolicy(user, Tenant).ShouldBeFalse($"{route}: IT cannot edit policy");

            var ctx = SecurityPrincipalContext.FromPrincipal(user, overrideTenant: Tenant) with
            {
                GroupSids = new HashSet<Sid>(),
                GroupResolutionStatus = Autheris.Domain.Identity.GroupResolutionStatus.Complete
            };
            (await caps.CanClassifyAsync(ctx, new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), Tenant, CancellationToken.None))
                .ShouldBeFalse($"{route}: IT cannot classify");
        }
    }

    [Fact]
    public async Task Governance_admin_is_allowed_by_the_same_policy()
    {
        var dg = Wp0.User("S-1-5-21-dg", Tenant.Value, ["GovernanceAdmin"]);
        (await PolicyAllowsAsync(dg, GatewayPolicies.GovernanceAdmin)).ShouldBeTrue();
        new GovernanceCapabilities().IsGovernance(dg, Tenant).ShouldBeTrue();
    }

    private static DatasetCatalogAdminService CatalogService(IGovernanceCapabilities caps, Writes writes)
    {
        var tables = NullFake.Create<ITableMetadataRepository>((m, args) => m.Name switch
        {
            nameof(ITableMetadataRepository.GetActiveTableIdentifierAsync) =>
                Task.FromResult<TableIdentifier?>(((TableIdentifier)args![0]!).Equals(Orders) ? Orders : null),
            nameof(ITableMetadataRepository.GetDatasetCatalogSettingsAsync) =>
                Task.FromResult<DatasetCatalogSettings?>(new DatasetCatalogSettings([])),
            nameof(ITableMetadataRepository.ReplaceDatasetTagsAsync) => CountWrite(writes),
            _ => NullFake.UseDefault
        });
        return new DatasetCatalogAdminService(
            tables, NullFake.Create<IDataOwnershipRepository>(), NullFake.Create<IAuditWriter>(),
            new StaticOptions(new GatewayOptions { Catalog = new DataCatalogOptions { MaxTagsPerDataset = 32 } }),
            capabilities: caps);
    }

    private static Task<DatasetCatalogChange?> CountWrite(Writes writes)
    {
        writes.TagWrites++;
        return Task.FromResult<DatasetCatalogChange?>(null);
    }

    [Fact]
    public async Task ClusterAdmin_cannot_edit_dataset_tags_or_privacy()
    {
        var writes = new Writes();
        var service = CatalogService(new StubCapabilities(it: true, governance: false), writes);

        var result = await service.ApplyAsync(ClusterAdmin(), "sales.dbo.orders", new DatasetCatalogEdit(["pii"], null, null, null, true), "admin", default);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.Forbidden);
        writes.TagWrites.ShouldBe(0);
    }

    [Fact]
    public async Task Governance_capacity_may_still_edit_dataset_tags()
    {
        var service = CatalogService(new StubCapabilities(it: true, governance: true), new Writes());

        var result = await service.ApplyAsync(ClusterAdmin(), "sales.dbo.orders", new DatasetCatalogEdit(["pii"], null, null), "admin", default, dryRun: true);

        result.Outcome.ShouldNotBe(DatasetCatalogOutcome.Forbidden);
    }

    private sealed class StaticOptions(GatewayOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private sealed class Writes
    {
        public int TagWrites { get; set; }
    }

    private sealed class StubCapabilities(bool it, bool governance) : IGovernanceCapabilities
    {
        public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) => governance;
        public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => it;
        public ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext principal, ContainerRef container, CancellationToken ct) => ValueTask.FromResult(false);
        public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext principal, ContainerRef container, TenantId objectTenant, CancellationToken ct) => ValueTask.FromResult(false);
        public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId objectTenant) => governance;
    }
}
