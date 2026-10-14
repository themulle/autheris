namespace Autheris.Tests.Unit.TestDoubles;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Test-only <see cref="IDataSourceExecutor"/> for SQL sources that returns deterministic synthetic rows (the former development
/// fallback of the removed <c>SqlDataSourceExecutor</c>). It keeps the Zero-Trust pre-checks (denied table, filtering on a column that
/// is not effectively clear) so tests of the gateway pipeline that need rows without a database stay meaningful. Never referenced
/// by production code.
/// </summary>
public sealed class SyntheticSqlDataSourceExecutor : IDataSourceExecutor
{
    public DataSourceType SupportedType => DataSourceType.Sql;

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.AccessDecision.IsAllowed)
        {
            var reasons = context.AccessDecision.DeniedReasons.Count > 0
                ? string.Join("; ", context.AccessDecision.DeniedReasons)
                : "Table is blocked by access policy.";
            throw new SecurityException($"Zero-Trust violation: Access to table '{context.Metadata.Identifier}' denied: {reasons}");
        }

        foreach (var (argKey, argVal) in context.Arguments)
        {
            if (string.Equals(argKey, "limit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argKey, "offset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var matchingCol = context.Metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, argKey, StringComparison.OrdinalIgnoreCase));
            if (matchingCol != null && argVal != null)
            {
                var access = context.AccessDecision.GetEffectiveColumnAccess(matchingCol.ColumnName, context.Metadata);
                if (access != ColumnAccessLevel.Clear)
                {
                    throw new SecurityException($"Zero-Trust violation: Filtering on column '{matchingCol.ColumnName}' in table '{context.Metadata.Identifier}' is not permitted (access level: {access}).");
                }
            }
        }

        return Task.FromResult(GenerateSyntheticRows(context));
    }

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
