namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>SQLite column affinity (https://www.sqlite.org/datatype3.html section 3.1).</summary>
public enum SqliteAffinity
{
    IntegerAffinity,
    TextAffinity,
    BlobAffinity,
    RealAffinity,
    NumericAffinity
}

/// <summary>
/// SQLite declared types are free text. The affinity rules decide how values compare: with NUMERIC or INTEGER affinity,
/// <c>'007'</c> and <c>'7'</c> compare equal, so such a column must not carry the tenant (SEC-X1-09).
/// </summary>
public static class SqliteTypeAffinity
{
    public static SqliteAffinity Of(string? declaredType)
    {
        string type = (declaredType ?? string.Empty).ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal)) return SqliteAffinity.IntegerAffinity;
        if (type.Contains("CHAR", StringComparison.Ordinal) || type.Contains("CLOB", StringComparison.Ordinal) ||
            type.Contains("TEXT", StringComparison.Ordinal)) return SqliteAffinity.TextAffinity;
        if (type.Length == 0 || type.Contains("BLOB", StringComparison.Ordinal)) return SqliteAffinity.BlobAffinity;
        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal) ||
            type.Contains("DOUB", StringComparison.Ordinal)) return SqliteAffinity.RealAffinity;
        return SqliteAffinity.NumericAffinity;
    }
}
