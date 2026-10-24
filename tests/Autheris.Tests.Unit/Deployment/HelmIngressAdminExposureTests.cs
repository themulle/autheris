using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Deployment;

/// <summary>
/// SEC-281-6 (conditions C1..C6): the Helm ingress denies <c>/api/admin</c> by default (<c>ingress.exposeAdmin: false</c>), adds
/// nginx <c>use-regex</c> so the deny is case-insensitive, and never emits snippet annotations. Chart sources are checked as text;
/// when <c>helm</c> is installed the rendered manifests are verified as well (CI job <c>helm-render</c> runs
/// <c>deploy/scripts/verify-hardening.sh</c> with the same assertions).
/// </summary>
[Trait("Category", "Security")]
public sealed class HelmIngressAdminExposureTests
{
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

    private static string Render(string extra = "") => RunHelm("template rel " + Chart() + BaseSet + " " + extra);

    [Fact]
    public void Values_ExposeAdmin_DefaultsToFalse_AndBlockAdminPathIsGone()
    {
        var values = Read("values.yaml");

        values.ShouldContain("exposeAdmin: false");
        values.ShouldContain("nginxCaseInsensitiveAdminDeny: true");
        values.ShouldContain("use-regex");
        foreach (var file in Directory.EnumerateFiles(Chart(), "*", SearchOption.AllDirectories))
        {
            File.ReadAllText(file).ShouldNotContain("blockAdminPath", Case.Sensitive, Path.GetFileName(file));
        }

        File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "scripts", "verify-hardening.sh")).ShouldNotContain("blockAdminPath");
    }

    [Fact]
    public void Template_DeniesUnlessExposeAdminIsBooleanTrue_AndNeverUsesSnippets()
    {
        var ingress = Read("templates/ingress-traefik.yaml");
        var helpers = Read("templates/_helpers.tpl");

        // SEC-281-6-C3: only a real boolean true opens the path; any other value (e.g. the string "false" or "true") denies.
        helpers.ShouldContain("kindIs \"bool\" .Values.ingress.exposeAdmin");
        ingress.ShouldContain("autheris.exposeAdmin");
        ingress.ShouldContain("-admin-blocked");
        ingress.ShouldContain("nginx.ingress.kubernetes.io/use-regex");
        ingress.ShouldContain("nginxCaseInsensitiveAdminDeny");
        Read("templates/service-admin-blocked.yaml").ShouldContain("autheris.exposeAdmin");
        Read("templates/service-admin-blocked.yaml").ShouldNotContain("selector");

        // SEC-281-6-C2
        foreach (var file in Directory.EnumerateFiles(Chart(), "*.yaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("annotation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            text.ShouldNotContain("-snippet", Case.Insensitive, Path.GetFileName(file));
        }

        ingress.ShouldNotContain("snippet", Case.Insensitive);
    }

    [Fact]
    public void VerifyHardeningScript_AssertsTheIngressRenders()
    {
        // SEC-281-6-C6: the CI helm-render job runs this script, so the render assertions cannot pass vacuously in CI.
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "scripts", "verify-hardening.sh"));

        script.ShouldContain("ingress.exposeAdmin=true");
        script.ShouldContain("ingress.className=nginx");
        script.ShouldContain("use-regex");
        script.ShouldContain("snippet");
        script.ShouldContain("nginxCaseInsensitiveAdminDeny=false");
        File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml")).ShouldContain("verify-hardening.sh");
    }

    [Fact]
    public void Helm_DefaultIngress_DoesNotRoute_ApiAdmin()
    {
        if (!HelmAvailable())
        {
            return;
        }

        var ingress = IngressDocument(Render());

        ingress.ShouldContain("path: /api/admin");
        var denyBlock = ingress[ingress.IndexOf("path: /api/admin", StringComparison.Ordinal)..];
        denyBlock = denyBlock[..denyBlock.IndexOf("- path:", 10, StringComparison.Ordinal)];
        denyBlock.ShouldContain("-admin-blocked");
        denyBlock.ShouldNotContain("name: rel-autheris\n");
        Render().ShouldContain("-admin-blocked");
    }

    [Fact]
    public void Helm_ExposeAdminTrue_OmitsDenyRule()
    {
        if (!HelmAvailable())
        {
            return;
        }

        var output = Render("--set ingress.exposeAdmin=true");

        output.ShouldNotContain("/api/admin");
        output.ShouldNotContain("-admin-blocked");
    }

    [Theory]
    [InlineData("--set-string ingress.exposeAdmin=true")]
    [InlineData("--set-string ingress.exposeAdmin=false")]
    [InlineData("--set ingress.exposeAdmin=null")]
    public void Helm_ExposeAdminNonBoolean_StillDenies(string setting)
    {
        if (!HelmAvailable())
        {
            return;
        }

        var output = Render(setting);

        output.ShouldContain("path: /api/admin");
        output.ShouldContain("-admin-blocked");
    }

    [Fact]
    public void Helm_NginxClass_RendersUseRegex_AndNoSnippet()
    {
        if (!HelmAvailable())
        {
            return;
        }

        var output = Render("--set ingress.className=nginx");

        output.ShouldContain("nginx.ingress.kubernetes.io/use-regex: \"true\"");
        output.ShouldContain("path: /api/admin");
        output.ShouldNotContain("snippet", Case.Insensitive);
    }

    [Fact]
    public void Helm_TraefikClass_DeniesWithoutNginxAnnotations()
    {
        if (!HelmAvailable())
        {
            return;
        }

        var output = Render();

        output.ShouldContain("path: /api/admin");
        output.ShouldNotContain("nginx.ingress.kubernetes.io");
    }

    [Fact]
    public void Helm_NginxOptOut_StillRendersDenyRule()
    {
        if (!HelmAvailable())
        {
            return;
        }

        var output = Render("--set ingress.className=nginx --set ingress.nginxCaseInsensitiveAdminDeny=false");

        output.ShouldContain("path: /api/admin");
        output.ShouldContain("-admin-blocked");
        output.ShouldNotContain("use-regex");
    }

    [Fact]
    public void Helm_UserAnnotations_CannotDisableUseRegex()
    {
        if (!HelmAvailable())
        {
            return;
        }

        var output = Render("--set ingress.className=nginx --set-string ingress.annotations.nginx\\.ingress\\.kubernetes\\.io/use-regex=false");

        output.ShouldContain("nginx.ingress.kubernetes.io/use-regex: \"true\"");
        output.ShouldNotContain("use-regex: \"false\"");
    }

    [Fact]
    public void Helm_AnyRender_ContainsNoSnippetAnnotation()
    {
        if (!HelmAvailable())
        {
            return;
        }

        foreach (var extra in new[] { "", "--set ingress.className=nginx", "--set ingress.exposeAdmin=true", "--set ingress.className=nginx --set ingress.exposeAdmin=true" })
        {
            Render(extra).ShouldNotContain("snippet", Case.Insensitive, extra);
        }
    }

    [Fact]
    public void Helm_NginxRegexMode_FailsForRegexMetacharacterPath()
    {
        if (!HelmAvailable())
        {
            return;
        }

        var (exit, error) = TryHelm("template rel " + Chart() + BaseSet + " --set ingress.className=nginx --set ingress.hosts[0].host=gw.example --set ingress.hosts[0].paths[0].path=/v1.*");

        exit.ShouldNotBe(0);
        error.ShouldContain("regex");
    }

    private static string IngressDocument(string rendered) =>
        rendered.Split("\n---", StringSplitOptions.RemoveEmptyEntries).Single(d => d.Contains("kind: Ingress", StringComparison.Ordinal));

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
