using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;

namespace Autheris.Tests.Unit.Masking;

/// <summary>One golden row: a catalog rule applied to one clear value, and the output both engines must produce.</summary>
/// <param name="Id">Stable name of the row (theory key).</param>
/// <param name="Rule">The catalog masking rule.</param>
/// <param name="Clear">The clear value (CLR value for the in-memory engine; bound as is for SQLite).</param>
/// <param name="DataType">The catalog data type of the column (SQL side).</param>
/// <param name="Expected">The expected masked value; null is SQL NULL.</param>
public sealed record MaskCase(string Id, MaskingRule Rule, object? Clear, string DataType, object? Expected);

/// <summary>
/// WP-ARC-B-04 / ARC-C-12: golden corpus of mask semantics (kind x value kind). <c>SqlDataMaskingProvider</c> (typed spec rendered by
/// the SQLite generator) and <c>ColumnMaskingProvider</c> (in-memory) both have to reproduce every row. Divergences that existed
/// before the engines shared <c>MaskSpec</c> are marked "fixed divergence".
/// </summary>
public static class MaskSemanticsCorpus
{
    private static MaskingRule Rule(string type) => new() { RuleType = type };

    private static MaskingRule Partial(int prefix, int suffix, char maskChar = '*') =>
        new() { RuleType = "PARTIAL_MASK", KeepPrefix = prefix, KeepSuffix = suffix, MaskChar = maskChar };

    public static readonly IReadOnlyList<MaskCase> Cases =
    [
        // Nullify: every value kind becomes NULL.
        new("nullify.text", Rule("NULLIFY"), "secret", "text", null),
        new("nullify.empty", Rule("NULLIFY"), "", "text", null),
        new("nullify.numeric", Rule("NULLIFY"), 42L, "integer", null),
        new("nullify.date", Rule("NULLIFY"), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), "datetime", null),

        // Redact: text becomes the constant (default "***"; fixed divergence: the in-memory default used to be "REDACTED"),
        // numeric and temporal columns become a typed NULL.
        new("redact.text", Rule("REDACT"), "secret", "text", "***"),
        new("redact.empty", Rule("REDACT"), "", "text", "***"),
        new("redact.unicode", Rule("REDACT"), "Müller-Straße", "text", "***"),
        new("redact.replacement", new MaskingRule { RuleType = "REDACT", Replacement = "[X]" }, "secret", "text", "[X]"),
        new("redact.numeric", Rule("REDACT"), 42L, "integer", null),
        new("redact.date", Rule("REDACT"), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), "datetime", null),

        // Partial mask: prefix + one mask character per hidden character + suffix; a value that is not longer than the revealed
        // parts gives five mask characters (fixed divergence: the in-memory empty string used to stay empty).
        new("partial.text", Partial(1, 1), "abcdef", "text", "a****f"),
        new("partial.unicode", Partial(1, 1), "Müller", "text", "M****r"),
        new("partial.maskchar", Partial(2, 0, '#'), "abcdef", "text", "ab####"),
        new("partial.short", Partial(1, 1), "ab", "text", "*****"),
        new("partial.empty", Partial(1, 1), "", "text", "*****"),
        new("partial.numeric", Partial(1, 1), 12345678L, "integer", "1******8"),

        // Fixed divergence: a fixed-length partial mask reveals nothing in SQL, so it does in memory, too (it used to keep the ends).
        new("partial.fixedlength", new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 1, KeepSuffix = 1, FixedLength = true }, "abcdef", "text", "*****"),

        // Fixed divergence: e-mail and IBAN masks are constants in both engines (the in-memory ones used to keep characters).
        new("email.text", Rule("MASK_EMAIL"), "john.doe@company.org", "text", "***@***"),
        new("email.nodomain", Rule("MASK_EMAIL"), "no-at-sign", "text", "***@***"),
        new("iban.text", Rule("MASK_IBAN"), "DE89370400440532013000", "text", "****"),
        new("iban.short", Rule("MASK_IBAN"), "DE12", "text", "****"),

        // Geo jitter (round mode): rounding to Decimals, the (0, 0) null island stays 0.
        new("geo.round", new MaskingRule { RuleType = "GEO_JITTER", Decimals = 2 }, 48.298198d, "real", 48.3d),
        new("geo.zero", new MaskingRule { RuleType = "GEO_JITTER", Decimals = 2 }, 0.0d, "real", 0.0d)
    ];

    /// <summary>Corpus rows of both engines for null input: every kind keeps NULL.</summary>
    public static readonly IReadOnlyList<MaskingRule> NullRules =
    [
        Rule("NULLIFY"), Rule("REDACT"), Partial(1, 1), Rule("MASK_EMAIL"), Rule("MASK_IBAN"),
        new MaskingRule { RuleType = "GEO_JITTER", Decimals = 2 }
    ];

    public static MaskCase Get(string id) => Cases.Single(c => c.Id == id);
}
