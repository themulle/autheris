namespace Autheris.Tests.Unit.Governance;

using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Identity;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-SIMPL-87 (OD-9): global operations are reserved to the canonical ClusterAdmin; GovernanceAdmin is always tenant-bound,
/// and a token without a tenant claim resolves to the default tenant (never to a global scope).
/// </summary>
[Trait("Category", "Security")]
public sealed class Batch5OD9ScopeTests
{
    private static ClaimsPrincipal Principal(string? tenant, string role, bool defaultStamped = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "admin"), new(ClaimTypes.Role, role) };
        if (tenant is not null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        if (defaultStamped)
        {
            claims.Add(new Claim("autheris:tenant_status", "default"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public void GovernanceAdmin_without_tenant_claim_is_not_global()
    {
        var principal = Principal(null, "GovernanceAdmin");

        EndpointSecurity.IsGlobalGovernanceAdmin(principal).ShouldBeFalse();
        EndpointSecurity.IsTenantBoundGovernanceAdmin(principal).ShouldBeTrue();
    }

    [Fact]
    public void GovernanceAdmin_with_tenant_is_tenant_bound()
    {
        var principal = Principal("tenant-a", "GovernanceAdmin");

        EndpointSecurity.IsGlobalGovernanceAdmin(principal).ShouldBeFalse();
        EndpointSecurity.IsTenantBoundGovernanceAdmin(principal).ShouldBeTrue();
    }

    [Fact]
    public void Canonical_ClusterAdmin_is_global()
    {
        var principal = Principal("tenant-a", "ClusterAdmin");

        EndpointSecurity.IsGlobalGovernanceAdmin(principal).ShouldBeTrue();
        EndpointSecurity.IsTenantBoundGovernanceAdmin(principal).ShouldBeFalse();
    }

    [Fact]
    public void Null_principal_is_not_global()
    {
        EndpointSecurity.IsGlobalGovernanceAdmin(null).ShouldBeFalse();
        EndpointSecurity.IsTenantBoundGovernanceAdmin(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Token_without_tenant_claim_uses_default_tenant()
    {
        // A claim-less GovernanceAdmin token runs through the real claims transformation (the one default-assignment ingress).
        var registry = Substitute.For<IIdentityRealmRegistry>();
        var resolver = new TenantResolver(() => new TenancyOptions { DefaultTenantId = "dflt" }, registry, NullLogger<TenantResolver>.Instance);
        var transformation = new EnterpriseClaimsTransformation(registry, tenantResolver: resolver);

        var principal = await transformation.TransformAsync(Principal(null, "GovernanceAdmin"));

        principal.GetTenantId().Value.ShouldBe("dflt");
        EndpointSecurity.IsGlobalGovernanceAdmin(principal).ShouldBeFalse();
        EndpointSecurity.IsTenantBoundGovernanceAdmin(principal).ShouldBeTrue();
    }

    public static TheoryData<string> GlobalOnlySites() =>
    [
        "IsOpenApiAdmin",
        "IsListAdmin",
        "CatalogCrossDataSourceOptIn",
        "CdcStreamAdmin",
        "SimulationSunsettingRules",
        "DataContractPrivilegedView",
        "DbtCrossTenantOperation",
        "HitLCrossTenantView",
        "ClassificationOwnerCapable"
    ];

    // SEC-SIMPL-87-b: a tenant-bound GovernanceAdmin (with and without tenant claim) never reaches a global-only operation.
    [Theory]
    [MemberData(nameof(GlobalOnlySites))]
    public async Task Tenant_bound_GovernanceAdmin_is_denied_on_global_only_sites(string site)
    {
        foreach (var tenant in new string?[] { "tenant-a", null })
        {
            var context = new DefaultHttpContext { User = Principal(tenant, "GovernanceAdmin") };
            var denied = site switch
            {
                "IsOpenApiAdmin" => !ODataEndpoints.IsOpenApiAdmin(context.User),
                "IsListAdmin" => !SqlEndpointRoutes.IsListAdmin(context.User),
                "CatalogCrossDataSourceOptIn" => IsForbidden(await CatalogApiEndpoints.SetRelationCrossDataSourceAsync(
                    Guid.NewGuid(), new SetRelationCrossDataSourceDto(true), context,
                    Substitute.For<Autheris.Application.Common.Interfaces.ITableRelationRepository>(), default)),
                "CdcStreamAdmin" => IsForbidden(await CdcStreamAdminEndpoints.ListAsync(context, null!, default)),
                "SimulationSunsettingRules" => IsForbidden(await GovernanceEndpoints.RegisterSunsettingRuleAsync(
                    null, Substitute.For<ISchemaSunsettingService>(), context)),
                // The remaining sites branch directly on the shared predicate (lambda handlers or non-privileged scope).
                _ => !EndpointSecurity.IsGlobalGovernanceAdmin(context.User)
            };

            denied.ShouldBeTrue($"{site} / tenant={tenant ?? "none"}");
        }
    }

    private static bool IsForbidden(IResult result) =>
        result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status403Forbidden };
}
