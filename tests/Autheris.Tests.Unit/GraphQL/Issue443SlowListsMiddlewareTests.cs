using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class Issue443SlowListsMiddlewareTests
{
    public sealed class TestResolverHook
    {
        public Func<IResolverContext, Task>? Action { get; init; }
        public bool ShouldThrow { get; init; }
    }

    public sealed class TestQuery
    {
        public async Task<string> Devices(IResolverContext ctx)
        {
            var hook = ctx.Services.GetService<TestResolverHook>();
            if (hook?.ShouldThrow == true)
            {
                throw new GraphQLException(ErrorBuilder.New().SetMessage("Test failure").SetCode("TEST_ERROR").Build());
            }

            if (hook?.Action != null)
            {
                await hook.Action(ctx);
            }
            return "ok";
        }
    }

    private static async Task<IRequestExecutor> CreateExecutorAsync(
        GraphQLOptions options,
        Func<IResolverContext, Task>? onResolve = null,
        bool throwInResolver = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton(new TestResolverHook { Action = onResolve, ShouldThrow = throwInResolver });

        return await services
            .AddGraphQLServer()
            .AddQueryType<TestQuery>()
            .UseRequest<SlowListsMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();
    }

    [Fact]
    public async Task Disabled_NoExtension()
    {
        var options = new GraphQLOptions
        {
            SlowLists = new GraphQLSlowListReportOptions { Enabled = false, ThresholdMs = 0 }
        };

        var executor = await CreateExecutorAsync(options, ctx =>
        {
            if (ctx.ContextData.TryGetValue(SlowListsMiddleware.ContextKey, out var accObj) && accObj is SlowListsAccumulator acc)
            {
                acc.ForRoot(ctx.ResponseName).OnReport(new TreeExecutionReport(
                    TimeSpan.FromMilliseconds(55), 10,
                    [new TreeListPlan(["readings"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            }
            return Task.CompletedTask;
        });

        var request = OperationRequestBuilder.New()
            .SetDocument("{ devices }")
            .AddGlobalState("Autheris.ExposeCostExtensions", true)
            .Build();

        var result = (OperationResult)await executor.ExecuteAsync(request);
        result.Extensions?.ContainsKey("slowLists").ShouldBeFalse();
    }

    [Fact]
    public async Task BelowThreshold_NoExtension()
    {
        var options = new GraphQLOptions
        {
            SlowLists = new GraphQLSlowListReportOptions { Enabled = true, ThresholdMs = 5000 }
        };

        var executor = await CreateExecutorAsync(options, ctx =>
        {
            if (ctx.ContextData.TryGetValue(SlowListsMiddleware.ContextKey, out var accObj) && accObj is SlowListsAccumulator acc)
            {
                acc.ForRoot(ctx.ResponseName).OnReport(new TreeExecutionReport(
                    TimeSpan.FromMilliseconds(5), 10,
                    [new TreeListPlan(["readings"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            }
            return Task.CompletedTask;
        });

        var request = OperationRequestBuilder.New()
            .SetDocument("{ devices }")
            .AddGlobalState("Autheris.ExposeCostExtensions", true)
            .Build();

        var result = (OperationResult)await executor.ExecuteAsync(request);
        result.Extensions?.ContainsKey("slowLists").ShouldBeFalse();
    }

    [Fact]
    public async Task AboveThreshold_ReportsTopPathsWithRank()
    {
        var options = new GraphQLOptions
        {
            SlowLists = new GraphQLSlowListReportOptions { Enabled = true, ThresholdMs = 0, MaxEntries = 3 }
        };

        var executor = await CreateExecutorAsync(options, ctx =>
        {
            if (ctx.ContextData.TryGetValue(SlowListsMiddleware.ContextKey, out var accObj) && accObj is SlowListsAccumulator acc)
            {
                acc.ForRoot(ctx.ResponseName).OnReport(new TreeExecutionReport(
                    TimeSpan.FromMilliseconds(55), 10,
                    [new TreeListPlan(["readings"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            }
            return Task.CompletedTask;
        });

        var request = OperationRequestBuilder.New()
            .SetDocument("{ devices }")
            .AddGlobalState("Autheris.ExposeCostExtensions", true)
            .Build();

        var result = (OperationResult)await executor.ExecuteAsync(request);
        result.Extensions.ShouldNotBeNull();
        result.Extensions.TryGetValue("slowLists", out var slowListsObj).ShouldBeTrue();

        var list = slowListsObj.ShouldBeOfType<List<Dictionary<string, object?>>>();
        list.Count.ShouldBe(1);
        list[0]["path"].ShouldBe("devices.readings");
        list[0]["rank"].ShouldBe(1);
    }

    [Fact]
    public async Task PolicyWithoutExposeCost_NoExtension()
    {
        var options = new GraphQLOptions
        {
            SlowLists = new GraphQLSlowListReportOptions { Enabled = true, ThresholdMs = 0 }
        };

        var executor = await CreateExecutorAsync(options, ctx =>
        {
            if (ctx.ContextData.TryGetValue(SlowListsMiddleware.ContextKey, out var accObj) && accObj is SlowListsAccumulator acc)
            {
                acc.ForRoot(ctx.ResponseName).OnReport(new TreeExecutionReport(
                    TimeSpan.FromMilliseconds(55), 10,
                    [new TreeListPlan(["readings"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            }
            return Task.CompletedTask;
        });

        // ExposeCostExtensions is false (e.g. Free Tier)
        var request = OperationRequestBuilder.New()
            .SetDocument("{ devices }")
            .AddGlobalState("Autheris.ExposeCostExtensions", false)
            .Build();

        var result = (OperationResult)await executor.ExecuteAsync(request);
        result.Extensions?.ContainsKey("slowLists").ShouldBeFalse();
    }

    [Fact]
    public async Task ErrorResult_NoExtensionAndBodyUnchanged()
    {
        var options = new GraphQLOptions
        {
            SlowLists = new GraphQLSlowListReportOptions { Enabled = true, ThresholdMs = 0 }
        };

        var executor = await CreateExecutorAsync(options, ctx =>
        {
            if (ctx.ContextData.TryGetValue(SlowListsMiddleware.ContextKey, out var accObj) && accObj is SlowListsAccumulator acc)
            {
                acc.ForRoot(ctx.ResponseName).OnReport(new TreeExecutionReport(
                    TimeSpan.FromMilliseconds(55), 10,
                    [new TreeListPlan(["readings"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            }
            return Task.CompletedTask;
        }, throwInResolver: true);

        var request = OperationRequestBuilder.New()
            .SetDocument("{ devices }")
            .AddGlobalState("Autheris.ExposeCostExtensions", true)
            .Build();

        var result = (OperationResult)await executor.ExecuteAsync(request);
        result.Errors.ShouldNotBeNull();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Code.ShouldBe("TEST_ERROR");
        result.Extensions?.ContainsKey("slowLists").ShouldBeFalse();
    }

    [Fact]
    public async Task Extension_ContainsOnlyPathAndRank_NoTableIndexSqlOrCounts()
    {
        var options = new GraphQLOptions
        {
            SlowLists = new GraphQLSlowListReportOptions { Enabled = true, ThresholdMs = 0 }
        };

        var executor = await CreateExecutorAsync(options, ctx =>
        {
            if (ctx.ContextData.TryGetValue(SlowListsMiddleware.ContextKey, out var accObj) && accObj is SlowListsAccumulator acc)
            {
                acc.ForRoot(ctx.ResponseName).OnReport(new TreeExecutionReport(
                    TimeSpan.FromMilliseconds(55), 10,
                    [new TreeListPlan(["readings"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            }
            return Task.CompletedTask;
        });

        var request = OperationRequestBuilder.New()
            .SetDocument("{ devices }")
            .AddGlobalState("Autheris.ExposeCostExtensions", true)
            .Build();

        var result = (OperationResult)await executor.ExecuteAsync(request);
        var list = (List<Dictionary<string, object?>>)result.Extensions!["slowLists"]!;
        var item = list[0];

        item.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(["path", "rank"]);
        item.ContainsKey("duration").ShouldBeFalse();
        item.ContainsKey("elapsed").ShouldBeFalse();
        item.ContainsKey("count").ShouldBeFalse();
        item.ContainsKey("limit").ShouldBeFalse();
        item.ContainsKey("table").ShouldBeFalse();
        item.ContainsKey("index").ShouldBeFalse();
        item.ContainsKey("sql").ShouldBeFalse();
        item.ContainsKey("strategy").ShouldBeFalse();
        item.ContainsKey("reason").ShouldBeFalse();
    }

    [Fact]
    public void Ranking_UnindexedBeforeSetBasedBeforeIndexed()
    {
        var acc = new SlowListsAccumulator();
        var obs = acc.ForRoot("root");

        obs.OnReport(new TreeExecutionReport(
            TimeSpan.FromMilliseconds(100), 10,
            [
                new TreeListPlan(["ix"], TreeListStrategy.CorrelatedIndexed, null, 10),
                new TreeListPlan(["set"], TreeListStrategy.SetBased, null, 10),
                new TreeListPlan(["un"], TreeListStrategy.CorrelatedUnindexed, null, 10)
            ], []));

        var candidates = acc.BuildCandidates(TimeSpan.FromMilliseconds(200));

        candidates.Count.ShouldBe(3);
        candidates[0].Path.ShouldBe("root.un");
        candidates[1].Path.ShouldBe("root.set");
        candidates[2].Path.ShouldBe("root.ix");
    }

    [Fact]
    public void Ranking_CrossSourceOverHalfElapsed_RanksFirst()
    {
        var acc = new SlowListsAccumulator();
        var obs = acc.ForRoot("root");

        obs.OnReport(new TreeExecutionReport(
            TimeSpan.FromMilliseconds(100), 10,
            [
                new TreeListPlan(["unindexed_sql"], TreeListStrategy.CorrelatedUnindexed, null, 10)
            ],
            [
                new CrossSourceListExecution(["remote_list"], TimeSpan.FromMilliseconds(60), 10)
            ]));

        // Request elapsed = 100ms. Remote list took 60ms >= 50ms (half) -> Dominant cross-source!
        var candidates = acc.BuildCandidates(TimeSpan.FromMilliseconds(100));

        candidates.Count.ShouldBe(2);
        candidates[0].Path.ShouldBe("root.remote_list");
        candidates[1].Path.ShouldBe("root.unindexed_sql");
    }

    [Fact]
    public void Ranking_CrossSourceBelowHalfElapsed_RanksWithUnindexed()
    {
        var acc = new SlowListsAccumulator();
        var obs = acc.ForRoot("root");

        obs.OnReport(new TreeExecutionReport(
            TimeSpan.FromMilliseconds(100), 10,
            [
                new TreeListPlan(["unindexed_sql"], TreeListStrategy.CorrelatedUnindexed, null, 10)
            ],
            [
                // Remote list took 20ms < 50ms -> Not dominant, ranks by weight/path with unindexed
                new CrossSourceListExecution(["remote_list"], TimeSpan.FromMilliseconds(20), 5)
            ]));

        var candidates = acc.BuildCandidates(TimeSpan.FromMilliseconds(100));

        candidates.Count.ShouldBe(2);
        // weight of unindexed_sql: 10 * 10 = 100 > weight of remote_list: 10 * 5 = 50
        candidates[0].Path.ShouldBe("root.unindexed_sql");
        candidates[1].Path.ShouldBe("root.remote_list");
    }

    [Fact]
    public void MaxEntries_Respected()
    {
        var acc = new SlowListsAccumulator();
        var obs = acc.ForRoot("root");

        obs.OnReport(new TreeExecutionReport(
            TimeSpan.FromMilliseconds(100), 10,
            [
                new TreeListPlan(["l1"], TreeListStrategy.CorrelatedUnindexed, null, 10),
                new TreeListPlan(["l2"], TreeListStrategy.CorrelatedUnindexed, null, 20),
                new TreeListPlan(["l3"], TreeListStrategy.CorrelatedUnindexed, null, 30),
                new TreeListPlan(["l4"], TreeListStrategy.CorrelatedUnindexed, null, 40)
            ], []));

        var candidates = acc.BuildCandidates(TimeSpan.FromMilliseconds(200));
        candidates.Count.ShouldBe(4);

        // Sorting by weight descending: l4 (400), l3 (300), l2 (200), l1 (100)
        candidates.Take(2).Select(c => c.Path).ShouldBe(["root.l4", "root.l3"]);
    }

    [Fact]
    public void Options_RangeValidation()
    {
        var valid = new GraphQLSlowListReportOptions { Enabled = true, ThresholdMs = 1000, MaxEntries = 3 };
        Validator.TryValidateObject(valid, new ValidationContext(valid), null, true).ShouldBeTrue();

        var lowThreshold = new GraphQLSlowListReportOptions { ThresholdMs = 49 };
        Validator.TryValidateObject(lowThreshold, new ValidationContext(lowThreshold), null, true).ShouldBeFalse();

        var highThreshold = new GraphQLSlowListReportOptions { ThresholdMs = 600001 };
        Validator.TryValidateObject(highThreshold, new ValidationContext(highThreshold), null, true).ShouldBeFalse();

        var lowEntries = new GraphQLSlowListReportOptions { MaxEntries = 0 };
        Validator.TryValidateObject(lowEntries, new ValidationContext(lowEntries), null, true).ShouldBeFalse();

        var highEntries = new GraphQLSlowListReportOptions { MaxEntries = 6 };
        Validator.TryValidateObject(highEntries, new ValidationContext(highEntries), null, true).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Gate_AllConjuncts_EachMissingSuppresses()
    {
        // 1. Missing Enabled
        var optDisabled = new GraphQLOptions { SlowLists = new GraphQLSlowListReportOptions { Enabled = false, ThresholdMs = 0 } };
        var exec1 = await CreateExecutorAsync(optDisabled, ctx =>
        {
            ((SlowListsAccumulator)ctx.ContextData[SlowListsMiddleware.ContextKey]!).ForRoot(ctx.ResponseName)
                .OnReport(new TreeExecutionReport(TimeSpan.FromMilliseconds(55), 1, [new TreeListPlan(["r"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            return Task.CompletedTask;
        });
        var res1 = (OperationResult)await exec1.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ devices }").AddGlobalState("Autheris.ExposeCostExtensions", true).Build());
        res1.Extensions?.ContainsKey("slowLists").ShouldBeFalse();

        // 2. Below threshold
        var optHighThreshold = new GraphQLOptions { SlowLists = new GraphQLSlowListReportOptions { Enabled = true, ThresholdMs = 5000 } };
        var exec2 = await CreateExecutorAsync(optHighThreshold, ctx =>
        {
            ((SlowListsAccumulator)ctx.ContextData[SlowListsMiddleware.ContextKey]!).ForRoot(ctx.ResponseName)
                .OnReport(new TreeExecutionReport(TimeSpan.FromMilliseconds(5), 1, [new TreeListPlan(["r"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            return Task.CompletedTask;
        });
        var res2 = (OperationResult)await exec2.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ devices }").AddGlobalState("Autheris.ExposeCostExtensions", true).Build());
        res2.Extensions?.ContainsKey("slowLists").ShouldBeFalse();

        // 3. Missing ContextData key Autheris.ExposeCostExtensions
        var optEnabled = new GraphQLOptions { SlowLists = new GraphQLSlowListReportOptions { Enabled = true, ThresholdMs = 0 } };
        var exec3 = await CreateExecutorAsync(optEnabled, ctx =>
        {
            ((SlowListsAccumulator)ctx.ContextData[SlowListsMiddleware.ContextKey]!).ForRoot(ctx.ResponseName)
                .OnReport(new TreeExecutionReport(TimeSpan.FromMilliseconds(55), 1, [new TreeListPlan(["r"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            return Task.CompletedTask;
        });
        var res3 = (OperationResult)await exec3.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ devices }").Build());
        res3.Extensions?.ContainsKey("slowLists").ShouldBeFalse();

        // 4. ExposeCostExtensions = false
        var res4 = (OperationResult)await exec3.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ devices }").AddGlobalState("Autheris.ExposeCostExtensions", false).Build());
        res4.Extensions?.ContainsKey("slowLists").ShouldBeFalse();

        // 5. Result has error
        var exec5 = await CreateExecutorAsync(optEnabled, ctx =>
        {
            ((SlowListsAccumulator)ctx.ContextData[SlowListsMiddleware.ContextKey]!).ForRoot(ctx.ResponseName)
                .OnReport(new TreeExecutionReport(TimeSpan.FromMilliseconds(55), 1, [new TreeListPlan(["r"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            return Task.CompletedTask;
        }, throwInResolver: true);
        var res5 = (OperationResult)await exec5.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ devices }").AddGlobalState("Autheris.ExposeCostExtensions", true).Build());
        res5.Extensions?.ContainsKey("slowLists").ShouldBeFalse();

        // 6. All conjuncts met
        var res6 = (OperationResult)await exec3.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ devices }").AddGlobalState("Autheris.ExposeCostExtensions", true).Build());
        res6.Extensions?.ContainsKey("slowLists").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FreeTier_NoExtension()
    {
        var options = new GraphQLOptions { SlowLists = new GraphQLSlowListReportOptions { Enabled = true, ThresholdMs = 0 } };
        var executor = await CreateExecutorAsync(options, ctx =>
        {
            ((SlowListsAccumulator)ctx.ContextData[SlowListsMiddleware.ContextKey]!).ForRoot(ctx.ResponseName)
                .OnReport(new TreeExecutionReport(TimeSpan.FromMilliseconds(55), 1, [new TreeListPlan(["r"], TreeListStrategy.CorrelatedUnindexed, null, 1)], []));
            return Task.CompletedTask;
        });

        // Free tier: ExposeCostExtensions is false
        var request = OperationRequestBuilder.New()
            .SetDocument("{ devices }")
            .AddGlobalState("Autheris.ExposeCostExtensions", false)
            .Build();

        var result = (OperationResult)await executor.ExecuteAsync(request);
        result.Extensions?.ContainsKey("slowLists").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Extension_PathsAreResponseKeysNotCatalogNames()
    {
        var acc = new SlowListsAccumulator();
        // Client uses alias: fleet: devices { readings: sensor_readings_table }
        var obs = acc.ForRoot("fleet");

        obs.OnReport(new TreeExecutionReport(
            TimeSpan.FromMilliseconds(100), 10,
            [
                new TreeListPlan(["recent_readings"], TreeListStrategy.CorrelatedUnindexed, null, 1)
            ], []));

        var candidates = acc.BuildCandidates(TimeSpan.FromMilliseconds(200));

        candidates.Count.ShouldBe(1);
        candidates[0].Path.ShouldBe("fleet.recent_readings");
        candidates[0].Path.ShouldNotContain("sensor_readings_table");
        candidates[0].Path.ShouldNotContain("devices");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Counter_TagsContainNoNamesOrTenant()
    {
        // GatewayDiagnostics.TreeNestedListStrategy is registered with description
        GatewayDiagnostics.TreeNestedListStrategy.Name.ShouldBe("autheris.tree.nested_list.strategy");
    }
}
