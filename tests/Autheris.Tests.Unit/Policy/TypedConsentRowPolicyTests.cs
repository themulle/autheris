using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>WP-X0l: consent decisions carry a typed row policy built from the same filter inputs as the legacy text.</summary>
public sealed class TypedConsentRowPolicyTests
{
    private static readonly TenantId Tenant = new("acme");
    private static readonly Sid User = new("S-1");
    private static readonly TableIdentifier Orders = new("d", "main", "orders");

    private static TableMetadata Meta(string sourceType = "SQLite", DataSourceType type = DataSourceType.Sql) => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "ds", SchemaName = "main", TableName = "orders", SourceType = sourceType, DataSourceType = type },
        Columns = [new TableColumn { ColumnName = "region", DataType = "TEXT" }, new TableColumn { ColumnName = "id", DataType = "INTEGER" }]
    };

    private static Consent Consent(ConsentEffect effect, params ConsentRowFilter[] filters) => new()
    {
        TableIdentifier = Orders,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = User,
        Effect = effect,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = filters
    };

    private static ConsentRowFilter Region(string value, int group = 0) =>
        new() { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = $"\"{value}\"", FilterGroup = group };

    private static IConsentRepository Repo(params Consent[] consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents.ToList()));
        return repo;
    }

    private static int Calls(IConsentRepository repo) => repo.ReceivedCalls().Count();

    private static TableAccessPolicy Policy(IConsentRepository repo, RowPolicyFactory? factory, IConsentCacheService? cache = null) =>
        new(repo, new ConsentResolutionService(),
            cache ?? Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            Autheris.Application.Policy.Services.NullPolicyEnforcementService.Instance,
            Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null, new GatewayOptions(), NullMandatoryRowFilterResolver.Instance,
            rowPolicyFactory: factory);

    private static RowPolicyFactory Factory() => new(new PolicyPredicateRenderer());

    private static Task<TableAccessDecision> Decide(TableAccessPolicy policy, TableMetadata? meta = null) =>
        policy.DecideAsync(new TableAccessQuery(User, Tenant, new HashSet<Sid>(), new HashSet<string>(), meta ?? Meta()), CancellationToken.None);

    [Fact]
    public async Task ConsentRowFilter_IsTyped_AndTheTextIsItsProjection()
    {
        var decision = await Decide(Policy(Repo(Consent(ConsentEffect.Allow, Region("EU"))), Factory()));

        decision.RowPolicy.ShouldNotBeNull();
        decision.CombinedRowFilterSql!.ShouldBe(decision.RowPolicy!.Project().Sql);
        decision.CombinedRowFilterSql!.ShouldNotContain("EU");
        decision.CombinedRowFilterSql!.ShouldContain("@__gql_rls_");
        decision.RowFilterParameters!.Values.ShouldContain("EU");
        decision.RequireTypedRowPolicy().ShouldBeSameAs(decision);
    }

    [Fact]
    public async Task TwoAllowConsents_AreOrCombined_WithBothValuesBound()
    {
        var decision = await Decide(Policy(Repo(Consent(ConsentEffect.Allow, Region("EU")), Consent(ConsentEffect.Allow, Region("US"))), Factory()));

        decision.RowFilterParameters!.Values.OrderBy(v => v).ShouldBe(new object?[] { "EU", "US" });
        decision.CombinedRowFilterSql!.ShouldContain(" OR ");
        decision.RequireTypedRowPolicy().IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task DenyConsentWithFilter_IsTypedAsNot()
    {
        var decision = await Decide(Policy(Repo(
            Consent(ConsentEffect.Allow, Region("EU")), Consent(ConsentEffect.Deny, Region("DE"))), Factory()));

        decision.CombinedRowFilterSql!.ShouldContain("NOT");
        decision.RowFilterParameters!.Values.OrderBy(v => v).ShouldBe(new object?[] { "DE", "EU" });
        decision.RequireTypedRowPolicy().IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task UnconstrainedAllow_HasNoRowPolicy()
    {
        var decision = await Decide(Policy(Repo(Consent(ConsentEffect.Allow)), Factory()));

        decision.IsAllowed.ShouldBeTrue();
        decision.RowPolicy.ShouldBeNull();
        decision.CombinedRowFilterSql!.ShouldBeNull();
        decision.ConsentFilterRecipe.ShouldBeNull();
    }

    [Fact]
    public async Task WithoutARowPolicyFactory_TheLegacyTextIsKept()
    {
        var decision = await Decide(Policy(Repo(Consent(ConsentEffect.Allow, Region("EU"))), factory: null));

        decision.RowPolicy.ShouldBeNull();
        decision.CombinedRowFilterSql!.ShouldContain("'EU'");
        decision.RequireTypedRowPolicy().IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task NonSqlSource_KeepsTheLegacyText()
    {
        var decision = await Decide(
            Policy(Repo(Consent(ConsentEffect.Allow, Region("EU"))), Factory()), Meta("HttpDeclarative", DataSourceType.HttpDeclarative));

        decision.RowPolicy.ShouldBeNull();
        decision.CombinedRowFilterSql!.ShouldContain("'EU'");
    }

    [Fact]
    public async Task CachedDecisionWithTheRecipe_IsTypedWithoutReadingTheConsents()
    {
        var first = await Decide(Policy(Repo(Consent(ConsentEffect.Allow, Region("EU"))), factory: null));
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(first);
        var repo = Repo();

        var decision = await Decide(Policy(repo, Factory(), cache));

        Calls(repo).ShouldBe(0);
        decision.RowPolicy.ShouldNotBeNull();
        decision.RowFilterParameters!.Values.ShouldContain("EU");
    }

    [Fact]
    public async Task CachedDecisionWithoutTheRecipe_IsNotTrusted_AndTheConsentsAreResolvedAgain()
    {
        var legacy = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), "region = 'EU'", hasUnconstrainedColumnAllow: true);
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(legacy);
        var repo = Repo(Consent(ConsentEffect.Allow, Region("EU")));

        var decision = await Decide(Policy(repo, Factory(), cache));

        Calls(repo).ShouldBe(1);
        decision.RowPolicy.ShouldNotBeNull();
    }

    [Fact]
    public async Task TypedConsentPolicy_AndATypedVirtualFilter_AreMergedIntoOnePolicy()
    {
        var vf = Substitute.For<IMandatoryRowFilterResolver>();
        var vfPredicate = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>());
        var vfPolicy = Factory().FromPredicate(
            TrinoSqlEngine.Governance.PolicyPredicate.Create(
                new TrinoSqlEngine.Ast.Nodes.BinaryExpression(
                    PolicyParameterNamer.Column("id"), TrinoSqlEngine.Ast.Nodes.BinaryOperator.Equal,
                    new TrinoSqlEngine.Ast.Nodes.PolicyParameterExpression("__gql_vf_0_0", TrinoSqlEngine.Ast.Emit.SqlParameterType.Int64)),
                new Dictionary<string, TrinoSqlEngine.Governance.PolicyValue> { ["__gql_vf_0_0"] = new(7L, TrinoSqlEngine.Ast.Emit.SqlParameterType.Int64) }),
            DatabaseDialect.Sqlite);
        vf.ResolveAsync(Arg.Any<MandatoryFilterQuery>(), Arg.Any<CancellationToken>()).Returns(
            new MandatoryFilterOutcome(false, null, vfPolicy.Project().Sql, ["vf"]) { TypedPredicate = ((PolicyPredicateRowPolicy)vfPolicy).Predicate });
        var policy = new TableAccessPolicy(Repo(Consent(ConsentEffect.Allow, Region("EU"))), new ConsentResolutionService(),
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            Autheris.Application.Policy.Services.NullPolicyEnforcementService.Instance,
            Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            null, new GatewayOptions(), vf, rowPolicyFactory: Factory());

        var decision = await Decide(policy);

        decision.RowFilterParameters!.Keys.Count().ShouldBe(2);
        decision.MandatoryRowPredicateSql.ShouldNotBeNull();
        decision.RequireTypedRowPolicy().ShouldBeSameAs(decision);
        vfPredicate.ShouldNotBeNull();
    }

    [Fact]
    public void Recipe_RoundTripsThroughJson_IncludingCorrelatedFilters()
    {
        var correlated = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("d", "main", "grants"),
            DependentTableAlias = "g",
            ForeignKeyColumn = "order_id",
            PrimaryKeyColumn = "id",
            SubqueryFilterPredicateJson = "{\"user\":\"x\"}",
            AdditionalHops = [new SubqueryJoinHop { Table = new TableIdentifier("d", "main", "teams"), TableAlias = "t", LeftJoinColumn = "a", RightJoinColumn = "b" }]
        };
        var recipe = new ConsentRowFilterRecipe([[Region("EU"), correlated], []], [[Region("DE", 1)]]);

        ConsentRowFilterRecipe.TryParse(recipe.ToJson(), out var parsed).ShouldBeTrue();

        parsed!.Allow.Count.ShouldBe(2);
        parsed.Allow[0][0].ValueJson.ShouldBe("\"EU\"");
        parsed.Allow[0][1].DependentTable!.Value.TableName.ShouldBe("grants");
        parsed.Allow[0][1].AdditionalHops![0].Table.TableName.ShouldBe("teams");
        parsed.Allow[1].ShouldBeEmpty();
        parsed.Deny[0][0].FilterGroup.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Allow\":null,\"Deny\":null}")]
    public void Recipe_FromGarbage_IsRejected(string? json) => ConsentRowFilterRecipe.TryParse(json, out _).ShouldBeFalse();

    [Fact]
    public async Task Envelope_CarriesTheRecipe_ThroughL2()
    {
        var first = await Decide(Policy(Repo(Consent(ConsentEffect.Allow, Region("EU"))), factory: null));

        var restored = CachedConsentEnvelope.FromDecision(first, epoch: 3).ToDecision();

        restored.ConsentFilterRecipe.ShouldNotBeNull();
        restored.ConsentFilterRecipe!.Allow[0][0].ColumnName.ShouldBe("region");
        restored.CombinedRowFilterSql.ShouldBe(first.CombinedRowFilterSql);
    }

    [Fact]
    public void Envelope_WithoutARecipe_StaysCompatible()
    {
        var envelope = new CachedConsentEnvelope { Schema = "main", TableName = "orders", IsAllowed = true, RowFilterSql = "x = 1" };

        envelope.ToDecision().ConsentFilterRecipe.ShouldBeNull();
    }
}
