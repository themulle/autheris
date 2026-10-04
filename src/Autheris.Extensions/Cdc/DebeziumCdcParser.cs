namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;

public static class DebeziumCdcParser
{
    public static CdcEvent Parse(string jsonString, string? defaultTenantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonString);

        using var doc = JsonDocument.Parse(jsonString);
        var root = doc.RootElement;

        // Support Kafka Connect envelope: { "schema": {...}, "payload": {...} }
        if (root.TryGetProperty("payload", out var payloadElem) && payloadElem.ValueKind == JsonValueKind.Object)
        {
            root = payloadElem;
        }

        // Operation: 'c' (create), 'u' (update), 'd' (delete), 'r' (read/snapshot)
        var opStr = root.TryGetProperty("op", out var opElem) ? opElem.GetString() : "u";
        var op = opStr switch
        {
            "c" => CdcOperation.Insert,
            "u" => CdcOperation.Update,
            "d" => CdcOperation.Delete,
            "r" => CdcOperation.Snapshot,
            _ => CdcOperation.Update
        };

        // Source: schema, table, db
        var schemaName = "public";
        var tableName = "unknown";
        var serverName = "default";
        var tsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (root.TryGetProperty("source", out var sourceElem) && sourceElem.ValueKind == JsonValueKind.Object)
        {
            if (sourceElem.TryGetProperty("schema", out var sProp)) schemaName = sProp.GetString() ?? schemaName;
            if (sourceElem.TryGetProperty("table", out var tProp)) tableName = tProp.GetString() ?? tableName;
            if (sourceElem.TryGetProperty("name", out var nProp)) serverName = nProp.GetString() ?? serverName;
            if (sourceElem.TryGetProperty("ts_ms", out var tsProp) && tsProp.TryGetInt64(out var ts)) tsMs = ts;
        }

        var tableIdentifier = new TableIdentifier(serverName, schemaName, tableName);

        // Before & After Payloads
        var before = ExtractRow(root, "before");
        var after = ExtractRow(root, "after");

        var eventId = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsMs);

        // Check for tenant_id in after or before
        var tenantId = defaultTenantId;
        if (after?.TryGetValue("tenant_id", out var tVal) == true && tVal != null)
        {
            tenantId = tVal.ToString();
        }
        else if (before?.TryGetValue("tenant_id", out var bVal) == true && bVal != null)
        {
            tenantId = bVal.ToString();
        }

        return new CdcEvent(
            EventId: eventId,
            Table: tableIdentifier,
            Operation: op,
            TenantId: tenantId,
            Before: before,
            After: after,
            Timestamp: timestamp
        );
    }

    private static IReadOnlyDictionary<string, object?>? ExtractRow(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var elem) || elem.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in elem.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonElement(prop.Value);
        }

        return dict;
    }

    private static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };
}
