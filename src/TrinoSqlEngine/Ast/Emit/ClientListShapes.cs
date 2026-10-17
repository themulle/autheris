namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The value-free shape of the client lists of one statement: list name to expanded element count (names case-insensitive). It is
/// part of the compile cache key through <see cref="Canonical"/>, its only public instance property.
/// </summary>
public sealed class ClientListShapes
{
    private static readonly Regex NameRegex = new(@"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly Dictionary<string, int> _counts;

    private ClientListShapes(Dictionary<string, int> counts, string canonical)
    {
        _counts = counts;
        Canonical = canonical;
    }

    public static ClientListShapes Empty { get; } = new(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), string.Empty);

    /// <summary>Lower-cased <c>name:count</c> pairs, ordinal sorted, separated by <c>;</c>. Names and digits only: unambiguous.</summary>
    public string Canonical { get; }

    /// <summary>Counts pass through <see cref="ListShapeSizer"/>, the seam for a later bucket policy (identity today).</summary>
    public static ClientListShapes Create(IReadOnlyDictionary<string, int> expandedCounts)
    {
        ArgumentNullException.ThrowIfNull(expandedCounts);
        if (expandedCounts.Count == 0)
        {
            return Empty;
        }

        var counts = new Dictionary<string, int>(expandedCounts.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, count) in expandedCounts)
        {
            if (!NameRegex.IsMatch(name) || count < 0)
            {
                throw new ArgumentException("Invalid client list shape.", nameof(expandedCounts));
            }

            if (!counts.TryAdd(name, ListShapeSizer.Size(count)))
            {
                throw new ArgumentException("Duplicate client list shape.", nameof(expandedCounts));
            }
        }

        var sb = new StringBuilder();
        foreach (var (name, count) in counts.OrderBy(kv => kv.Key.ToLowerInvariant(), StringComparer.Ordinal))
        {
            sb.Append(name.ToLowerInvariant()).Append(':').Append(count.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        return new ClientListShapes(counts, sb.ToString());
    }

    public bool TryGetCount(string name, out int count) => _counts.TryGetValue(name, out count);

    /// <summary>The largest list of the shape (0 when empty). A method, not a property: public properties are part of the cache key.</summary>
    public int MaxCount() => _counts.Count == 0 ? 0 : _counts.Values.Max();

    internal bool IsEmpty => _counts.Count == 0;
}

/// <summary>Sizing seam of the list shape (PLAN-SQL-128 A-6): identity in this item; a bucket policy would change only this function.</summary>
internal static class ListShapeSizer
{
    public static int Size(int count) => count;
}
