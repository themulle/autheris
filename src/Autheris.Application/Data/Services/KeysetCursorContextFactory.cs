namespace Autheris.Application.Data.Services;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Autheris.Application.Data.Interfaces;
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
}
