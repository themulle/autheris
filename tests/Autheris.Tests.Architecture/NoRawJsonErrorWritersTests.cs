using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// NL-SC-20/23 (O-5b step 2, WP-7): an HTTP error body is written only by <c>ApiProblems</c>, <c>GatewayProblemWriter</c> (and the MCP
/// executor shape <c>McpToolError</c>, which is no HTTP writer). A raw JSON writer (<c>Response.WriteAsync</c>, <c>WriteAsJsonAsync</c>,
/// <c>Results.Json</c>) whose payload looks like an error (<c>error</c>/<c>errors</c>/<c>message</c>/<c>detail</c>/<c>title</c> members) is
/// rejected unless it sits in the explicit, reasoned allowlist <see cref="Allowed"/> (path, exact count, reason). Success payloads are not
/// matched because only error-like member names count. The scan is fail-closed: it throws when it covers nothing.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class NoRawJsonErrorWritersTests
{
    private static readonly string[] WriterFiles =
    [
        "src/Autheris.Api/Errors/ApiProblems.cs",
        "src/Autheris.Api/Middleware/GatewayProblemWriter.cs",
        "src/Autheris.Application/Mcp/Services/McpToolError.cs"
    ];

    /// <summary>Path, exact number of raw error-like writers, and the reason (an external protocol shape or a problem writer with fixed texts).</summary>
    internal static readonly IReadOnlyList<(string Path, int Count, string Reason)> Allowed =
    [
        ("src/Autheris.Api/Endpoints/McpEndpoints.cs", 1, "JSON-RPC 2.0 batch error object (protocol allowlist)"),
        ("src/Autheris.Api/Endpoints/ODataEndpoints.Query.cs", 6, "OData v4 JSON error object (protocol allowlist)"),
        ("src/Autheris.Api/Endpoints/ODataEndpoints.cs", 1, "OData v4 JSON error object, fixed message (protocol allowlist)"),
        ("src/Autheris.Api/Security/BasicAuthenticationHandler.cs", 2, "OData basic-auth challenge object and the fixed-text diagnostics problem of the auth handler"),
        ("src/Autheris.Api/Endpoints/ScimFilterGuard.cs", 1, "SCIM 2.0 Error message (RFC 7644 section 3.12), fixed text (protocol allowlist)"),
        ("src/Autheris.Api/Endpoints/ScimMassChangeResult.cs", 1, "SCIM 2.0 Error message (RFC 7644 section 3.12), fixed text with Retry-After (protocol allowlist)"),
        ("src/Autheris.Api/Endpoints/IcebergRestCatalogEndpoints.cs", 1, "Iceberg REST catalog ErrorModel (protocol allowlist)"),
        ("src/Autheris.Api/Endpoints/BackstageEndpoints.cs", 1, "Backstage ErrorResponseBody (protocol allowlist)"),
        ("src/Autheris.Api/Endpoints/EnvoyExtAuthzEndpoints.cs", 1, "Envoy ext_authz denied-response pass-through, text sanitised (protocol allowlist)"),
        ("src/Autheris.Api/Endpoints/WebSqlEndpoints.Governance.cs", 2, "Trino QueryResults.error inside a 200 result and the fixed-text execution timeout problem with its own type URI")
    ];

    [GeneratedRegex(@"(?:Response\.WriteAsync|WriteAsJsonAsync|Results\.Json)\s*\(")]
    private static partial Regex Writer();

    /// <summary>Non-2xx result factories (Results/TypedResults); <c>SafeErrorResults.X</c> and <c>ApiProblems.X</c> are not matched.</summary>
    [GeneratedRegex(@"(?<![\w.])(?:Typed)?Results\.(?:BadRequest|NotFound|Conflict|UnprocessableEntity|Unauthorized|Forbid|Problem)\s*\(")]
    private static partial Regex ResultsError();

    [GeneratedRegex(@"\b(?:errors?|message|detail|title)\s*=(?!=)|\\?""(?:errors?|message|detail|title)\\?""|\bnew\s*\{\s*error\b|\{\s*message\s*,|\berror\s*,")]
    private static partial Regex ErrorLike();

    [Fact]
    public void No_Raw_Json_Error_Writers_Outside_The_Problem_Writers_And_Protocol_Allowlist()
    {
        var files = RepositoryScan.ProductionFiles;
        files.Count.ShouldBeGreaterThan(500, "the scan must cover the production tree");

        var counts = Measure(files.Select(f => (f.RelativePath, f.Text)));
        var problems = new List<string>();
        foreach (var (path, count) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var allowed = Allowed.Where(a => a.Path == path).Select(a => a.Count).DefaultIfEmpty(0).Single();
            if (count != allowed)
            {
                problems.Add($"{path}: {count} raw error-like JSON writer(s), allowlist {allowed}. Use ApiProblems (a new protocol shape needs a Phase 3 note and an allowlist entry).");
            }
        }

        foreach (var (path, _, _) in Allowed.Where(a => !counts.ContainsKey(a.Path)))
        {
            problems.Add($"{path}: stale allowlist entry; remove it");
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Allowlist_Entries_Are_Live_Unique_And_Reasoned()
    {
        var paths = RepositoryScan.ProductionFiles.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, count, reason) in Allowed)
        {
            paths.ShouldContain(path);
            count.ShouldBeGreaterThan(0);
            reason.ShouldNotBeNullOrWhiteSpace();
        }

        Allowed.Select(a => a.Path).Distinct().Count().ShouldBe(Allowed.Count);
        foreach (var writer in WriterFiles)
        {
            paths.ShouldContain(writer);
        }
    }

    [Fact]
    public void Guard_Detects_Planted_Raw_Error_Writers()
    {
        string[] planted =
        [
            "await context.Response.WriteAsync(JsonSerializer.Serialize(new { errors = new[] { new { message = \"x\" } } }));",
            "await context.Response.WriteAsync(\"{\\\"errors\\\":[{\\\"message\\\":\\\"x\\\"}]}\");",
            "await context.Response.WriteAsJsonAsync(new { error = \"x\" }, ct);",
            "return Results.Json(new { error = \"x\" }, statusCode: 400);",
            "return Results.Json(new { message = ex.Message }, statusCode: 500);",
            "var body = new Dictionary<string, object?> { [\"error\"] = \"x\" };\nawait response.WriteAsJsonAsync(body, ct);",
            "await response.WriteAsJsonAsync(new { error, supported }, ct);"
        ];

        foreach (var text in planted)
        {
            Measure([("src/Planted.cs", text)]).ShouldContainKeyAndValue("src/Planted.cs", 1, text);
        }

        Measure([("src/Autheris.Api/Errors/ApiProblems.cs", planted[0])]).ShouldBeEmpty("the problem writers are exempt");
    }

    [Fact]
    public void Guard_Detects_Planted_Results_Error_Factories_And_Unparseable_Calls()
    {
        string[] planted =
        [
            "return Results.BadRequest($\"Invalid table '{name}'\");",
            "return Results.BadRequest(\"bad\");",
            "return Results.NotFound(new { Message = \"x\" });",
            "return TypedResults.NotFound(new { error = \"x\" });",
            "return Results.Conflict(new ErrorDto(\"x\"));",
            "return Results.UnprocessableEntity(new { detail = 1 });",
            "return Results.Unauthorized(new { x = 1 });",
            "return Results.BadRequest(new TotpVerifyResult(false, \"x\"));",
            "return Results.Problem(\"x\");",
            // Unmatched paren (verbatim string hides the closing quote): fail closed.
            "await response.WriteAsJsonAsync(@\"unterminated \" \"\"x\"\" "
        ];

        foreach (var text in planted)
        {
            Measure([("src/Planted.cs", text)]).ShouldContainKeyAndValue("src/Planted.cs", 1, text);
        }
    }

    [Fact]
    public void Guard_Ignores_Governed_And_Bodyless_Results()
    {
        Measure([("src/Ok.cs", "return Results.NotFound();\nreturn Results.BadRequest(result);\nreturn SafeErrorResults.BadRequest(\"X\", \"y\", ctx);\nreturn ApiProblems.NotFound(\"X\", \"y\", ctx);\nreturn Results.Problem(detail: \"x\", statusCode: 500);\nreturn Results.StatusCode(403);")]).ShouldBeEmpty();
    }

    [Fact]
    public void Guard_Ignores_Success_Payloads_And_Comments()
    {
        Measure([("src/Ok.cs", "await response.WriteAsJsonAsync(new { columns, rows, rowCount }, ct);\nreturn Results.Json(new { items = list });")]).ShouldBeEmpty();
        Measure([("src/Ok.cs", "return Results.Json(new { status = \"Accepted\", message = \"done\" }, statusCode: StatusCodes.Status202Accepted);")]).ShouldBeEmpty();
        Measure([("src/Ok.cs", "// await context.Response.WriteAsync(\"{\\\"errors\\\":1}\");\nreturn Results.Ok();")]).ShouldBeEmpty();
    }

    [Fact]
    public void Guard_Is_Fail_Closed_When_Nothing_Is_Scanned()
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
            if (WriterFiles.Contains(path, StringComparer.Ordinal))
            {
                continue;
            }

            var count = Count(text);
            if (count > 0)
            {
                result[path] = count;
            }
        }

        if (scanned == 0)
        {
            throw new InvalidOperationException("The raw JSON error writer scan covered nothing; refusing to pass vacuously.");
        }

        return result;
    }

    private static int Count(string text)
    {
        var code = StripLineComments(text);
        var count = 0;
        foreach (Match m in Writer().Matches(code))
        {
            var open = m.Index + m.Length - 1;
            var close = FindClosingParen(code, open);
            if (close < 0)
            {
                // Fail closed: a call the scanner cannot delimit (verbatim/raw string, broken source) is a violation, never skipped.
                count++;
                continue;
            }

            var args = code[(open + 1)..close];
            var probe = args;
            var identifier = Regex.Match(args.TrimStart(), @"^([A-Za-z_]\w*)\s*(?:,|$)");
            if (identifier.Success)
            {
                // A payload variable: look at its definition in the preceding statements.
                var back = code[Math.Max(0, m.Index - 900)..m.Index];
                var def = back.LastIndexOf(identifier.Groups[1].Value + " =", StringComparison.Ordinal);
                if (def >= 0)
                {
                    probe += back[def..];
                }
            }

            // A 2xx payload is a success body even when it carries a status message.
            if (!Regex.IsMatch(args, @"Status2\d\d") && ErrorLike().IsMatch(probe))
            {
                count++;
            }
        }

        foreach (Match m in ResultsError().Matches(code))
        {
            var open = m.Index + m.Length - 1;
            var close = FindClosingParen(code, open);
            if (close < 0)
            {
                count++;
                continue;
            }

            // A non-2xx Results.X(...) whose argument is an object (new ...) or a (interpolated) string literal is a legacy/echo body.
            var args = code[(open + 1)..close].TrimStart();
            if (Regex.IsMatch(args, @"^(?:new\b|\$?@?""|@\$"")"))
            {
                count++;
            }
        }

        return count;
    }

    private static string StripLineComments(string text) => Regex.Replace(text, @"(?m)^\s*//.*$", string.Empty);

    private static int FindClosingParen(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"')
            {
                i++;
                while (i < code.Length && code[i] != '"')
                {
                    if (code[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
