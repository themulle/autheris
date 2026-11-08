namespace Autheris.Tests.Unit.Policy;

using System;
using Autheris.Application.Governance;
using Autheris.Application.Policy;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AccessDecisionPointTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Constructor_without_policy_service_throws()
    {
        var options = Options.Create(new GatewayOptions());

        var ex = Should.Throw<ArgumentNullException>(() =>
            new AccessDecisionPoint(
                options,
                policyEnforcementService: null!));

        ex.ParamName.ShouldBe("policyEnforcementService");
    }
}
