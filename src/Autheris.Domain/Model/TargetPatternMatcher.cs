namespace Autheris.Domain.Model;

using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

/// <summary>
/// F-GOV-14 (SEC-7, SEC-8): regex target patterns of Virtual Filter bindings (<c>^...</c> or <c>regex:...</c>).
/// <list type="bullet">
/// <item>A pattern matches the full, source-qualified name <c>domain.schema.table</c>
/// (<see cref="TableIdentifier.ToString"/>) and nothing else; it is anchored at both ends (<c>^(?:pattern)$</c>).</item>
/// <item>Matching runs on the linear <see cref="RegexOptions.NonBacktracking"/> engine, case-insensitive and
/// culture-invariant like <see cref="TableIdentifier"/>, with a timeout as defense in depth.</item>
/// <item>A pattern is at most <see cref="MaxPatternLength"/> characters; constructs that need backtracking
/// (backreferences, lookarounds) are rejected.</item>
/// <item><see cref="IsMatch(string, TableIdentifier)"/> never catches: an invalid, unsupported or overlong pattern and a
/// timeout propagate, so the caller denies (fail closed) instead of treating the binding as "not applying".</item>
/// </list>
/// </summary>
public static class TargetPatternMatcher
{
    public const int MaxPatternLength = 256;

    public const string RegexPrefix = "regex:";

    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private const int MaxCachedPatterns = 1024;

    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>True for the regex forms <c>^...</c> and <c>regex:...</c> (prefix case-insensitive).</summary>
    public static bool IsRegex(string? pattern) =>
        pattern != null &&
        (pattern.StartsWith('^') || pattern.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Write-time check: throws <see cref="ArgumentException"/> for an overlong, invalid or unsupported pattern.</summary>
    public static void Validate(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        try
        {
            _ = Compile(pattern);
        }
        catch (NotSupportedException ex)
        {
            throw new ArgumentException($"The target pattern uses a construct the linear regex engine does not support: {ex.Message}", nameof(pattern), ex);
        }
    }

    /// <summary>
    /// Whether the regex <paramref name="pattern"/> fully matches <c>domain.schema.table</c> of <paramref name="table"/>.
    /// Every error propagates (<see cref="ArgumentException"/>, <see cref="NotSupportedException"/>,
    /// <see cref="RegexMatchTimeoutException"/>).
    /// </summary>
    public static bool IsMatch(string pattern, TableIdentifier table)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return IsMatch(Cache.TryGetValue(pattern, out var cached) ? cached : CompileAndCache(pattern), table);
    }

    /// <summary>Matches a prepared expression against the source-qualified name; no exception is caught.</summary>
    internal static bool IsMatch(Regex regex, TableIdentifier table)
    {
        ArgumentNullException.ThrowIfNull(regex);
        return regex.IsMatch(table.ToString());
    }

    /// <summary>Drops the compiled patterns (called when a new Virtual Filter snapshot generation is loaded).</summary>
    public static void ClearCache() => Cache.Clear();

    private static Regex CompileAndCache(string pattern)
    {
        // Failures are not cached; the cache is bounded so stored patterns cannot grow it without limit.
        var regex = Compile(pattern);
        if (Cache.Count >= MaxCachedPatterns)
        {
            Cache.Clear();
        }

        Cache[pattern] = regex;
        return regex;
    }

    private static Regex Compile(string pattern)
    {
        if (!IsRegex(pattern))
        {
            throw new ArgumentException("The target pattern is not a regex pattern ('^...' or 'regex:...').", nameof(pattern));
        }

        var body = pattern.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase) ? pattern[RegexPrefix.Length..] : pattern;
        if (body.Length == 0 || body.Length > MaxPatternLength)
        {
            throw new ArgumentException($"A regex target pattern must have 1 to {MaxPatternLength} characters.", nameof(pattern));
        }

        // The body must parse on its own, so it cannot close the wrapping group and escape the anchoring.
        _ = new Regex(body, Options, MatchTimeout);
        return new Regex("^(?:" + body + ")$", Options, MatchTimeout);
    }
}
