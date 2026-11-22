namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

/// <summary>SR-L-04: malformed role values can never resolve to a role; the evaluators agree.</summary>
[Trait("Category", "Security")]
public class GatewayRoleParsingTests
{
    private readonly IGatewayRoleEvaluator _evaluator = new GatewayRoleEvaluator();

    public static TheoryData<string> AdversarialValues => new()
    {
        ":ClusterAdmin",
        ":GovernanceAdmin",
        "::ClusterAdmin",
        "tenant-1:",
        ":",
        "a:b:ClusterAdmin",
        "a:b:SchemaPublisher",
        ":SchemaPublisher",
        "tenant-1:ClusterAdmin:"
    };

    private static ClaimsPrincipal PrincipalWith(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));

    [Theory]
    [MemberData(nameof(AdversarialValues))]
    public void GetEffectiveRoles_AdversarialValue_YieldsNoRoles(string raw)
    {
        var user = PrincipalWith(raw);

        _evaluator.GetEffectiveRoles(user).ShouldBeEmpty();
        _evaluator.GetEffectiveRoles(user, "tenant-1").ShouldBeEmpty();
        _evaluator.GetEffectiveRoles(user, "a").ShouldBeEmpty();
    }

    [Fact]
    public void GetEffectiveRoles_LeadingColonClusterAdmin_YieldsNoRoles()
    {
        var user = PrincipalWith(":ClusterAdmin");

        _evaluator.GetEffectiveRoles(user).ShouldBeEmpty();
        _evaluator.HasRole(user, GatewayRole.ClusterAdmin).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(AdversarialValues))]
    public void HasRole_StringOverload_AdversarialValue_False(string raw)
    {
        var user = PrincipalWith(raw);

        _evaluator.HasRole(user, "ClusterAdmin").ShouldBeFalse();
        _evaluator.HasRole(user, "SchemaPublisher", "tenant-1").ShouldBeFalse();
    }

    [Fact]
    public void HasRole_StringOverload_ColonClusterAdmin_False()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "ClusterAdmin")], "Test"));

        _evaluator.HasRole(user, "tenant-1:ClusterAdmin").ShouldBeFalse();
        _evaluator.HasRole(user, ":ClusterAdmin").ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(AdversarialValues))]
    public void SecurityPrincipalContext_AdversarialValue_GrantsNothing(string raw)
    {
        var context = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-U1"),
            TenantId = new TenantId("tenant-1"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string> { raw },
            ClusterRoles = new HashSet<string> { raw },
            AuthenticationScheme = "Test"
        };

        context.IsClusterAdmin.ShouldBeFalse();
        context.HasRole(GatewayRole.ClusterAdmin).ShouldBeFalse();
        context.HasRole(GatewayRole.SchemaPublisher).ShouldBeFalse();
    }

    [Fact]
    public void GetEffectiveRoles_WellFormedValues_StillResolve()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "ClusterAdmin"), new Claim(ClaimTypes.Role, "tenant-1:SchemaPublisher"), new Claim(ClaimTypes.Role, "*:Consumer")],
            "Test"));

        var global = _evaluator.GetEffectiveRoles(user);
        global.Count.ShouldBe(1);
        global.ShouldContain(GatewayRole.ClusterAdmin);
        _evaluator.GetEffectiveRoles(user, "tenant-1").ShouldContain(GatewayRole.SchemaPublisher);
        _evaluator.GetEffectiveRoles(user, "tenant-2").ShouldContain(GatewayRole.Consumer);
        _evaluator.GetEffectiveRoles(user, "tenant-2").ShouldNotContain(GatewayRole.SchemaPublisher);
    }

    [Theory]
    [InlineData("SchemaPublisher", true)]
    [InlineData(" SchemaPublisher ", true)]
    [InlineData(":SchemaPublisher", false)]
    [InlineData("t:SchemaPublisher", false)]
    [InlineData("DataOwner", false)]
    [InlineData("", false)]
    [InlineData("Unknown", false)]
    public void TryParseGlobalRole_IsStrict(string input, bool expected)
    {
        GatewayRoleExtensions.TryParseGlobalRole(input, out var role).ShouldBe(expected);
        if (expected)
        {
            role.ShouldBe(GatewayRole.SchemaPublisher);
        }
    }

    [Theory]
    [InlineData("t1:SchemaPublisher", true)]
    [InlineData("*:Consumer", true)]
    [InlineData("t1:DataOwner", false)]
    [InlineData(":SchemaPublisher", false)]
    [InlineData("t1:", false)]
    [InlineData(":", false)]
    [InlineData("a:b:SchemaPublisher", false)]
    [InlineData("SchemaPublisher", false)]
    [InlineData("t1:Nope", false)]
    public void TryParseTenantScopedRole_RequiresExactlyOneColonAndNonEmptyParts(string input, bool expected)
    {
        GatewayRoleExtensions.TryParseTenantScopedRole(input, out var tenant, out _).ShouldBe(expected);
        if (!expected)
        {
            tenant.ShouldBeEmpty();
        }
    }
}
