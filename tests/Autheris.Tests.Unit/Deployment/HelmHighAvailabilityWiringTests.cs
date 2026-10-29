using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Deployment;

/// <summary>
/// ARCH-MNT-06-FU-04 (WAVE3-STARTUP-SC-03/04): the chart renders the four <c>Gateway__HighAvailability__*</c> env vars from values and fails
/// the render when an NF-HA-01 inequality is violated or a value is unset, non-numeric or not positive. Sources and values are checked as
/// text; when <c>helm</c> is installed the rendered manifests are verified as well (CI job <c>helm-render</c> runs verify-hardening.sh).
/// </summary>
[Trait("Category", "Security")]
public sealed class HelmHighAvailabilityWiringTests
{
    private static readonly string[] EnvKeys = ["DrainDelaySeconds", "ShutdownTimeoutSeconds", "QueryTimeoutSeconds", "TerminationGracePeriodSeconds"];
    private const string BaseSet = " --set gateway.allowedHosts[0]=gw.example";

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static string Chart() => Path.Combine(RepoRoot(), "deploy", "helm", "autheris");

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Chart(), relative));

    private static int Number(string values, string key) =>
        int.Parse(Regex.Match(values, @"^\s*" + Regex.Escape(key) + @":\s*(\d+)\s*$", RegexOptions.Multiline).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Deployment_RendersHighAvailabilityEnv()
    {
        var helpers = Read("templates/_helpers.tpl");
        var deployment = Read("templates/deployment.yaml");

        deployment.ShouldContain("include \"autheris.highAvailabilityEnv\" .");
        foreach (var key in EnvKeys)
        {
            helpers.ShouldContain("name: Gateway__HighAvailability__" + key);
        }

        // WAVE3-STARTUP-SC-04: values only, no free-text sources
        helpers.ShouldContain(".Values.gateway.queryTimeoutSeconds");
        helpers.ShouldContain(".Values.terminationGracePeriodSeconds");

        if (!HelmAvailable())
        {
            return;
        }

        var rendered = RunHelm("template rel " + Chart() + BaseSet);
        foreach (var key in EnvKeys)
        {
            rendered.ShouldContain("name: Gateway__HighAvailability__" + key);
        }

        rendered.ShouldContain("value: \"" + Number(Read("values.yaml"), "queryTimeoutSeconds") + "\"");
    }

    [Fact]
    public void Template_FailsClosed_OnBothInequalitiesAndInvalidNumbers()
    {
        var helpers = Read("templates/_helpers.tpl");

        helpers.ShouldContain("NF-HA-01a");
        helpers.ShouldContain("NF-HA-01b");
        helpers.ShouldContain("lt $shutdown (add $query 10)");
        helpers.ShouldContain("lt $grace (add (mul 2 $drain) $shutdown 10)");
        helpers.ShouldContain("le $f 0.0");
        // no silent default to a passing value
        Read("templates/deployment.yaml").ShouldNotContain("| default 5");
        Read("templates/deployment.yaml").ShouldNotContain("| default 60");
    }

    [Theory]
    [InlineData("values.yaml")]
    [InlineData("values.production.yaml")]
    public void Values_SatisfyNfHa01(string file)
    {
        var (drain, shutdown, query, grace) = Resolve(file);

        shutdown.ShouldBeGreaterThanOrEqualTo(query + 10);
        grace.ShouldBeGreaterThanOrEqualTo((2 * drain) + shutdown + 10);
        // application ranges (DegradedModeOptions)
        drain.ShouldBeInRange(1, 30);
        query.ShouldBeInRange(5, 120);
        shutdown.ShouldBeInRange(10, 180);
        grace.ShouldBeInRange(20, 300);
    }

    [Fact]
    public void Render_GraceTooShort_Fails() => AssertRenderFails("--set terminationGracePeriodSeconds=30", "NF-HA-01b");

    [Fact]
    public void Render_ShutdownBelowQueryPlus10_Fails() => AssertRenderFails("--set gateway.queryTimeoutSeconds=25", "NF-HA-01a");

    [Fact]
    public void Render_NonPositiveDrain_Fails() => AssertRenderFails("--set gateway.drainDelaySeconds=0", "gateway.drainDelaySeconds must be a positive");

    [Fact]
    public void Render_NonNumericShutdown_Fails() => AssertRenderFails("--set-string gateway.shutdownTimeoutSeconds=abc", "gateway.shutdownTimeoutSeconds must be a positive");

    [Theory]
    [InlineData("gateway.drainDelaySeconds=31", "gateway.drainDelaySeconds must be between 1 and 30")]
    [InlineData("gateway.queryTimeoutSeconds=121", "gateway.queryTimeoutSeconds must be between 5 and 120")]
    [InlineData("gateway.shutdownTimeoutSeconds=181", "gateway.shutdownTimeoutSeconds must be between 10 and 180")]
    [InlineData("terminationGracePeriodSeconds=301", "terminationGracePeriodSeconds must be between 20 and 300")]
    public void Render_ValueAboveApplicationRange_Fails(string set, string expected) => AssertRenderFails("--set " + set, expected);

    [Fact]
    public void Template_DeclaresApplicationRanges_MatchingHighAvailabilityOptions()
    {
        // HighAvailabilityOptions [Range] attributes: drain 1..30, query 5..120, shutdown 10..180, grace 20..300
        var wiring = Read("templates/_helpers.tpl") + Read("templates/deployment.yaml");

        wiring.ShouldContain("\"value\" .Values.gateway.drainDelaySeconds \"min\" 1 \"max\" 30");
        wiring.ShouldContain("\"value\" .Values.gateway.queryTimeoutSeconds \"min\" 5 \"max\" 120");
        wiring.ShouldContain("\"value\" .Values.gateway.shutdownTimeoutSeconds \"min\" 10 \"max\" 180");
        wiring.ShouldContain("\"value\" .Values.terminationGracePeriodSeconds \"min\" 20 \"max\" 300");
    }

    [Fact]
    public void VerifyHardeningScript_AssertsHaWiring()
    {
        // the CI helm-render job runs this script, so the render assertions cannot pass vacuously in CI
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "scripts", "verify-hardening.sh"));

        foreach (var key in EnvKeys)
        {
            script.ShouldContain("Gateway__HighAvailability__${ha_key}");
            script.ShouldContain(key);
        }

        script.ShouldContain("--set terminationGracePeriodSeconds=30");
        script.ShouldContain("NF-HA-01a");
        script.ShouldContain("NF-HA-01b");
    }

    private static (int Drain, int Shutdown, int Query, int Grace) Resolve(string file)
    {
        // values.production.yaml overrides values.yaml; keys it does not set fall back to the base file
        var baseValues = Read("values.yaml");
        var values = Read(file);
        int Get(string key) => Regex.IsMatch(values, @"^\s*" + key + @":\s*\d+\s*$", RegexOptions.Multiline) ? Number(values, key) : Number(baseValues, key);
        return (Get("drainDelaySeconds"), Get("shutdownTimeoutSeconds"), Get("queryTimeoutSeconds"), Get("terminationGracePeriodSeconds"));
    }

    private static void AssertRenderFails(string extra, string expected)
    {
        if (!HelmAvailable())
        {
            return;
        }

        var (exit, error) = TryHelm("template rel " + Chart() + BaseSet + " " + extra);
        exit.ShouldNotBe(0);
        error.ShouldContain(expected);
    }

    private static bool HelmAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("helm", "version --short") { RedirectStandardOutput = true, RedirectStandardError = true });
            process!.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (int Exit, string Error) TryHelm(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("helm", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.ExitCode, error);
    }

    private static string RunHelm(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("helm", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        process.ExitCode.ShouldBe(0, error);
        return output;
    }
}
