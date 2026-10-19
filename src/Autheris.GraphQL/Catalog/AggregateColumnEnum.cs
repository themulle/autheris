namespace Autheris.GraphQL.Catalog;

/// <summary>
/// #112 A-1: the values of the per-table <c>T_column</c> enum. A value is the entity field name; GraphQL forbids the enum values
/// <c>true</c>, <c>false</c> and <c>null</c>, so such a field becomes <c>&lt;name&gt;_col</c>, made unique against every field name.
/// </summary>
internal static class AggregateColumnEnum
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "true", "false", "null" };

    public static IReadOnlyList<(string EnumValue, CatalogColumnField Column)> Build(CatalogTableType table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var taken = new HashSet<string>(table.Columns.Select(c => c.FieldName).Where(n => !Reserved.Contains(n)), StringComparer.Ordinal);
        var result = new List<(string, CatalogColumnField)>(table.Columns.Count);
        foreach (var column in table.Columns)
        {
            var value = column.FieldName;
            if (Reserved.Contains(value))
            {
                var candidate = value + "_col";
                for (var i = 2; taken.Contains(candidate); i++)
                {
                    candidate = value + "_col" + i;
                }

                taken.Add(candidate);
                value = candidate;
            }

            result.Add((value, column));
        }

        return result;
    }
}
