namespace Autheris.Domain.Grants;

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Autheris.Domain.Model;

/// <summary>
/// Produces deterministic compact UTF-8 JSON with sorted keys and UTC ISO-8601 timestamps for grant record hashing and signing.
/// </summary>
public static class GrantRecordCanonicalizer
{
    private static readonly JsonWriterOptions CompactWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] CanonicalizeUtf8(GrantRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, CompactWriterOptions);

        writer.WriteStartObject();
        WriteOptionalGuid(writer, "consent_id", record.ConsentId);
        writer.WriteString("decided_at", FormatUtcIso8601(record.DecidedAt));
        writer.WriteString("decision_json", NormalizeEmbeddedJson(record.DecisionJson));
        writer.WriteString("grant_source", record.GrantSource.ToSqlString());
        writer.WriteString("grantee_ref", record.GranteeRef);
        writer.WriteString("grantee_type", record.GranteeType);
        writer.WriteString("id", record.Id.ToString("D"));
        writer.WriteString("object_id", record.ObjectId);
        writer.WriteString("prev_hash", record.PrevHash);
        WriteOptionalGuid(writer, "profile_assignment_id", record.ProfileAssignmentId);
        writer.WriteString("record_type", ToSqlString(record.RecordType));
        writer.WriteString("recorded_at", FormatUtcIso8601(record.RecordedAt));
        WriteOptionalGuid(writer, "request_id", record.RequestId);
        writer.WriteString("requested_by", record.RequestedBy);
        writer.WriteString("scope_json", NormalizeEmbeddedJson(record.ScopeJson));
        writer.WriteNumber("seq", record.Seq);
        writer.WriteString("tenant_id", record.TenantId);
        WriteOptionalTimestamp(writer, "valid_from", record.ValidFrom);
        WriteOptionalTimestamp(writer, "valid_until", record.ValidUntil);
        writer.WriteEndObject();
        writer.Flush();

        return buffer.ToArray();
    }

    public static string Canonicalize(GrantRecord record) =>
        Encoding.UTF8.GetString(CanonicalizeUtf8(record));

    public static string FormatUtcIso8601(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    public static string CanonicalizeJson(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);

        using var doc = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, CompactWriterOptions);
        WriteCanonicalElement(writer, doc.RootElement);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string ToSqlString(GrantRecordType recordType) => recordType switch
    {
        GrantRecordType.Issued => "ISSUED",
        GrantRecordType.Renewed => "RENEWED",
        GrantRecordType.Revoked => "REVOKED",
        GrantRecordType.Expired => "EXPIRED",
        GrantRecordType.Rejected => "REJECTED",
        GrantRecordType.RequestExpired => "REQUEST_EXPIRED",
        _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, "Unsupported grant record type.")
    };

    public static GrantRecordType ParseSqlString(string value) => value switch
    {
        "ISSUED" => GrantRecordType.Issued,
        "RENEWED" => GrantRecordType.Renewed,
        "REVOKED" => GrantRecordType.Revoked,
        "EXPIRED" => GrantRecordType.Expired,
        "REJECTED" => GrantRecordType.Rejected,
        "REQUEST_EXPIRED" => GrantRecordType.RequestExpired,
        _ => throw new InvalidOperationException($"Unsupported grant record type '{value}'.")
    };

    private static string NormalizeEmbeddedJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "{}";

        try
        {
            return CanonicalizeJson(value);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = new List<JsonProperty>();
                foreach (var prop in element.EnumerateObject())
                    properties.Add(prop);
                properties.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
                foreach (var prop in properties)
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonicalElement(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalElement(writer, item);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteOptionalGuid(Utf8JsonWriter writer, string propertyName, Guid? value)
    {
        if (value.HasValue)
            writer.WriteString(propertyName, value.Value.ToString("D"));
        else
            writer.WriteNull(propertyName);
    }

    private static void WriteOptionalTimestamp(Utf8JsonWriter writer, string propertyName, DateTimeOffset? value)
    {
        if (value.HasValue)
            writer.WriteString(propertyName, FormatUtcIso8601(value.Value));
        else
            writer.WriteNull(propertyName);
    }
}
