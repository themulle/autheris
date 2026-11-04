namespace Autheris.Tests.Unit.DataSources;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.DataSources;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #447 (SEC-ATR-09): one audit event and one metric per denial; the audit outcome never changes the denial.</summary>
[Trait("Category", "Security")]
public sealed class TransportDenialRecorderTests
{
    private static HierarchicalDataSourceOptionsResolver Resolver() =>
        new(new DataSourceTransportGateTests.FixedMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sales"] = new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.GraphQL }
            }
        }));

    private static AuditContext Context() => new()
    {
        TraceId = "trace-1",
        ActorSid = new Sid("S-1-5-21-1"),
        TenantId = new TenantId("tenant-a")
    };

    [Fact]
    public async Task Denial_WritesExactlyOneAuditEventWithRequiredFields()
    {
        using var resolver = Resolver();
        var writer = Substitute.For<IAuditWriter>();
        var recorder = new TransportDenialRecorder(writer, Context(), resolver);

        await recorder.RecordDenialAsync(new TransportDenial("sa\nles", "Sales", DataSourceTransportFlags.WebSql, TransportDecisionReason.FlagNotSet));

        var call = writer.ReceivedCalls().ShouldHaveSingleItem();
        var entry = (AuditLogEntry)call.GetArguments()[0]!;
        entry.EventType.ShouldBe(AuditEventTypes.TransportNotAllowed);
        entry.Decision.ShouldBe("DENY");
        entry.ActorSid.ToString().ShouldContain("S-1-5-21-1");
        entry.TenantId.Value.ShouldBe("tenant-a");
        entry.TraceId.ShouldBe("trace-1");
        entry.TargetTable.ShouldBe("sales");
        using var details = JsonDocument.Parse(entry.DetailsJson);
        details.RootElement.GetProperty("transport").GetString().ShouldBe("WebSql");
        details.RootElement.GetProperty("reason").GetString().ShouldBe("TransportNotAllowed");
        details.RootElement.GetProperty("subReason").GetString().ShouldBe("FlagNotSet");
        details.RootElement.GetProperty("outcome").GetString().ShouldBe("DENY");
    }

    [Fact]
    public async Task Gate_AuditWriterThrows_StillDenies()
    {
        using var resolver = Resolver();
        var writer = Substitute.For<IAuditWriter>();
        writer.RecordAsync(Arg.Any<AuditLogEntry>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("down"));
        var recorder = new TransportDenialRecorder(writer, Context(), resolver);

        // the recorder never throws, so the gate still throws TransportNotAllowedException
        await Should.NotThrowAsync(() => recorder.RecordDenialAsync(
            new TransportDenial("Sales", "Sales", DataSourceTransportFlags.WebSql, TransportDecisionReason.FlagNotSet)));
    }

    [Fact]
    public async Task AuditFailure_StillCountsDenialMetric()
    {
        using var resolver = Resolver();
        var writer = Substitute.For<IAuditWriter>();
        writer.RecordAsync(Arg.Any<AuditLogEntry>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("down"));
        var recorder = new TransportDenialRecorder(writer, Context(), resolver);

        var measurements = Capture(() => recorder.RecordDenialAsync(
            new TransportDenial("Sales", "Sales", DataSourceTransportFlags.Rest, TransportDecisionReason.FlagNotSet)));

        (await measurements).ShouldHaveSingleItem().ShouldBe(("Rest", "Sales"));
    }

    [Fact]
    public async Task Metric_UnknownSourceTaggedUnknown()
    {
        using var resolver = Resolver();
        var recorder = new TransportDenialRecorder(Substitute.For<IAuditWriter>(), Context(), resolver);

        var measurements = Capture(() => recorder.RecordDenialAsync(
            new TransportDenial("attacker-name", "attacker-name", DataSourceTransportFlags.Mcp, TransportDecisionReason.UnknownSource)));

        (await measurements).ShouldHaveSingleItem().ShouldBe(("Mcp", "unknown"));
    }

    private static async Task<List<(string Transport, string DataSource)>> Capture(Func<Task> act)
    {
        var seen = new List<(string, string)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "autheris_transport_denied_total") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? transport = null, source = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "transport") transport = tag.Value as string;
                if (tag.Key == "datasource") source = tag.Value as string;
            }

            lock (seen) seen.Add((transport ?? "", source ?? ""));
        });
        listener.Start();
        await act();
        listener.Dispose();
        return seen;
    }
}
