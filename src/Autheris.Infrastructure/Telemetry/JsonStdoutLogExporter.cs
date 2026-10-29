namespace Autheris.Infrastructure.Telemetry;

using System;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using OpenTelemetry;
using OpenTelemetry.Logs;

/// <summary>
/// Safe JSON stdout exporter writing OpenTelemetry log records as structured, escaped NDJSON lines.
/// Enforces SEC-OT06-15: only safe attribute value types are serialised (everything else becomes a fixed
/// <c>&lt;unsupported:TypeName&gt;</c> marker without calling <c>ToString()</c> on the value), and
/// <see cref="Export"/> never throws because it runs on the OpenTelemetry exporter thread where an unhandled
/// exception terminates the process.
/// </summary>
public sealed class JsonStdoutLogExporter : BaseExporter<LogRecord>
{
    private const int MaxArrayElements = 256;
    private const long DiagnosticIntervalTicks = TimeSpan.TicksPerMinute;

    private static readonly Meter ExporterMeter = new("Autheris.Core");
    private static readonly Counter<long> UnsupportedAttributes =
        ExporterMeter.CreateCounter<long>("autheris.log_exporter.unsupported_attributes");
    private static readonly Counter<long> ExportFailures =
        ExporterMeter.CreateCounter<long>("autheris.log_exporter.export_failures");

    private readonly TextWriter _writer;
    private readonly TextWriter _diagnostics;
    private readonly TimeProvider _time;
    private long _lastDiagnosticTicks = long.MinValue;

    public JsonStdoutLogExporter(TextWriter? writer = null, TextWriter? diagnostics = null, TimeProvider? timeProvider = null)
    {
        _writer = writer ?? Console.Out;
        _diagnostics = diagnostics ?? Console.Error;
        _time = timeProvider ?? TimeProvider.System;
    }

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        var result = ExportResult.Success;
        try
        {
            using var stream = new MemoryStream();
            foreach (var record in batch)
            {
                try
                {
                    stream.SetLength(0);
                    WriteRecord(stream, record);
                    _writer.WriteLine(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
                }
                catch (Exception ex)
                {
                    result = ExportResult.Failure;
                    ReportFailure(ex);
                }
            }
        }
        catch (Exception ex)
        {
            result = ExportResult.Failure;
            ReportFailure(ex);
        }

        return result;
    }

    private static void WriteRecord(MemoryStream stream, LogRecord record)
    {
        using var jsonWriter = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        jsonWriter.WriteStartObject();
        jsonWriter.WriteString("timestamp", record.Timestamp.ToString("O"));
        jsonWriter.WriteString("severity_text", record.LogLevel.ToString());
        jsonWriter.WriteNumber("severity_number", (int)record.LogLevel);
        jsonWriter.WriteString("body", record.Body ?? string.Empty);

        if (record.TraceId != default)
        {
            jsonWriter.WriteString("trace_id", record.TraceId.ToHexString());
        }

        if (record.SpanId != default)
        {
            jsonWriter.WriteString("span_id", record.SpanId.ToHexString());
        }

        if (record.Attributes != null && record.Attributes.Count > 0)
        {
            jsonWriter.WriteStartObject("attributes");
            foreach (var attr in record.Attributes)
            {
                if (string.IsNullOrWhiteSpace(attr.Key) || attr.Key.Length > 128)
                {
                    continue;
                }

                jsonWriter.WritePropertyName(attr.Key);
                WriteValue(jsonWriter, attr.Value, allowArray: true);
            }

            jsonWriter.WriteEndObject();
        }

        if (record.Exception != null)
        {
            jsonWriter.WriteStartObject("exception");
            var exType = record.Exception is RedactedException re
                ? re.OriginalTypeName
                : record.Exception.GetType().FullName ?? record.Exception.GetType().Name;
            jsonWriter.WriteString("type", exType);
            jsonWriter.WriteString("message", record.Exception.Message);
            if (!string.IsNullOrEmpty(record.Exception.StackTrace))
            {
                jsonWriter.WriteString("stack_trace", record.Exception.StackTrace);
            }

            jsonWriter.WriteEndObject();
        }

        jsonWriter.WriteEndObject();
        jsonWriter.Flush();
    }

    private static void WriteValue(Utf8JsonWriter w, object? value, bool allowArray)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case char c:
                w.WriteStringValue(c.ToString());
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case byte or sbyte or short or ushort or int or uint:
                w.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case ulong ul:
                w.WriteNumberValue(ul);
                break;
            case float f:
                WriteDouble(w, f);
                break;
            case double d:
                WriteDouble(w, d);
                break;
            case decimal m:
                w.WriteNumberValue(m);
                break;
            case DateTime dt:
                w.WriteStringValue(dt);
                break;
            case DateTimeOffset dto:
                w.WriteStringValue(dto);
                break;
            case TimeSpan ts:
                w.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture));
                break;
            case Guid g:
                w.WriteStringValue(g);
                break;
            case Enum e:
                w.WriteStringValue(e.ToString());
                break;
            case Array arr when allowArray:
                w.WriteStartArray();
                var written = 0;
                foreach (var item in arr)
                {
                    if (written++ >= MaxArrayElements)
                    {
                        break;
                    }

                    WriteValue(w, item, allowArray: false);
                }

                w.WriteEndArray();
                break;
            default:
                UnsupportedAttributes.Add(1);
                w.WriteStringValue("<unsupported:" + value.GetType().Name + ">");
                break;
        }
    }

    private static void WriteDouble(Utf8JsonWriter w, double d)
    {
        if (double.IsFinite(d))
        {
            w.WriteNumberValue(d);
        }
        else
        {
            w.WriteStringValue(d.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void ReportFailure(Exception ex)
    {
        try
        {
            ExportFailures.Add(1);
            var now = _time.GetUtcNow().UtcTicks;
            var last = Interlocked.Read(ref _lastDiagnosticTicks);
            if ((last == long.MinValue || now - last >= DiagnosticIntervalTicks)
                && Interlocked.CompareExchange(ref _lastDiagnosticTicks, now, last) == last)
            {
                // Direct write, never through ILogger (no recursion). Exception type only, no message (no value leak).
                _diagnostics.WriteLine("[Autheris] WARNING: JsonStdoutLogExporter dropped a log record (" + ex.GetType().Name + ").");
            }
        }
        catch
        {
            // Self-diagnostics must never throw out of the exporter thread.
        }
    }
}
