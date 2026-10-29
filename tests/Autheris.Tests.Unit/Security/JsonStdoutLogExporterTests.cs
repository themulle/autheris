namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Autheris.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-OT06-15: the JSON stdout log exporter must never throw out of Export (it runs on the OpenTelemetry
/// exporter thread, where an unhandled exception terminates the process) and must serialise only safe attribute
/// value types. Everything else becomes a fixed type marker without calling ToString() on the value.
/// </summary>
public sealed class JsonStdoutLogExporterTests
{
    private sealed class Secret
    {
        public string Token => "super-secret-token";

        public override string ToString() => "super-secret-token";
    }

    private sealed class ThrowingFormatter
    {
        public override string ToString() => throw new InvalidOperationException("must not be called");
    }

    private enum Color
    {
        Red,
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (ILoggerFactory Factory, StringWriter Out, StringWriter Diag) Create(TimeProvider? time = null)
    {
        var output = new StringWriter();
        var diag = new StringWriter();
        var exporter = new JsonStdoutLogExporter(output, diag, time);
        var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.ParseStateValues = true;
            o.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
        }));
        return (factory, output, diag);
    }

    private static KeyValuePair<string, object?> KV(string k, object? v) => new(k, v);

    private static void Log(ILogger logger, params KeyValuePair<string, object?>[] state) =>
        logger.Log(LogLevel.Information, new EventId(1), (IReadOnlyList<KeyValuePair<string, object?>>)state, null, (s, _) => "msg");

    private static (Func<long> Unsupported, Func<long> Failures, IDisposable Listener) Listen()
    {
        long unsupported = 0, failures = 0;
        var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name.StartsWith("autheris.log_exporter.", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, _, _) =>
        {
            if (i.Name.EndsWith("unsupported_attributes", StringComparison.Ordinal))
            {
                Interlocked.Add(ref unsupported, v);
            }
            else if (i.Name.EndsWith("export_failures", StringComparison.Ordinal))
            {
                Interlocked.Add(ref failures, v);
            }
        });
        listener.Start();
        return (() => Interlocked.Read(ref unsupported), () => Interlocked.Read(ref failures), listener);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_OT06_15_MethodInfoTypeDelegateAndObjectAttributes_AreMarkersNotExceptions()
    {
        var (unsupported, _, listener) = Listen();
        using var _l = listener;
        var (factory, output, _) = Create();
        using var _f = factory;

        MethodInfo method = typeof(JsonStdoutLogExporterTests).GetMethod(nameof(SEC_OT06_15_MethodInfoTypeDelegateAndObjectAttributes_AreMarkersNotExceptions))!;
        Action del = () => { };

        Should.NotThrow(() => Log(
            factory.CreateLogger("t"),
            KV("method", method),
            KV("type", typeof(string)),
            KV("del", del),
            KV("obj", new Secret()),
            KV("bad", new ThrowingFormatter()),
            KV("ok", "value")));
        factory.Dispose();

        var text = output.ToString();
        text.ShouldNotContain("super-secret-token");
        var attrs = JsonDocument.Parse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).RootElement.GetProperty("attributes");
        attrs.GetProperty("method").GetString().ShouldStartWith("<unsupported:");
        attrs.GetProperty("type").GetString().ShouldStartWith("<unsupported:");
        attrs.GetProperty("del").GetString().ShouldStartWith("<unsupported:");
        attrs.GetProperty("obj").GetString().ShouldBe("<unsupported:Secret>");
        attrs.GetProperty("ok").GetString().ShouldBe("value");
        unsupported().ShouldBe(5);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_OT06_15_SafeAttributeTypes_AreSerialisedAsValues()
    {
        var (factory, output, _) = Create();
        var guid = Guid.NewGuid();
        Log(
            factory.CreateLogger("t"),
            KV("s", "a"), KV("b", true), KV("i", 5), KV("l", 6L), KV("d", 1.5), KV("m", 2.5m),
            KV("dt", new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)), KV("ts", TimeSpan.FromSeconds(3)),
            KV("g", guid), KV("e", Color.Red), KV("arr", new[] { 1, 2 }), KV("sarr", new[] { "x", "y" }));
        factory.Dispose();

        var attrs = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).RootElement.GetProperty("attributes");
        attrs.GetProperty("i").GetInt32().ShouldBe(5);
        attrs.GetProperty("b").GetBoolean().ShouldBeTrue();
        attrs.GetProperty("d").GetDouble().ShouldBe(1.5);
        attrs.GetProperty("m").GetDecimal().ShouldBe(2.5m);
        attrs.GetProperty("g").GetString().ShouldBe(guid.ToString());
        attrs.GetProperty("e").GetString().ShouldBe("Red");
        attrs.GetProperty("arr").GetArrayLength().ShouldBe(2);
        attrs.GetProperty("sarr")[1].GetString().ShouldBe("y");
        attrs.GetProperty("ts").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_OT06_15_ArrayWithUnsupportedElement_ElementIsMarker()
    {
        var (factory, output, _) = Create();
        Log(factory.CreateLogger("t"), KV("arr", new object[] { 1, new Secret() }));
        factory.Dispose();

        var attrs = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).RootElement.GetProperty("attributes");
        attrs.GetProperty("arr")[1].GetString().ShouldBe("<unsupported:Secret>");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_OT06_15_WriterThrows_ExportDoesNotThrow_CountsFailureAndLogsOneDiagnosticPerWindow()
    {
        var (_, failures, listener) = Listen();
        using var _l = listener;
        var time = new FakeTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var diag = new StringWriter();
        var exporter = new JsonStdoutLogExporter(new ThrowingWriter(), diag, time);
        var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
        }));
        var logger = factory.CreateLogger("t");

        Should.NotThrow(() =>
        {
            logger.LogInformation("one");
            logger.LogInformation("two");
        });
        failures().ShouldBe(2);
        diag.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1);
        diag.ToString().ShouldNotContain("disk full");

        time.Now = time.Now.AddMinutes(5);
        logger.LogInformation("three");
        diag.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2);
        factory.Dispose();
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("disk full");
    }
}
