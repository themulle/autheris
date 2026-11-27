using Autheris.Application.Analytics;

namespace Autheris.GraphQL.Catalog;

/// <summary>
/// One flat group row. <see cref="Key"/> holds the group columns by field name, <see cref="Values"/> the selected count and aggregate
/// fields by row field name; the row fields resolve from these two maps only.
/// </summary>
internal sealed record FlatGroupRow(
    IReadOnlyDictionary<string, object?> Key,
    bool IsMergedOther,
    IReadOnlyDictionary<string, object?> Values);

/// <summary>Projects the typed aggregation result (already mapped by <see cref="AggregateResultMapper"/>) onto flat rows.</summary>
internal static class FlatGroupRowMapper
{
    public static IReadOnlyList<FlatGroupRow> ToRows(AggregateResultDto dto, FlatAggregateSelection selection, CatalogTableType table)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(selection);

        var aggregateFields = FlatAggregateRowFields.For(table)
            .Where(f => f.Role is FlatAggregateRowFieldRole.Count or FlatAggregateRowFieldRole.Aggregate &&
                        selection.AliasByFieldName.ContainsKey(f.FieldName))
            .ToList();

        var rows = new List<FlatGroupRow>(dto.Groups.Count);
        foreach (var group in dto.Groups)
        {
            var values = new Dictionary<string, object?>(aggregateFields.Count, StringComparer.Ordinal);
            foreach (var field in aggregateFields)
            {
                var alias = selection.AliasByFieldName[field.FieldName];
                var aggregate = group.Aggregates.FirstOrDefault(a => string.Equals(a.Alias, alias, StringComparison.Ordinal));
                values[field.FieldName] = field.Role == FlatAggregateRowFieldRole.Count
                    ? aggregate?.IntValue ?? group.RowCount
                    : ValueOf(aggregate);
            }

            rows.Add(new FlatGroupRow(group.Key, group.IsMergedOther, values));
        }

        return rows;
    }

    private static object? ValueOf(AggregateValueDto? value) => value?.Kind switch
    {
        AggregateValueKind.Int => value.IntValue,
        AggregateValueKind.Decimal => value.DecimalValue,
        AggregateValueKind.Float => value.FloatValue,
        AggregateValueKind.String => value.StringValue,
        AggregateValueKind.Boolean => value.BooleanValue,
        AggregateValueKind.DateTime => value.DateTimeValue,
        _ => null
    };
}
