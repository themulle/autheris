namespace Autheris.Tests.Unit.Policy;

using System;
using Autheris.Application.Governance;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Services;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Options;
using Autheris.Application.Common.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class TableAccessPolicySecurityTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Constructor_without_policy_service_throws()
    {
        var repo = Substitute.For<IConsentRepository>();
        var resolution = new ConsentResolutionService();
        var cache = NullConsentCacheService.Instance;
        var rebac = NullRebacEvaluator.Instance;
        var options = new GatewayOptions();
        var filters = NullMandatoryRowFilterResolver.Instance;

        var ex = Should.Throw<ArgumentNullException>(() =>
            new TableAccessPolicy(
                repo,
                resolution,
                cache,
                policyEnforcementService: null!,
                rebac,
                clientIpResolver: null,
                options,
                filters));

        ex.ParamName.ShouldBe("policyEnforcementService");
    }
}
