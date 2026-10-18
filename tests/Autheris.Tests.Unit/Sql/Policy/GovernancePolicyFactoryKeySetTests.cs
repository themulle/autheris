namespace Autheris.Tests.Unit.Sql.Policy;

using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Issue #107 WP-C: the factory integration of the key-set stage (A-2). Conditions: SEC-RF107-04 a, -10 b/c/d, -17 and D-WPC-1/2.
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernancePolicyFactoryKeySetTests
{
    private static readonly TableIdentifier OrdersId = new("default", "public", "orders");
    private static readonly TableIdentity Orders = new("public", "orders");

    private sealed class RecordingStage(IRowFilterKeySetStage? inner = null) : IRowFilterKeySetStage
    {
        public int ApplyCalls;
        public int SkippedCalls;
        public List<TableIdentity> Tables { get; } = [];
        public KeySetFallbackReason? SkippedReason;
        public RowFilterKeySetContext? LastContext;

        public ValueTask<IReadOnlyDictionary<TableIdentity, PolicyPredicate?>> ApplyAsync(
            RowFilterKeySetContext context, IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates, RowFilterKeySetReport report, CancellationToken ct)
        {
            ApplyCalls++;
            LastContext = context;
            Tables.AddRange(predicates.Keys);
            return inner is null ? ValueTask.FromResult(predicates) : inner.ApplyAsync(context, predicates, report, ct);
        }

        public void ReportSkipped(RowFilterKeySetContext context, IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates, RowFilterKeySetReport report, KeySetFallbackReason reason)
        {
            SkippedCalls++;
            LastContext = context;
            SkippedReason = reason;
            inner?.ReportSkipped(context, predicates, report, reason);
        }
    }

    private static TableMetadata Meta(string name, params (string Column, string Type)[] columns) => new()
    {
        Identifier = new TableIdentifier("default", "public", name),
        Table = new Table { TableName = name, SchemaName = "public", SourceType = "PostgreSQL", SourceName = "default", IsActive = true },
        Columns = columns.Select(c => new TableColumn { ColumnName = c.Column, DataType = c.Type }).ToList()
    };

    private static (GovernedPolicyInput Input, PolicyPredicate Predicate, ITableMetadataRepository Repo, IOptions<GatewayOptions> Options) Setup(bool drifted = false)
    {
        var orders = Meta("orders", ("id", "bigint"), ("client_id", "bigint"), ("tenant_id", "varchar"));
        var customers = Meta("customers", ("id", "bigint"), ("country", "varchar"), ("tenant_id", "varchar"));
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(ci.Arg<TableIdentifier>().TableName switch { "orders" => orders, "customers" => customers, _ => null }));
        var filter = new ConsentRowFilter
        {
            Id = Guid.NewGuid(), FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("default", "public", "customers"), DependentTableAlias = "c",
            ForeignKeyColumn = "client_id", PrimaryKeyColumn = "id", SubqueryFilterPredicateJson = "{\"c.country\": \"CH\"}"
        };
        var predicate = new TypedRowFilterBuilder().BuildCombined([new Consent { Effect = ConsentEffect.Allow, RowFilters = [filter] }], [], Orders)!;
        var decision = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(new PolicyPredicateRowPolicy(predicate, TargetSqlDialect.PostgreSql, new PolicyPredicateRenderer()));
        if (drifted)
        {
            decision = decision with { MandatoryRowPredicateSql = "region = 'EU'" };
        }

        var input = new GovernedPolicyInput("default", new TenantId("tenant_a"),
            new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = decision }, new HashSet<TableIdentity>(), GovernedSubqueryStrategy.Correlated)
        {
            Metadata = new Dictionary<TableIdentity, TableMetadata> { [Orders] = orders }
        };
        return (input, predicate, repo, Options.Create(new GatewayOptions()));
    }

    private static GovernancePolicyFactory Factory(ITableMetadataRepository repo, IOptions<GatewayOptions> options, IRowFilterKeySetStage? stage) =>
        GovernedTestSupport.PolicyFactory(repo, options, stage);

    private static StageHarness Harness()
    {
        var h = new StageHarness(provider: "PostgreSql");
        h.Options.CurrentValue = StageHarness.Gateway(true, RowFilterEvaluationStrategy.KeySetCache, provider: "PostgreSql", dataSource: "default");
        return h;
    }

    private static RowFilterKeySetStatement Read(bool disabled = false) => new(true, 4000, CompileRequestFactory.DefaultMaxExpansionFactor, disabled);

    [Fact]
    public async Task NoRegisteredSite_StageIsNotInvoked_AndNoDependentRecordIsRead()
    {
        var (input, _, repo, _) = Setup();
        var simple = new TypedRowFilterBuilder().BuildCombined(
            [new Consent { Effect = ConsentEffect.Allow, RowFilters = [new ConsentRowFilter { FilterGroup = 0, ColumnName = "region", Operator = "EQ", ValueJson = "\"EU\"" }] }], [], Orders)!;
        var decision = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(new PolicyPredicateRowPolicy(simple, TargetSqlDialect.PostgreSql, new PolicyPredicateRenderer()));
        var options = Options.Create(StageHarness.Gateway(true, RowFilterEvaluationStrategy.KeySetCache, provider: "PostgreSql", dataSource: "default"));
        var stage = new RecordingStage();

        await Factory(repo, options, stage).CreateAssemblyAsync(
            input with { Decisions = new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = decision }, KeySetStatement = Read() }, default);

        stage.ApplyCalls.ShouldBe(0);
        stage.SkippedCalls.ShouldBe(0);
        await repo.DidNotReceive().GetTableMetadataAsync(Arg.Is<TableIdentifier>(t => t.TableName == "customers"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DataSourceWithoutKeySetOptIn_BuildsNoDependentEpochKeys()
    {
        var (input, _, repo, options) = Setup();   // default options: no data source is opted in
        var stage = new RecordingStage();

        await Factory(repo, options, stage).CreateAssemblyAsync(input with { KeySetStatement = Read() }, default);

        stage.ApplyCalls.ShouldBe(1);
        stage.LastContext!.GovernanceIdentifiers[Orders].ShouldBe([OrdersId]);
    }

    [Fact]
    public async Task OptedInDataSource_AddsTheDependentToTheEpochKeys()
    {
        var (input, _, repo, _) = Setup();
        var options = Options.Create(StageHarness.Gateway(true, RowFilterEvaluationStrategy.KeySetCache, provider: "PostgreSql", dataSource: "default"));
        var stage = new RecordingStage();

        await Factory(repo, options, stage).CreateAssemblyAsync(input with { KeySetStatement = Read() }, default);

        stage.LastContext!.GovernanceIdentifiers[Orders].Select(i => i.TableName).ShouldBe(["orders", "customers"]);
    }

    [Fact]
    public async Task WithoutKeySetStatement_StageIsNotInvoked_AC8()
    {
        var (input, _, repo, options) = Setup();
        var stage = new RecordingStage();

        var assembly = await Factory(repo, options, stage).CreateAssemblyAsync(input, default);

        stage.ApplyCalls.ShouldBe(0);
        stage.SkippedCalls.ShouldBe(0);
        assembly.KeySets.Outcomes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task WriteStatement_StageNotInvoked_ReportsWriteStatement(string kind)
    {
        kind.ShouldNotBeNullOrEmpty();   // the statement type reaches the factory only as IsRead=false (SEC-RF107-17)
        var (input, _, repo, options) = Setup();
        var h = Harness();
        var stage = new RecordingStage(h.Stage);

        var assembly = await Factory(repo, options, stage).CreateAssemblyAsync(
            input with { KeySetStatement = new RowFilterKeySetStatement(false, 80, 64, false) }, default);

        stage.ApplyCalls.ShouldBe(0);
        stage.SkippedReason.ShouldBe(KeySetFallbackReason.WriteStatement);
        assembly.KeySets.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.WriteStatement);
        (h.Epochs.Calls, h.Compiler.Calls, h.Loader.Calls).ShouldBe((0, 0, 0));
        h.Dispose();
    }

    [Fact]
    public async Task UnknownStatementType_StageNotInvoked_SEC_RF107_17()
    {
        // IsRead is the positive test (== Select): any other or future statement type arrives as IsRead = false.
        var (input, _, repo, options) = Setup();
        var stage = new RecordingStage();

        await Factory(repo, options, stage).CreateAssemblyAsync(input with { KeySetStatement = new RowFilterKeySetStatement(false, 10, 64, false) }, default);

        stage.ApplyCalls.ShouldBe(0);
    }

    [Fact]
    public async Task DecisionAndRecipe_AreNotMutated_SEC_RF107_10()
    {
        var (input, predicate, repo, options) = Setup();
        var before = input.Decisions[Orders];
        var h = Harness();
        h.Loader.KeyCount = 3;

        var assembly = await Factory(repo, options, h.Stage).CreateAssemblyAsync(input with { KeySetStatement = Read() }, default);

        input.Decisions[Orders].ShouldBeSameAs(before);
        ((PolicyPredicateRowPolicy)before.RowPolicy!).Predicate.ShouldBeSameAs(predicate);
        CorrelatedKeySetSubstitution.FindSites(predicate).Count.ShouldBe(1);   // the original keeps its EXISTS and its registration
        var substituted = assembly.Policy.RowFilters.GetPredicate(Orders);
        CorrelatedKeySetSubstitution.FindSites(substituted).ShouldBeEmpty();
        substituted.ShouldNotBeSameAs(predicate);
        assembly.KeySets.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Miss);
        h.Dispose();
    }

    [Fact]
    public async Task FullySubstitutedPolicy_NoLongerResolvesTheDependentTables_A2()
    {
        var (input, _, repo, options) = Setup();
        var h = Harness();
        var factory = Factory(repo, options, h.Stage);

        var substituted = (await factory.CreateAssemblyAsync(input with { KeySetStatement = Read() }, default)).Policy;
        var exact = (await factory.CreateAssemblyAsync(input with { KeySetStatement = Read(disabled: true) }, default)).Policy;

        substituted.Catalog.Resolve(new TrinoSqlEngine.Ast.Nodes.SqlQualifiedName([new TrinoSqlEngine.Ast.Nodes.SqlIdentifier("public", true), new TrinoSqlEngine.Ast.Nodes.SqlIdentifier("customers", true)])).ShouldBeNull();
        exact.Catalog.Resolve(new TrinoSqlEngine.Ast.Nodes.SqlQualifiedName([new TrinoSqlEngine.Ast.Nodes.SqlIdentifier("public", true), new TrinoSqlEngine.Ast.Nodes.SqlIdentifier("customers", true)])).ShouldNotBeNull();
        h.Dispose();
    }

    [Fact]
    public async Task DriftedDecision_DeniedBeforeStage_SEC_RF107_04a()
    {
        var (input, _, repo, options) = Setup(drifted: true);
        var stage = new RecordingStage();

        var assembly = await Factory(repo, options, stage).CreateAssemblyAsync(input with { KeySetStatement = Read() }, default);

        stage.Tables.ShouldBeEmpty();   // the stage never sees a table that admission refused
        assembly.Policy.Catalog.Resolve(Orders.ToQualifiedName()).ShouldBeNull();
    }

    [Fact]
    public async Task CreateAsync_DelegatesAndReturnsThePolicy()
    {
        var (input, _, repo, options) = Setup();
        var policy = await Factory(repo, options, null).CreateAsync(input, default);

        policy.RowFilters.GetPredicate(Orders).ShouldNotBeNull();
    }
}
