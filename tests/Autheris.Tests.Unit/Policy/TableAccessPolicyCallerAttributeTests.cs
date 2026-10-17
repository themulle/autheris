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
using Autheris.Tests.Unit.Identity.Attributes;
using TrinoSqlEngine;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>PLAN-GOV-129 tests 13, 14, 15, 18 (A-1, A-3): the typed consent policy is bound per request; the cached decision holds the recipe only.</summary>
[Trait("Category", "Security")]
public sealed class TableAccessPolicyCallerAttributeTests : IDisposable
{
    private static readonly TenantId Tenant = new("acme");
    private static readonly TableIdentifier Orders = new("d", "main", "orders");
    private const string OidA = "11111111-1111-1111-1111-111111111111";
    private const string OidB = "22222222-2222-2222-2222-222222222222";
    private const string OidC = "33333333-3333-3333-3333-333333333333";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private sealed class MapProvider(Dictionary<string, string[]> byOid) : ICallerAttributeProvider
    {
        public int Calls;
        public string Kind => "claims";

        public ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var oid = subject.KeyFor(SubjectKeyKind.EntraObjectId);
            return ValueTask.FromResult(oid is not null && byOid.TryGetValue(oid, out var values)
                ? CallerAttributeResult.Resolved(new CallerAttributeValue(values.Cast<object>().ToList(), SqlParameterType.String))
                : CallerAttributeResult.Missing());
        }
    }

    private static TableMetadata Meta() => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "ds", SchemaName = "main", TableName = "orders", SourceType = "SQLite", DataSourceType = DataSourceType.Sql },
        Columns = [new TableColumn { ColumnName = "cost_center", DataType = "TEXT" }, new TableColumn { ColumnName = "id", DataType = "INTEGER" }]
    };

    private static ConsentRowFilter Attr(string op = "EQ") => new() { ColumnName = "cost_center", Operator = op, ValueJson = "[]", ValueSource = "USER_ATTRIBUTE", UserAttribute = "cost_center" };

    private static Consent Consent(ConsentEffect effect, params ConsentRowFilter[] filters) => new()
    {
        TableIdentifier = Orders, TenantId = Tenant, GranteeType = GranteeType.Role, RoleName = "analyst", Effect = effect,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1), RowFilters = filters
    };

    private static IConsentRepository Repo(params Consent[] consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents.ToList()));
        return repo;
    }

    private CallerAttributeResolver Resolver(MapProvider provider, bool enabled = true, int maxValues = 50)
    {
        var def = CallerAttributeTestSupport.Definition("cost_center", "claims", CallerAttributeType.Text, CallerAttributeCardinality.List, maxValues, 60) with { Tenant = Tenant };
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [def], []));
        var options = CallerAttributeTestSupport.Options(enabled);
        var resolver = new CallerAttributeResolver(registry, [provider], Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = options }));
        _disposables.Add(resolver);
        return resolver;
    }

    private static TableAccessPolicy Policy(IConsentRepository repo, ICallerAttributeResolver? resolver, IConsentCacheService? cache = null, bool typed = true) =>
        new(repo, new ConsentResolutionService(),
            cache ?? Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
            Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null, new GatewayOptions(), NullMandatoryRowFilterResolver.Instance,
            rowPolicyFactory: typed ? new RowPolicyFactory(new PolicyPredicateRenderer()) : null,
            callerAttributes: resolver);

    private static ClaimsPrincipal Person(string oid) => CallerAttributeTestSupport.Principal(CallerAttributeTestSupport.Bearer, [("oid", oid)]);

    private static Task<TableAccessDecision> Decide(TableAccessPolicy policy, ClaimsPrincipal? principal, string userKey = OidA) =>
        policy.DecideAsync(new TableAccessQuery(new Sid(userKey), Tenant, new HashSet<Sid>(), new HashSet<string> { "analyst" }, Meta(), Principal: principal), CancellationToken.None);

    private static MapProvider Map() => new(new Dictionary<string, string[]> { [OidA] = ["CC100"], [OidB] = ["CC200"] });

    [Fact]
    public async Task Two_Persons_WithDifferentValues_GetTheirOwnPolicy_AThirdWithoutTheAttributeGetsNone()
    {
        var policy = Policy(Repo(Consent(ConsentEffect.Allow, Attr())), Resolver(Map()));

        var a = await Decide(policy, Person(OidA), OidA);
        var b = await Decide(policy, Person(OidB), OidB);
        var c = await Decide(policy, Person(OidC), OidC);

        a.RowFilterParameters!.Values.ShouldBe(["CC100"]);
        b.RowFilterParameters!.Values.ShouldBe(["CC200"]);
        a.CombinedRowFilterSql.ShouldBe(b.CombinedRowFilterSql);   // same shape, the text carries markers only
        a.CombinedRowFilterSql!.ShouldNotContain("CC100");
        (c.RowFilterParameters ?? new Dictionary<string, object?>()).ShouldBeEmpty();
        c.CombinedRowFilterSql!.ShouldBe("1 = 0");
        a.RequireTypedRowPolicy().ShouldBeSameAs(a);
    }

    [Fact]
    public async Task ST_129_Cache_OneCachedRecipe_ServesBothPersons_AndNeverHoldsValues()
    {
        var recipeDecision = await Decide(Policy(Repo(Consent(ConsentEffect.Allow, Attr())), resolver: null, typed: false), Person(OidA));   // legacy text + recipe, no values
        recipeDecision.ConsentFilterRecipe.ShouldNotBeNull();
        recipeDecision.RowPolicy.ShouldBeNull();
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(recipeDecision);
        var repo = Repo();
        var policy = Policy(repo, Resolver(Map()), cache);

        var a = await Decide(policy, Person(OidA), OidA);
        var b = await Decide(policy, Person(OidB), OidB);

        a.RowFilterParameters!.Values.ShouldBe(["CC100"]);
        b.RowFilterParameters!.Values.ShouldBe(["CC200"]);
        repo.ReceivedCalls().Count().ShouldBe(0);   // both came from the cached recipe
        recipeDecision.RowPolicy.ShouldBeNull();    // mutation control: the cached object was never given a value
        (recipeDecision.RowFilterParameters ?? new Dictionary<string, object?>()).ShouldBeEmpty();
        recipeDecision.CombinedRowFilterSql!.ShouldNotContain("CC");
    }

    [Fact]
    public async Task Deny_WithAMissingAttribute_Blocks_AndAllowWithAMissingAttribute_MatchesNothing()
    {
        var policy = Policy(Repo(Consent(ConsentEffect.Allow), Consent(ConsentEffect.Deny, Attr())), Resolver(Map()));
        var resolved = await Decide(policy, Person(OidA), OidA);
        resolved.RowPolicy!.Project().Sql.ShouldContain("NOT (");
        resolved.RowFilterParameters!.Values.ShouldBe(["CC100"]);

        var missing = await Decide(policy, Person(OidC), OidC);
        missing.CombinedRowFilterSql!.ShouldBe("NOT (1 = 1)");
    }

    [Fact]
    public async Task Disabled_KeepsTheConstants_AndNeverCallsAProvider()
    {
        var provider = Map();
        var policy = Policy(Repo(Consent(ConsentEffect.Allow, Attr())), Resolver(provider, enabled: false));
        var decision = await Decide(policy, Person(OidA), OidA);
        decision.CombinedRowFilterSql.ShouldBe("1 = 0");
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task NoResolverAndNoPrincipal_AreTheConstant()
    {
        (await Decide(Policy(Repo(Consent(ConsentEffect.Allow, Attr())), resolver: null), Person(OidA))).CombinedRowFilterSql.ShouldBe("1 = 0");
        (await Decide(Policy(Repo(Consent(ConsentEffect.Allow, Attr())), Resolver(Map())), principal: null)).CombinedRowFilterSql.ShouldBe("1 = 0");
    }

    [Fact]
    public async Task ListAttribute_WithIn_ExpandsPadded_AndEqWithTwoValuesIsTheConstant()
    {
        var provider = new MapProvider(new Dictionary<string, string[]> { [OidA] = ["a", "b", "c"] });
        var inPolicy = Policy(Repo(Consent(ConsentEffect.Allow, Attr("IN"))), Resolver(provider));
        (await Decide(inPolicy, Person(OidA))).RowFilterParameters!.Count.ShouldBe(4);

        var eqPolicy = Policy(Repo(Consent(ConsentEffect.Allow, Attr("EQ"))), Resolver(provider));
        (await Decide(eqPolicy, Person(OidA))).CombinedRowFilterSql.ShouldBe("1 = 0");
    }

    [Fact]
    public async Task ST_129_15_TheBindReserve_IsPerRequest_AndTheExcessIsTheMissingConstant()
    {
        var many = Enumerable.Range(0, 200).Select(i => $"v{i}").ToArray();   // pads to 256
        var provider = new MapProvider(new Dictionary<string, string[]> { [OidA] = many });
        var policy = Policy(Repo(Consent(ConsentEffect.Allow, Attr("IN"))), Resolver(provider, maxValues: 200));
        var principal = Person(OidA);

        var first = await Decide(policy, principal);          // 256 of the 400 reserved
        first.RowFilterParameters!.Count.ShouldBe(256);
        var second = await Decide(policy, principal);         // same request: 512 > 400 -> same outcome as Missing
        second.CombinedRowFilterSql.ShouldBe("1 = 0");
        (second.RowFilterParameters ?? new Dictionary<string, object?>()).ShouldBeEmpty();
    }

    [Fact]
    public async Task OverCardinality_IsTheConstant_NeverATruncatedList()
    {
        var provider = new MapProvider(new Dictionary<string, string[]> { [OidA] = Enumerable.Range(0, 60).Select(i => $"v{i}").ToArray() });
        var decision = await Decide(Policy(Repo(Consent(ConsentEffect.Allow, Attr("IN"))), Resolver(provider)), Person(OidA));   // definition cap is 50
        decision.CombinedRowFilterSql.ShouldBe("1 = 0");
    }

    [Fact]
    public async Task ARecipeWithoutAttributeLeaves_NeverTouchesTheResolver()
    {
        var provider = Map();
        var literal = new ConsentRowFilter { ColumnName = "cost_center", Operator = "EQ", ValueJson = "\"CC1\"" };
        var decision = await Decide(Policy(Repo(Consent(ConsentEffect.Allow, literal)), Resolver(provider)), Person(OidA));
        decision.RowFilterParameters!.Values.ShouldBe(["CC1"]);
        provider.Calls.ShouldBe(0);   // G8: no new work on a decision without attribute filters
    }

    [Fact]
    public async Task ST_129_Sqlite_EndToEnd_EachPersonSeesOnlyTheirRows()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in new[]
        {
            "CREATE TABLE orders (id INTEGER, cost_center TEXT)",
            "INSERT INTO orders VALUES (1, 'CC100'), (2, 'CC200'), (3, 'CC100'), (4, 'CC300')"
        })
        {
            using var c = connection.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        var policy = Policy(Repo(Consent(ConsentEffect.Allow, Attr())), Resolver(Map()));
        async Task<int[]> Rows(string oid)
        {
            var decision = await Decide(policy, Person(oid), oid);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT id FROM orders AS {RowFilterAliases.Target} WHERE " + decision.CombinedRowFilterSql;
            foreach (var (name, value) in decision.RowFilterParameters ?? new Dictionary<string, object?>())
            {
                cmd.Parameters.AddWithValue("@" + name.TrimStart('@'), value ?? DBNull.Value);
            }

            var ids = new List<int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt32(0));
            return ids.Order().ToArray();
        }

        (await Rows(OidA)).ShouldBe([1, 3]);
        (await Rows(OidB)).ShouldBe([2]);
        (await Rows(OidC)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ST_129_15_TheDecision_CarriesAuditEntries_WithIdsAndOutcomes_NeverNamesOrValues()
    {
        var policy = Policy(Repo(Consent(ConsentEffect.Allow, Attr())), Resolver(Map()));
        var resolved = await Decide(policy, Person(OidA), OidA);
        var missing = await Decide(policy, Person(OidC), OidC);

        var entry = resolved.CallerAttributes.ShouldNotBeNull().ShouldHaveSingleItem();
        entry.Outcome.ShouldBe("Resolved");
        Guid.TryParse(entry.Id, out _).ShouldBeTrue();
        missing.CallerAttributes!.Single().Outcome.ShouldBe("Missing");
        foreach (var e in new[] { entry, missing.CallerAttributes!.Single() })
        {
            e.ToString().ShouldNotContain("cost_center");
            e.ToString().ShouldNotContain("CC100");
        }
    }

    [Fact]
    public async Task ADecisionWithoutAttributeLeaves_HasNoAuditEntries()
    {
        var literal = new ConsentRowFilter { ColumnName = "cost_center", Operator = "EQ", ValueJson = "\"CC1\"" };
        (await Decide(Policy(Repo(Consent(ConsentEffect.Allow, literal)), Resolver(Map())), Person(OidA))).CallerAttributes.ShouldBeNull();
    }
}
