namespace Autheris.Tests.Unit.VirtualFilters;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mesh.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, decision 7: Envoy ext_authz enforces no row filters. A caller whose profile has the requested
/// table in its scope is denied there (the data has to be read through a channel that applies the filter).
/// </summary>
public sealed class EnvoyVirtualFilterTests
{
    private static EnvoyExtAuthzService Service(params VirtualFilterAccessProfile[] profiles)
    {
        var policy = Substitute.For<IPolicyEnforcementService>();
        policy.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => TableAccessDecision.Allowed(ci.Arg<SecurityEvaluationContext>().TargetTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        var snapshot = new MandatoryRowFilterResolverTests.FixedSnapshot(new VirtualFilterSnapshot(1, [], profiles));
        return new EnvoyExtAuthzService(policy, NullLogger<EnvoyExtAuthzService>.Instance, snapshot);
    }

    private static ClaimsPrincipal Caller(string sid) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, sid), new Claim("tenant_id", MandatoryRowFilterResolverTests.Tenant.Value)], "MeshTls"));

    // The path /api/v1/air1 addresses the table default.public.air1 (ExtractResourceFromPath).
    private static VirtualFilterAccessProfile DavidProfile(string scope = "default.*.*") =>
        MandatoryRowFilterResolverTests.Profile(UncoveredPolicy.Skip, new FilterBinding { FilterName = "filter_a" }) with { Scope = scope };

    private static Task<EnvoyCheckResponse> CheckAsync(EnvoyExtAuthzService service, string sid) =>
        service.CheckHttpAsync("GET", "/api/v1/air1", new Dictionary<string, string>(), Caller(sid), CancellationToken.None).AsTask();

    [Fact]
    public async Task CallerWithAProfileCoveringTheTable_IsDenied()
    {
        var response = await CheckAsync(Service(DavidProfile()), MandatoryRowFilterResolverTests.David.Value);

        response.HttpResponse.DeniedResponse.ShouldNotBeNull();
    }

    [Fact]
    public async Task OtherCallers_AndTablesOutsideTheScope_AreUnaffected()
    {
        (await CheckAsync(Service(DavidProfile()), "S-1-5-21-OTHER")).HttpResponse.OkResponse.ShouldNotBeNull();
        (await CheckAsync(Service(DavidProfile("default.md.*")), MandatoryRowFilterResolverTests.David.Value)).HttpResponse.OkResponse.ShouldNotBeNull();
    }

    [Fact]
    public async Task RoleProfiles_CannotBeCheckedHere_AndDeny()
    {
        var roleProfile = DavidProfile() with { GranteeType = GranteeType.Role, GranteeSid = null, RoleName = "CraneAnalyst" };

        (await CheckAsync(Service(roleProfile), "S-1-5-21-OTHER")).HttpResponse.DeniedResponse.ShouldNotBeNull();
    }
}
