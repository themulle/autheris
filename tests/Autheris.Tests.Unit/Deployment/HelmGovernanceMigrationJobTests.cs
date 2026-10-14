using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Deployment;

/// <summary>
/// WP-ARC-B-12 (D7): the optional Helm pre-install/pre-upgrade job that applies the governance schema migrations. The checks read
/// the chart sources as text; when <c>helm</c> is installed the rendered manifests are verified as well.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class HelmGovernanceMigrationJobTests
{
    private static string ChartDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
            {
                return Path.Combine(current.FullName, "deploy", "helm", "autheris");
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(ChartDirectory(), relative));

    private static string Job() => Read("templates/governance-migration-job.yaml");

    [Fact]
    public void Values_DisableTheMigrationJobByDefault()
    {
        var values = Read("values.yaml");

        values.ShouldContain("governanceMigrationJob:");
        EnabledFalse().IsMatch(values).ShouldBeTrue("governanceMigrationJob.enabled must default to false");
        values.ShouldContain("backoffLimit: 0");
        values.ShouldContain("activeDeadlineSeconds: 600");
        values.ShouldContain("ttlSecondsAfterFinished: 3600");
    }

    [GeneratedRegex(@"governanceMigrationJob:\s*\n(?:\s*#.*\n)*\s*enabled:\s*false", RegexOptions.None)]
    private static partial Regex EnabledFalse();

    [Fact]
    public void Template_IsOnlyRenderedWhenEnabled_AndFailsForSqlite()
    {
        var job = Job();

        job.ShouldStartWith("{{- if .Values.governanceMigrationJob.enabled }}");
        job.ShouldContain("fail ");
        job.ShouldContain("sqlite");
    }

    [Fact]
    public void Job_RunsAsHelmHook_BeforeInstallAndUpgrade_WithConfigMapFirst()
    {
        var job = Job();

        job.ShouldContain("\"helm.sh/hook\": pre-install,pre-upgrade");
        job.ShouldContain("\"helm.sh/hook-weight\": \"-10\"");
        job.ShouldContain("\"helm.sh/hook-weight\": \"-5\"");
        job.ShouldContain("before-hook-creation,hook-succeeded");
        job.ShouldContain("kind: ConfigMap");
        job.ShouldContain("kind: Job");
        job.ShouldContain("-migrate");
    }

    [Fact]
    public void Job_InvokesTheMigrationCommandInApplyMode()
    {
        var job = Job();

        job.ShouldContain("--migrate-governance");
        job.ShouldContain("Gateway__GovernanceDb__Migrations__Mode");
        job.ShouldContain("Apply");
        job.ShouldContain("Gateway__GovernanceDb__MigrationConnectionString");
        job.ShouldContain("restartPolicy: Never");
    }

    [Fact]
    public void Job_IsHardened_NonRoot_ReadOnlyRootFilesystem_NoApiToken()
    {
        var job = Job();

        job.ShouldContain("automountServiceAccountToken: false");
        job.ShouldNotContain("serviceAccountName");
        job.ShouldContain(".Values.podSecurityContext");
        job.ShouldContain(".Values.securityContext");
        Read("values.yaml").ShouldContain("runAsNonRoot: true");
        Read("values.yaml").ShouldContain("readOnlyRootFilesystem: true");
        job.ShouldContain("mountPath: /tmp");
    }

    [Fact]
    public void Job_PodLabels_DoNotMatchTheDeploymentSelector()
    {
        var job = Job();

        job.ShouldContain("app.kubernetes.io/component: governance-migration");
        // The job pod must not carry the selector labels of the Deployment, the Service or the NetworkPolicy of the application.
        job.ShouldNotContain("autheris.selectorLabels");
        job.ShouldContain("autheris.labels");
    }

    [Fact]
    public void Deployment_RunsInVerifyMode_OnlyWhenTheJobIsEnabled()
    {
        var env = Read("templates/_helpers.tpl");
        var deployment = Read("templates/deployment.yaml");

        deployment.ShouldContain(".Values.governanceMigrationJob.enabled");
        deployment.ShouldContain("Gateway__GovernanceDb__Migrations__Mode");
        deployment.ShouldContain("Verify");
        deployment.ShouldNotContain("Apply");
        env.ShouldContain("define \"autheris.gatewayEnv\"");
        env.ShouldContain("define \"autheris.configData\"");
    }

    [Fact]
    public void NetworkPolicy_CoversTheMigrationJobEgress_WhenEnabled()
    {
        var policy = Read("templates/networkpolicy.yaml");

        policy.ShouldContain(".Values.governanceMigrationJob.enabled");
        policy.ShouldContain("app.kubernetes.io/component: governance-migration");
    }

    [Fact]
    public void Ci_LintsTheChart_AndRendersTheMigrationJob()
    {
        var root = Path.GetFullPath(Path.Combine(ChartDirectory(), "..", "..", ".."));
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        ci.ShouldContain("helm lint");
        ci.ShouldContain("helm template");
        ci.ShouldContain("governanceMigrationJob.enabled=true");
    }

    [Fact]
    public void HelmTemplate_WhenHelmIsInstalled_RendersHooksOnlyWhenEnabled()
    {
        if (!HelmAvailable())
        {
            return;
        }

        var disabled = RunHelm("template rel " + ChartDirectory() + " --set gateway.allowedHosts[0]=gw.example");
        disabled.ShouldNotContain("governance-migration");
        disabled.ShouldNotContain("Gateway__GovernanceDb__Migrations__Mode");

        var enabled = RunHelm("template rel " + ChartDirectory() + " --set gateway.allowedHosts[0]=gw.example --set governanceMigrationJob.enabled=true");
        enabled.ShouldContain("kind: Job");
        enabled.ShouldContain("--migrate-governance");
        enabled.ShouldContain("pre-install,pre-upgrade");
        enabled.ShouldContain("Verify");
        enabled.ShouldContain("Apply");
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
