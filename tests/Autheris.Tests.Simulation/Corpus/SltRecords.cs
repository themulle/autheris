using System.Security.Cryptography;
using System.Text;

namespace Autheris.Tests.Simulation.Corpus;

public enum SltSortMode
{
    NoSort,
    RowSort,
    ValueSort,
}

/// <summary>A <c>skipif</c> / <c>onlyif</c> condition attached to the next record.</summary>
public sealed record SltCondition(bool Skip, string Engine);

/// <summary>Raised for malformed or hostile corpus text (fail-closed, SEC-SIM-12).</summary>
public sealed class SltParseException(string message) : Exception(message);

public abstract record SltRecord(string File, int Line)
{
    public IReadOnlyList<SltCondition> Conditions { get; init; } = [];

    /// <summary>Gate <c>CorpusStatement</c> id format: first 12 hex characters of sha256(<c>file:line:sql</c>).</summary>
    public static string IdOf(string file, int line, string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{file}:{line}:{sql}")))[..12];
}

/// <summary>The recorded expectation of a query: literal values or a count plus md5 (hash-threshold form).</summary>
public abstract record SltExpected;

public sealed record SltValues(IReadOnlyList<string> Values) : SltExpected;

public sealed record SltHash(int Count, string Md5) : SltExpected;

public sealed record SltStatement(string File, int Line, bool ExpectOk, string Sql) : SltRecord(File, Line)
{
    public string Id => IdOf(File, Line, Sql);
}

public sealed record SltQuery(string File, int Line, string Types, SltSortMode Sort, string? Label, string Sql, SltExpected Expected) : SltRecord(File, Line)
{
    public string Id => IdOf(File, Line, Sql);
}

public sealed record SltHashThreshold(string File, int Line, int Threshold) : SltRecord(File, Line);

public sealed record SltHalt(string File, int Line) : SltRecord(File, Line);
