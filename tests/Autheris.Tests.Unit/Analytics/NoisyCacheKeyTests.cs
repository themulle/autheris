namespace Autheris.Tests.Unit.Analytics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#112 A1-2 / A1-12 (SEC-112-12, -21, -26): the noisy-result cache key and the decision fingerprint.</summary>
[Trait("Category", "Security")]
public sealed class NoisyCacheKeyTests
{
    private static readonly TableIdentifier TableId = new("sales", "public", "orders");

    private static readonly TableMetadata Meta = new()
    {
        Identifier = TableId,
        Table = new Table { Id = Guid.NewGuid(), SourceType = "PostgreSQL", SourceName = "default", SchemaName = "public", TableName = "orders", DataSourceType = DataSourceType.Sql, IsActive = true },
        Columns =
        [
            new TableColumn { ColumnName = "department", DataType = "varchar" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" }
        ]
    };

    private static TableAccessDecision Decision(
        ColumnAccessLevel amount = ColumnAccessLevel.Clear,
        string? rowFilter = null,
        IReadOnlyDictionary<string, object?>? rowParameters = null) =>
        TableAccessDecision.Allowed(
            TableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = ColumnAccessLevel.Clear,
                ["region"] = ColumnAccessLevel.Clear,
                ["amount"] = amount
            },
            rowFilter,
            rowFilterParameters: rowParameters);

    private static string Fingerprint(TableAccessDecision decision, long generation = 1) =>
        NoisyCacheKey.Fingerprint(decision, Meta, generation);

    private static TableFilterClause Where(string predicate, params (string Name, object? Value)[] parameters) =>
        new(predicate, parameters.ToDictionary(p => p.Name, p => p.Value), ["department"]);

    private static string Key(
        string tenant = "tenant-a",
        string sid = "S-1",
        IReadOnlyList<string>? groupBy = null,
        TableFilterClause? where = null,
        string fingerprint = "fp",
        TableIdentifier? table = null,
        IReadOnlyList<AggregateSelection>? aggregates = null) =>
        NoisyCacheKey.Build(
            new TenantId(tenant),
            new Sid(sid),
            table ?? TableId,
            groupBy ?? ["department"],
            aggregates ?? [new AggregateSelection(AggregateFunction.Count, null, "cnt")],
            where,
            null,
            10,
            2,
            SmallGroupStrategy.Suppress,
            "20261018",
            fingerprint);

    [Fact]
    public void NoisyCacheKey_SameInputs_SameKey() =>
        Key(where: Where("\"department\" = @p0", ("@p0", "A"))).ShouldBe(Key(where: Where("\"department\" = @p0", ("@p0", "A"))));

    [Fact]
    public void NoisyCacheKey_DiffersByTenant() => Key(tenant: "tenant-a").ShouldNotBe(Key(tenant: "tenant-b"));

    [Fact]
    public void NoisyCacheKey_DiffersBySubject() => Key(sid: "S-1").ShouldNotBe(Key(sid: "S-2"));

    [Fact]
    public void NoisyCacheKey_DiffersByParameterValue_SamePredicateText() =>
        Key(where: Where("\"department\" = @p0", ("@p0", "A"))).ShouldNotBe(Key(where: Where("\"department\" = @p0", ("@p0", "B"))));

    [Fact]
    public void NoisyCacheKey_ParameterOrderDoesNotMatter() =>
        Key(where: Where("x", ("@a", 1), ("@b", 2))).ShouldBe(Key(where: Where("x", ("@b", 2), ("@a", 1))));

    [Fact]
    public void NoisyCacheKey_ParameterTypeIsPartOfTheKey() =>
        Key(where: Where("x", ("@a", 1))).ShouldNotBe(Key(where: Where("x", ("@a", "1"))));

    [Fact]
    public void NoisyCacheKey_NoDelimiterCollision_GroupByColumns() =>
        Key(groupBy: ["a", "b,c"]).ShouldNotBe(Key(groupBy: ["a,b", "c"]));

    [Fact]
    public void NoisyCacheKey_NoDelimiterCollision_ParameterNamesAndValues()
    {
        Key(where: Where("x", ("@a", "b|@c=s:d"))).ShouldNotBe(Key(where: Where("x", ("@a", "b"), ("@c", "d"))));
        Key(where: Where("x|y")).ShouldNotBe(Key(where: Where("x", ("y", null))));
    }

    [Fact]
    public void NoisyCacheKey_NoDelimiterCollision_TenantSubjectAndTable()
    {
        Key(tenant: "a-b", sid: "c").ShouldNotBe(Key(tenant: "a", sid: "b-c"));
        Key(table: new TableIdentifier("a", "b-c", "d")).ShouldNotBe(Key(table: new TableIdentifier("a-b", "c", "d")));
    }

    [Fact]
    public void NoisyCacheKey_DiffersByFingerprint() => Key(fingerprint: "fp1").ShouldNotBe(Key(fingerprint: "fp2"));

    [Fact]
    public void NoisyCacheKey_DiffersAfterPolicyChange_ColumnDecision() =>
        Fingerprint(Decision(amount: ColumnAccessLevel.Clear)).ShouldNotBe(Fingerprint(Decision(amount: ColumnAccessLevel.Mask)));

    [Fact]
    public void NoisyCacheKey_DiffersAfterPolicyChange_RowFilterText() =>
        Fingerprint(Decision(rowFilter: "region = 'EU'")).ShouldNotBe(Fingerprint(Decision(rowFilter: "region = 'US'")));

    [Fact]
    public void NoisyCacheKey_DiffersByCallerAttributeValue_SameGeneration()
    {
        var eu = Decision(rowFilter: "region = @__gql_rls_0", rowParameters: new Dictionary<string, object?> { ["@__gql_rls_0"] = "EU" });
        var us = Decision(rowFilter: "region = @__gql_rls_0", rowParameters: new Dictionary<string, object?> { ["@__gql_rls_0"] = "US" });

        Fingerprint(eu, generation: 7).ShouldNotBe(Fingerprint(us, generation: 7));
    }

    [Fact]
    public void NoisyCacheKey_DiffersAfterCallerAttributeChange_Generation()
    {
        var d = Decision(rowFilter: "region = @__gql_rls_0", rowParameters: new Dictionary<string, object?> { ["@__gql_rls_0"] = "EU" });

        Fingerprint(d, generation: 7).ShouldNotBe(Fingerprint(d, generation: 8));
    }

    [Fact]
    public void NoisyCacheKey_SameDecision_SameFingerprint()
    {
        var a = Decision(rowFilter: "region = @__gql_rls_0", rowParameters: new Dictionary<string, object?> { ["@__gql_rls_0"] = "EU" });
        var b = Decision(rowFilter: "region = @__gql_rls_0", rowParameters: new Dictionary<string, object?> { ["@__gql_rls_0"] = "EU" });

        Fingerprint(a).ShouldBe(Fingerprint(b));
        Fingerprint(a).Length.ShouldBe(64);
    }

    [Fact]
    public void NoisyCacheKey_FingerprintAndKey_DoNotContainValues()
    {
        var d = Decision(rowFilter: "region = @__gql_rls_0", rowParameters: new Dictionary<string, object?> { ["@__gql_rls_0"] = "SECRET-REGION" });

        var key = Key(where: Where("x", ("@a", "SECRET-WHERE")), fingerprint: Fingerprint(d));

        key.ShouldNotContain("SECRET");
        Fingerprint(d).ShouldNotContain("SECRET");
    }

    // ---------------------------------------------------------------- service level

    private sealed class CountingExecutor(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IDataSourceExecutor
    {
        public int Calls { get; private set; }

        public DataSourceType SupportedType => DataSourceType.Sql;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(rows);
        }
    }

    private sealed class NullAudit : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private static ClaimsPrincipal Principal(string tenant = "tenant-a") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ANALYST"),
            new Claim(ClaimTypes.Name, "analyst"),
            new Claim(ClaimTypes.Role, "Analyst"),
            new Claim("tenant_id", tenant)
        ], "TestAuth"));

    private static (GovernedAggregationService Service, CountingExecutor Executor, Func<double> NoiseCalls) CreateNoisyService(
        int cacheMaxEntries,
        ICallerAttributeRegistry? registry = null)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (var i = 0; i < 6; i++)
        {
            rows.Add(new Dictionary<string, object?> { ["department"] = "A", ["region"] = "EU", ["amount"] = 1m + i });
        }

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(TableId, Arg.Any<CancellationToken>()).Returns(Meta);
        var executor = new CountingExecutor(rows);
        double draws = 0;
        var service = new GovernedAggregationService(
            metaRepo,
            new NullAudit(),
            dataSourceExecutors: [executor],
            analyticsOptions: Options.Create(new AnalyticsOptions
            {
                Privacy = new AnalyticsPrivacyOptions
                {
                    DefaultMinGroupSize = 2,
                    Noise = new AnalyticsNoiseOptions
                    {
                        Enabled = true,
                        Epsilon = 1.0,
                        DailyBudgetEpsilon = 1000.0,
                        CacheMaxEntries = cacheMaxEntries,
                        MeasureBounds = new Dictionary<string, MeasureBoundsOptions>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["amount"] = new MeasureBoundsOptions { Min = 0, Max = 1000 }
                        }
                    }
                }
            }),
            dpEngine: null,
            callerAttributeRegistry: registry)
        {
            NoiseSampler = _ => ++draws
        };
        return (service, executor, () => draws);
    }

    private static AggregationQuery Query(int limit) => new(
        TableId,
        GroupBy: ["department"],
        Aggregates: [new AggregateSelection(AggregateFunction.Count, null, "cnt")],
        Limit: limit,
        PreEvaluatedDecision: Decision());

    private static long Cnt(AggregationResult r) => Convert.ToInt64(r.Groups.Single().Aggregates["cnt"]);

    [Fact]
    public async Task NoisyCache_SameQuery_ReturnsTheCachedNoisyResult()
    {
        var (service, executor, _) = CreateNoisyService(cacheMaxEntries: 10);

        var first = await service.ExecuteAggregationAsync(Principal(), Query(5));
        var second = await service.ExecuteAggregationAsync(Principal(), Query(5));

        Cnt(second).ShouldBe(Cnt(first));
        executor.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task NoisyCache_Full_EvictsOldest_KeepsSameQuerySameNoise()
    {
        var (service, executor, _) = CreateNoisyService(cacheMaxEntries: 2);

        var a1 = await service.ExecuteAggregationAsync(Principal(), Query(1));
        var b1 = await service.ExecuteAggregationAsync(Principal(), Query(2));
        var c1 = await service.ExecuteAggregationAsync(Principal(), Query(3)); // cache full: evicts the oldest (A) only
        executor.Calls.ShouldBe(3);

        var b2 = await service.ExecuteAggregationAsync(Principal(), Query(2));
        var c2 = await service.ExecuteAggregationAsync(Principal(), Query(3));
        executor.Calls.ShouldBe(3); // B and C survived: same query, same noise, no new read
        Cnt(b2).ShouldBe(Cnt(b1));
        Cnt(c2).ShouldBe(Cnt(c1));

        var a2 = await service.ExecuteAggregationAsync(Principal(), Query(1));
        executor.Calls.ShouldBe(4); // A was evicted and is computed again with a fresh draw
        Cnt(a2).ShouldNotBe(Cnt(a1));
    }

    [Fact]
    public async Task NoisyCacheKey_AttributeResolutionFault_NoCacheReuse()
    {
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns<ValueTask<CallerAttributeSnapshot>>(_ => throw new InvalidOperationException("registry down"));
        var (service, executor, _) = CreateNoisyService(cacheMaxEntries: 10, registry);

        var first = await service.ExecuteAggregationAsync(Principal(), Query(5));
        var second = await service.ExecuteAggregationAsync(Principal(), Query(5));

        executor.Calls.ShouldBe(2); // neither read from nor written to the noisy cache
        Cnt(second).ShouldNotBe(Cnt(first));
    }

    [Fact]
    public async Task NoisyCacheKey_DiffersAfterCallerAttributeChange_ServiceDoesNotReuseAcrossGenerations()
    {
        long generation = 1;
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<CallerAttributeSnapshot>(new CallerAttributeSnapshot(generation, [], [])));
        var (service, executor, _) = CreateNoisyService(cacheMaxEntries: 10, registry);

        await service.ExecuteAggregationAsync(Principal(), Query(5));
        await service.ExecuteAggregationAsync(Principal(), Query(5));
        executor.Calls.ShouldBe(1);

        generation = 2;
        await service.ExecuteAggregationAsync(Principal(), Query(5));
        executor.Calls.ShouldBe(2);
    }
}
