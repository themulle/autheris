namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Mcp.ScopeLevelTestKit;

/// <summary>MISC-06: the consent stage tags its denials with a typed kind; metric consents may only cure <see cref="TableAccessDenialKind.ConsentMissing"/>.</summary>
public sealed class TableAccessPolicyDenialKindTests
{
    private static readonly TableMetadata Meta = CreateTable();

    private static TableAccessPolicy Policy(IReadOnlyList<Consent> consents, IPolicyEnforcementService? abac = null)
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>(consents));
        return new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            Substitute.For<IConsentCacheService>(),
            abac ?? Substitute.For<IPolicyEnforcementService>(),
            Substitute.For<IRebacEvaluator>(),
            Substitute.For<IClientIpResolver>(),
            new GatewayOptions(),
            NullMandatoryRowFilterResolver.Instance,
            contractManager: null);
    }

    private static TableAccessQuery Query(IReadOnlyList<Consent>? supplemental = null) =>
        new(new Sid(UserSid), new TenantId(Tenant), new HashSet<Sid>(), new HashSet<string>(), Meta, SupplementalConsents: supplemental);

    [Fact]
    [Trait("Category", "Security")]
    public async Task NoConsent_DenialKindConsentMissing()
    {
        var decision = await Policy([]).DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenialKind.ShouldBe(TableAccessDenialKind.ConsentMissing);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DenyConsent_DenialKindConsentDeny()
    {
        var decision = await Policy([DenyEffect()]).DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenialKind.ShouldBe(TableAccessDenialKind.ConsentDeny);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CasbinDeny_DenialKindPolicy()
    {
        var abac = Substitute.For<IPolicyEnforcementService>();
        abac.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        abac.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(TableAccessDecision.Denied(Orders, "denied")));

        var decision = await Policy([Allow(Rule("id", ColumnAccessLevel.Clear))], abac).DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenialKind.ShouldBe(TableAccessDenialKind.Policy);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SupplementalConsent_CuresConsentMissing_ButNotCasbinDeny()
    {
        var supplemental = new[] { Allow(Rule("id", ColumnAccessLevel.Clear)) };

        var cured = await Policy([]).DecideAsync(Query(supplemental), CancellationToken.None);
        cured.IsAllowed.ShouldBeTrue();

        var abac = Substitute.For<IPolicyEnforcementService>();
        abac.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        abac.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(TableAccessDecision.Denied(Orders, "denied")));
        var stillDenied = await Policy([], abac).DecideAsync(Query(supplemental), CancellationToken.None);
        stillDenied.IsAllowed.ShouldBeFalse();
    }
}
