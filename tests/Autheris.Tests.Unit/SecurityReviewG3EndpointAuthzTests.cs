namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Mesh.Services;
using Autheris.Application.OData.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Security review G3: HTTP endpoint authorization fixes.</summary>
public class SecurityReviewG3EndpointAuthzTests
{
    private static DefaultHttpContext Ctx(string tenant, string[] roles, IDataOwnershipRepository? repo = null)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToList();
        claims.Add(new Claim("tenant_id", tenant));
        claims.Add(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-OWNER"));
        var services = new ServiceCollection();
        if (repo != null) services.AddSingleton(repo);
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role)),
            RequestServices = services.BuildServiceProvider()
        };
    }

    [Fact]
    public void A1_PlatformAdmin_IsTenantScopedNotCdcClusterAdmin()
    {
        var user = Ctx("tenant-a", ["PlatformAdmin"]).User;
        StreamingCdcEndpoints.IsCdcClusterAdmin(user).ShouldBeFalse();
        StreamingCdcEndpoints.IsAuthorizedCdcIngestion(user).ShouldBeTrue();
        StreamingCdcEndpoints.IsCdcClusterAdmin(Ctx("tenant-a", ["ClusterAdmin"]).User).ShouldBeTrue();
    }

    [Fact]
    public async Task A2_PolicySimulation_AuthorizationMatrix()
    {
        foreach (var role in new[] { "GovernanceAdmin", "ClusterAdmin", "PrivacyAdmin", "Auditor" })
        {
            (await GovernanceEndpoints.IsAuthorizedForSimulationAsync(Ctx("t", [role]), null)).ShouldBeTrue(role);
        }

        (await GovernanceEndpoints.IsAuthorizedForSimulationAsync(Ctx("t", ["Reader"]), "t.dbo.x")).ShouldBeFalse();

        var repo = Substitute.For<IDataOwnershipRepository>();
        repo.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(true);
        (await GovernanceEndpoints.IsAuthorizedForSimulationAsync(Ctx("t", ["DataOwner"], repo), null)).ShouldBeFalse();
        (await GovernanceEndpoints.IsAuthorizedForSimulationAsync(Ctx("t", ["DataOwner"], repo), "t.dbo.x")).ShouldBeTrue();

        var denyRepo = Substitute.For<IDataOwnershipRepository>();
        denyRepo.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(false);
        (await GovernanceEndpoints.IsAuthorizedForSimulationAsync(Ctx("t", ["DataOwner"], denyRepo), "t.dbo.x")).ShouldBeFalse();
    }

    [Fact]
    public void Gateway_NameListRoleCheck_IsExactNotHierarchical()
    {
        var analyst = Ctx("t", ["Analyst"]).User;
        GatewayPolicies.HasAnyRole(analyst, GatewayPolicies.SchemaPublisherRoles).ShouldBeFalse();
        GatewayPolicies.HasAnyRole(analyst, GatewayPolicies.SchemaAdminRoles).ShouldBeFalse();

        GatewayPolicies.HasAnyRole(Ctx("t", ["SchemaPublisher"]).User, GatewayPolicies.SchemaAdminRoles).ShouldBeFalse();
        GatewayPolicies.HasAnyRole(Ctx("t", ["PrivacyAdmin"]).User, GatewayPolicies.SchemaAdminRoles).ShouldBeFalse();

        GatewayPolicies.HasAnyRole(Ctx("t", ["SchemaAdmin"]).User, GatewayPolicies.SchemaPublisherRoles).ShouldBeTrue();
        GatewayPolicies.HasAnyRole(Ctx("t", ["ClusterAdmin"]).User, GatewayPolicies.SchemaAdminRoles).ShouldBeTrue();
        GatewayPolicies.HasAnyRole(Ctx("t", ["Developer"]).User, GatewayPolicies.SchemaPublisherRoles).ShouldBeTrue();
    }

    [Fact]
    public void SqlEndpointListing_HidesInternalsForNonAdmins()
    {
        var def = new SqlEndpointDefinition("q", "sum", "SELECT secret FROM hr.salaries", "ds",
            [new SqlEndpointParameter("country", "@country", typeof(string))], null, ["hr.salaries"]);
        var json = System.Text.Json.JsonSerializer.Serialize(SqlEndpointRoutes.ToPublicListing([def]));
        json.ShouldContain("country");
        json.ShouldNotContain("salaries");
        json.ShouldNotContain("SELECT");
        json.ShouldNotContain("\"ds\"");

        SqlEndpointRoutes.IsListAdmin(Ctx("t", ["Reader"]).User).ShouldBeFalse();
        SqlEndpointRoutes.IsListAdmin(Ctx("t", ["GovernanceAdmin"]).User).ShouldBeTrue();
    }

    [Fact]
    public void OpenApi_TenantScopedRolesAreNotAdmins()
    {
        foreach (var r in new[] { "DataOwner", "SchemaAdmin", "CatalogReader", "PlatformAdmin" })
        {
            ODataEndpoints.IsOpenApiAdmin(Ctx("t", [r]).User).ShouldBeFalse(r);
        }

        ODataEndpoints.IsOpenApiAdmin(Ctx("t", ["ClusterAdmin"]).User).ShouldBeTrue();
        ODataEndpoints.IsOpenApiAdmin(Ctx("t", ["GovernanceAdmin"]).User).ShouldBeTrue();
    }

    [Fact]
    public void OpenApiCacheKey_DoesNotCollideForDifferentDomains()
    {
        OpenApiCacheManager.BuildKey("a.b", false, false).ShouldBeNull();
        OpenApiCacheManager.BuildKey("ab", false, false).ShouldNotBeNull();
        OpenApiCacheManager.BuildKey("sales", false, false).ShouldNotBe(OpenApiCacheManager.BuildKey("sales", true, false));
    }

    [Fact]
    public async Task OpenApiCache_DoesNotCacheNonIdentifierDomains()
    {
        var cache = new OpenApiCacheManager();
        var calls = 0;
        Task<string> Factory(CancellationToken _) { calls++; return Task.FromResult("x" + calls); }
        await cache.GetOrAddAsync("a.b", false, false, Factory);
        await cache.GetOrAddAsync("a.b", false, false, Factory);
        calls.ShouldBe(2);
    }

    [Fact]
    public void Envoy_ExportEmitsPathPrefixAndValidatesParameters()
    {
        var svc = (EnvoyExtAuthzService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EnvoyExtAuthzService));
        svc.GenerateIstioEnvoyFilterYaml().ShouldContain("path_prefix: /api/v1/envoy/check");

        Should.Throw<ArgumentException>(() => svc.GenerateIstioEnvoyFilterYaml(new EnvoyFilterExportOptions { MeshNamespace = "x\n  evil: true" }));
        Should.Throw<ArgumentException>(() => svc.GenerateIstioEnvoyFilterYaml(new EnvoyFilterExportOptions { ServiceHost = "h:1\nfoo: bar" }));
        Should.Throw<ArgumentException>(() => svc.GenerateIstioEnvoyFilterYaml(new EnvoyFilterExportOptions { ServicePort = 70000 }));
        Should.Throw<ArgumentException>(() => svc.GenerateIstioWasmPluginYaml(new EnvoyFilterExportOptions { MeshNamespace = "a b" }));
    }
}
