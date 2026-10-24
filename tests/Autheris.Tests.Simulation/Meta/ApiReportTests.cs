using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Autheris.Tests.Simulation.Meta;

/// <summary>Tests of <c>.github/scripts/sim-api-report.py</c> as an external process (python3 -I, no network, no host).</summary>
public sealed class ApiReportTests : IDisposable
{
    private const string MaskCanary = "SIMCANARY-MASK-7f3a91";
    private const string TenantCanary = "SIMCANARY-TENANT-9b1c42";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sim-apireport-" + Guid.NewGuid().ToString("N"));

    public ApiReportTests()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "ApiFuzz"));
        Directory.CreateDirectory(Path.Combine(_tmp, "KnownGaps", "drafts"));
        File.WriteAllText(Path.Combine(_tmp, "canaries.txt"), $"{MaskCanary}\n{TenantCanary}\n");
        File.WriteAllText(GapsPath, """{"v":1,"gaps":[]}""");
    }

    public void Dispose() => Directory.Delete(_tmp, true);

    private string GapsPath => Path.Combine(_tmp, "ApiFuzz", "gaps.json");

    private string OutDir => Path.Combine(_tmp, "out");

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
        }
    }

    private static string Checker => Path.Combine(RepoRoot, ".github", "scripts", "sim-api-report.py");

    private static string Fixture(string name) => Path.Combine(RepoRoot, "tests", "Autheris.Tests.Simulation", "ApiFuzz", "fixtures", name);

    private static (int Exit, string Output) Python(params string[] args)
    {
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(Checker);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["AUTHERIS_SIM_SEED"] = "20261024";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            p.Kill(true);
            throw new TimeoutException("sim-api-report.py did not finish in 120 s");
        }

        return (p.ExitCode, output.Result + error.Result);
    }

    private (int Exit, string Output) Check(string[] hars, string[]? junits = null, string? gaps = null, string? canaries = null)
    {
        var args = new List<string> { "--har" };
        args.AddRange(hars);
        if (junits is { Length: > 0 })
        {
            args.Add("--junit");
            args.AddRange(junits);
        }

        args.AddRange(["--canaries", canaries ?? Path.Combine(_tmp, "canaries.txt"), "--gaps", gaps ?? GapsPath, "--out", OutDir]);
        return Python([.. args]);
    }

    private string WriteHar(string name, params (int Status, string Body, (string Name, string Value)[]? Headers)[] entries)
    {
        var array = new JsonArray();
        foreach (var (status, body, headers) in entries)
        {
            var responseHeaders = new JsonArray { new JsonObject { ["name"] = "content-type", ["value"] = "application/json" } };
            foreach (var (hn, hv) in headers ?? [])
            {
                responseHeaders.Add(new JsonObject { ["name"] = hn, ["value"] = hv });
            }

            array.Add(new JsonObject
            {
                ["request"] = new JsonObject
                {
                    ["method"] = "GET",
                    ["url"] = "http://127.0.0.1:5071/odata/v4/default/main/sim_accounts",
                    ["headers"] = new JsonArray { new JsonObject { ["name"] = "X-Test-Roles", ["value"] = "Reader" } },
                },
                ["response"] = new JsonObject
                {
                    ["status"] = status,
                    ["headers"] = responseHeaders,
                    ["content"] = new JsonObject { ["mimeType"] = "application/json", ["text"] = body },
                },
            });
        }

        var path = Path.Combine(_tmp, name);
        File.WriteAllText(path, new JsonObject { ["log"] = new JsonObject { ["version"] = "1.2", ["entries"] = array } }.ToJsonString());
        return path;
    }

    private static (int Status, string Body, (string, string)[]? Headers) Ok(int status = 200, string body = "{}") => (status, body, null);

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-18, SEC-SIM-19
    public void ApiReport_SelfTestDetectsPlantedFaults()
    {
        var (exit, output) = Python("--self-test");
        exit.ShouldBe(0, output);
        output.ShouldContain("self-test passed");

        // the committed fixtures through the real command line
        (string Name, int Expected)[] expected = [("clean.har", 0), ("planted-5xx.har", 1), ("planted-canary.har", 1), ("planted-stacktrace.har", 1), ("malformed.har", 2)];
        foreach (var (name, code) in expected)
        {
            File.Exists(Fixture(name)).ShouldBeTrue(name);
            Check([Fixture(name)]).Exit.ShouldBe(code, name);
        }

        Check([Fixture("planted-5xx.har")]).Output.ShouldContain("ServerError");
        Check([Fixture("planted-canary.har")]).Output.ShouldContain("PolicyLeak");
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-19
    public void ApiReport_RejectsMalformedAndOversizedInput()
    {
        Check([Fixture("malformed.har")]).Exit.ShouldBe(2);

        var truncated = Path.Combine(_tmp, "truncated.har");
        File.WriteAllText(truncated, """{"log":{"version":"1.2","entries":[""");
        Check([truncated]).Exit.ShouldBe(2);

        var unknownVersion = Path.Combine(_tmp, "v9.har");
        File.WriteAllText(unknownVersion, """{"log":{"version":"9.9","entries":[]}}""");
        Check([unknownVersion]).Exit.ShouldBe(2);

        var oversized = Path.Combine(_tmp, "big.har");
        using (var fs = new FileStream(oversized, FileMode.Create))
        {
            fs.SetLength((50L * 1024 * 1024) + 1);
        }

        var (exit, output) = Check([oversized]);
        exit.ShouldBe(2);
        output.ShouldContain("50 MB");

        Check([Path.Combine(_tmp, "missing.har")]).Exit.ShouldBe(2);
        Check([Fixture("clean.har")], canaries: Path.Combine(_tmp, "missing-canaries.txt")).Exit.ShouldBe(2);
        File.WriteAllText(Path.Combine(_tmp, "empty-canaries.txt"), "\n");
        Check([Fixture("clean.har")], canaries: Path.Combine(_tmp, "empty-canaries.txt")).Exit.ShouldBe(2);
        Check([Fixture("clean.har")], junits: [Path.Combine(_tmp, "missing.xml")]).Exit.ShouldBe(2);

        var doctype = Path.Combine(_tmp, "doctype.xml");
        File.WriteAllText(doctype, "<!DOCTYPE x [<!ENTITY a 'b'>]><testsuites><testsuite><testcase name='GET /a'/></testsuite></testsuites>");
        Check([Fixture("clean.har")], junits: [doctype]).Exit.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-18
    public void ApiReport_RejectsSecurityClassInGapsFile()
    {
        File.WriteAllText(Path.Combine(_tmp, "KnownGaps", "drafts", "SIM-GAP-901.md"), "draft");
        var clean = Fixture("clean.har");
        string Gaps(string kind, string cls, string extra = "") =>
            $$"""{"v":1,"gaps":[{"id":"SIM-GAP-901","kind":"{{kind}}","class":"{{cls}}","draft":"SIM-GAP-901.md"{{extra}}}]}""";

        foreach (var (gaps, reason) in new[]
                 {
                     (Gaps("ServerError", "x"), "ServerError kind"),
                     (Gaps("PolicyLeak", "x"), "PolicyLeak kind"),
                     (Gaps("Crash", "x"), "Crash kind"),
                     (Gaps("Hang", "x"), "Hang kind"),
                     (Gaps("SchemaNonConformance", "ServerError:500:E"), "ServerError class"),
                     (Gaps("Documented4xx", "500:E"), "5xx class"),
                     (Gaps("Unknown", "x"), "unknown kind"),
                     (Gaps("SchemaNonConformance", "x", ",\"extra\":1"), "unknown key"),
                 })
        {
            File.WriteAllText(GapsPath, gaps);
            var (exit, output) = Check([clean]);
            exit.ShouldBe(2, $"{reason}: {output}");
        }

        File.WriteAllText(GapsPath, """{"v":1,"gaps":[],"extra":1}""");
        Check([clean]).Exit.ShouldBe(2);

        // a gap without an issue draft is refused
        File.WriteAllText(GapsPath, Gaps("SchemaNonConformance", "x").Replace("901", "902", StringComparison.Ordinal));
        Check([clean]).Exit.ShouldBe(2);

        // the committed gaps file loads and its gap reproduces from the committed JUnit fixture (no 5xx, no leak in the clean HAR)
        var committed = Path.Combine(RepoRoot, "tests", "Autheris.Tests.Simulation", "ApiFuzz", "api-known-gaps.json");
        var (okExit, okOutput) = Check([clean], junits: [Fixture("schema-nonconformance.xml")], gaps: committed);
        okExit.ShouldBe(0, okOutput);
        // without the reproduction the gap is stale (strict xfail)
        Check([clean], gaps: committed).Exit.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-19
    public void ApiReport_FailsOnEmptyHar()
    {
        var empty = Path.Combine(_tmp, "empty.har");
        File.WriteAllText(empty, """{"log":{"version":"1.2","entries":[]}}""");

        var (exit, output) = Check([empty]);

        exit.ShouldNotBe(0, output);
        output.ShouldContain("no requests");
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-14
    public void ApiReport_ScrubsHeadersAndCanariesFromOutput()
    {
        var har = WriteHar("scrub.har",
            Ok(),
            (500, $$"""{"code":"E","detail":"{{MaskCanary}} and {{TenantCanary}} | `x` <b>bold</b> {{new string('z', 400)}}"}""", [("Authorization", "Bearer super-secret-token"), ("X-Test-Roles", "Reader"), ("Set-Cookie", "sid=abc")]));

        // a canary in a request path ends up in the repro line, which must be redacted
        File.WriteAllText(har, File.ReadAllText(har).Replace("sim_accounts", MaskCanary, StringComparison.Ordinal));

        var (exit, _) = Check([har]);

        exit.ShouldBe(1);
        var text = File.ReadAllText(Path.Combine(OutDir, "api-report.md")) + File.ReadAllText(Path.Combine(OutDir, "api-results.jsonl"));
        text.ShouldNotContain("SIMCANARY");
        text.ShouldNotContain("super-secret-token");
        text.ShouldNotContain("Authorization");
        text.ShouldNotContain("X-Test");
        text.ShouldNotContain("sid=abc");
        text.ShouldNotContain("<b>");
        text.ShouldContain("<canary>");
        text.ShouldNotContain("zzzz", Case.Sensitive); // response excerpts are never emitted
    }

    [Fact]
    public void ApiReport_DedupesByStatusAndCode()
    {
        var har = WriteHar("dedupe.har",
            Ok(),
            Ok(400, """{"code":"INVALID_FILTER"}"""),
            Ok(400, """{"error":{"code":"INVALID_FILTER"}}"""),
            Ok(400, """{"code":"INVALID_FILTER","detail":"other text"}"""),
            Ok(400, """{"code":"BAD_REQUEST"}"""),
            Ok(403, """{"code":"INVALID_FILTER"}"""),
            Ok(404, "not json"));

        var (exit, output) = Check([har]);

        exit.ShouldBe(0, output);
        var rows = File.ReadAllLines(Path.Combine(OutDir, "api-results.jsonl")).Select(l => JsonNode.Parse(l)!).ToList();
        rows.Count.ShouldBe(4, "400:INVALID_FILTER, 400:BAD_REQUEST, 403:INVALID_FILTER, 404:(none)");
        var grouped = rows.Single(r => (string?)r["errorClass"] == "400:INVALID_FILTER");
        ((int?)grouped["count"]).ShouldBe(3);
        ((string?)grouped["repro"]).ShouldStartWith("st run <schema> --seed 20261024 --include-path /default/main/sim_accounts --include-method GET");
        rows.ShouldAllBe(r => !string.IsNullOrEmpty((string?)r["repro"]));
        JsonSerializer.Serialize(rows).ShouldNotContain("5xx");
    }
}
