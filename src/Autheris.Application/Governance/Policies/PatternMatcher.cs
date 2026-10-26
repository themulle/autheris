namespace Autheris.Application.Governance.Policies;

using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

public static class PatternMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> CompiledCache = new(StringComparer.Ordinal);
    internal const int MaxCachedPatterns = 1024;
    private static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(10);

    public static Regex CompilePattern(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return CompiledCache.TryGetValue(pattern, out var cached) ? cached : CompileAndCache(pattern);
    }

    internal static int CachedPatternCount => CompiledCache.Count;

    internal static void ClearCache() => CompiledCache.Clear();

    private static Regex CompileAndCache(string pattern)
    {
        // Failures are not cached; the cache is bounded so distinct policy patterns cannot grow it without limit.
        var regex = new Regex(
            ConvertGlobToRegex(pattern),
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
            DefaultMatchTimeout);
        if (CompiledCache.Count >= MaxCachedPatterns)
        {
            CompiledCache.Clear();
        }

        CompiledCache[pattern] = regex;
        return regex;
    }

    public static bool IsMatch(string pattern, string input)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            var regex = CompilePattern(pattern);
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            // SEC-SC-13: ReDoS fail-closed
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string ConvertGlobToRegex(string glob)
    {
        var trimmed = glob.Trim();

        // If already looks like an explicit regex anchor
        if (trimmed.StartsWith('^') && trimmed.EndsWith('$'))
        {
            return trimmed;
        }

        // Convert wildcard glob (*, ?) into regex
        var escaped = Regex.Escape(trimmed)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");

        return $"^{escaped}$";
    }
}
