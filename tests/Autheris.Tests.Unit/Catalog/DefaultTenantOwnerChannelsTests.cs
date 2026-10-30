namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-DT-3 (DT-A-7): the default tenant owns the unrestricted candidate sets of OData and the tenant-less configured users.</summary>
[Trait("Category", "Security")]
public sealed class DefaultTenantOwnerChannelsTests
{
    private static readonly TenantId D = new("tenant-d");

    private static TenantResolver Resolver(string? defaultTenant) =>
        new(() => new TenancyOptions { DefaultTenantId = defaultTenant }, null, NullLogger<TenantResolver>.Instance);

    private static async Task<string[]> CsdlEntitiesAsync(ClaimsPrincipal user, ITenantResolver? resolver)
    {
        var tables = new[] { Wp0.Table("alpha", "t_alpha"), Wp0.Table("beta", "t_beta"), Wp0.Table("default", "t_shared") };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(tables));
        var exec = Substitute.For<IGatewayExecutionService>();
        exec.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(TableAccessDecision.Allowed(
                ci.Arg<TableMetadata>().Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));
        var handler = new ODataHandler(repo, exec, NullLogger<ODataHandler>.Instance, tenantResolver: resolver);

        var csdl = await handler.GetMetadataCsdlAsync(user);
        var ns = XNamespace.Get("http://docs.oasis-open.org/odata/ns/edm");
        return XDocument.Parse(csdl).Descendants(ns + "EntityType").Select(e => e.Attribute("Name")!.Value).Order().ToArray();
    }

    [Fact]
    public async Task OData_DefaultTenant_Sees_Every_Table_As_Candidate()
    {
        var names = await CsdlEntitiesAsync(Wp0.User("u", null), Resolver(D.Value));

        names.Length.ShouldBe(3);
    }

    [Fact]
    public async Task OData_Other_Tenant_Is_Confined_To_Its_Domain_And_The_Shared_Default_Domain()
    {
        var names = await CsdlEntitiesAsync(Wp0.User("u", "alpha"), Resolver(D.Value));

        names.Length.ShouldBe(2);
        names.ShouldNotContain(n => n.Contains("t_beta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OData_Claimless_Without_Default_Sees_Nothing()
    {
        (await CsdlEntitiesAsync(Wp0.User("u", null), Resolver(null))).ShouldBeEmpty();
        (await CsdlEntitiesAsync(Wp0.User("u", null), null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task OData_Invalid_Claim_Sees_Nothing_Even_With_Default()
        => (await CsdlEntitiesAsync(Wp0.User("u", "bad tenant!!"), Resolver(D.Value))).ShouldBeEmpty();

    private static async Task<int> ResolveCountAsync(string? defaultTenant, string callerTenant)
    {
        var options = new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = defaultTenant } };
        options.Authentication.BasicAuth.Users.Add(new BasicAuthUserConfig { Username = "bob", Sid = "S-BOB", TenantId = null });
        options.Authentication.BasicAuth.Users.Add(new BasicAuthUserConfig { Username = "bobby", Sid = "S-BOBBY", TenantId = "tenant-a" });
        var service = new PrincipalResolverService(Options.Create(options), NullLogger<PrincipalResolverService>.Instance);
        return (await service.ResolvePrincipalAsync("bob", new RequestContext(new TenantId(callerTenant), new Sid("user:admin")))).Count;
    }

    [Fact]
    public async Task PrincipalResolver_Configured_User_Without_Tenant_Belongs_To_The_Default_Tenant()
    {
        (await ResolveCountAsync(D.Value, D.Value)).ShouldBe(1);
        (await ResolveCountAsync(D.Value, "tenant-a")).ShouldBe(1);
        (await ResolveCountAsync(D.Value, "tenant-b")).ShouldBe(0);
        (await ResolveCountAsync(null, D.Value)).ShouldBe(0);
        (await ResolveCountAsync(null, Autheris.Tests.Common.TestTenants.Default.Value)).ShouldBe(0);
    }
}
