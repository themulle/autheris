namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// The value-free null shape of one statement: the names of the referenced scalar client parameters whose value is null (names
/// case-insensitive). It is part of the compile cache key through <see cref="Canonical"/>, its only public instance property. A
/// parameter that is absent from the request is not a null shape.
/// </summary>
public sealed class ClientNullShapes
{
    private static readonly Regex NameRegex = new(@"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly HashSet<string> _names;

    private ClientNullShapes(HashSet<string> names, string canonical)
    {
        _names = names;
        Canonical = canonical;
    }

    public static ClientNullShapes Empty { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), string.Empty);

    /// <summary>Lower-cased names, ordinal sorted, each followed by <c>;</c>. Names only, never values.</summary>
    public string Canonical { get; }

    public static ClientNullShapes Create(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (name is null || !NameRegex.IsMatch(name))
            {
                throw new ArgumentException("Invalid client null shape.", nameof(names));
            }

            set.Add(name);
        }

        if (set.Count == 0)
        {
            return Empty;
        }

        var canonical = string.Concat(set.Select(n => n.ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal).Select(n => n + ";"));
        return new ClientNullShapes(set, canonical);
    }

    /// <summary>A method, not a property: public properties are part of the cache key.</summary>
    public bool Contains(string name) => _names.Contains(name);

    internal bool IsEmpty => _names.Count == 0;
}
