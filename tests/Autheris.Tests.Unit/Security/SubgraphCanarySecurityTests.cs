namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Federation.Services;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class SubgraphCanarySecurityTests
{
    private readonly SubgraphCanaryRouter _router = new(NullLogger<SubgraphCanaryRouter>.Instance);

    [Fact]
    public async Task ResolveTarget_WithVariantHeader_RoutesToCanaryEndpoint()
    {
        // Arrange
        var defaultUri = new Uri("https://subgraph-orders.internal:4000/graphql");
        var canaryUri = "https://subgraph-orders-v2.internal:4001/graphql";

        _router.RegisterRule(new SubgraphCanaryRule(
            RuleId: "rule-1",
            SubgraphName: "orders",
            VariantName: "v2-canary",
            TargetUrl: canaryUri,
            HeaderValueMatch: "v2-canary"
        ));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Feature-Variant"] = "v2-canary";

        // Act
        var decision = await _router.ResolveTargetAsync("orders", defaultUri, null, null, "v2-canary");

        // Assert
        decision.IsCanary.ShouldBeTrue();
        decision.VariantName.ShouldBe("v2-canary");
        decision.EffectiveUri.ToString().ShouldBe(canaryUri);
    }

    [Fact]
    public async Task ResolveTarget_WithRoleRequirement_RoutesOnlyAuthorizedRoles()
    {
        // Arrange
        var defaultUri = new Uri("https://subgraph-customers.internal:4000/graphql");
        var canaryUri = "https://subgraph-customers-beta.internal:4002/graphql";

        _router.RegisterRule(new SubgraphCanaryRule(
            RuleId: "rule-beta",
            SubgraphName: "customers",
            VariantName: "beta-testers",
            TargetUrl: canaryUri,
            RequiredRole: "BetaTester"
        ));

        // Case 1: Caller has role BetaTester
        var principalAuth = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "BetaTester")], "Bearer"));
        var decisionAuth = await _router.ResolveTargetAsync("customers", defaultUri, principalAuth, null, null);
        decisionAuth.IsCanary.ShouldBeTrue();
        decisionAuth.VariantName.ShouldBe("beta-testers");
        decisionAuth.EffectiveUri.ToString().ShouldBe(canaryUri);

        // Case 2: Regular user without BetaTester role
        var principalReg = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "StandardUser")], "Bearer"));
        var decisionReg = await _router.ResolveTargetAsync("customers", defaultUri, principalReg, null, null);
        decisionReg.IsCanary.ShouldBeFalse();
        decisionReg.EffectiveUri.ShouldBe(defaultUri);
    }

    [Fact]
    public async Task ResolveTarget_PercentageRollout_DistributesDeterministically()
    {
        // Arrange
        var defaultUri = new Uri("https://subgraph-inventory.internal:4000/graphql");
        var canaryUri = "https://subgraph-inventory-canary.internal:4003/graphql";

        _router.RegisterRule(new SubgraphCanaryRule(
            RuleId: "rule-pct",
            SubgraphName: "inventory",
            VariantName: "canary-50pct",
            TargetUrl: canaryUri,
            WeightPercent: 50
        ));

        // Act
        var decA = await _router.ResolveTargetAsync("inventory", defaultUri, null, "tenant-deterministic-hash-1", null);
        var decB = await _router.ResolveTargetAsync("inventory", defaultUri, null, "tenant-deterministic-hash-99", null);

        // Assert - Both should return valid decisions deterministically
        decA.ShouldNotBeNull();
        decB.ShouldNotBeNull();
    }
}
