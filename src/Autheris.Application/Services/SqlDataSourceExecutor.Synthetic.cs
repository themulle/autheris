using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Services;

public sealed partial class SqlDataSourceExecutor : IDataSourceExecutor
{
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> GenerateSyntheticRows(DataSourceExecutionContext context)
    {
        context.Items["IsSyntheticMock"] = true;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var metadata = context.Metadata;
        var count = Math.Max(1, context.Limit);
        var offset = Math.Max(0, context.Offset);

        if (context.Items.TryGetValue(TableQueryItems.Filter, out var filterObj) &&
            filterObj is TableFilterClause filterClause &&
            filterClause.ReferencedColumns.Any(c => c.Equals("parent_id", StringComparison.OrdinalIgnoreCase) || c.Equals("invoice_id", StringComparison.OrdinalIgnoreCase)))
        {
            var joinCol = metadata.Columns.FirstOrDefault(c => c.ColumnName.Equals("parent_id", StringComparison.OrdinalIgnoreCase))?.ColumnName
                ?? metadata.Columns.FirstOrDefault(c => c.ColumnName.Equals("invoice_id", StringComparison.OrdinalIgnoreCase))?.ColumnName
                ?? "parent_id";

            foreach (var pVal in filterClause.Parameters.Values)
            {
                var parentId = pVal?.ToString() ?? "";
                if (string.IsNullOrEmpty(parentId)) continue;

                for (int i = 1; i <= 2; i++)
                {
                    var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["id"] = $"{parentId}-ITEM-{i}",
                        [joinCol] = parentId,
                        ["product_name"] = $"Enterprise License Pack {i}",
                        ["price"] = 1250.00m * i,
                        ["sensitive_note"] = $"Confidential spec for item {i} of invoice {parentId}"
                    };
                    rows.Add(dict);
                }
            }
            return rows;
        }

        for (int i = 1; i <= count; i++)
        {
            var rowNum = offset + i;
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var col in metadata.Columns)
            {
                // SEC-AC-02: Zero-Trust: Do not emit synthetic data for Denied columns
                if (context.AccessDecision.GetColumnAccess(col.ColumnName) == ColumnAccessLevel.Deny)
                {
                    continue;
                }

                object? rawVal = col.ColumnName.ToLowerInvariant() switch
                {
                    "id" => rowNum,
                    "name" => $"Sample {metadata.Identifier.TableName} Record #{rowNum}",
                    "amount" => 100.50m * rowNum,
                    "email" => $"user{rowNum}@corp.local",
                    "created_at" => DateTimeOffset.UtcNow.AddDays(-rowNum),
                    _ => GenerateSyntheticValueForType(col.DataType, rowNum)
                };

                dict[col.ColumnName] = rawVal;
            }

            rows.Add(dict);
        }

        return rows;
    }

    private static object? GenerateSyntheticValueForType(string? dataType, int rowNum)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return $"Value_{rowNum}";
        }

        var normalizedType = dataType.Trim().ToLowerInvariant();
        if (normalizedType is "geometry" or "geography" or "spatial" or "point" or "polygon" or "linestring")
        {
            return $$"""{"type":"Point","coordinates":[13.4{{rowNum}},52.5{{rowNum}}]}""";
        }

        if (normalizedType is "bytea" or "binary" or "varbinary" or "blob" or "image")
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes($"binary_payload_{rowNum}"));
        }

        if (normalizedType is "timestamptz" or "datetimeoffset" or "datetime2" or "timestamp")
        {
            return DateTimeOffset.UtcNow.AddDays(-rowNum).ToString("O", CultureInfo.InvariantCulture);
        }

        return $"Value_{rowNum}";
    }
}
