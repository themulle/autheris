namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 3: the mandatory predicate joins the consent decision with AND in the single access
/// decision (<see cref="TableAccessPolicy"/>). An additional consent without row filter cannot lift it, a binding
/// never grants, the consent cache stays free of it, and Casbin restrictions still apply on top.
/// </summary>
public sealed class TableAccessPolicyMandatoryFilterTests
{
    private static readonly TenantId Tenant = MandatoryRowFilterResolverTests.Tenant;
    private static readonly Sid David = MandatoryRowFilterResolverTests.David;
    private static readonly TableMetadata Air1 = MandatoryRowFilterResolverTests.Table("fms", "air1", "id", "client_id", "region");
    private static readonly TableMetadata Crane = MandatoryRowFilterResolverTests.Table("md", "crane", "serial_number");

    private static readonly VirtualFilterSnapshot Snapshot = new(1,
        [MandatoryRowFilterResolverTests.Filter("filter_a", "client_id")],
        [MandatoryRowFilterResolverTests.Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a", TargetPattern = "lwetem_prod.*.*.client_id" })]);

    private static Consent Allow(TableIdentifier table, params ConsentRowFilter[] filters) => new()
    {
        TableIdentifier = table,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = David,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = filters
    };

    private static ConsentRowFilter RegionEu() => new() { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EU\"" };

    private static IConsentRepository Consents(params Consent[] consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c => c.TableIdentifier == ci.ArgAt<TableIdentifier>(1)).ToList()));
        return repo;
    }

    private static TableAccessPolicy Policy(
        IConsentRepository consents,
        IConsentCacheService? cache = null,
        IPolicyEnforcementService? casbin = null,
        GatewayOptions? options = null) =>
        new(consents, new ConsentResolutionService(), cache, casbin, rebacEvaluator: null, clientIpResolver: null, options ?? new GatewayOptions(),
            new MandatoryRowFilterResolver(new MandatoryRowFilterResolverTests.FixedSnapshot(Snapshot), new MandatoryRowFilterResolverTests.PlaceholderPredicates()));

    private static TableAccessQuery Query(TableMetadata table) =>
        new(David, Tenant, new HashSet<Sid>(), new HashSet<string>(), table);

    [Fact]
    public async Task ConsentFilterAndBinding_AreCombinedWithAnd()
    {
        var decision = await Policy(Consents(Allow(Air1.Identifier, RegionEu()))).DecideAsync(Query(Air1), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldStartWith("(");
        decision.CombinedRowFilterSql.ShouldContain("region");
        decision.CombinedRowFilterSql.ShouldEndWith(") AND (P_filter_a(client_id))");
        decision.MandatoryRowPredicateSql.ShouldBe("P_filter_a(client_id)");
        decision.AppliedVirtualFilters.ShouldBe(["filter_a"]);
    }

    [Fact]
    public async Task AdditionalConsentWithoutRowFilter_CannotLiftTheMandatoryPredicate()
    {
        var decision = await Policy(Consents(Allow(Air1.Identifier, RegionEu()), Allow(Air1.Identifier))).DecideAsync(Query(Air1), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("P_filter_a(client_id)");   // the consent filter is lifted (OR), the binding is not
    }

    [Fact]
    public async Task BindingWithoutConsent_GrantsNothing()
    {
        var decision = await Policy(Consents()).DecideAsync(Query(Air1), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task UncoveredObject_IsDenied_EvenWithAnUnrestrictedConsent()
    {
        var decision = await Policy(Consents(Allow(Crane.Identifier))).DecideAsync(Query(Crane), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldHaveSingleItem().ShouldContain("uncovered");
    }

    [Fact]
    public async Task CasbinRestriction_IsAddedOnTop()
    {
        var casbin = Substitute.For<IPolicyEnforcementService>();
        casbin.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        casbin.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(Air1.Identifier, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: "CASBIN", hasUnconstrainedColumnAllow: true));

        var decision = await Policy(Consents(Allow(Air1.Identifier)), casbin: casbin).DecideAsync(Query(Air1), CancellationToken.None);

        decision.CombinedRowFilterSql.ShouldBe("(P_filter_a(client_id)) AND (CASBIN)");
    }

    [Fact]
    public async Task ConsentCache_StoresTheDecisionWithoutTheMandatoryPredicate()
    {
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((TableAccessDecision?)null);

        var decision = await Policy(Consents(Allow(Air1.Identifier)), cache).DecideAsync(Query(Air1), CancellationToken.None);

        decision.CombinedRowFilterSql.ShouldBe("P_filter_a(client_id)");
        await cache.Received(1).SetCachedDecisionAsync(Tenant, David, Air1.Identifier,
            Arg.Is<TableAccessDecision>(d => d.CombinedRowFilterSql == null && d.MandatoryRowPredicateSql == null),
            Arg.Any<TimeSpan>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachedConsentDecision_StillGetsTheMandatoryPredicate()
    {
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(Air1.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var decision = await Policy(Consents(), cache).DecideAsync(Query(Air1), CancellationToken.None);

        decision.CombinedRowFilterSql.ShouldBe("P_filter_a(client_id)");
    }

    [Fact]
    public async Task ConsentBypass_StillAppliesTheBinding()
    {
        // Decision 1: bindings only restrict, so a test system with the consent bypass shows the same filters.
        var options = new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true } };
        options.IsConsentBypassed.ShouldBeTrue();

        var decision = await Policy(Consents(), options: options).DecideAsync(Query(Air1), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("P_filter_a(client_id)");
        (await Policy(Consents(), options: options).DecideAsync(Query(Crane), CancellationToken.None)).IsAllowed.ShouldBeFalse();
    }
}
