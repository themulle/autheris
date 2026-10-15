using System.Globalization;

namespace Autheris.Domain.Model;

/// <summary>Outcome of <see cref="TableKeyMarkers.Parse"/>: <see cref="Columns"/> is empty when no marker exists or the markers are invalid.</summary>
public readonly record struct TableKeyMarkerResult(bool IsValid, IReadOnlyList<string> Columns);

/// <summary>
/// The one parser of the column <c>Meta</c> primary key markers (<c>primary_key_ordinal</c>, <c>is_primary_key=true</c>,
/// <c>role=primary_key</c>), shared by the persistence layer and every read path (issue #94, H2). Markers are a declaration
/// channel: all-or-nothing, a corrupt ordinal, a gap, a duplicate or two marker kinds that name different columns make the
/// whole marker key invalid. They apply only when no declared primary key rows exist.
/// </summary>
public static class TableKeyMarkers
{
    public const string OrdinalKey = "primary_key_ordinal";
    public const string FlagKey = "is_primary_key";
    public const string RoleKey = "role";
    public const string RoleValue = "primary_key";

    public static TableKeyMarkerResult Parse(IReadOnlyList<TableColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        List<(int Ordinal, string Name)>? ordinals = null;
        List<string>? flagged = null;
        foreach (var column in columns)
        {
            var meta = column.Meta;
            if (meta is null)
            {
                continue;
            }

            if (meta.TryGetValue(OrdinalKey, out var ordinalText))
            {
                if (!int.TryParse(ordinalText, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal < 1)
                {
                    return Invalid;
                }

                (ordinals ??= new()).Add((ordinal, column.ColumnName));
            }

            if (IsFlagged(meta))
            {
                (flagged ??= new()).Add(column.ColumnName);
            }
        }

        if (ordinals is null)
        {
            return new TableKeyMarkerResult(true, flagged is null ? Array.Empty<string>() : flagged);
        }

        ordinals.Sort(static (a, b) => a.Ordinal.CompareTo(b.Ordinal));
        var names = new List<string>(ordinals.Count);
        for (var i = 0; i < ordinals.Count; i++)
        {
            if (ordinals[i].Ordinal != i + 1)
            {
                return Invalid;
            }

            names.Add(ordinals[i].Name);
        }

        if (flagged is not null &&
            (flagged.Count != names.Count || flagged.Except(names, StringComparer.OrdinalIgnoreCase).Any()))
        {
            return Invalid;
        }

        return new TableKeyMarkerResult(true, names);
    }

    public static bool HasMarker(TableColumn column) =>
        column.Meta is { Count: > 0 } meta &&
        (meta.ContainsKey(OrdinalKey) || meta.ContainsKey(FlagKey) || IsFlagged(meta));

    public static bool IsMarkerEntry(string key, string value) =>
        string.Equals(key, OrdinalKey, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, FlagKey, StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(key, RoleKey, StringComparison.OrdinalIgnoreCase) && string.Equals(value, RoleValue, StringComparison.OrdinalIgnoreCase));

    private static bool IsFlagged(IReadOnlyDictionary<string, string> meta) =>
        (meta.TryGetValue(FlagKey, out var isKey) && string.Equals(isKey, "true", StringComparison.OrdinalIgnoreCase)) ||
        (meta.TryGetValue(RoleKey, out var role) && string.Equals(role, RoleValue, StringComparison.OrdinalIgnoreCase));

    private static TableKeyMarkerResult Invalid => new(false, Array.Empty<string>());
}
