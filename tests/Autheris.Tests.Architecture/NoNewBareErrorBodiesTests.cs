using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit.Abstractions;

namespace Autheris.Tests.Architecture;

/// <summary>
/// NL-SC-05 / NL-T-05 (O-5b step 1): a response body shaped <c>{ error = ... }</c> is the bare error shape that ProblemDetails
/// (<c>ApiProblems</c>) replaces. The existing sites are a shrink-only baseline (Baselines/bare-error-body-counts.txt, one
/// <c>path|count</c> line per file, no wildcards); a new site or a grown count fails the build, a lower count yields a hint, and a
/// baseline entry without any site left fails (remove it). Detected shapes: single-line and multi-line anonymous objects with a
/// top-level <c>error</c> member, and dictionary bodies (<c>["error"] = ...</c>, <c>{ "error", ... }</c>). Audit payloads that are
/// not responses are excluded by the explicit, reasoned list <see cref="Exclusions"/> (path plus exact site text, never a pattern).
/// The scan covers every file under src/ (including Autheris.Extensions); it throws instead of passing when nothing is scanned.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class NoNewBareErrorBodiesTests(ITestOutputHelper output)
{
    private const string BaselineFile = "bare-error-body-counts.txt";

    /// <summary>Sites that carry the member but are not response bodies. Key: path|normalized site text; value: reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> Exclusions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["src/Autheris.Api/Endpoints/CallerAttributeAdminEndpoints.cs|new { error = \"Forbidden: missing required role\" }"] =
            "Audit DetailsJson payload of a denied admin call, not a response body",
        ["src/Autheris.Api/Endpoints/VirtualFilterEndpoints.cs|new { error = \"Forbidden: missing required role\", requiredRole = requiredRole.ToString() }"] =
            "Audit DetailsJson payload of a denied virtual filter call, not a response body",
        ["src/Autheris.Application/VirtualFilters/EffectiveFilterInspector.cs|new { error = \"Forbidden: missing required role for effective-filters\" }"] =
            "Audit DetailsJson payload of a denied inspection, not a response body",
        ["src/Autheris.Application/Sql/Services/FederatedDuckDbExecutionService.cs|[\"error\"] = errorText"] =
            "Audit details dictionary of a rejected federated statement, not a response body",
        ["src/Autheris.Api/Endpoints/WebSqlEndpoints.Governance.cs|[\"error\"] = new Dictionary<string, object> { [\"type\"] = \"string\" },"] =
            "OpenAPI schema property declaration, not a response body"
    };

    /// <summary>
    /// NL-SC-29: protocol-mandated bodies whose shape is dictated by an external spec and therefore stay as they are. Key: path;
    /// each entry is a site-text prefix (line independent), the exact number of live sites it covers and the spec. An addition needs a
    /// new Phase 3 note. Messages of these sites are fixed texts (no input echo), except the OData entity-set not-found message.
    /// </summary>
    internal static readonly IReadOnlyList<(string Path, string SitePrefix, int Count, string Spec)> ProtocolAllowlist =
    [
        ("src/Autheris.Api/Endpoints/McpEndpoints.cs", "new { jsonrpc = \"2.0\"", 1, "JSON-RPC 2.0 section 5.1 error object"),
        ("src/Autheris.Api/Endpoints/ODataEndpoints.Query.cs", "new { error = new { code", 6, "OData v4 JSON section 21 error object"),
        ("src/Autheris.Extensions/OData/ODataResponseFormatter.cs", "new { error = new { code, message }", 1, "OData v4 JSON section 21 error object"),
        ("src/Autheris.Api/Security/BasicAuthenticationHandler.cs", "new { error = new { code = \"UNAUTHORIZED\"", 1, "OData v4 JSON error object for the OData basic-auth challenge"),
        ("src/Autheris.Api/Endpoints/ODataEndpoints.cs", "new { error = new { code = \"NotFound\"", 1, "OData v4 JSON error object (reshaped, fixed message)"),
        ("src/Autheris.Api/Endpoints/IcebergRestCatalogEndpoints.cs", "new { error = new { message, type = code", 1, "Iceberg REST catalog ErrorModel {message,type,code}"),
        ("src/Autheris.Api/Endpoints/BackstageEndpoints.cs", "new { error = new { name = \"NotFoundError\"", 1, "Backstage ErrorResponseBody {error{name,message},response{statusCode}} (reshaped, fixed message)"),
        ("src/Autheris.Api/Endpoints/EnvoyExtAuthzEndpoints.cs", "new { error = body, status = statusCode }", 1, "Envoy ext_authz denied-response pass-through"),
        ("src/Autheris.Api/Endpoints/WebSqlEndpoints.Governance.cs", "[\"error\"] = trinoError", 1, "Trino in-protocol QueryResults.error (a spec member of a 200 result, not an HTTP-level error body)")
    ];

    [GeneratedRegex(@"\bnew\s*\{")]
    private static partial Regex AnonymousObjectStart();

    [GeneratedRegex(@"\[\s*""error""\s*\]\s*=(?!=)|\{\s*""error""\s*,")]
    private static partial Regex DictionaryError();

    [GeneratedRegex(@"^\s*error\s*=(?!=)")]
    private static partial Regex ErrorMember();

    [Fact]
    public void No_New_Bare_Error_Bodies()
    {
        var measured = Measure(RepositoryScan.ProductionFiles.Select(f => (f.RelativePath, f.Text)));
        // Non-vacuity: the scan must really see the known multi-line and dictionary sites.
        RepositoryScan.ProductionFiles.Count.ShouldBeGreaterThan(500);
        measured.Keys.ShouldContain("src/Autheris.Api/Endpoints/DbtEndpoints.cs");
        measured.Keys.ShouldContain("src/Autheris.Api/Endpoints/SqlErrorResponse.cs");

        if (Environment.GetEnvironmentVariable("AUTHERIS_UPDATE_BASELINES") == "1")
        {
            var path = Path.Combine(RepositoryScan.RepoRoot, "tests", "Autheris.Tests.Architecture", "Baselines", BaselineFile);
            var header = "# O-5b step 1 (NL-SC-05): bare { error = ... } response bodies per src file. Format: path|count. Counts may only fall; no wildcards.\n";
            File.WriteAllText(path, header + string.Join("\n", measured.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key}|{m.Value}")) + "\n");
        }

        var baseline = RepositoryScan.ParseCountBaseline(RepositoryScan.ReadBaseline(BaselineFile));
        var (problems, hints) = Evaluate(measured, baseline);
        foreach (var hint in hints)
        {
            output.WriteLine("HINT (not a failure): " + hint);
        }

        problems.ShouldBeEmpty("Do not add bare { error = ... } response bodies; return ApiProblems (ProblemDetails with a governed code) instead. The baseline may only shrink.");
    }

    [Fact]
    public void Baseline_Has_No_Wildcards_And_Sorted_Unique_Src_Paths()
    {
        var lines = RepositoryScan.ReadBaseline(BaselineFile);
        lines.ShouldNotBeEmpty();
        foreach (var line in lines)
        {
            var key = line[..line.LastIndexOf('|')];
            key.ShouldStartWith("src/");
            key.ShouldEndWith(".cs");
            key.ShouldNotContain("*");
            key.ShouldNotContain("?");
        }

        RepositoryScan.ParseCountBaseline(lines).Values.ShouldAllBe(v => v > 0);
    }

    [Fact]
    public void Ratchet_Fails_For_A_Synthetic_New_Site_And_A_Stale_Baseline_Entry()
    {
        var measured = Measure([("src/X/New.cs", "return Results.BadRequest(new { error = \"x\" });")]);
        measured["src/X/New.cs"].ShouldBe(1);

        Evaluate(measured, new Dictionary<string, int>()).Problems.ShouldHaveSingleItem().ShouldContain("NEW src/X/New.cs");
        Evaluate(measured, new Dictionary<string, int> { ["src/X/New.cs"] = 1 }).Problems.ShouldBeEmpty();
        Evaluate(measured, new Dictionary<string, int> { ["src/X/New.cs"] = 0 + 1, ["src/X/Gone.cs"] = 2 }).Problems.ShouldHaveSingleItem().ShouldContain("STALE src/X/Gone.cs");

        var grown = Measure([("src/X/New.cs", "a(new { error = 1 }); b(new { error = 2 });")]);
        Evaluate(grown, new Dictionary<string, int> { ["src/X/New.cs"] = 1 }).Problems.ShouldHaveSingleItem().ShouldContain("GROWN");

        var shrunk = Evaluate(measured, new Dictionary<string, int> { ["src/X/New.cs"] = 3 });
        shrunk.Problems.ShouldBeEmpty();
        shrunk.Hints.ShouldNotBeEmpty();
    }

    [Fact]
    public void Ratchet_Detects_MultiLine_And_Dictionary_Bare_Error_Shapes()
    {
        const string multiLine = "return Results.Json(\n    new\n    {\n        status = 1,\n        error = \"x\",\n        message = Foo(new { a = 1 })\n    },\n    statusCode: 400);";
        const string dictionary = "var body = new Dictionary<string, object?>\n{\n    [\"error\"] = ErrorName(s),\n    [\"message\"] = m\n};";
        const string initializer = "var body = new Dictionary<string, object> { { \"error\", \"x\" } };";

        Measure([("src/A.cs", multiLine)]).ShouldContainKeyAndValue("src/A.cs", 1);
        Measure([("src/A.cs", dictionary)]).ShouldContainKeyAndValue("src/A.cs", 1);
        Measure([("src/A.cs", initializer)]).ShouldContainKeyAndValue("src/A.cs", 1);
        Measure([("src/A.cs", "x = new { error = 1 }; y = new\n{ error\n = 2 };")]).ShouldContainKeyAndValue("src/A.cs", 2);
    }

    [Fact]
    public void Ratchet_Ignores_Non_Bodies_Comments_Strings_And_Nested_Members()
    {
        Measure([("src/A.cs", "// new { error = 1 }\n/* new { error = 2 } */\nvar s = \"new { error = 3 }\";")]).ShouldBeEmpty();
        Measure([("src/A.cs", "var s = $@\"new {{ error = 4 }} {x}\"; var raw = \"\"\"new { error = 5 }\"\"\";")]).ShouldBeEmpty();
        Measure([("src/A.cs", "return new { errorCode = 1, nested = new { x = 1 }, status = Call(error = 2) };")]).ShouldBeEmpty();
        Measure([("src/A.cs", "return new { code = \"c\", status = 400 };")]).ShouldBeEmpty();
        Measure([("src/A.cs", "var ok = a.error == b;")]).ShouldBeEmpty();
    }

    [Fact]
    public void Every_Exclusion_Matches_A_Live_Site_And_Has_A_Reason()
    {
        var live = RepositoryScan.ProductionFiles.SelectMany(f => FindAllSites(f.Text).Select(t => f.RelativePath + "|" + t)).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, reason) in Exclusions)
        {
            live.ShouldContain(key, "Stale exclusion; remove it.");
            reason.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Protocol_Mandated_Error_Bodies_Keep_Spec_Shape()
    {
        // NL-T-23 / NL-SC-29: every allowlisted site is live, the count is exact, and the spec shape is kept (no top-level bare string error).
        foreach (var (path, prefix, count, spec) in ProtocolAllowlist)
        {
            spec.ShouldNotBeNullOrWhiteSpace();
            var file = RepositoryScan.ProductionFiles.Single(f => f.RelativePath == path);
            var live = FindAllSites(file.Text).Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            live.Count.ShouldBe(count, $"Allowlist entry {path} '{prefix}' does not match the live sites; update it (a new entry needs a Phase 3 note).");
            live.ShouldAllBe(s => !Regex.IsMatch(s, "^new \\{ error = \"") , $"{path}: a protocol body must keep its spec shape, not a bare string error.");
        }

        ProtocolAllowlist.Select(a => a.Path + "|" + a.SitePrefix).Distinct().Count().ShouldBe(ProtocolAllowlist.Count);
    }

    [Fact]
    public void Scanner_Is_Fail_Closed_When_Nothing_Is_Scanned()
    {
        Should.Throw<InvalidOperationException>(() => Measure([])).Message.ShouldContain("nothing");
    }

    private static Dictionary<string, int> Measure(IEnumerable<(string Path, string Text)> files)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var scanned = 0;
        foreach (var (path, text) in files)
        {
            scanned++;
            var count = FindSites(path, text).Count;
            if (count > 0)
            {
                result[path] = count;
            }
        }

        if (scanned == 0)
        {
            throw new InvalidOperationException("The bare error body scan covered nothing; refusing to pass vacuously.");
        }

        return result;
    }

    /// <summary>The normalized site texts of a file (excluded sites removed).</summary>
    internal static List<string> FindSites(string path, string text) =>
        FindAllSites(text).Where(s => !Exclusions.ContainsKey(path + "|" + s) && !IsProtocolSite(path, s)).ToList();

    private static bool IsProtocolSite(string path, string site) =>
        ProtocolAllowlist.Any(a => a.Path == path && site.StartsWith(a.SitePrefix, StringComparison.Ordinal));

    private static List<string> FindAllSites(string text)
    {
        var code = StripComments(text);
        var sites = new List<string>();
        foreach (Match start in AnonymousObjectStart().Matches(code))
        {
            if (IsInsideStringOrComment(code, start.Index))
            {
                continue;
            }

            var open = start.Index + start.Length - 1;
            var close = FindClosing(code, open);
            if (close < 0)
            {
                continue;
            }

            if (HasTopLevelErrorMember(code, open + 1, close))
            {
                sites.Add(Normalize(code.Substring(start.Index, Math.Min(close - start.Index + 1, 200))));
            }
        }

        foreach (Match m in DictionaryError().Matches(code))
        {
            if (!IsInsideStringOrComment(code, m.Index))
            {
                var lineEnd = code.IndexOfAny(['\r', '\n'], m.Index);
                sites.Add(Normalize(code[m.Index..(lineEnd < 0 ? code.Length : lineEnd)]));
            }
        }

        return sites;
    }

    private static string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static (List<string> Problems, List<string> Hints) Evaluate(IReadOnlyDictionary<string, int> measured, IReadOnlyDictionary<string, int> baseline)
    {
        var problems = new List<string>();
        var hints = new List<string>();
        foreach (var (path, count) in measured.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!baseline.TryGetValue(path, out var ceiling))
            {
                problems.Add($"NEW {path}: {count} bare error bod{(count == 1 ? "y" : "ies")}");
            }
            else if (count > ceiling)
            {
                problems.Add($"GROWN {path}: {count} > {ceiling}");
            }
            else if (count < ceiling)
            {
                hints.Add($"{path}: {count} < {ceiling}; lower the baseline entry");
            }
        }

        foreach (var (path, _) in baseline.Where(b => !measured.ContainsKey(b.Key)).OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            problems.Add($"STALE {path}: no bare error body left; remove the baseline entry");
        }

        return (problems, hints);
    }

    private static bool HasTopLevelErrorMember(string code, int from, int to)
    {
        var depth = 0;
        var memberStart = from;
        for (var i = from; i < to; i++)
        {
            var c = code[i];
            if (c is '"' or '\'' )
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                if (ErrorMember().IsMatch(code.AsSpan(memberStart, i - memberStart).ToString()))
                {
                    return true;
                }

                memberStart = i + 1;
            }
        }

        return ErrorMember().IsMatch(code.AsSpan(memberStart, to - memberStart).ToString());
    }

    private static int FindClosing(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Returns the index of the last character of the string or char literal that starts at <paramref name="i"/> (the text end when unterminated).</summary>
    private static int SkipLiteral(string s, int i)
    {
        if (s[i] == '\'')
        {
            for (var j = i + 1; j < s.Length; j++)
            {
                if (s[j] == '\\') { j++; continue; }
                if (s[j] == '\'') return j;
            }

            return s.Length - 1;
        }

        var verbatim = i > 0 && (s[i - 1] == '@' || (i > 1 && s[i - 1] == '$' && s[i - 2] == '@') || (i > 1 && s[i - 1] == '@' && s[i - 2] == '$'));
        if (i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
        {
            var quotes = 3;
            while (i + quotes < s.Length && s[i + quotes] == '"') quotes++;
            var terminator = new string('"', quotes);
            var end = s.IndexOf(terminator, i + quotes, StringComparison.Ordinal);
            return end < 0 ? s.Length - 1 : end + quotes - 1;
        }

        for (var j = i + 1; j < s.Length; j++)
        {
            if (verbatim)
            {
                if (s[j] == '"' && j + 1 < s.Length && s[j + 1] == '"') { j++; continue; }
                if (s[j] == '"') return j;
            }
            else
            {
                if (s[j] == '\\') { j++; continue; }
                if (s[j] == '"') return j;
            }
        }

        return s.Length - 1;
    }

    /// <summary>Replaces comments by spaces (same length, newlines kept) so offsets stay valid; string-aware.</summary>
    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i);
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n' && text[i] != '\r') { sb[i] = ' '; i++; }
                i--;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 2;
                for (var j = i; j < stop; j++)
                {
                    if (text[j] != '\n' && text[j] != '\r') sb[j] = ' ';
                }

                i = stop - 1;
            }
        }

        return sb.ToString();
    }

    /// <summary>True when the offset lies inside a string or char literal (a match found by regex in text that a literal contains).</summary>
    private static bool IsInsideStringOrComment(string code, int index)
    {
        for (var i = 0; i < index; i++)
        {
            if (code[i] is '"' or '\'')
            {
                var end = SkipLiteral(code, i);
                if (index <= end)
                {
                    return true;
                }

                i = end;
            }
        }

        return false;
    }
}
