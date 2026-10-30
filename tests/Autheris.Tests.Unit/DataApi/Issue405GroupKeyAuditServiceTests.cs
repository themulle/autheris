namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Tests.Unit.Analytics;
using Autheris.Application.Audit;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#405: the refused group-key type is audited with its closed family. I405-SC-01..03/-07/-09.</summary>
[Trait("Category", "Security")]
public sealed class Issue405GroupKeyAuditServiceTests
{
    private const string Sentinel = "SENTINEL_9f";
    private static readonly TableIdentifier TableId = new("sales", "dbo", "orders");

    private static readonly string[] RefusalKeyOrder = ["is_allowed", "reasons", "group_by", "filter", "refusal_reason", "group_key_type_family"];

    private sealed class Harness
    {
        public required GovernedAggregationService Service { get; init; }

        public required List<AuditLogEntry> Entries { get; init; }

        public required IGovernedSqlExecutionService Sql { get; init; }

        public required IGovernedTableReadPlanner Planner { get; init; }

        public required IDifferentialPrivacyEngine Dp { get; init; }
    }

    private static TableMetadata Metadata(string sourceType, DataSourceType type, params (string Name, string Type)[] cols)
    {
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = TableId,
            Table = new Table { Id = id, SourceType = sourceType, SourceName = "sales", SchemaName = "dbo", TableName = "orders", DataSourceType = type, IsActive = true },
            Columns = cols.Select(c => new TableColumn { TableId = id, ColumnName = c.Name, DataType = c.Type }).ToList()
        };
    }

    private static TableAccessDecision Decision(params (string Name, ColumnAccessLevel Level)[] levels) =>
        TableAccessDecision.Allowed(TableId, levels.ToDictionary(l => l.Name, l => l.Level, StringComparer.OrdinalIgnoreCase), hasUnconstrainedColumnAllow: true);

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ANALYST"),
            new Claim(ClaimTypes.Name, "analyst"),
            new Claim("tenant_id", "tenant-a")
        ], "TestAuth"));

    private sealed class FixedExecutor(DataSourceType type) : IDataSourceExecutor
    {
        public DataSourceType SupportedType => type;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, object?> row = new Dictionary<string, object?> { ["grp"] = "a", ["amount"] = 1m };
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([row, row]);
        }
    }

    private static Harness Create(TableMetadata metadata, IGovernedTableReadPlanner? planner = null, IReadOnlyList<IReadOnlyDictionary<string, object?>>? sqlRows = null, bool noise = false)
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(TableId, Arg.Any<CancellationToken>()).Returns(metadata);

        var rows = sqlRows ?? [];
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(_ => new GovernedSqlResult("select", [], rows, rows.Count, 1));

        var entries = new List<AuditLogEntry>();
        var writer = Substitute.For<IAuditWriter>();
        writer.RecordRequiredAsync(Arg.Do<AuditLogEntry>(entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        writer.RecordBestEffortAsync(Arg.Do<AuditLogEntry>(entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        writer.RecordAsync(Arg.Do<AuditLogEntry>(entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var spyPlanner = planner ?? Substitute.For<IGovernedTableReadPlanner>();
        var dp = Substitute.For<IDifferentialPrivacyEngine>();
        var service = new GovernedAggregationService(
            metaRepo,
            sqlExecutionService: sql,
            dataSourceExecutors: [new FixedExecutor(DataSourceType.HttpDeclarative)],
            auditWriter: writer,
            analyticsOptions: Options.Create(new AnalyticsOptions { Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 1 } }),
            planner: spyPlanner,
            dpEngine: dp);
        return new Harness { Service = service, Entries = entries, Sql = sql, Planner = spyPlanner, Dp = dp };
    }

    private static AggregationQuery Query(string[] groupBy, TableAccessDecision decision, TableFilterClause? where = null, AggregateSelection[]? aggregates = null) =>
        new(TableId, GroupBy: groupBy, Aggregates: aggregates ?? [new AggregateSelection(AggregateFunction.Count, null, "n")], Where: where, PreEvaluatedDecision: decision.AsPdpEvaluated());

    private static TableAccessDecision AllClear(params string[] extra) =>
        Decision(new[] { "id", "grp", "k1", "k2", "region", "amount" }.Concat(extra).Select(n => (n, ColumnAccessLevel.Clear)).ToArray());

    private static string[] Keys(AuditLogEntry entry) => JsonDocument.Parse(entry.DetailsJson!).RootElement.EnumerateObject().Select(p => p.Name).ToArray();

    private static string Prop(AuditLogEntry entry, string name) => JsonDocument.Parse(entry.DetailsJson!).RootElement.GetProperty(name).GetString()!;

    [Fact]
    public async Task I405_T_06_RefusedKey_WritesDenyAudit_WithFamily_PlannerAndSqlNotCalled()
    {
        var h = Create(Metadata("sqlserver", DataSourceType.Sql, ("id", "int"), ("grp", "xml"), ("amount", "decimal")));

        await Should.ThrowAsync<AggregationGroupKeyNotSupportedException>(() => h.Service.ExecuteAggregationAsync(Principal(), Query(["grp"], AllClear())));

        var entry = h.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.GovernedAggregation);
        entry.EventType.ShouldBe("AGGREGATE_QUERY");
        entry.Decision.ShouldBe("DENY");
        Prop(entry, "group_key_type_family").ShouldBe("xml");
        Prop(entry, "refusal_reason").ShouldBe("GROUP_KEY_TYPE_NOT_GROUPABLE");
        h.Planner.ReceivedCalls().ShouldBeEmpty();
        h.Sql.ReceivedCalls().ShouldBeEmpty();
        h.Dp.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task I405_T_07_TypeSentinel_AbsentFrom_Audit_Logs_Exception_ProblemDetails()
    {
        var h = Create(Metadata("sqlserver", DataSourceType.Sql, ("id", "int"), ("grp", "udt_" + Sentinel), ("amount", "decimal")));

        var ex = await Should.ThrowAsync<AggregationGroupKeyNotSupportedException>(() => h.Service.ExecuteAggregationAsync(Principal(), Query(["grp"], AllClear())));

        var entry = h.Entries.ShouldHaveSingleItem();
        Prop(entry, "group_key_type_family").ShouldBe("unknown");
        entry.DetailsJson.ShouldNotContain(Sentinel);
        ex.Message.ShouldNotContain(Sentinel);
        ex.ToString().ShouldNotContain(Sentinel);
        ex.Data.Count.ShouldBe(0);
    }

    [Fact]
    public async Task I405_T_S03_NoFamilyOrName_InActivityTagsOrMeters()
    {
        var tags = new List<string>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => tags.AddRange(a.Tags.Select(t => $"{t.Key}={t.Value}").Concat(a.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}")))),
        };
        ActivitySource.AddActivityListener(activityListener);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (i, l) => l.EnableMeasurementEvents(i);
        meterListener.SetMeasurementEventCallback<long>((_, _, t, _) => tags.AddRange(t.ToArray().Select(x => $"{x.Key}={x.Value}")));
        meterListener.SetMeasurementEventCallback<double>((_, _, t, _) => tags.AddRange(t.ToArray().Select(x => $"{x.Key}={x.Value}")));
        meterListener.SetMeasurementEventCallback<int>((_, _, t, _) => tags.AddRange(t.ToArray().Select(x => $"{x.Key}={x.Value}")));
        meterListener.Start();

        var h = Create(Metadata("sqlserver", DataSourceType.Sql, ("id", "int"), ("col_" + Sentinel, "udt_" + Sentinel), ("amount", "decimal")));
        await Should.ThrowAsync<AggregationGroupKeyNotSupportedException>(() => h.Service.ExecuteAggregationAsync(
            Principal(), Query(["col_" + Sentinel], Decision(("id", ColumnAccessLevel.Clear), ("col_" + Sentinel, ColumnAccessLevel.Clear), ("amount", ColumnAccessLevel.Clear)))));
        meterListener.RecordObservableInstruments();

        tags.ShouldAllBe(t => !t.Contains(Sentinel) && !t.Contains("group_key_type_family") && !t.Contains("GROUP_KEY_TYPE_NOT_GROUPABLE"));
    }

    public static TheoryData<string> MaskedOrUnavailableCases() => new() { "masked", "unavailable", "maskedMeasure", "deniedWhere" };

    [Theory]
    [MemberData(nameof(MaskedOrUnavailableCases))]
    public async Task I405_T_S01_RefusedKey_AccessErrorsWinOverTypeRefusal(string scenario)
    {
        var h = Create(Metadata("sqlserver", DataSourceType.Sql, ("id", "int"), ("k1", "xml"), ("k2", "varchar(10)"), ("amount", "decimal"), ("region", "varchar(10)")));
        var query = scenario switch
        {
            "masked" => Query(["k1", "k2"], Decision(("id", ColumnAccessLevel.Clear), ("k1", ColumnAccessLevel.Clear), ("k2", ColumnAccessLevel.Mask), ("amount", ColumnAccessLevel.Clear))),
            "unavailable" => Query(["k1", "k2"], Decision(("id", ColumnAccessLevel.Clear), ("k1", ColumnAccessLevel.Clear), ("k2", ColumnAccessLevel.Deny), ("amount", ColumnAccessLevel.Clear))),
            "maskedMeasure" => Query(["k1"], Decision(("id", ColumnAccessLevel.Clear), ("k1", ColumnAccessLevel.Clear), ("amount", ColumnAccessLevel.Mask)), aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount", "s")]),
            _ => Query(["k1"], Decision(("id", ColumnAccessLevel.Clear), ("k1", ColumnAccessLevel.Clear), ("amount", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Deny)),
                TableFilterClause.FromTrinoText("\"region\" = 'eu'", ["region"]))
        };

        var ex = await Should.ThrowAsync<Exception>(() => h.Service.ExecuteAggregationAsync(Principal(), query));

        ex.ShouldNotBeOfType<AggregationGroupKeyNotSupportedException>();
        if (scenario == "masked" || scenario == "maskedMeasure")
        {
            ex.ShouldBeOfType<MaskedAggregationException>();
        }
        else if (scenario == "unavailable")
        {
            ex.ShouldBeOfType<AggregationColumnUnavailableException>();
        }

        var entry = h.Entries.ShouldHaveSingleItem();
        Keys(entry).ShouldNotContain("group_key_type_family");
        Keys(entry).ShouldNotContain("refusal_reason");
    }

    [Fact]
    public async Task I405_T_S02_PlannerSecondLine_Throw_AuditedOnce()
    {
        var planner = Substitute.For<IGovernedTableReadPlanner>();
        planner.PlanAggregate(Arg.Any<TableAggregateIntent>(), Arg.Any<TableMetadata>(), Arg.Any<TableAccessDecision>())
            .Returns(_ => throw new AggregationGroupKeyNotSupportedException(GroupKeyTypeFamily.Spatial));
        var h = Create(Metadata("sqlserver", DataSourceType.Sql, ("id", "int"), ("grp", "varchar(10)"), ("amount", "decimal")), planner);

        await Should.ThrowAsync<AggregationGroupKeyNotSupportedException>(() => h.Service.ExecuteAggregationAsync(Principal(), Query(["grp"], AllClear())));

        var entry = h.Entries.ShouldHaveSingleItem();
        entry.Decision.ShouldBe("DENY");
        Prop(entry, "group_key_type_family").ShouldBe("spatial");
        Keys(entry).ShouldBe(RefusalKeyOrder);

        // the validation-path refusal also yields exactly one entry
        var h2 = Create(Metadata("sqlserver", DataSourceType.Sql, ("id", "int"), ("grp", "geography"), ("amount", "decimal")));
        await Should.ThrowAsync<AggregationGroupKeyNotSupportedException>(() => h2.Service.ExecuteAggregationAsync(Principal(), Query(["grp"], AllClear())));
        h2.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task I405_T_S07_DetailsKeyOrder_IsFixed_ForRefusal()
    {
        var h = Create(Metadata("sqlserver", DataSourceType.Sql, ("id", "int"), ("grp", "image"), ("amount", "decimal")));

        await Should.ThrowAsync<AggregationGroupKeyNotSupportedException>(() => h.Service.ExecuteAggregationAsync(Principal(), Query(["grp"], AllClear())));

        Keys(h.Entries.ShouldHaveSingleItem()).ShouldBe(RefusalKeyOrder);
    }

    [Fact]
    public async Task I405_T_S06_DenyEntry_WithFamily_HashChainVerifies_AndTamperingFamilyBreaksChain()
    {
        var h = Create(Metadata("sqlserver", DataSourceType.Sql, ("id", "int"), ("grp", "xml"), ("amount", "decimal")));
        await Should.ThrowAsync<AggregationGroupKeyNotSupportedException>(() => h.Service.ExecuteAggregationAsync(Principal(), Query(["grp"], AllClear())));
        var entry = h.Entries.ShouldHaveSingleItem();

        var key = new byte[32];
        var hash = AuditCanonicalizer.ComputeEntryHash(key, 7, "GENESIS", entry);
        AuditCanonicalizer.ComputeEntryHash(key, 7, "GENESIS", entry).ShouldBe(hash);
        AuditCanonicalizer.BuildCanonicalPayload(7, entry.Id.ToString(), "GENESIS", entry.OccurredAt, entry.EventType, entry.ActorSid.Value, entry.TargetTable, entry.TargetColumn, entry.Decision, entry.TraceId, entry.DetailsJson, entry.TenantId.Value)
            .ShouldContain("\"group_key_type_family\":\"xml\"");

        AuditCanonicalizer.ComputeEntryHash(
            key, 7, entry.Id.ToString(), "GENESIS", entry.OccurredAt, entry.EventType, entry.ActorSid.Value, entry.TargetTable, entry.TargetColumn, entry.Decision, entry.TraceId,
            entry.DetailsJson, entry.TenantId.Value).ShouldBe(hash);
        var tampered = AuditCanonicalizer.ComputeEntryHash(
            key, 7, entry.Id.ToString(), "GENESIS", entry.OccurredAt, entry.EventType, entry.ActorSid.Value, entry.TargetTable, entry.TargetColumn, entry.Decision, entry.TraceId,
            entry.DetailsJson!.Replace("\"group_key_type_family\":\"xml\"", "\"group_key_type_family\":\"json\"", StringComparison.Ordinal), entry.TenantId.Value);
        tampered.ShouldNotBe(hash);
    }

    [Fact]
    public async Task I405_T_10_NonSqlSource_InMemoryPath_NotRefused()
    {
        var h = Create(Metadata("http", DataSourceType.HttpDeclarative, ("id", "int"), ("grp", "xml"), ("amount", "decimal")));

        var result = await h.Service.ExecuteAggregationAsync(Principal(), Query(["grp"], AllClear()));

        result.Groups.ShouldNotBeEmpty();
        h.Entries.ShouldAllBe(e => e.Decision == "ALLOW" && !e.DetailsJson!.Contains("group_key_type_family"));
    }

    [Fact]
    public async Task I405_T_11_Sqlite_NeverRefused_NoFamily()
    {
        IReadOnlyDictionary<string, object?> row = new Dictionary<string, object?> { ["grp"] = "a", [GovernedTableReadPlanner.GroupCountColumnAlias] = 3L, ["n"] = 3L };
        var h = Create(Metadata("sqlite", DataSourceType.Sql, ("id", "int"), ("grp", "BLOB"), ("amount", "decimal")), new GovernedTableReadPlanner(), [row]);

        var result = await h.Service.ExecuteAggregationAsync(Principal(), Query(["grp"], AllClear()));

        result.Groups.ShouldNotBeEmpty();
        h.Entries.ShouldAllBe(e => e.Decision == "ALLOW" && !e.DetailsJson!.Contains("group_key_type_family"));
    }

    [Fact]
    public void I405_T_12_Planner_DefenseInDepth_ThrowsTypedException()
    {
        var meta = Metadata("sqlserver", DataSourceType.Sql, ("id", "int"), ("grp", "geography"));
        var intent = new TableAggregateIntent(TableId, ["grp"], [new AggregateSelection(AggregateFunction.Count)], null, null, 10);

        var ex = Should.Throw<AggregationGroupKeyNotSupportedException>(() => new GovernedTableReadPlanner().PlanAggregate(intent, meta, Decision(("id", ColumnAccessLevel.Clear), ("grp", ColumnAccessLevel.Clear))));

        ex.Family.ShouldBe(GroupKeyTypeFamily.Spatial);
    }
}
