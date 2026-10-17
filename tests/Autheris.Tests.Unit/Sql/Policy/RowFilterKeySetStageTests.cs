namespace Autheris.Tests.Unit.Sql.Policy;

using System.Security;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Issue #107 WP-C: the per-statement stage (PLAN-GOV-107 A-2, A-10, A-13, D-WPC-2/3/7/9). Conditions: SEC-RF107-01 (a, c), -02, -04, -05, -08, -13, -17, -18.
/// </summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetStageTests : IDisposable
{
    private readonly List<StageHarness> _harnesses = [];
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        foreach (var h in _harnesses) h.Dispose();
    }

    private StageHarness Harness(
        bool allowSource = true, RowFilterEvaluationStrategy strategy = RowFilterEvaluationStrategy.KeySetCache, long cacheSize = 64L * 1024 * 1024,
        int concurrency = 4, string provider = "SqlServer")
    {
        var h = new StageHarness(allowSource, strategy, cacheSize, concurrency, provider: provider);
        _harnesses.Add(h);
        return h;
    }

    private static ConsentRowFilter Correlated(
        string fk = "client_id", Guid? id = null, RowFilterEvaluationStrategy? strategy = null, int? duration = null, int? maxKeys = null,
        int group = 0, string? temporal = null, string? validFrom = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        FilterGroup = group,
        FilterType = RowFilterType.SubqueryCorrelated,
        DependentTable = new TableIdentifier("erp", "main", "customers"),
        DependentTableAlias = "c",
        ForeignKeyColumn = fk,
        PrimaryKeyColumn = "id",
        SubqueryFilterPredicateJson = "{\"c.country\": \"CH\"}",
        KeySetStrategy = strategy,
        KeySetDurationSeconds = duration,
        KeySetMaxKeys = maxKeys,
        TargetTemporalColumn = temporal,
        DependentValidFromColumn = validFrom
    };

    private static ConsentRowFilter Simple() => new() { FilterGroup = 0, ColumnName = "region", Operator = "EQ", ValueJson = "\"EU\"" };

    private static Consent Allow(params ConsentRowFilter[] filters) => new() { Effect = ConsentEffect.Allow, RowFilters = filters };

    private static Consent Deny(params ConsentRowFilter[] filters) => new() { Effect = ConsentEffect.Deny, RowFilters = filters };

    private static PolicyPredicate Predicate(TableIdentity table, params ConsentRowFilter[] filters) =>
        new TypedRowFilterBuilder().BuildCombined([Allow(filters)], [], table)!;

    private static Dictionary<TableIdentity, PolicyPredicate?> Preds(params (TableIdentity Table, PolicyPredicate Predicate)[] items) =>
        items.ToDictionary(i => i.Table, i => (PolicyPredicate?)i.Predicate);

    private static ExistsExpression Node(PolicyPredicate predicate, int index = 0) => CorrelatedKeySetSubstitution.FindSites(predicate)[index].Node;

    private static string Sql(PolicyPredicate predicate) => new PolicyPredicateRenderer().Render(predicate, TargetSqlDialect.SqlServer, RowFilterAliases.Target).Sql;

    private static async Task<(IReadOnlyDictionary<TableIdentity, PolicyPredicate?> Result, RowFilterKeySetReport Report)> Apply(
        StageHarness h, RowFilterKeySetContext context, IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates, CancellationToken ct = default)
    {
        var report = new RowFilterKeySetReport();
        var result = await h.Stage.ApplyAsync(context, predicates, report, ct);
        return (result, report);
    }

    private static string CacheKeyOf(StageHarness h, RowFilterKeySetContext context, PolicyPredicate predicate, int maxKeys = 2000)
    {
        var target = context.Catalog.Resolve(StageHarness.Orders.ToQualifiedName())!;
        var compiler = new FakeKeyQueryCompiler { KeyType = h.Compiler.KeyType, ForeignKeyType = h.Compiler.ForeignKeyType };
        var query = compiler.CompileKeyQuery(
            new PolicyKeyQueryRequest(Node(predicate), predicate.Parameters, context.Catalog, target, context.TenantBinding, context.Dialect, maxKeys + 1), default);
        var epochs = context.GovernanceIdentifiers[StageHarness.Orders].ToDictionary(i => i, _ => h.Epochs.Epoch);
        return RowFilterKeySetCacheKey.Compute(new RowFilterKeySetCacheKeyInput(
            context.Dialect == TargetSqlDialect.PostgreSql ? "PostgreSql" : "SqlServer", "Server=x", context.Dialect, context.Tenant.Value, context.DataSourceName, query, maxKeys, epochs));
    }

    // ---- substitution, hit and miss ----

    [Fact]
    public async Task KeySetStrategy_SubstitutesCachedKeys_MissThenHit()
    {
        var h = Harness();
        var predicate = Predicate(StageHarness.Orders, Correlated());

        var (first, firstReport) = await Apply(h, StageHarness.Context(), Preds((StageHarness.Orders, predicate)));
        var (second, secondReport) = await Apply(h, StageHarness.Context(), Preds((StageHarness.Orders, predicate)));

        var o1 = firstReport.Outcomes.Single();
        o1.Outcome.ShouldBe(KeySetOutcomeKind.Miss);
        o1.KeyCount.ShouldBe(3);
        o1.PaddedCount.ShouldBe(4);
        secondReport.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Hit);
        Sql(first[StageHarness.Orders]!).ShouldContain("IN (");
        Sql(first[StageHarness.Orders]!).ShouldContain("__rls_ks0_");
        Sql(second[StageHarness.Orders]!).ShouldBe(Sql(first[StageHarness.Orders]!));
        CorrelatedKeySetSubstitution.FindSites(first[StageHarness.Orders]!).ShouldBeEmpty();
        h.Loader.Calls.ShouldBe(1);
        h.Compiler.RowLimits.ShouldAllBe(l => l == 2001);
    }

    [Fact]
    public async Task DefaultStrategyExists_NoEpochCacheOrLoaderAccess_AndNoDiagnostics()
    {
        var h = Harness(strategy: RowFilterEvaluationStrategy.Exists);
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        var (result, report) = await Apply(h, StageHarness.Context(), preds);

        result.ShouldBeSameAs(preds);
        report.Outcomes.ShouldBeEmpty();
        (h.Epochs.Calls, h.Compiler.Calls, h.Loader.Calls).ShouldBe((0, 0, 0));
    }

    [Fact]
    public async Task Disabled_AndNonReadStatements_PerformNoAccess_SEC_RF107_15_17()
    {
        var h = Harness();
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        var (disabled, r1) = await Apply(h, StageHarness.Context(disabled: true), preds);
        var (nonRead, r2) = await Apply(h, StageHarness.Context(isRead: false), preds);

        disabled.ShouldBeSameAs(preds);
        nonRead.ShouldBeSameAs(preds);
        r1.Outcomes.ShouldBeEmpty();
        r2.Outcomes.ShouldBeEmpty();
        (h.Epochs.Calls, h.Compiler.Calls, h.Loader.Calls).ShouldBe((0, 0, 0));
    }

    [Fact]
    public void ReportSkipped_ReportsWriteStatementOnlyForKeySetSites_SEC_RF107_17()
    {
        var h = Harness();
        var keySet = Correlated(strategy: RowFilterEvaluationStrategy.KeySetCache);
        var exact = Correlated(fk: "crane_id", strategy: RowFilterEvaluationStrategy.Exists);
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, keySet, exact)));
        var report = new RowFilterKeySetReport();

        h.Stage.ReportSkipped(StageHarness.Context(isRead: false), preds, report, KeySetFallbackReason.WriteStatement);

        var outcome = report.Outcomes.Single();
        outcome.Outcome.ShouldBe(KeySetOutcomeKind.Fallback);
        outcome.Reason.ShouldBe(KeySetFallbackReason.WriteStatement);
        outcome.RuleId.ShouldBe(keySet.Id);
        (h.Epochs.Calls, h.Compiler.Calls, h.Loader.Calls).ShouldBe((0, 0, 0));
    }

    [Fact]
    public async Task ObjectLevelStrategy_AppliesWhenTheRuleInherits()
    {
        var h = Harness(strategy: RowFilterEvaluationStrategy.Exists);
        var metadata = new Dictionary<TableIdentity, TableMetadata?>
        {
            [StageHarness.Orders] = new()
            {
                Table = new Table { SourceName = "ds", SchemaName = "main", TableName = "orders" },
                Identifier = StageHarness.OrdersId,
                Columns = [],
                RowFilterEvaluation = new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, null, 10)
            }
        };

        var (_, report) = await Apply(h, StageHarness.Context(metadata: metadata), Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated()))));

        report.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Miss);
        report.Outcomes.Single().StrategySource.ShouldBe(RowFilterEvaluationSource.Object);
        h.Compiler.RowLimits.ShouldBe([11]);
    }

    // ---- fallbacks (SEC-RF107-01, -05, -08) ----

    [Fact]
    public async Task NonPolicyParameterOrigin_FallsBack_SEC_RF107_01a()
    {
        // The compiler refuses a body whose parameters are not all policy-origin values (NotDerivable); the stage keeps the EXISTS.
        var h = Harness();
        h.Compiler.Failure = new KeyQueryNotDerivableException(KeyQueryNotDerivableReasons.NotDerivable, "NonPolicyParameter");
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        var (result, report) = await Apply(h, StageHarness.Context(), preds);

        result.ShouldBeSameAs(preds);
        report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.NotDerivable);
        h.Loader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task NotDerivable_FallsBack_AndReasonCodesMapOneToOne_SEC_RF107_08()
    {
        var h = Harness();
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        h.Compiler.Failure = new KeyQueryNotDerivableException(KeyQueryNotDerivableReasons.TypeNotBindable);
        (await Apply(h, StageHarness.Context(), preds)).Report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.TypeNotBindable);
        h.Compiler.Failure = new KeyQueryNotDerivableException(KeyQueryNotDerivableReasons.DialectUnsupported);
        (await Apply(h, StageHarness.Context(), preds)).Report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.DialectUnsupported);
        h.Compiler.Failure = new KeyQueryNotDerivableException("anything else");
        (await Apply(h, StageHarness.Context(), preds)).Report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.NotDerivable);
        h.Loader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task CompilerSecurityException_Propagates_SEC_RF107_08()
    {
        var h = Harness();
        h.Compiler.Failure = new SecurityException("coverage");
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        await Should.ThrowAsync<SecurityException>(async () => await Apply(h, StageHarness.Context(), preds));

        h.Loader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task SourceWithoutKeySetCacheAllowed_FallsBack_SEC_RF107_01c()
    {
        var h = Harness(allowSource: false);
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        var (result, report) = await Apply(h, StageHarness.Context(), preds);

        result.ShouldBeSameAs(preds);
        report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.SourceNotEligible);
        (h.Epochs.Calls, h.Compiler.Calls, h.Loader.Calls).ShouldBe((0, 0, 0));
    }

    [Fact]
    public async Task UnknownDataSource_AndProviderMismatch_FallBackSourceNotEligible()
    {
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));
        var unknown = Harness();
        (await Apply(unknown, StageHarness.Context(dataSource: "other"), preds)).Report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.SourceNotEligible);

        var mismatch = Harness(provider: "PostgreSql");
        (await Apply(mismatch, StageHarness.Context(), preds)).Report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.SourceNotEligible);
        mismatch.Loader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task UnsupportedDialect_FallsBack()
    {
        var h = Harness();
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        var (_, report) = await Apply(h, StageHarness.Context(dialect: TargetSqlDialect.Snowflake), preds);

        report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.DialectUnsupported);
        h.Loader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task NonAuthoritativeEpochs_FallBackWithoutKeyQuery_SEC_RF107_05()
    {
        var h = Harness();
        h.Epochs.Authoritative = false;
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        var (result, report) = await Apply(h, StageHarness.Context(), preds);

        result.ShouldBeSameAs(preds);
        report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.EpochUnavailable);
        (h.Compiler.Calls, h.Loader.Calls).ShouldBe((0, 0));
    }

    [Fact]
    public async Task TypeNotBindableAndNotBindableSentinel_FallBack()
    {
        var h = Harness();
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));
        h.Compiler.ForeignKeyType = "float";
        (await Apply(h, StageHarness.Context(), preds)).Report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.TypeNotBindable);

        h.Compiler.ForeignKeyType = "bigint";
        h.Loader.Override = (r, _) => Task.FromResult(RowFilterKeySetEntry.NotBindableSentinel(h.Time.GetUtcNow(), r.Tenant.Value, r.DataSourceName));
        var (result, report) = await Apply(h, StageHarness.Context(), preds);

        result.ShouldBeSameAs(preds);
        report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.ValueNotBindable);
    }

    [Fact]
    public async Task LoaderFailure_DeniesWithStableCode_CachesNothing_NextRetries_SEC_RF107_08()
    {
        var h = Harness();
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));
        h.Loader.Override = (_, _) => throw RowFilterKeySetErrorCodes.Unavailable(new InvalidOperationException("boom"));

        var denied = await Should.ThrowAsync<GatewaySecurityException>(async () => await Apply(h, StageHarness.Context(), preds));
        denied.ErrorCode.ShouldBe("ROW_FILTER_KEY_SET_UNAVAILABLE");

        h.Loader.Override = null;
        var (_, report) = await Apply(h, StageHarness.Context(), preds);

        report.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Miss);
        h.Loader.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task ErrorMessage_NamesNoDependentTableOrRule_SEC_RF107_08()
    {
        var h = Harness();
        var rule = Guid.NewGuid();
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated(id: rule))));
        h.Loader.Override = (_, _) => throw RowFilterKeySetErrorCodes.Unavailable(new InvalidOperationException("customers " + rule));

        var denied = await Should.ThrowAsync<GatewaySecurityException>(async () => await Apply(h, StageHarness.Context(), preds));

        denied.Message.ShouldBe(RowFilterKeySetErrorCodes.UnavailableMessage);
        denied.Message.ShouldNotContain("customers");
        denied.Message.ShouldNotContain(rule.ToString());
        h.Logs.ShouldAllBe(l => !l.Message.Contains("customers", StringComparison.Ordinal));
    }

    // ---- tenant, age, revocation (SEC-RF107-02, -04) ----

    [Fact]
    public async Task TwoTenants_IdenticalFilter_NeverShareEntry_SEC_RF107_02()
    {
        var h = Harness();
        var predicate = Predicate(StageHarness.Orders, Correlated());
        var preds = Preds((StageHarness.Orders, predicate));

        await Apply(h, StageHarness.Context(tenant: "t1"), preds);
        await Apply(h, StageHarness.Context(tenant: "t2"), preds);
        var (_, again) = await Apply(h, StageHarness.Context(tenant: "t1"), preds);

        h.Loader.Calls.ShouldBe(2);
        h.Loader.Requests.Select(r => r.Tenant.Value).ShouldBe(["t1", "t2"]);
        again.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Hit);
    }

    [Fact]
    public async Task ForgedEntryOfOtherTenant_IsTreatedAsMiss_SEC_RF107_02()
    {
        var h = Harness();
        var predicate = Predicate(StageHarness.Orders, Correlated());
        var context = StageHarness.Context(tenant: "t1");
        var key = CacheKeyOf(h, context, predicate);
        h.Cache.InsertForTest(key, new RowFilterKeySetEntry([999L], false, h.Time.GetUtcNow(), "t2", StageHarness.DataSource), TimeSpan.FromMinutes(5));

        var (result, report) = await Apply(h, context, Preds((StageHarness.Orders, predicate)));

        report.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Miss);
        h.Loader.Calls.ShouldBe(1);
        result[StageHarness.Orders]!.Parameters.Values.Any(v => Equals(v.Value, 999L)).ShouldBeFalse();
        h.Logs.ShouldContain(l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task AgeIsMeasuredFromQueryStart_SEC_RF107_04b()
    {
        var h = Harness();
        h.Loader.Override = async (r, _) =>
        {
            var startedAt = h.Time.GetUtcNow();   // the entry is stamped before the key query runs
            h.Time.Advance(TimeSpan.FromMinutes(4));   // ... and the query takes four minutes
            return new RowFilterKeySetEntry([1L, 2L], false, startedAt, r.Tenant.Value, r.DataSourceName);
        };
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        await Apply(h, StageHarness.Context(), preds);   // loaded at T, finished at T+4m, default duration 5m
        h.Time.Advance(TimeSpan.FromSeconds(59));
        var (_, stillFresh) = await Apply(h, StageHarness.Context(), preds);   // T+4m59s
        h.Time.Advance(TimeSpan.FromSeconds(2));
        var (_, expired) = await Apply(h, StageHarness.Context(), preds);   // T+5m01s

        stillFresh.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Hit);
        expired.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Miss);
        h.Loader.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task LoweredDuration_ForcesReload_AC12()
    {
        var h = Harness();
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));
        await Apply(h, StageHarness.Context(), preds);
        h.Time.Advance(TimeSpan.FromSeconds(100));
        (await Apply(h, StageHarness.Context(), preds)).Report.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Hit);

        h.Options.CurrentValue = StageHarness.Gateway(true, RowFilterEvaluationStrategy.KeySetCache, duration: TimeSpan.FromSeconds(60));
        var (_, report) = await Apply(h, StageHarness.Context(), preds);

        report.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Miss);
        h.Loader.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task RevokedConsent_TakesEffectWhileKeysCached_SEC_RF107_04()
    {
        var h = Harness();
        var withConsent = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));
        await Apply(h, StageHarness.Context(), withConsent);
        int epochCalls = h.Epochs.Calls, compileCalls = h.Compiler.Calls;

        // The consent is revoked: the next statement's decision carries a different predicate, which has no key-set site at all.
        var revoked = Preds((StageHarness.Orders, new TypedRowFilterBuilder().BuildCombined([Allow(Simple())], [], StageHarness.Orders)!));
        var (result, report) = await Apply(h, StageHarness.Context(), revoked);

        result.ShouldBeSameAs(revoked);
        report.Outcomes.ShouldBeEmpty();
        (h.Epochs.Calls, h.Compiler.Calls, h.Loader.Calls).ShouldBe((epochCalls, compileCalls, 1));
    }

    [Fact]
    public async Task NoStaleServeWhileReloadInFlight_SEC_RF107_04d()
    {
        var h = Harness();
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));
        await Apply(h, StageHarness.Context(), preds);   // 3 keys
        h.Time.Advance(TimeSpan.FromMinutes(6));          // expired

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Loader.Override = async (r, _) =>
        {
            started.TrySetResult();
            await gate.Task;
            return new RowFilterKeySetEntry([1L, 2L, 3L, 4L, 5L], false, h.Time.GetUtcNow(), r.Tenant.Value, r.DataSourceName);
        };

        var first = Apply(h, StageHarness.Context(), preds);
        await started.Task.WaitAsync(Wait);
        var second = Apply(h, StageHarness.Context(), preds);
        await Task.Delay(100);
        gate.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(Wait);

        results.ShouldAllBe(r => r.Report.Outcomes.Single().Outcome == KeySetOutcomeKind.Miss && r.Report.Outcomes.Single().KeyCount == 5);
        h.Loader.Calls.ShouldBe(2);
    }

    // ---- memory and load (SEC-RF107-13) ----

    [Fact]
    public async Task OversizedEntry_FallsBackWithSentinel_SEC_RF107_13()
    {
        var h = Harness(cacheSize: 2048);   // an entry may take at most 256 bytes
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        var (first, firstReport) = await Apply(h, StageHarness.Context(), preds);
        var (_, secondReport) = await Apply(h, StageHarness.Context(), preds);

        first.ShouldBeSameAs(preds);
        firstReport.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.Overflow);
        secondReport.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.Overflow);
        h.Loader.Calls.ShouldBe(1);   // the sentinel is cached for the duration: no key query per statement
    }

    [Fact]
    public async Task ConcurrencyLimit_FallsBackWithoutLoad_SEC_RF107_13()
    {
        var h = Harness(concurrency: 1);
        var filterA = Correlated("a_id");
        var filterB = Correlated("b_id");
        var predA = Predicate(StageHarness.Orders, filterA);
        var predB = Predicate(StageHarness.Orders, filterB);
        h.Compiler.ForeignKeys[Node(predA)] = "a_id";
        h.Compiler.ForeignKeys[Node(predB)] = "b_id";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Loader.Override = async (r, _) =>
        {
            started.TrySetResult();
            await gate.Task;
            return new RowFilterKeySetEntry([1L], false, h.Time.GetUtcNow(), r.Tenant.Value, r.DataSourceName);
        };

        var running = Apply(h, StageHarness.Context(), Preds((StageHarness.Orders, predA)));
        await started.Task.WaitAsync(Wait);
        var (throttled, report) = await Apply(h, StageHarness.Context(), Preds((StageHarness.Orders, predB)));
        gate.SetResult();
        await running.WaitAsync(Wait);

        report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.LoadThrottled);
        Sql(throttled[StageHarness.Orders]!).ShouldContain("EXISTS");
        h.Loader.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task JoiningInFlightLoad_IsNotThrottled_SEC_RF107_13()
    {
        var h = Harness(concurrency: 1);
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Loader.Override = async (r, _) =>
        {
            started.TrySetResult();
            await gate.Task;
            return new RowFilterKeySetEntry([1L, 2L], false, h.Time.GetUtcNow(), r.Tenant.Value, r.DataSourceName);
        };

        var first = Apply(h, StageHarness.Context(), preds);
        await started.Task.WaitAsync(Wait);
        var second = Apply(h, StageHarness.Context(), preds);
        await Task.Delay(100);
        gate.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(Wait);

        results.ShouldAllBe(r => r.Report.Outcomes.Single().Outcome == KeySetOutcomeKind.Miss);
        h.Loader.Calls.ShouldBe(1);
    }

    // ---- text and bind budget (D-WPC-3, SEC-RF107-18) ----

    private static RowFilterKeySetContext PostgreSql(int userSqlLength, int factor = 64, string tenant = "t1") =>
        StageHarness.Context(tenant: tenant, userSqlLength: userSqlLength, dialect: TargetSqlDialect.PostgreSql) with { MaxExpansionFactor = factor };

    [Fact]
    public async Task ShortStatement_LargeKeySet_FallsBackExpansionBudget_NotCached_SEC_RF107_18()
    {
        var h = Harness(provider: "PostgreSql");
        h.Loader.KeyCount = 2000;
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        var (short1, shortReport) = await Apply(h, PostgreSql(40), preds);
        var (long1, longReport) = await Apply(h, PostgreSql(4000), preds);

        short1.ShouldBeSameAs(preds);
        shortReport.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.ExpansionBudget);
        longReport.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Hit);   // the load of the short statement was kept, not discarded
        longReport.Outcomes.Single().PaddedCount.ShouldBe(2000);
        h.Loader.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task PreLoadGate_SkipsKeyQuery_SEC_RF107_18()
    {
        var h = Harness(provider: "PostgreSql");
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        var (result, report) = await Apply(h, PostgreSql(64, factor: 1), preds);   // budget 32 characters: not even a 1-key list fits

        result.ShouldBeSameAs(preds);
        report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.ExpansionBudget);
        h.Loader.Calls.ShouldBe(0);
    }

    private (StageHarness H, Dictionary<TableIdentity, PolicyPredicate?> Preds, ExistsExpression A, ExistsExpression B) TwoSites(string provider)
    {
        var h = Harness(provider: provider);
        var predicate = Predicate(StageHarness.Orders, Correlated("a_id", group: 0), Correlated("b_id", group: 1));
        var sites = CorrelatedKeySetSubstitution.FindSites(predicate);
        h.Compiler.ForeignKeys[sites[0].Node] = "a_id";
        h.Compiler.ForeignKeys[sites[1].Node] = "b_id";
        h.Loader.Override = (r, _) =>
        {
            int count = r.Query.ForeignKeyColumn == "a_id" ? 100 : 30;
            return Task.FromResult(new RowFilterKeySetEntry(
                Enumerable.Range(1, count).Select(i => (object)(long)i).ToArray(), false, h.Time.GetUtcNow(), r.Tenant.Value, r.DataSourceName));
        };
        return (h, Preds((StageHarness.Orders, predicate)), sites[0].Node, sites[1].Node);
    }

    [Fact]
    public async Task CombinedBudget_DropsLargestFirst_ReasonPerConstraint_SEC_RF107_18()
    {
        // Text constraint (PostgreSQL has a huge bind budget): factor chosen so that both lists together exceed the budget but the larger alone fits.
        var (h, preds, _, _) = TwoSites("PostgreSql");
        long a = RowFilterKeySetTextBudget.EstimateListChars(TargetSqlDialect.PostgreSql, 0, 128, "a_id");
        long b = RowFilterKeySetTextBudget.EstimateListChars(TargetSqlDialect.PostgreSql, 1, 32, "b_id");
        int factor = (int)((a + b) / 32);   // KeyListBudget = factor * 64 / 2 = 32 * factor <= a + b - 1 at most
        while (32L * factor >= a + b) factor--;
        (32L * factor).ShouldBeGreaterThan(a);

        var (_, textReport) = await Apply(h, PostgreSql(64, factor), preds);

        textReport.Outcomes.Single(o => o.Outcome == KeySetOutcomeKind.Fallback).Reason.ShouldBe(KeySetFallbackReason.ExpansionBudget);
        textReport.Outcomes.Single(o => o.Outcome != KeySetOutcomeKind.Fallback).PaddedCount.ShouldBe(32);   // the smaller list survives

        // Bind constraint (SQL Server, reserve raised): the larger list falls back first, reason ParameterBudget.
        var (h2, preds2, _, _) = TwoSites("SqlServer");
        h2.Options.CurrentValue = new GatewayOptions
        {
            RowFilters = new RowFilterOptions { KeySetCache = new RowFilterKeySetCacheOptions { DefaultStrategy = RowFilterEvaluationStrategy.KeySetCache, BindParameterReserve = 1_000 } },
            DataSources = StageHarness.Gateway(true, RowFilterEvaluationStrategy.KeySetCache).DataSources
        };
        h2.Loader.Override = (r, _) =>
        {
            int count = r.Query.ForeignKeyColumn == "a_id" ? 1000 : 100;
            return Task.FromResult(new RowFilterKeySetEntry(
                Enumerable.Range(1, count).Select(i => (object)(long)i).ToArray(), false, h2.Time.GetUtcNow(), r.Tenant.Value, r.DataSourceName));
        };

        var (_, bindReport) = await Apply(h2, StageHarness.Context(userSqlLength: 4000), preds2);

        bindReport.Outcomes.Single(o => o.Outcome == KeySetOutcomeKind.Fallback).Reason.ShouldBe(KeySetFallbackReason.ParameterBudget);
        bindReport.Outcomes.Single(o => o.Outcome != KeySetOutcomeKind.Fallback).PaddedCount.ShouldBe(128);
    }

    [Fact]
    public async Task BudgetFallback_DoesNotEvictOrChangeCachedEntry_SEC_RF107_18()
    {
        var h = Harness(provider: "PostgreSql");
        h.Loader.KeyCount = 500;
        var predicate = Predicate(StageHarness.Orders, Correlated());
        var preds = Preds((StageHarness.Orders, predicate));
        var key = CacheKeyOf(h, PostgreSql(4000), predicate);

        await Apply(h, PostgreSql(4000), preds);
        var cached = h.Cache.TryGetRaw(key);
        cached.ShouldNotBeNull();
        long est = RowFilterKeySetTextBudget.EstimateListChars(TargetSqlDialect.PostgreSql, 0, 512, "client_id");
        int factor = (int)(est / 32) - 2;   // budget below the list, above the 1-key gate
        var (_, tight) = await Apply(h, PostgreSql(64, factor), preds);
        var (_, relaxed) = await Apply(h, PostgreSql(4000), preds);

        tight.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.ExpansionBudget);
        h.Cache.TryGetRaw(key).ShouldBeSameAs(cached);
        relaxed.Outcomes.Single().Outcome.ShouldBe(KeySetOutcomeKind.Hit);
        h.Loader.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task VaryingStatementLength_LoadsOncePerCacheKey_SEC_RF107_18()
    {
        var h = Harness(provider: "PostgreSql");
        h.Loader.KeyCount = 300;
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        foreach (int length in new[] { 10, 100, 1000, 4000, 70, 5 })
        {
            await Apply(h, PostgreSql(length), preds);
        }

        h.Loader.Calls.ShouldBe(1);
    }

    // ---- ordinals and programming errors (D-WPC-7) ----

    [Fact]
    public async Task SiteOrdinals_UniqueAcrossTables_StableUnderFallback()
    {
        var invoices = Predicate(StageHarness.Invoices, Correlated());
        var orders = Predicate(StageHarness.Orders, Correlated());
        var tables = new[] { StageHarness.Orders, StageHarness.Invoices };

        var h = Harness();
        var (result, _) = await Apply(h, StageHarness.Context(tables), Preds((StageHarness.Orders, orders), (StageHarness.Invoices, invoices)));
        string[] ordersNames = result[StageHarness.Orders]!.Parameters.Keys.Where(k => k.StartsWith("__rls_ks", StringComparison.Ordinal)).Select(k => k[..10]).Distinct().ToArray();
        string[] invoicesNames = result[StageHarness.Invoices]!.Parameters.Keys.Where(k => k.StartsWith("__rls_ks", StringComparison.Ordinal)).Select(k => k[..10]).Distinct().ToArray();
        ordersNames.ShouldBe(["__rls_ks1_"]);     // "main.invoices" sorts before "main.orders"
        invoicesNames.ShouldBe(["__rls_ks0_"]);

        // The invoices site falls back (overflow sentinel for its key column); the orders ordinal does not move.
        var h2 = Harness();
        var invoices2 = Predicate(StageHarness.Invoices, Correlated("order_ref"));
        var orders2 = Predicate(StageHarness.Orders, Correlated());
        h2.Compiler.ForeignKeys[Node(invoices2)] = "order_ref";
        h2.Loader.Override = (r, _) => Task.FromResult(r.Query.ForeignKeyColumn == "order_ref"
            ? RowFilterKeySetEntry.OverflowSentinel(h2.Time.GetUtcNow(), r.Tenant.Value, r.DataSourceName)
            : new RowFilterKeySetEntry([1L, 2L], false, h2.Time.GetUtcNow(), r.Tenant.Value, r.DataSourceName));
        var (result2, _) = await Apply(h2, StageHarness.Context(tables), Preds((StageHarness.Orders, orders2), (StageHarness.Invoices, invoices2)));
        result2[StageHarness.Orders]!.Parameters.Keys.Where(k => k.StartsWith("__rls_ks", StringComparison.Ordinal)).Select(k => k[..10]).Distinct().ShouldBe(["__rls_ks1_"]);
    }

    [Fact]
    public async Task SubstitutionProgrammingError_Propagates_D_WPC_7()
    {
        var h = Harness();
        var stage = new RowFilterKeySetStage(h.Options, h.Compiler, h.Epochs, h.Cache, h.Loader)
        {
            Substituter = (_, _, _) => throw new InvalidOperationException("seam")
        };
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, Correlated())));

        await Should.ThrowAsync<InvalidOperationException>(async () => await stage.ApplyAsync(StageHarness.Context(), preds, new RowFilterKeySetReport(), default));
    }

    // ---- operator diagnostics (D-WPC-9) ----

    [Fact]
    public async Task IneligibleStoredSetting_WarnsOncePerRule_NoValues()
    {
        var h = Harness(strategy: RowFilterEvaluationStrategy.Exists);
        var denyRule = Correlated(strategy: RowFilterEvaluationStrategy.KeySetCache, duration: 7654321);
        var temporalRule = Correlated(strategy: RowFilterEvaluationStrategy.KeySetCache, duration: 8765432, temporal: "created_at", validFrom: "valid_from");
        var negated = new TypedRowFilterBuilder().BuildCombined([Allow(Simple())], [Deny(denyRule)], StageHarness.Orders)!;
        var temporal = Predicate(StageHarness.Invoices, temporalRule);
        var preds = Preds((StageHarness.Orders, negated), (StageHarness.Invoices, temporal));
        var context = StageHarness.Context([StageHarness.Orders, StageHarness.Invoices]);

        var (_, first) = await Apply(h, context, preds);
        await Apply(h, context, preds);

        first.Outcomes.Select(o => o.Reason).OrderBy(r => r).ShouldBe([KeySetFallbackReason.Deny_Negated, KeySetFallbackReason.Temporal]);
        var warnings = h.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message).ToList();
        warnings.Count.ShouldBe(2);
        warnings.ShouldAllBe(m => !m.Contains("7654321") && !m.Contains("8765432"));
        warnings.ShouldContain(m => m.Contains(denyRule.Id.ToString()));
        warnings.ShouldContain(m => m.Contains(temporalRule.Id.ToString()));
        (h.Epochs.Calls, h.Loader.Calls).ShouldBe((0, 0));
    }

    [Fact]
    public async Task InvalidStoredSetting_Warns_AndKeepsExists()
    {
        var h = Harness();
        var rule = Correlated(strategy: RowFilterEvaluationStrategy.KeySetCache, duration: -5);
        var preds = Preds((StageHarness.Orders, Predicate(StageHarness.Orders, rule)));

        var (result, report) = await Apply(h, StageHarness.Context(), preds);
        await Apply(h, StageHarness.Context(), preds);

        result.ShouldBeSameAs(preds);
        report.Outcomes.Single().Reason.ShouldBe(KeySetFallbackReason.InvalidStoredSetting);
        h.Logs.Count(l => l.Level == LogLevel.Warning).ShouldBe(1);
        h.Loader.Calls.ShouldBe(0);
    }
}
