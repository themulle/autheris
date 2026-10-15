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
        => Parse(jsonString, defaultTenantId, null);

    /// <summary>
    /// SEC SR-L-20: parses a Debezium event and reads the row tenant from <paramref name="tenantColumn"/> (the registered tenant
    /// column of the table). Falls back to <c>tenant_id</c> and then to any column whose name equals <c>tenantid</c> after
    /// removing <c>_</c>/<c>-</c> (same rule as <c>TableMetadata.TenantColumnName</c>). A conflicting tenant between the
    /// before and after image is rejected with an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static CdcEvent Parse(string jsonString, string? defaultTenantId, string? tenantColumn)
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

        // Deterministic id (hash of the trimmed body): the same body ingested twice dedupes downstream, and no row value leaks into the id.
        var eventId = "dbz-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(jsonString.Trim())))[..32];
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsMs);

        var afterTenant = ExtractTenant(after, tenantColumn);
        var beforeTenant = ExtractTenant(before, tenantColumn);
        if (afterTenant != null && beforeTenant != null &&
            !string.Equals(afterTenant, beforeTenant, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CDC event carries different tenant values in the before and after image.");
        }

        var tenantId = afterTenant ?? beforeTenant ?? defaultTenantId;

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

    private static string? ExtractTenant(IReadOnlyDictionary<string, object?>? row, string? tenantColumn)
    {
        if (row == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(tenantColumn) && TryGetNonEmpty(row, tenantColumn, out var configured))
        {
            return configured;
        }

        if (TryGetNonEmpty(row, "tenant_id", out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in row)
        {
            if (string.Equals(key.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal), "tenantid", StringComparison.OrdinalIgnoreCase)
                && value != null && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static bool TryGetNonEmpty(IReadOnlyDictionary<string, object?> row, string column, out string? value)
    {
        value = null;
        if (row.TryGetValue(column, out var raw) && raw != null && !string.IsNullOrWhiteSpace(raw.ToString()))
        {
            value = raw.ToString();
            return true;
        }

        return false;
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
