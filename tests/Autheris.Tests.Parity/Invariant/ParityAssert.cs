using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Invariant;

/// <summary>An unexpected 5xx or internal fault code was observed. A hard failure: never a known gap (SEC-PAR-05b).</summary>
public sealed class ServerErrorObservedException(string message) : ParityHardFailureException(message);

/// <summary>An error body leaked internals (SEC-PAR-07). A hard failure: never a known gap (SEC-PAR-05b).</summary>
public sealed class LeakDetectedException(string message) : ParityHardFailureException(message);

/// <summary>
/// Invariant assertions of block C. <see cref="NoServerError"/> and <see cref="NoLeak"/> throw hard failures;
/// <see cref="StableCode"/> throws a plain assertion failure (a missing or unknown 4xx code is a functional gap, not a security failure).
/// </summary>
public static class ParityAssert
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly (string Name, Regex Pattern)[] LeakPatterns =
    [
        ("stack-frame", Rx(@"\bat [\w.<>`]+\(")),
        ("source-line", Rx(@"\.cs:line \d+")),
        ("exception-type", Rx(@"System\.\w+Exception")),
        ("exception-colon", Rx(@"\w+Exception:")),
        ("microsoft-type", Rx(@"Microsoft\.\w+")),
        ("sqlite-message", Rx(@"SQLite Error")),
        ("sqlite-exception", Rx(@"SqliteException")),
        ("syntax-near", Rx(@"near \\?""[^""]*""\s*: syntax error")),
        ("no-such-object", Rx(@"no such (table|column)")),
        ("npgsql", Rx(@"Npgsql")),
        ("sql-exception", Rx(@"SqlException")),
        ("windows-path", Rx(@"[A-Za-z]:\\")),
        ("unix-path", Rx(@"/(home|root|src|app|tmp)/")),
        ("connection-string", Rx(@"Data Source=")),
        ("credential-token", Rx(@"Password=")),
        ("stack-trace-word", Rx(@"\bStackTrace\b")),
        ("inner-exception", Rx(@"\bInnerException\b")),
        ("hotchocolate-type", Rx(@"\bHotChocolate\.")),
        ("autheris-type", Rx(@"\bAutheris\.(Api|Application|Infrastructure)\.")),
        ("server-setting", Rx(@"\bServer=")),
        ("host-setting", Rx(@"\bHost=")),
    ];

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>Names of the leak patterns matched by <paramref name="text"/> (empty when clean).</summary>
    public static IReadOnlyList<string> FindLeaks(string? text) =>
        string.IsNullOrEmpty(text) ? [] : LeakPatterns.Where(p => p.Pattern.IsMatch(text)).Select(p => p.Name).ToList();

    public static void NoLeak(string? text)
    {
        var leaks = FindLeaks(text);
        if (leaks.Count > 0)
        {
            var value = text ?? string.Empty;
            var first = LeakPatterns.First(p => p.Pattern.IsMatch(value)).Pattern.Match(value);
            var from = Math.Max(0, first.Index - 30);
            var excerpt = value[from..Math.Min(value.Length, first.Index + first.Length + 30)];
            throw new LeakDetectedException($"Error body leaks internals: {string.Join(", ", leaks)} near '{excerpt}'.");
        }
    }

    /// <summary>True for HTTP 4xx/5xx and for GraphQL responses that carry a non-empty <c>errors</c> array.</summary>
    public static bool IsError(ChannelResult r)
    {
        if (r.Status >= 400)
        {
            return true;
        }

        if (r.NotExpressible || r.IsPending || !r.RawBody.Contains("\"errors\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(r.RawBody);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("errors", out var errors)
                   && errors.ValueKind == JsonValueKind.Array
                   && errors.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTolerated501(ChannelResult r) =>
        r.Status == 501 && r.Code is not null && ParityErrorCodes.All.Contains(r.Code);

    /// <summary>
    /// No 5xx (except a 501 with a catalogue code, treated as Unsupported and recorded as an observation), no internal-fault code and no leaked internals in any error body
    /// (every status code, SEC-PAR-07). <paramref name="submittedSql"/> must not be echoed back by a 5xx.
    /// </summary>
    public static void NoServerError(ChannelResult r, string? submittedSql = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (IsError(r))
        {
            NoLeak(r.RawBody);
        }

        if (r.Status >= 500 && submittedSql is { Length: >= 8 } && r.RawBody.Contains(submittedSql, StringComparison.Ordinal))
        {
            throw new LeakDetectedException($"HTTP {r.Status} echoes the submitted SQL back.");
        }

        if (r.Code is not null && ParityErrorCodes.InternalFaultCodes.Contains(r.Code, StringComparer.Ordinal))
        {
            throw new ServerErrorObservedException($"Internal fault code '{r.Code}' (HTTP {r.Status}).");
        }

        if (r.Status == 501 && IsTolerated501(r))
        {
            ParityObservations.RecordTolerated501(r.Code!);
        }

        if (r.Status >= 500 && !IsTolerated501(r))
        {
            throw new ServerErrorObservedException($"HTTP {r.Status} (code '{r.Code}'): unexpected server error.");
        }
    }

    /// <summary>A 4xx (or GraphQL error) must carry a non-empty code from <see cref="ParityErrorCodes.All"/>.</summary>
    public static void StableCode(ChannelResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!IsError(r) || r.Status >= 500)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(r.Code))
        {
            throw new XunitException($"HTTP {r.Status}: error without a stable code: {Snippet(r.RawBody)}");
        }

        if (!ParityErrorCodes.All.Contains(r.Code) && !ParityErrorCodes.IsGraphQlEngineCode(r.Code))
        {
            throw new XunitException($"HTTP {r.Status}: code '{r.Code}' is not a registered stable code: {Snippet(r.RawBody)}");
        }
    }

    private static string Snippet(string body) => body.Length <= 200 ? body : body[..200] + "...";

    /// <summary>The full invariant: <see cref="NoServerError"/> then <see cref="StableCode"/>.</summary>
    public static void Invariant(ChannelResult r, string? submittedSql = null)
    {
        NoServerError(r, submittedSql);
        StableCode(r);
    }
}
