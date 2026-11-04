namespace Autheris.Application.Data.Services;

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Model;

/// <summary>
/// The single source of the bulk-stream keyset cursor binding. The streaming endpoint (issuing <c>nextCursor</c>) and
/// <see cref="GovernedTableReadPlanner.PlanKeysetStream"/> (verifying the supplied cursor) both build the context here, so
/// an issued cursor verifies on the continuation request and only there: same tenant, same caller, same table
/// (domain, schema, name), same key columns and direction, and the same filter and projection.
/// </summary>
public static class KeysetCursorContextFactory
{
    /// <summary>The direction signature of a stream whose key columns are all ascending (also the value of every cursor issued before issue #390).</summary>
    public const string AscendingDirection = "asc";

    /// <summary>
    /// The direction signature bound into the cursor: <c>asc</c> when every column is ascending (existing cursors stay valid), otherwise one
    /// <c>asc</c>/<c>desc</c> word per key column (e.g. <c>asc,desc</c>), so a cursor issued before a direction change fails the context check.
    /// </summary>
    public static string DirectionSignature(IReadOnlyList<KeyColumnDirection>? directions)
    {
        if (!KeyDirections.AnyDescending(directions))
        {
            return AscendingDirection;
        }

        return string.Join(',', directions!.Select(d => d == KeyColumnDirection.Descending ? "desc" : "asc"));
    }

    /// <summary>Builds the bulk-stream cursor context.</summary>
    /// <param name="tenantId">The request tenant id value.</param>
    /// <param name="metadata">The table being streamed (its identifier names domain, schema and table).</param>
    /// <param name="keyColumns">The resolved seek key (primary key, else the first unique index).</param>
    /// <param name="callerSid">The caller SID the cursor is issued to.</param>
    /// <param name="request">The stream request; only its filter and projection are bound (never the cursor or the slice limit).</param>
    /// <param name="keyDirections">The declared directions of <paramref name="keyColumns"/> (empty or null = all ascending).</param>
    public static KeysetCursorContext ForBulkStream(
        string? tenantId,
        TableMetadata metadata,
        IReadOnlyList<string> keyColumns,
        string? callerSid,
        BulkTableStreamRequest request,
        IReadOnlyList<KeyColumnDirection>? keyDirections = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(request);

        var table = metadata.Identifier;
        return new KeysetCursorContext(
            TenantId: tenantId ?? string.Empty,
            DataSource: table.Domain ?? string.Empty,
            TableName: table.TableName ?? string.Empty,
            Direction: DirectionSignature(keyDirections),
            Columns: keyColumns,
            Caller: callerSid ?? string.Empty,
            Schema: table.Schema ?? string.Empty,
            RequestHash: ComputeRequestHash(request.Filter, request.Select));
    }

    /// <summary>Builds the GraphQL keyset cursor context binding tenant, table, caller SID, and query shape.</summary>
    /// <param name="tenantId">The request tenant id value.</param>
    /// <param name="metadata">The table being queried.</param>
    /// <param name="keyColumns">The resolved seek key columns.</param>
    /// <param name="callerSid">The caller SID the cursor is issued to.</param>
    /// <param name="filter">The user where filter (tree).</param>
    /// <param name="select">The selected columns under node.</param>
    /// <param name="keyDirections">The declared directions of <paramref name="keyColumns"/> (empty or null = all ascending).</param>
    public static KeysetCursorContext ForGraphQl(
        string? tenantId,
        TableMetadata metadata,
        IReadOnlyList<string> keyColumns,
        string? callerSid,
        TreeFilter? filter,
        IReadOnlyList<string>? select,
        IReadOnlyList<KeyColumnDirection>? keyDirections = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(keyColumns);

        var table = metadata.Identifier;
        return new KeysetCursorContext(
            TenantId: tenantId ?? string.Empty,
            DataSource: table.Domain ?? string.Empty,
            TableName: table.TableName ?? string.Empty,
            Direction: DirectionSignature(keyDirections),
            Columns: keyColumns,
            Caller: callerSid ?? string.Empty,
            Schema: table.Schema ?? string.Empty,
            RequestHash: ComputeRequestHash(filter, select));
    }

    /// <summary>
    /// SHA-256 over a canonical JSON rendering of the filter AST and the (order-insensitive, case-insensitive) projection.
    /// Filter values are re-emitted through <see cref="JsonElement.WriteTo"/>, so insignificant whitespace does not change the digest.
    /// </summary>
    internal static string ComputeRequestHash(TypedFilterNode? filter, IReadOnlyList<string>? select)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("filter");
            WriteFilter(writer, filter);
            writer.WritePropertyName("select");
            if (select is { Count: > 0 })
            {
                writer.WriteStartArray();
                foreach (var column in select
                    .Select(c => (c ?? string.Empty).Trim().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(c => c, StringComparer.Ordinal))
                {
                    writer.WriteStringValue(column);
                }

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteFilter(Utf8JsonWriter writer, TypedFilterNode? node)
    {
        if (node is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("field", node.Field);
        writer.WriteString("op", node.Op);
        writer.WritePropertyName("value");
        if (node.Value is { } value && value.ValueKind != JsonValueKind.Undefined)
        {
            value.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }

        WriteFilterList(writer, "and", node.And);
        WriteFilterList(writer, "or", node.Or);
        writer.WritePropertyName("not");
        WriteFilter(writer, node.Not);
        writer.WriteEndObject();
    }

    private static void WriteFilterList(Utf8JsonWriter writer, string name, IReadOnlyList<TypedFilterNode>? nodes)
    {
        writer.WritePropertyName(name);
        if (nodes is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var child in nodes)
        {
            WriteFilter(writer, child);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Computes deterministic SHA-256 over a canonical JSON rendering of the GraphQL TreeFilter and selected fields.
    /// </summary>
    internal static string ComputeRequestHash(TreeFilter? filter, IReadOnlyList<string>? select)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("filter");
            WriteTreeFilter(writer, filter);
            writer.WritePropertyName("select");
            if (select is { Count: > 0 })
            {
                writer.WriteStartArray();
                foreach (var column in select
                    .Select(c => (c ?? string.Empty).Trim().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(c => c, StringComparer.Ordinal))
                {
                    writer.WriteStringValue(column);
                }

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteTreeFilter(Utf8JsonWriter writer, TreeFilter? filter)
    {
        if (filter is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        switch (filter)
        {
            case TreeComparison comp:
                writer.WriteString("type", "comp");
                writer.WriteString("column", comp.Column?.ToLowerInvariant());
                writer.WriteString("op", comp.Operator.ToString().ToLowerInvariant());
                writer.WriteBoolean("nullOp", comp.BindNullOperand);
                writer.WritePropertyName("value");
                WriteObjectValue(writer, comp.Value);
                break;
            case TreeAndFilter and:
                writer.WriteString("type", "and");
                writer.WritePropertyName("items");
                writer.WriteStartArray();
                foreach (var item in and.Items)
                {
                    WriteTreeFilter(writer, item);
                }
                writer.WriteEndArray();
                break;
            case TreeOrFilter or:
                writer.WriteString("type", "or");
                writer.WritePropertyName("items");
                writer.WriteStartArray();
                foreach (var item in or.Items)
                {
                    WriteTreeFilter(writer, item);
                }
                writer.WriteEndArray();
                break;
            case TreeNotFilter not:
                writer.WriteString("type", "not");
                writer.WritePropertyName("item");
                WriteTreeFilter(writer, not.Item);
                break;
            default:
                writer.WriteString("type", filter.GetType().Name);
                break;
        }
        writer.WriteEndObject();
    }

    private static void WriteObjectValue(Utf8JsonWriter writer, object? val)
    {
        switch (val)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case Guid g:
                writer.WriteStringValue(g.ToString());
                break;
            case JsonElement je:
                je.WriteTo(writer);
                break;
            case IEnumerable en:
                writer.WriteStartArray();
                foreach (var item in en)
                {
                    WriteObjectValue(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(val, CultureInfo.InvariantCulture));
                break;
        }
    }
}
