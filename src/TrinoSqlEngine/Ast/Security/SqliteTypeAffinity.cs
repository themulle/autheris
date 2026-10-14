namespace TrinoSqlEngine.Ast.Security;

using System;

/// <summary>
/// SQLite column type affinity from the declared type text (https://www.sqlite.org/datatype3.html, section 3.1). The rules are tested
/// in order, so <c>INTERESTING TEXT</c> is INTEGER. SQLite declared types are free text; the tenant column must have TEXT affinity
/// (SEC-X1-09), otherwise a numeric affinity stores <c>'007'</c> as <c>7</c> and the byte-exact tenant comparison is meaningless.
/// </summary>
public static class SqliteTypeAffinity
{
    /// <summary>The five SQLite affinities.</summary>
    public enum Kind { Text, Whole, Real, Numeric, Blob }

    /// <summary>Affinity of a declared type, by the ordered rules of the SQLite documentation.</summary>
    public static Kind Of(string? declaredType)
    {
        if (string.IsNullOrWhiteSpace(declaredType)) return Kind.Blob;
        if (Contains(declaredType, "INT")) return Kind.Whole;
        if (Contains(declaredType, "CHAR") || Contains(declaredType, "CLOB") || Contains(declaredType, "TEXT")) return Kind.Text;
        if (Contains(declaredType, "BLOB")) return Kind.Blob;
        if (Contains(declaredType, "REAL") || Contains(declaredType, "FLOA") || Contains(declaredType, "DOUB")) return Kind.Real;
        return Kind.Numeric;
    }

    /// <summary>The CAST target keyword of an affinity (a closed set, never request text).</summary>
    public static string Keyword(Kind kind) => kind switch
    {
        Kind.Text => "TEXT",
        Kind.Whole => "INTEGER",
        Kind.Real => "REAL",
        Kind.Blob => "BLOB",
        _ => "NUMERIC"
    };

    /// <summary>True when the declared type gives the column TEXT affinity (contains CHAR, CLOB or TEXT, and not INT).</summary>
    public static bool IsText(string? declaredType) => Of(declaredType) == Kind.Text;

    private static bool Contains(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
