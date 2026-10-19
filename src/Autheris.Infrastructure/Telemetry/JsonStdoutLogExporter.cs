namespace Autheris.Infrastructure.Telemetry;

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Logs;

/// <summary>
/// Safe JSON stdout exporter writing OpenTelemetry log records as structured, escaped NDJSON lines.
/// Enforces SEC-OT06-15.
/// </summary>
public sealed class JsonStdoutLogExporter : BaseExporter<LogRecord>
{
    private readonly TextWriter _writer;

    public JsonStdoutLogExporter(TextWriter? writer = null)
    {
        _writer = writer ?? Console.Out;
    }

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        using var stream = new MemoryStream();

        foreach (var record in batch)
        {
            stream.SetLength(0);
            using (var jsonWriter = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
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
                        if (attr.Value == null)
                        {
                            jsonWriter.WriteNullValue();
                        }
                        else
                        {
                            JsonSerializer.Serialize(jsonWriter, attr.Value);
                        }
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

            var line = Encoding.UTF8.GetString(stream.ToArray());
            _writer.WriteLine(line);
        }

        return ExportResult.Success;
    }
}
