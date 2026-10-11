using System.Collections.Immutable;
using System.Text;

namespace TrinoSqlEngine.Tests.Properties;

internal enum ColKind { Int, Str }

/// <summary>A column visible in a generated scope.</summary>
internal sealed record ScopeCol(string Alias, string Name, ColKind Kind)
{
    public string Ref => Alias + "." + Name;
}

/// <summary>
/// A generated statement as a token list plus the facts an oracle needs: how many physical table references the text contains,
/// which hostile string literals it carries (raw values) and whether it contains a function that is not allow-listed.
/// </summary>
internal sealed record Frag(ImmutableArray<string> Tokens, int PhysicalRefs, ImmutableArray<string> Literals, bool UnknownFunction)
{
    public static readonly Frag Empty = new(ImmutableArray<string>.Empty, 0, ImmutableArray<string>.Empty, false);

    public static Frag Of(params string[] tokens) => new(tokens.ToImmutableArray(), 0, ImmutableArray<string>.Empty, false);

    public Frag Add(Frag other) => new(
        Tokens.AddRange(other.Tokens), PhysicalRefs + other.PhysicalRefs, Literals.AddRange(other.Literals), UnknownFunction || other.UnknownFunction);

    public Frag Add(params string[] tokens) => this with { Tokens = Tokens.AddRange(tokens) };

    public Frag Paren() => Of("(").Add(this).Add(")");
}

/// <summary>How a token list is turned into text. Different styles of the same statement must compile identically.</summary>
internal readonly record struct RenderStyle(int KeywordCase, int SeparatorMode, int Seed);

internal static class TokenRenderer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "distinct", "from", "where", "group", "by", "having", "order", "limit", "offset", "as", "join", "inner", "left",
        "outer", "cross", "on", "and", "or", "not", "in", "exists", "like", "is", "null", "between", "case", "when", "then", "else",
        "end", "union", "all", "except", "intersect", "with", "recursive", "over", "partition", "values", "true", "false", "update",
        "set", "delete", "insert", "into", "asc", "desc", "distinct", "rows", "row", "only", "next", "fetch"
    };

    public static string Render(Frag frag, RenderStyle style)
    {
        var rnd = new Random(style.Seed);
        var sb = new StringBuilder();
        foreach (var token in frag.Tokens)
        {
            if (sb.Length > 0)
            {
                sb.Append(Separator(style.SeparatorMode, rnd));
            }

            sb.Append(Keywords.Contains(token) ? CaseKeyword(token, style.KeywordCase, rnd) : token);
        }

        return sb.ToString();
    }

    private static string Separator(int mode, Random rnd) => mode switch
    {
        0 => " ",
        1 => rnd.Next(4) switch { 0 => " ", 1 => "  ", 2 => "\n", _ => "\t" },
        _ => rnd.Next(3) switch { 0 => "\r\n  ", 1 => " \t ", _ => "   " }
    };

    private static string CaseKeyword(string keyword, int mode, Random rnd) => mode switch
    {
        0 => keyword.ToUpperInvariant(),
        1 => keyword.ToLowerInvariant(),
        _ => string.Concat(keyword.Select(c => rnd.Next(2) == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)))
    };
}
