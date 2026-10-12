namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>Number and form of the arguments a native type accepts.</summary>
internal enum NativeTypeArguments
{
    None,
    /// <summary>Zero or one numeric argument (precision, fractional seconds, length).</summary>
    OptionalOne,
    /// <summary>Exactly one argument, a length (a number or <c>max</c> where the dialect has it).</summary>
    RequiredLength,
    /// <summary>Zero, one or two numeric arguments (precision and scale).</summary>
    OptionalPrecisionScale
}

/// <summary>
/// CR-ADG-42: the closed set of provider-native column types the INSERT check option can cast to, per dialect. The check casts every
/// written value to the catalog type of its column, so the policy is evaluated on what the database will store (decimal rounding,
/// date and time truncation, length). A type that is unknown, not listed here, or listed for no dialect has no cast and the INSERT
/// is rejected (fail closed). The emitter and the coverage verifier both resolve through this one table, so the emitted type name
/// can never be request text.
/// </summary>
internal static class CatalogTypeMap
{
    /// <summary>
    /// The parsed shape of a catalog type string. Hand-written and linear (no backtracking, no timeout): the compile result must not
    /// depend on the machine load. Grammar, ASCII only and case-insensitive:
    /// <c>ws* name ws* [ '(' ws* (max | d{1,5}) ws* [',' ws* d{1,3}] ws* ')' ] [ ws+ ('with'|'without') ws+ 'time' ws+ 'zone' ] ws* end</c>,
    /// where <c>name</c> is words (<c>[A-Za-z][A-Za-z0-9_]*</c>) separated by exactly one space; the name is the shortest run of words
    /// after which the rest of the string is a valid tail.
    /// </summary>
    private readonly record struct TypeShape(string Name, bool HasA, string A, bool HasB, string B, string TimeZone);

    private static bool IsSpace(char c) =>
        c is '\t' or '\n' or '\v' or '\f' or '\r' or '\u0085' ||
        (c > '\u007f' && char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.SpaceSeparator or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator) ||
        c == ' ';

    private static bool IsLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static int SkipSpace(ReadOnlySpan<char> text, int pos)
    {
        while (pos < text.Length && IsSpace(text[pos])) pos++;
        return pos;
    }

    private static int ReadWord(ReadOnlySpan<char> text, int pos)
    {
        if (pos >= text.Length || !IsLetter(text[pos])) return pos;
        pos++;
        while (pos < text.Length && (IsLetter(text[pos]) || IsDigit(text[pos]) || text[pos] == '_')) pos++;
        return pos;
    }

    // Reads 1 to maxDigits ASCII digits; false when there is none or when more follow.
    private static bool ReadDigits(ReadOnlySpan<char> text, ref int pos, int maxDigits, out string digits)
    {
        int start = pos;
        while (pos < text.Length && IsDigit(text[pos])) pos++;
        digits = text[start..pos].ToString();
        return pos > start && pos - start <= maxDigits;
    }

    private static bool MatchWord(ReadOnlySpan<char> text, ref int pos, string word)
    {
        if (text.Length - pos < word.Length || !text.Slice(pos, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase)) return false;
        pos += word.Length;
        return true;
    }

    // The tail after the name: optional argument list, optional time zone suffix, end of text.
    private static bool TryParseTail(ReadOnlySpan<char> text, int pos, out bool hasA, out string a, out bool hasB, out string b, out string timeZone)
    {
        hasA = hasB = false;
        a = b = timeZone = string.Empty;
        int afterName = pos;
        pos = SkipSpace(text, pos);
        bool spaceBeforeNext = pos > afterName;

        if (pos < text.Length && text[pos] == '(')
        {
            pos = SkipSpace(text, pos + 1);
            if (MatchWord(text, ref pos, "max"))
            {
                hasA = true;
                a = "max";
            }
            else if (ReadDigits(text, ref pos, 5, out a))
            {
                hasA = true;
            }
            else
            {
                return false;
            }

            pos = SkipSpace(text, pos);
            if (pos < text.Length && text[pos] == ',')
            {
                // The second argument exists only after a numeric first one ('max' never has a scale: the regex accepted it here too and the caller rejects it).
                pos = SkipSpace(text, pos + 1);
                if (!ReadDigits(text, ref pos, 3, out b)) return false;
                hasB = true;
                pos = SkipSpace(text, pos);
            }

            if (pos >= text.Length || text[pos] != ')') return false;
            int afterParen = pos + 1;
            pos = SkipSpace(text, afterParen);
            spaceBeforeNext = pos > afterParen;
        }

        if (pos < text.Length)
        {
            // ws+ ('with'|'without') ws+ 'time' ws+ 'zone'
            if (!spaceBeforeNext) return false;
            string prefix;
            if (MatchWord(text, ref pos, "without")) prefix = "without";
            else if (MatchWord(text, ref pos, "with")) prefix = "with";
            else return false;
            int p = SkipSpace(text, pos);
            if (p == pos) return false;
            pos = p;
            if (!MatchWord(text, ref pos, "time")) return false;
            p = SkipSpace(text, pos);
            if (p == pos) return false;
            pos = p;
            if (!MatchWord(text, ref pos, "zone")) return false;
            timeZone = prefix + " time zone";
            pos = SkipSpace(text, pos);
            if (pos < text.Length) return false;
        }

        return true;
    }

    private static bool TryParse(string text, out TypeShape shape)
    {
        shape = default;
        ReadOnlySpan<char> span = text;
        int start = SkipSpace(span, 0);
        int pos = ReadWord(span, start);
        if (pos == start) return false;

        while (true)
        {
            if (TryParseTail(span, pos, out bool hasA, out string a, out bool hasB, out string b, out string tz))
            {
                shape = new TypeShape(span[start..pos].ToString(), hasA, a, hasB, b, tz);
                return true;
            }

            // The name continues with one space and the next word; anything else cannot be a type.
            if (pos + 1 >= span.Length || span[pos] != ' ') return false;
            int next = ReadWord(span, pos + 1);
            if (next == pos + 1) return false;
            pos = next;
        }
    }

    private readonly record struct Entry(NativeTypeArguments Arguments, bool IsText = false, bool AllowsMax = false, bool AllowsTimeZone = false);

    private static readonly FrozenDictionary<string, Entry> SqlServer = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        ["bit"] = new(NativeTypeArguments.None), ["tinyint"] = new(NativeTypeArguments.None), ["smallint"] = new(NativeTypeArguments.None),
        ["int"] = new(NativeTypeArguments.None), ["bigint"] = new(NativeTypeArguments.None), ["real"] = new(NativeTypeArguments.None),
        ["money"] = new(NativeTypeArguments.None), ["smallmoney"] = new(NativeTypeArguments.None), ["date"] = new(NativeTypeArguments.None),
        ["smalldatetime"] = new(NativeTypeArguments.None), ["datetime"] = new(NativeTypeArguments.None), ["uniqueidentifier"] = new(NativeTypeArguments.None),
        ["float"] = new(NativeTypeArguments.OptionalOne), ["time"] = new(NativeTypeArguments.OptionalOne), ["datetime2"] = new(NativeTypeArguments.OptionalOne),
        ["datetimeoffset"] = new(NativeTypeArguments.OptionalOne),
        ["decimal"] = new(NativeTypeArguments.OptionalPrecisionScale), ["numeric"] = new(NativeTypeArguments.OptionalPrecisionScale),
        ["char"] = new(NativeTypeArguments.RequiredLength, IsText: true), ["nchar"] = new(NativeTypeArguments.RequiredLength, IsText: true),
        ["varchar"] = new(NativeTypeArguments.RequiredLength, IsText: true, AllowsMax: true),
        ["nvarchar"] = new(NativeTypeArguments.RequiredLength, IsText: true, AllowsMax: true),
        ["binary"] = new(NativeTypeArguments.RequiredLength), ["varbinary"] = new(NativeTypeArguments.RequiredLength, AllowsMax: true)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, Entry> PostgreSql = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        ["smallint"] = new(NativeTypeArguments.None), ["integer"] = new(NativeTypeArguments.None), ["int"] = new(NativeTypeArguments.None),
        ["bigint"] = new(NativeTypeArguments.None), ["real"] = new(NativeTypeArguments.None), ["double precision"] = new(NativeTypeArguments.None),
        ["boolean"] = new(NativeTypeArguments.None), ["date"] = new(NativeTypeArguments.None), ["uuid"] = new(NativeTypeArguments.None),
        ["bytea"] = new(NativeTypeArguments.None), ["text"] = new(NativeTypeArguments.None, IsText: true),
        ["numeric"] = new(NativeTypeArguments.OptionalPrecisionScale), ["decimal"] = new(NativeTypeArguments.OptionalPrecisionScale),
        ["varchar"] = new(NativeTypeArguments.OptionalOne, IsText: true), ["character varying"] = new(NativeTypeArguments.OptionalOne, IsText: true),
        ["char"] = new(NativeTypeArguments.OptionalOne, IsText: true), ["character"] = new(NativeTypeArguments.OptionalOne, IsText: true),
        ["time"] = new(NativeTypeArguments.OptionalOne, AllowsTimeZone: true), ["timestamp"] = new(NativeTypeArguments.OptionalOne, AllowsTimeZone: true)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, Entry> Oracle = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        ["number"] = new(NativeTypeArguments.OptionalPrecisionScale), ["float"] = new(NativeTypeArguments.OptionalOne),
        ["binary_float"] = new(NativeTypeArguments.None), ["binary_double"] = new(NativeTypeArguments.None), ["date"] = new(NativeTypeArguments.None),
        ["varchar2"] = new(NativeTypeArguments.RequiredLength, IsText: true), ["nvarchar2"] = new(NativeTypeArguments.RequiredLength, IsText: true),
        ["char"] = new(NativeTypeArguments.OptionalOne, IsText: true), ["nchar"] = new(NativeTypeArguments.OptionalOne, IsText: true),
        ["timestamp"] = new(NativeTypeArguments.OptionalOne, AllowsTimeZone: true), ["raw"] = new(NativeTypeArguments.RequiredLength)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, Entry> DuckDb = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        ["boolean"] = new(NativeTypeArguments.None), ["tinyint"] = new(NativeTypeArguments.None), ["smallint"] = new(NativeTypeArguments.None),
        ["integer"] = new(NativeTypeArguments.None), ["int"] = new(NativeTypeArguments.None), ["bigint"] = new(NativeTypeArguments.None),
        ["real"] = new(NativeTypeArguments.None), ["float"] = new(NativeTypeArguments.None), ["double"] = new(NativeTypeArguments.None),
        ["date"] = new(NativeTypeArguments.None), ["time"] = new(NativeTypeArguments.None), ["timestamp"] = new(NativeTypeArguments.None),
        ["uuid"] = new(NativeTypeArguments.None), ["blob"] = new(NativeTypeArguments.None), ["text"] = new(NativeTypeArguments.None, IsText: true),
        ["varchar"] = new(NativeTypeArguments.OptionalOne, IsText: true),
        ["decimal"] = new(NativeTypeArguments.OptionalPrecisionScale), ["numeric"] = new(NativeTypeArguments.OptionalPrecisionScale)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static FrozenDictionary<string, Entry>? TableOf(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => SqlServer,
        TargetSqlDialect.PostgreSql => PostgreSql,
        TargetSqlDialect.Oracle => Oracle,
        TargetSqlDialect.DuckDb => DuckDb,
        _ => null   // Databricks and the dialects without a governed DML path have no cast: the check option is rejected
    };

    /// <summary>
    /// Resolves a catalog data type to the native cast target of <paramref name="dialect"/>. Returns false for a null, empty,
    /// unknown or unlisted type (fail closed).
    /// </summary>
    public static bool TryResolve(TargetSqlDialect dialect, string? catalogType, out string native, out bool isText)
    {
        native = string.Empty;
        isText = false;
        if (dialect == TargetSqlDialect.Sqlite) return TryResolveSqlite(catalogType, out native, out isText);
        var table = TableOf(dialect);
        if (table is null || string.IsNullOrWhiteSpace(catalogType)) return false;

        if (!TryParse(catalogType, out var shape)) return false;
        string name = shape.Name.ToLowerInvariant();
        if (!table.TryGetValue(name, out var entry)) return false;

        bool hasA = shape.HasA, hasB = shape.HasB;
        string a = shape.A.ToLowerInvariant();
        bool max = a == "max";
        string tz = shape.TimeZone;

        if (tz.Length > 0 && !entry.AllowsTimeZone) return false;
        switch (entry.Arguments)
        {
            case NativeTypeArguments.None when hasA:
                return false;
            case NativeTypeArguments.RequiredLength when !hasA || hasB:
            case NativeTypeArguments.OptionalOne when hasB:
                return false;
        }

        if (max && !entry.AllowsMax) return false;
        if (hasB && entry.Arguments != NativeTypeArguments.OptionalPrecisionScale) return false;
        if (!max && hasA && (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out int an) || (an == 0 && entry.IsText))) return false;

        var sb = new StringBuilder();
        sb.Append(dialect == TargetSqlDialect.Oracle ? name.ToUpperInvariant() : name);
        if (hasA)
        {
            sb.Append('(').Append(a);
            if (hasB) sb.Append(',').Append(shape.B);
            sb.Append(')');
        }

        if (tz.Length > 0) sb.Append(' ').Append(dialect == TargetSqlDialect.Oracle ? tz.ToUpperInvariant() : tz);
        native = sb.ToString();
        isText = entry.IsText;
        return true;
    }

    /// <summary>
    /// SQLite declared types are free text and the database stores by type affinity, so the cast target is the affinity keyword (a closed
    /// set, idempotent: resolving <c>TEXT</c> gives <c>TEXT</c>). A column with BLOB affinity (no type or <c>BLOB</c>) has no cast and the
    /// INSERT is rejected. SQLite ignores length and precision, so no argument is carried over.
    /// </summary>
    private static bool TryResolveSqlite(string? catalogType, out string native, out bool isText)
    {
        native = string.Empty;
        isText = false;
        if (string.IsNullOrWhiteSpace(catalogType)) return false;

        if (!TryParse(catalogType, out _)) return false;

        var affinity = SqliteTypeAffinity.Of(catalogType);
        if (affinity == SqliteTypeAffinity.Kind.Blob) return false;

        // DATE, DATETIME, TIMESTAMP and TIME columns hold ISO text by SQLite convention (the generator emits typed literals as ISO text);
        // their declared name would give NUMERIC affinity, whose cast turns '2026-01-02' into 2026.
        if (affinity == SqliteTypeAffinity.Kind.Numeric &&
            (catalogType.Contains("DATE", StringComparison.OrdinalIgnoreCase) || catalogType.Contains("TIME", StringComparison.OrdinalIgnoreCase)))
        {
            native = SqliteTypeAffinity.Keyword(SqliteTypeAffinity.Kind.Text);
            isText = true;
            return true;
        }

        native = SqliteTypeAffinity.Keyword(affinity);
        isText = affinity == SqliteTypeAffinity.Kind.Text;
        return true;
    }
}
