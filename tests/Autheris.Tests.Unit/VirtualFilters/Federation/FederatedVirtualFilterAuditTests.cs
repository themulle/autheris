namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Audit;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-10 (FR-9, M9, ST-33, ST-34, ST-35): the audit trail of a Virtual Filter query is complete (start record
/// with tier and strategy, one record per key set read, upstream read with received/kept rows), correlated by one trace
/// id, free of key values and inner messages, fail-closed on audit write errors and classifies every denial code.
/// </summary>
public sealed class FederatedVirtualFilterAuditTests
{
    private const string KeyA = "KEYSECRET-4711";
    private const string KeyB = "KEYSECRET-4712";
    private const string Queried = "QUERYLIT-99";
    private static readonly TableMetadata Erp = SqlTable("erp", "public", "orders", "id", "customer_id");

    private const string Scan = "SELECT c.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id";

    private static TableMetadata Crm(string param = "ids") => WithEndpoint(
        HttpTable("crm_api", "public", "customers", "id", "name"),
        new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.example.internal",
            PathTemplate = "/customers",
            BatchType = HttpBatchType.QueryParameterList,
            BatchParamName = param,
            PrimaryKeyField = "id",
            CompleteResponse = true
        });

    private static (StubTableConnector Crm, Pipeline Pipeline, List<(LogLevel Level, string Message)> Logs) Setup(
        Action<FakeKeySetProvider>? keys = null,
        VirtualFilterExecutionStrategy strategy = VirtualFilterExecutionStrategy.Adaptive,
        bool pushdownCapable = true,
        Autheris.Domain.Options.VirtualFilterFederationOptions? options = null)
    {
        var crmTable = pushdownCapable ? Crm() : HttpTable("crm_api", "public", "customers", "id", "name");
        var crm = new StubTableConnector("crm_api", [Row(("id", KeyA), ("name", "A")), Row(("id", KeyB), ("name", "B")), Row(("id", "OTHER-1"), ("name", "X"))]);
        var erp = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", KeyA)), Row(("id", "11"), ("customer_id", KeyB))]);
        var provider = new FakeKeySetProvider().With("vf_customers", [KeyA], [KeyB]);
        keys?.Invoke(provider);
        var snapshot = AliceSnapshot("crm_api.*.*", "crm_api.public.customers");
        var profile = snapshot.Profiles[0];
        snapshot = snapshot with { Profiles = [profile with { Bindings = [profile.Bindings[0] with { Strategy = strategy }] }] };
        var logs = new List<(LogLevel Level, string Message)>();
        var pipeline = Build(
            [(crmTable, crm), (Erp, erp)],
            Consents(Allow(crmTable.Identifier), Allow(Erp.Identifier)),
            snapshot,
            provider,
            federation: options,
            logs: logs);
        return (crm, pipeline, logs);
    }

    private static List<AuditLogEntry> Entries(Pipeline pipeline) =>
        pipeline.Audit.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(Autheris.Application.Interfaces.IAuditLogRepository.RecordAuditEventAsync))
            .Select(c => (AuditLogEntry)c.GetArguments()[0]!)
            .ToList();

    private static JsonElement Details(AuditLogEntry entry) => JsonDocument.Parse(entry.DetailsJson!).RootElement;

    [Fact]
    public async Task TierOneMiss_WritesStartRecordWithTierAndShortCircuit_AndUpstreamReadWithUpstreamCalledFalse()
    {
        var (crm, pipeline, _) = Setup();

        (await pipeline.RunAsync(Scan + " WHERE c.id = '" + Queried + "'")).ShouldBeEmpty();

        crm.UpstreamCalls.ShouldBe(0);
        var entries = Entries(pipeline);
        var start = Details(entries.Single(e => e.EventType == AuditEventTypes.WebSqlCrossSourceQuery));
        var vf = start.GetProperty("virtualFilters").EnumerateArray().ShouldHaveSingleItem();
        vf.GetProperty("table").GetString().ShouldBe("crm_api.public.customers");
        vf.GetProperty("tier").GetString().ShouldBe("PointLookup");
        vf.GetProperty("tierReason").GetString().ShouldBe("point_lookup_miss");
        vf.GetProperty("shortCircuited").GetBoolean().ShouldBeTrue();
        var filter = vf.GetProperty("filters").EnumerateArray().ShouldHaveSingleItem();
        filter.GetProperty("name").GetString().ShouldBe("vf_customers");
        filter.GetProperty("strategy").GetString().ShouldBe("Adaptive");
        filter.GetProperty("bindingHash").GetString().ShouldNotBeNullOrWhiteSpace();

        var upstream = entries.Where(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.TargetTable == "public.customers"
                                          && Details(e).TryGetProperty("upstreamCalled", out _)).ShouldHaveSingleItem();
        var read = Details(upstream);
        read.GetProperty("upstreamCalled").GetBoolean().ShouldBeFalse();
        read.GetProperty("vfTier").GetString().ShouldBe("PointLookup");
        read.GetProperty("rowsReceived").GetInt32().ShouldBe(0);
        read.GetProperty("rowsKept").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task KeySourceRead_AuditedWithCountAndCacheHit_WithoutKeyValues()
    {
        var (_, pipeline, _) = Setup();

        await pipeline.RunAsync(Scan);

        var keyRead = Entries(pipeline)
            .Where(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead)
            .Select(Details)
            .Single(d => d.TryGetProperty("kind", out var k) && k.GetString() == "virtualFilterKeySource");
        keyRead.GetProperty("filter").GetString().ShouldBe("vf_customers");
        keyRead.GetProperty("table").GetString().ShouldBe("crm_api.public.customers");
        keyRead.GetProperty("keyCount").GetInt32().ShouldBe(2);
        keyRead.GetProperty("droppedKeys").GetInt32().ShouldBe(0);
        keyRead.GetProperty("cacheHit").GetBoolean().ShouldBeFalse();
        keyRead.TryGetProperty("source", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Tier3_UpstreamRead_HasRowsReceivedAndKept()
    {
        var (_, pipeline, _) = Setup(pushdownCapable: false);

        await pipeline.RunAsync(Scan);

        var read = Entries(pipeline)
            .Where(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.TargetTable == "public.customers")
            .Select(Details).Single(d => d.TryGetProperty("upstreamCalled", out _));
        read.GetProperty("vfTier").GetString().ShouldBe("FullFetch");
        read.GetProperty("upstreamCalled").GetBoolean().ShouldBeTrue();
        read.GetProperty("rowsReceived").GetInt32().ShouldBe(3);
        read.GetProperty("rowsKept").GetInt32().ShouldBe(2);
        read.GetProperty("pushdownKeyCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Tier2_UpstreamRead_RecordsThePushdownKeyCount()
    {
        var (_, pipeline, _) = Setup();

        await pipeline.RunAsync(Scan);

        var read = Entries(pipeline)
            .Where(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.TargetTable == "public.customers")
            .Select(Details).Single(d => d.TryGetProperty("upstreamCalled", out _));
        read.GetProperty("vfTier").GetString().ShouldBe("Pushdown");
        read.GetProperty("pushdownKeyCount").GetInt32().ShouldBe(2);
    }

    [Theory]
    [InlineData("tier0")]
    [InlineData("tier1miss")]
    [InlineData("tier2")]
    [InlineData("tier3")]
    public async Task NoKeyValueOrQueriedLiteral_InAnyAuditRecordOrLog(string scenario)
    {
        // ST-33
        var (_, pipeline, logs) = scenario switch
        {
            "tier0" => Setup(k => k.With("vf_customers")),
            "tier3" => Setup(pushdownCapable: false),
            _ => Setup()
        };
        var sql = scenario == "tier1miss" ? Scan + " WHERE c.id = '" + Queried + "'" : Scan;

        await pipeline.RunAsync(sql);

        var text = string.Join("\n", Entries(pipeline).Select(e => e.DetailsJson)) + "\n" + string.Join("\n", logs.Select(l => l.Message));
        text.ShouldNotContain(KeyA);
        text.ShouldNotContain(KeyB);
        text.ShouldNotContain(Queried);
    }

    [Fact]
    public async Task AllAuditRecordsOfOneQuery_ShareOneTraceId()
    {
        var (_, pipeline, _) = Setup();

        await pipeline.RunAsync(Scan);

        var traceIds = Entries(pipeline).Select(e => e.TraceId).Distinct().ToList();
        traceIds.Count.ShouldBe(1);
        traceIds[0].ShouldNotBeNullOrWhiteSpace();
    }

    public static IEnumerable<object[]> AllErrorCodes() =>
        typeof(VirtualFilterErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => new object[] { (string)f.GetRawConstantValue()! });

    [Theory]
    [MemberData(nameof(AllErrorCodes))]
    public async Task GatewaySecurityException_AuditedAsQueryDeniedWithErrorCode(string code)
    {
        // ST-35: every Virtual Filter code raised through the pipeline is a denial with its code; the message is not stored.
        var (crm, pipeline, _) = Setup(k => k.Failure = new GatewaySecurityException("secret-inner-detail " + KeyA, code));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(Scan));

        ex.ErrorCode.ShouldBe(code);
        crm.UpstreamCalls.ShouldBe(0);
        var denied = Entries(pipeline).Single(e => e.EventType == AuditEventTypes.WebSqlQueryDenied);
        denied.Decision.ShouldBe("DENY");
        Details(denied).GetProperty("reasonCode").GetString().ShouldBe(code);
        denied.DetailsJson.ShouldNotContain("secret-inner-detail");
        denied.DetailsJson.ShouldNotContain(KeyA);
    }

    [Fact]
    public async Task AuditWriteFailure_OfTheStartRecord_FailsTheQuery_BeforeAnyUpstreamCall()
    {
        var (crm, pipeline, _) = Setup();
        pipeline.Audit.RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.WebSqlCrossSourceQuery), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("audit store down"));

        await Should.ThrowAsync<InvalidOperationException>(() => pipeline.RunAsync(Scan));

        crm.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task AuditWriteFailure_OfTheKeySourceRecord_FailsTheQuery_BeforeAnyUpstreamCall()
    {
        var (crm, pipeline, _) = Setup();
        pipeline.Audit.RecordAuditEventAsync(
                Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.DetailsJson!.Contains("virtualFilterKeySource")),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("audit store down"));

        await Should.ThrowAsync<InvalidOperationException>(() => pipeline.RunAsync(Scan));

        crm.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task FailingDenialAudit_KeepsTheOriginalException_AndLogsExactlyOneError()
    {
        // ST-34
        var (_, pipeline, logs) = Setup(k => k.Failure = new GatewaySecurityException("denied", VirtualFilterErrorCodes.KeySetTooLarge));
        pipeline.Audit.RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.WebSqlQueryDenied), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("audit store down"));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(Scan));

        ex.ErrorCode.ShouldBe("VF_KEY_SET_TOO_LARGE");
        logs.Count(l => l.Level == LogLevel.Error).ShouldBe(1);
    }
}
