using System.Diagnostics;
using Shouldly;

namespace Autheris.Tests.Simulation.Meta;

/// <summary>Tests of <c>scripts/sim/run-api-fuzz.sh</c> as an external process: only its argument and URL validation, never Schemathesis, docker or the host.</summary>
public sealed class ApiReportScriptTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sim-apiscript-" + Guid.NewGuid().ToString("N"));

    public ApiReportScriptTests() => Directory.CreateDirectory(_tmp);

    public void Dispose() => Directory.Delete(_tmp, true);

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

    private static string Script => Path.Combine(RepoRoot, "scripts", "sim", "run-api-fuzz.sh");

    private static (int Exit, string Output) Bash(Action<ProcessStartInfo>? configure, params string[] args)
    {
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = RepoRoot };
        psi.ArgumentList.Add(Script);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        configure?.Invoke(psi);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            p.Kill(true);
            throw new TimeoutException("run-api-fuzz.sh did not finish in 60 s");
        }

        return (p.ExitCode, output.Result + error.Result);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-02
    public void ApiFuzz_RefusesNonLoopbackTargetUrl()
    {
        foreach (var good in new[] { "http://127.0.0.1:5071", "http://localhost:5071" })
        {
            Bash(null, "--check-url", good).Exit.ShouldBe(0, good);
        }

        foreach (var bad in new[]
                 {
                     "http://127.0.0.1.evil.com:5071",
                     "http://user@127.0.0.1:5071",
                     "http://127.0.0.1@evil.com:5071",
                     "http://evil.com@127.0.0.1:5071",
                     "http://[::]:5071",
                     "http://[::1]:5071",
                     "http://0.0.0.0:5071",
                     "http://127.0.0.2:5071",
                     "http://localhost.evil.com:5071",
                     "http://127.0.0.1",
                     "http://127.0.0.1:5071/odata",
                     "ftp://127.0.0.1:5071",
                     string.Empty,
                 })
        {
            Bash(null, "--check-url", bad).Exit.ShouldNotBe(0, bad);
        }

        // the full script exits before anything starts: a fake docker on PATH records any call
        var bin = Path.Combine(_tmp, "bin");
        Directory.CreateDirectory(bin);
        var marker = Path.Combine(_tmp, "docker-called");
        var fake = Path.Combine(bin, "docker");
        File.WriteAllText(fake, $"#!/usr/bin/env bash\ntouch '{marker}'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var url in new[] { "http://127.0.0.1.evil.com:5071", "http://user@127.0.0.1:5071", "http://[::]:5071", "http://0.0.0.0:5071" })
        {
            var (exit, output) = Bash(psi =>
            {
                psi.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                psi.Environment["RUNNER_TEMP"] = _tmp;
                psi.Environment["SCHEMATHESIS_IMAGE"] = "schemathesis/schemathesis:4.32.4@sha256:" + new string('a', 64);
                psi.Environment["SIM_API_BASE_URL"] = url;
            });
            exit.ShouldNotBe(0, url);
            output.ShouldContain("refusing target");
        }

        File.Exists(marker).ShouldBeFalse("docker must never be reached for a non-loopback target");
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-02
    public void ApiFuzz_SchemaServersOverrideStripped()
    {
        var input = Path.Combine(_tmp, "in.json");
        var output = Path.Combine(_tmp, "out.json");
        File.WriteAllText(input, """
            {"openapi":"3.1.0","servers":[{"url":"http://evil.example"}],"host":"evil.example","basePath":"/x","schemes":["https"],
             "paths":{"/a":{"servers":[{"url":"http://evil.example"}],"get":{"servers":[{"url":"http://evil.example"}],"responses":{"200":{"description":"OK"}}}}}}
            """);

        Bash(null, "--strip-servers", input, output).Exit.ShouldBe(0);

        var text = File.ReadAllText(output);
        text.ShouldNotContain("evil");
        text.ShouldNotContain("servers");
        text.ShouldContain("\"/a\"");
        text.ShouldContain("\"responses\"");

        File.WriteAllText(input, "[1,2]");
        Bash(null, "--strip-servers", input, output).Exit.ShouldNotBe(0);
        Bash(null, "--strip-servers", Path.Combine(_tmp, "missing.json"), output).Exit.ShouldNotBe(0);
    }

    [Fact]
    public void ApiFuzz_ScriptSelfTestAndSeedPortValidation()
    {
        var (exit, output) = Bash(null, "--self-test");
        exit.ShouldBe(0, output);
        output.ShouldContain("self-test passed");

        foreach (var (name, value) in new[] { ("AUTHERIS_SIM_SEED", "12345678901"), ("AUTHERIS_SIM_SEED", "abc"), ("SIM_API_PORT", "80"), ("SIM_API_PORT", "70000"), ("SIM_API_MAX_EXAMPLES", "0") })
        {
            var (code, text) = Bash(psi =>
            {
                psi.Environment["RUNNER_TEMP"] = _tmp;
                psi.Environment["SCHEMATHESIS_IMAGE"] = "schemathesis/schemathesis:4.32.4@sha256:" + new string('a', 64);
                psi.Environment[name] = value;
            });
            code.ShouldNotBe(0, $"{name}={value}");
            text.ShouldContain("must be");
        }
    }
}
