namespace TrinoSqlEngine.Governance;

using System;
using System.Globalization;

/// <summary>Class of a catalog key column type for the key-set cache (PLAN-GOV-107 A-9).</summary>
public enum KeyTypeClass
{
    /// <summary>Anything outside the allowlist: LOB and MAX types, floating point, dates, binary, unbounded text, unknown.</summary>
    Unsupported,
    WholeNumber,

    /// <summary>Exact numeric with scale 0.</summary>
    ExactNumeric,

    /// <summary>Bounded character type of a non-Unicode class (<c>varchar</c>, <c>char</c>, <c>varchar2</c> on SQL Server and Oracle).</summary>
    NonUnicodeText,

    /// <summary>Bounded character type of the Unicode class (<c>nvarchar</c>, <c>nchar</c>, <c>nvarchar2</c>; every bounded text on PostgreSQL and SQLite).</summary>
    UnicodeText,
    Uuid
}

/// <summary><paramref name="Rank"/> is the byte width of an <see cref="KeyTypeClass.WholeNumber"/>, otherwise 0.</summary>
public readonly record struct KeyTypeInfo(KeyTypeClass Class, int Rank);

/// <summary>
/// SEC-RF107-07 (a), (c): the closed allowlist of key type pairs. A key loaded through the primary key column of the dependent table is
/// bound against the foreign key column of the target table; every pair outside the allowlist, and every pair of columns with differing
/// catalog collations, is not bindable (the filter stays <c>EXISTS</c>). Pure functions over the catalog type strings.
/// </summary>
public static class KeySetKeyTypes
{
    /// <summary>Classifies a catalog data type of <paramref name="dialect"/>; unknown, empty and unlisted types are <see cref="KeyTypeClass.Unsupported"/>.</summary>
    public static KeyTypeInfo Classify(TargetSqlDialect dialect, string? catalogType)
    {
        if (string.IsNullOrWhiteSpace(catalogType) || catalogType.Length > 64)
        {
            return default;
        }

        if (!TryParse(catalogType, out string name, out int[] args, out bool maxArgument) || maxArgument)
        {
            return default;
        }

        switch (name)
        {
            case "tinyint" when args.Length == 0: return WholeNumber(dialect, 1);
            case "smallint" or "int2" when args.Length == 0: return WholeNumber(dialect, 2);
            case "int" or "integer" or "int4" or "mediumint" when args.Length == 0: return WholeNumber(dialect, 4);
            case "bigint" or "int8" when args.Length == 0: return WholeNumber(dialect, 8);
            case "uniqueidentifier" when dialect == TargetSqlDialect.SqlServer && args.Length == 0:
            case "uuid" when dialect == TargetSqlDialect.PostgreSql && args.Length == 0:
                return new KeyTypeInfo(KeyTypeClass.Uuid, 0);
        }

        if (IsExactNumeric(dialect, name, args))
        {
            return new KeyTypeInfo(KeyTypeClass.ExactNumeric, 0);
        }

        if (args.Length == 1 && args[0] > 0 && TextClass(dialect, name) is { } text && text != KeyTypeClass.Unsupported)
        {
            return new KeyTypeInfo(text, 0);
        }

        return default;
    }

    /// <summary>
    /// The A-9 pair allowlist: integer to integer of equal or wider range; exact numeric with scale 0 to the same; bounded character to
    /// bounded character of the same Unicode class (<c>varchar</c> to <c>nvarchar</c> is allowed, <c>nvarchar</c> to <c>varchar</c> is not);
    /// uuid to uuid. A differing collation of two columns that both expose one rejects the pair (SEC-RF107-07 c).
    /// </summary>
    public static bool IsBindablePair(TargetSqlDialect dialect, string? keyType, string? foreignKeyType, string? keyCollation = null, string? foreignKeyCollation = null)
    {
        if (keyCollation is not null && foreignKeyCollation is not null &&
            !string.Equals(keyCollation, foreignKeyCollation, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var key = Classify(dialect, keyType);
        var fk = Classify(dialect, foreignKeyType);
        return (key.Class, fk.Class) switch
        {
            (KeyTypeClass.WholeNumber, KeyTypeClass.WholeNumber) => key.Rank <= fk.Rank,
            (KeyTypeClass.ExactNumeric, KeyTypeClass.ExactNumeric) => true,
            (KeyTypeClass.NonUnicodeText, KeyTypeClass.NonUnicodeText or KeyTypeClass.UnicodeText) => true,
            (KeyTypeClass.UnicodeText, KeyTypeClass.UnicodeText) => true,
            (KeyTypeClass.Uuid, KeyTypeClass.Uuid) => true,
            _ => false
        };
    }

    private static KeyTypeInfo WholeNumber(TargetSqlDialect dialect, int rank) =>
        new(KeyTypeClass.WholeNumber, dialect == TargetSqlDialect.Sqlite ? 8 : rank);   // SQLite stores every integer type the same way

    private static bool IsExactNumeric(TargetSqlDialect dialect, string name, int[] args)
    {
        bool numeric = name is "decimal" or "numeric" or "number";
        if (!numeric || (name == "number") != (dialect == TargetSqlDialect.Oracle))
        {
            return false;
        }

        return args.Length switch
        {
            0 => dialect == TargetSqlDialect.SqlServer,   // SQL Server defaults to (18, 0); PostgreSQL and Oracle without precision are unbounded
            1 => args[0] > 0,                              // precision only: scale 0
            2 => args[0] > 0 && args[1] == 0,
            _ => false
        };
    }

    private static KeyTypeClass? TextClass(TargetSqlDialect dialect, string name) => dialect switch
    {
        TargetSqlDialect.SqlServer => name switch
        {
            "char" or "varchar" => KeyTypeClass.NonUnicodeText,
            "nchar" or "nvarchar" => KeyTypeClass.UnicodeText,
            _ => null
        },
        TargetSqlDialect.Oracle => name switch
        {
            "char" or "varchar2" => KeyTypeClass.NonUnicodeText,
            "nchar" or "nvarchar2" => KeyTypeClass.UnicodeText,
            _ => null
        },
        TargetSqlDialect.PostgreSql => name is "varchar" or "character varying" or "char" or "character" ? KeyTypeClass.UnicodeText : null,
        TargetSqlDialect.Sqlite => name is "varchar" or "character varying" or "char" or "character" or "nvarchar" or "nchar" ? KeyTypeClass.UnicodeText : null,
        _ => null
    };

    /// <summary>
    /// Hand-written parse of <c>name</c> or <c>name(d[, d])</c> (ASCII, case-insensitive, words separated by single spaces). Any other
    /// shape, including a <c>max</c> argument (reported through <paramref name="maxArgument"/>), is rejected.
    /// </summary>
    private static bool TryParse(string text, out string name, out int[] args, out bool maxArgument)
    {
        name = string.Empty;
        args = [];
        maxArgument = false;
        string t = text.Trim().ToLowerInvariant();
        int open = t.IndexOf('(', StringComparison.Ordinal);
        string head = (open < 0 ? t : t[..open]).Trim();
        if (head.Length == 0)
        {
            return false;
        }

        foreach (char c in head)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or ' '))
            {
                return false;
            }
        }

        name = head;
        if (open < 0)
        {
            return true;
        }

        if (!t.EndsWith(')') || t.IndexOf(')', StringComparison.Ordinal) != t.Length - 1)
        {
            return false;
        }

        string[] parts = t[(open + 1)..^1].Split(',');
        if (parts.Length is 0 or > 2)
        {
            return false;
        }

        args = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part == "max")
            {
                maxArgument = true;
                return true;
            }

            if (part.Length is 0 or > 5 || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out args[i]))
            {
                return false;
            }
        }

        return true;
    }
}
