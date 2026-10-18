namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Identity.Attributes;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// PLAN-GOV-129 tests 20, 21, 23, 24 and ST-129-06, ST-129-07 (A-1, A-4, BC-129-05): the Virtual Filter outcome stays value-free, the values are
/// bound per request in <see cref="TableAccessPolicy"/>, and a filter that cannot be bound is a deny, never a pass.
/// </summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeVirtualFilterTests : IDisposable
{
    private static readonly TenantId Tenant = MandatoryRowFilterResolverTests.Tenant;
    private static readonly Sid User = MandatoryRowFilterResolverTests.David;
    private static readonly TableMetadata Air1 = MandatoryRowFilterResolverTests.Table("fms", "air1", "id", "client_id", "region");
    private const string OidA = "11111111-1111-1111-1111-111111111111";
    private const string OidB = "22222222-2222-2222-2222-222222222222";
    private const string OidC = "33333333-3333-3333-3333-333333333333";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private static VirtualFilter CallerFilter(string name, string sql) => new()
    {
        TenantId = Tenant, Name = name, Source = "lwetem_prod", Sql = sql, SqlTargetColumns = ["client_id"]
    };

    private static readonly VirtualFilter ByDept = CallerFilter("by_dept", "from conf.client client where target.client_id = client.client_id and client.dept = {{caller.dept}}");
    private static readonly VirtualFilter ByRegions = CallerFilter("by_regions", "from conf.client client where target.client_id = client.client_id and client.region not in ({{caller.regions}})");

    private static VirtualFilterSnapshot Snapshot(params VirtualFilter[] filters) => new(1, filters,
    [
        MandatoryRowFilterResolverTests.Profile(UncoveredPolicy.Deny, filters.Select(f => new FilterBinding { FilterName = f.Name, TargetPattern = "lwetem_prod.*.*.client_id" }).ToArray())
    ]);

    private sealed class DroppingTypedBuilder(IVirtualFilterPredicateBuilder inner, string dropTypedOf) : IVirtualFilterPredicateBuilder
    {
        public string Build(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect) => inner.Build(filter, binding, target, dialect);

        public TrinoSqlEngine.Governance.PolicyPredicate? BuildTyped(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect, string parameterPrefix) =>
            filter.Name == dropTypedOf ? null : inner.BuildTyped(filter, binding, target, dialect, parameterPrefix);
    }

    private static MandatoryRowFilterResolver Resolver(VirtualFilterSnapshot snapshot, string? dropTypedOf = null)
    {
        IVirtualFilterPredicateBuilder builder = new StructuredFilterSqlBuilder();
        if (dropTypedOf != null) builder = new DroppingTypedBuilder(builder, dropTypedOf);
        return new MandatoryRowFilterResolver(new MandatoryRowFilterResolverTests.FixedSnapshot(snapshot), builder);
    }

    private static MandatoryFilterQuery Query() => new(User, new HashSet<Sid>(), new HashSet<string>(), Tenant, Air1);

    private sealed class MapProvider(Dictionary<string, string[]> byOid) : ICallerAttributeProvider
    {
        public string Kind => "claims";

        public ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct)
        {
            var oid = subject.KeyFor(SubjectKeyKind.EntraObjectId);
            return ValueTask.FromResult(oid is not null && byOid.TryGetValue(oid + "|" + definition.Name, out var values)
                ? CallerAttributeResult.Resolved(new CallerAttributeValue(values.Cast<object>().ToList(), SqlParameterType.String))
                : CallerAttributeResult.Missing());
        }
    }

    private CallerAttributeResolver CallerResolver(bool enabled = true)
    {
        var defs = new[]
        {
            CallerAttributeTestSupport.Definition("dept", "claims", CallerAttributeType.Text, CallerAttributeCardinality.Scalar, 1, 60) with { Tenant = Tenant },
            CallerAttributeTestSupport.Definition("regions", "claims", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60) with { Tenant = Tenant }
        };
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, defs, []));
        var provider = new MapProvider(new Dictionary<string, string[]>
        {
            [OidA + "|dept"] = ["D1"], [OidA + "|regions"] = ["EU", "US", "APAC"],
            [OidB + "|dept"] = ["D2"], [OidB + "|regions"] = ["LATAM"]
        });
        var resolver = new CallerAttributeResolver(registry, [provider], Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = CallerAttributeTestSupport.Options(enabled) }));
        _disposables.Add(resolver);
        return resolver;
    }

    private static IConsentRepository Consents()
    {
        var repo = Substitute.For<IConsentRepository>();
        var consent = new Consent
        {
            TableIdentifier = Air1.Identifier, TenantId = Tenant, GranteeType = GranteeType.User, GranteeSid = User, Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1), RowFilters = []
        };
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([consent]));
        return repo;
    }

    private static TableAccessPolicy Policy(MandatoryRowFilterResolver resolver, ICallerAttributeResolver? callerResolver) =>
        new(Consents(), new ConsentResolutionService(), Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
            Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null, new GatewayOptions(), resolver,
            rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer()),
            callerAttributes: callerResolver);

    private static ClaimsPrincipal Person(string oid) => CallerAttributeTestSupport.Principal(CallerAttributeTestSupport.Bearer, [("oid", oid)]);

    private static Task<TableAccessDecision> Decide(TableAccessPolicy policy, ClaimsPrincipal? principal) =>
        policy.DecideAsync(new TableAccessQuery(User, Tenant, new HashSet<Sid>(), new HashSet<string>(), Air1, Principal: principal), CancellationToken.None);

    // ---- resolver outcome ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Outcome_NamesTheAttributes_HasTheConstantText_AndHoldsNoValue()
    {
        var outcome = await Resolver(Snapshot(ByDept, ByRegions)).ResolveAsync(Query());

        outcome.IsDenied.ShouldBeFalse();
        outcome.CallerAttributes.ShouldBe(["dept", "regions"]);
        outcome.CallerAttributesTextConstant.ShouldBeTrue();
        outcome.PredicateSql.ShouldBe("((1 = 0)) AND ((1 = 0))");
        outcome.TypedPredicate.ShouldNotBeNull();
        outcome.TypedPredicate!.Parameters.Values.Where(v => v.Type == SqlParameterType.Null).Count().ShouldBe(2);   // two slots, no values
        outcome.TypedPredicate.Parameters.Values.ShouldAllBe(v => v.Value == null);
    }

    [Fact]
    public async Task TwoDifferentCallerFilters_BothStayInTheTypedPredicate_EvenThoughTheirTextIsIdentical()
    {
        var outcome = await Resolver(Snapshot(ByDept, ByRegions)).ResolveAsync(Query());
        outcome.TypedPredicate!.Parameters.Keys.ShouldContain("__gql_caller_dept__0");
        outcome.TypedPredicate.Parameters.Keys.ShouldContain("__gql_caller_regions__0");
    }

    [Fact]
    public async Task AFilterWithoutPlaceholders_HasNoCallerAttributes_AndNoTextConstant()
    {
        var plain = CallerFilter("plain", "from conf.client client where target.client_id = client.client_id and client.region = 'EU'");
        var outcome = await Resolver(Snapshot(plain)).ResolveAsync(Query());
        outcome.CallerAttributes.ShouldBeEmpty();
        outcome.CallerAttributesTextConstant.ShouldBeFalse();
    }

    [Fact]
    public async Task ST_129_Memo_SameKeyTuple_ServesOneValueFreeOutcome_ValuesDifferAfterBindingPerCaller()
    {
        var resolver = Resolver(Snapshot(ByDept));
        var first = await resolver.ResolveAsync(Query());
        var second = await resolver.ResolveAsync(Query());
        second.ShouldBeSameAs(first);   // the memoized object

        var policy = Policy(resolver, CallerResolver());
        var a = await Decide(policy, Person(OidA));
        var b = await Decide(policy, Person(OidB));
        a.MandatoryRowPredicateSql.ShouldBe(b.MandatoryRowPredicateSql);   // same shape (markers)
        a.MandatoryRowPredicateSql!.ShouldNotContain("D1");
        a.RowFilterParameters!.Values.ShouldContain("D1");
        b.RowFilterParameters!.Values.ShouldContain("D2");
        b.RowFilterParameters.Values.ShouldNotContain("D1");
        first.TypedPredicate!.Parameters.Values.ShouldAllBe(v => v.Value == null);   // the memoized outcome was never given a value
    }

    // ---- the decision point ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ST_129_Decision_BindsScalarAndList_PerCaller()
    {
        var policy = Policy(Resolver(Snapshot(ByDept, ByRegions)), CallerResolver());
        var a = await Decide(policy, Person(OidA));

        a.IsAllowed.ShouldBeTrue();
        a.RequireTypedRowPolicy().ShouldBeSameAs(a);
        a.MandatoryRowPredicateSql!.ShouldContain("@__gql_caller_dept__0");
        a.MandatoryRowPredicateSql!.ShouldContain("@__gql_caller_regions__0");
        a.RowFilterParameters!.Values.ShouldContain("D1");
        a.RowFilterParameters.Keys.Count(k => k.Contains("__gql_caller_regions__")).ShouldBe(4);   // 3 values padded to 4
    }

    [Theory]
    [InlineData(OidC)]      // no attribute at all
    public async Task ST_129_Decision_MissingAttribute_IsDenyAll_NotAPass(string oid)
    {
        var decision = await Decide(Policy(Resolver(Snapshot(ByDept)), CallerResolver()), Person(oid));
        decision.IsAllowed.ShouldBeTrue();
        decision.MandatoryRowPredicateSql.ShouldBe("1 = 0");
        (decision.RowFilterParameters ?? new Dictionary<string, object?>()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ST_129_Decision_NoPrincipal_FeatureOff_AndNoResolver_AreDenyAll()
    {
        (await Decide(Policy(Resolver(Snapshot(ByDept)), CallerResolver()), null)).MandatoryRowPredicateSql.ShouldBe("1 = 0");
        (await Decide(Policy(Resolver(Snapshot(ByDept)), CallerResolver(enabled: false)), Person(OidA))).MandatoryRowPredicateSql.ShouldBe("1 = 0");
        (await Decide(Policy(Resolver(Snapshot(ByDept)), null), Person(OidA))).MandatoryRowPredicateSql.ShouldBe("1 = 0");
    }

    [Fact]
    public async Task ST_129_06_ASiblingWithoutATypedForm_MakesTheTableDeny_NotFallBackToTheText()
    {
        var sibling = CallerFilter("legacy_text_only", "from conf.client client where target.client_id = client.client_id and client.region = 'EU'");
        var policy = Policy(Resolver(Snapshot(ByDept, sibling), dropTypedOf: "legacy_text_only"), CallerResolver());

        var decision = await Decide(policy, Person(OidA));

        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ST_129_06_WithoutARowPolicyFactory_ACallerFilterDeniesTheTable()
    {
        var policy = new TableAccessPolicy(Consents(), new ConsentResolutionService(), Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance, Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null, new GatewayOptions(), Resolver(Snapshot(ByDept)), rowPolicyFactory: null, callerAttributes: CallerResolver());

        (await Decide(policy, Person(OidA))).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task AFilterWithoutPlaceholders_BehavesAsBefore_NoResolverNeeded()
    {
        var plain = CallerFilter("plain", "from conf.client client where target.client_id = client.client_id and client.region = 'EU'");
        var decision = await Decide(Policy(Resolver(Snapshot(plain)), null), null);
        decision.IsAllowed.ShouldBeTrue();
        decision.MandatoryRowPredicateSql.ShouldNotBeNull();
        decision.MandatoryRowPredicateSql.ShouldNotBe("1 = 0");
    }

    [Fact]
    public async Task ST_129_15_TheBindReserve_AppliesToVirtualFilterExpansions_PerRequest()
    {
        var policy = Policy(Resolver(Snapshot(ByRegions)), CallerResolver());
        var principal = Person(OidA);
        (await Decide(policy, principal)).RowFilterParameters!.Count.ShouldBe(4);
        // The reserve (400) is far above 4 here: the request is not refused. A tighter reserve is covered by the consent path tests.
        (await Decide(policy, principal)).MandatoryRowPredicateSql!.ShouldContain("@__gql_caller_regions__0");
    }
}
