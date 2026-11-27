namespace Autheris.Application.Sql;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// The JSON members of a WebSQL result (<c>POST /api/v1/sql</c>), shared by the HTTP endpoint (which streams rows from a reader)
/// and by the MCP <c>query_sql</c> tool (which writes a buffered <see cref="GovernedSqlResult"/>), so both doors answer one contract.
/// </summary>
public static class WebSqlResultJson
{
    /// <summary>One entry of the <c>columns</c> array: name, Trino type name, nullability and the policy-mask flag.</summary>
    public static void WriteColumn(Utf8JsonWriter w, string name, SqlResultColumn column, bool masked)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(column);

        bool nullable = masked || (column.AllowDbNull ?? true);
        string typeName = TrinoColumnTypes.Map(column, Array.Empty<object?>()).Name;
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("type", typeName);
        w.WriteBoolean("nullable", nullable);
        w.WriteBoolean("masked", masked);
        w.WriteEndObject();
    }

    /// <summary>The members <c>rowCount</c>, <c>truncated</c>, <c>page</c>, <c>next</c> and <c>pagingUnavailable</c>, in that order.</summary>
    public static void WritePaging(
        Utf8JsonWriter w,
        int rowCount,
        bool truncated,
        GovernedSqlPageResponse? page,
        string? next,
        string? pagingUnavailable)
    {
        ArgumentNullException.ThrowIfNull(w);

        w.WriteNumber("rowCount", rowCount);
        w.WriteBoolean("truncated", truncated);

        if (page != null)
        {
            w.WriteStartObject("page");
            w.WriteNumber("number", page.Number);
            w.WriteNumber("offset", page.Offset);
            w.WriteNumber("size", page.Size);
            w.WriteEndObject();
        }

        if (!string.IsNullOrEmpty(next))
        {
            w.WriteString("next", next);
        }

        if (!string.IsNullOrEmpty(pagingUnavailable))
        {
            w.WriteString("pagingUnavailable", pagingUnavailable);
        }
    }

    /// <summary>The <c>indexHints</c> member; nothing is written for a missing or empty list.</summary>
    public static void WriteIndexHints(Utf8JsonWriter w, IReadOnlyList<WebSqlIndexHint>? hints)
    {
        ArgumentNullException.ThrowIfNull(w);

        if (hints is not { Count: > 0 })
        {
            return;
        }

        w.WriteStartArray("indexHints");
        foreach (var hint in hints)
        {
            w.WriteStartObject();
            w.WriteString("table", hint.Table);
            w.WriteString("reason", hint.Reason);
            w.WriteStartArray("helpfulIndexColumns");
            foreach (var col in hint.HelpfulIndexColumns)
            {
                w.WriteStringValue(col);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    /// <summary>
    /// The WebSQL body of a buffered result: typed <c>columns</c>, object <c>rows</c> in column order, the paging members and
    /// <c>indexHints</c>. Never the original SQL, the elapsed time or a result-cache outcome.
    /// </summary>
    public static string Serialize(GovernedSqlResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            var keys = result.Columns;
            var names = SqlResultColumns.MakeUnique(keys.Count == 0 ? Array.Empty<string?>() : [.. keys]);
            var described = result.ColumnDescriptions;
            var masks = result.MaskedColumns;

            w.WriteStartObject();
            w.WriteStartArray("columns");
            for (int i = 0; i < names.Length; i++)
            {
                var column = described is not null && i < described.Count
                    ? described[i]
                    : new SqlResultColumn(names[i], null);
                bool masked = !masks.IsDefault && i < masks.Length && masks[i];
                WriteColumn(w, names[i], column, masked);
            }

            w.WriteEndArray();

            w.WriteStartArray("rows");
            foreach (var row in result.Rows)
            {
                w.WriteStartObject();
                for (int i = 0; i < names.Length; i++)
                {
                    w.WritePropertyName(names[i]);
                    object? value = null;
                    if (!row.TryGetValue(keys[i], out value))
                    {
                        row.TryGetValue(names[i], out value);
                    }

                    WebSqlValueWriter.Write(w, value);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();

            WritePaging(w, result.RowCount, result.Truncated, result.Page, result.Next, result.PagingUnavailable);
            WriteIndexHints(w, result.IndexHints);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
