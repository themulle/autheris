using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// PLAN-OPS-150 WP-D: ReadyToRun for the framework-dependent API publish (docker-publish.yml) without touching the
/// workflows, the locked restore or the supply-chain gates (SEC-OPS150-13, SEC-OPS150-19).
/// </summary>
public sealed partial class ReadyToRunPublishSecurityTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void DockerPublish_ReadyToRun_KeepsLockedRestoreAuditPinsAndTestAuthExclusion()
    {
        // T-OPS150-16 / SEC-OPS150-13
        string root = FindRepoRoot();
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "docker-publish.yml"));

        workflow.ShouldContain("dotnet restore Autheris.sln --locked-mode");
        workflow.ShouldContain("dotnet list Autheris.sln package --vulnerable --include-transitive");
        workflow.ShouldContain("-p:AutherisIncludeTestAuth=false");
        workflow.ShouldContain("--filter \"FullyQualifiedName~ReleaseBuildExcludesTestAuthTests\"");
        workflow.ShouldContain("AUTHERIS_RELEASE_API_ASSEMBLY: ${{ github.workspace }}/dist/publish/Autheris.Api.dll");

        foreach (Match m in UsesRegex().Matches(workflow))
        {
            m.Groups[1].Value.ShouldMatch("^[0-9a-f]{40}$", $"action '{m.Value}' must be pinned to a commit SHA");
        }

        XElement project = LoadApiCsproj();
        project.Descendants("PublishReadyToRun").ShouldHaveSingleItem()
            .Attribute("Condition")!.Value.ShouldBe("'$(SelfContained)' != 'true'");
        project.Descendants("RuntimeIdentifiers").ShouldHaveSingleItem().Value.ShouldBe("linux-x64");
        project.Descendants("RuntimeIdentifier").ShouldHaveSingleItem()
            .Attribute("Condition")!.Value.ShouldContain("'$(SelfContained)' != 'true'");

        LockFileTargets().ShouldContain("net10.0/linux-x64");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ApiCsproj_ReadyToRun_NoPackVersionOverrideNoFeedOrAuditChange()
    {
        // T-OPS150-23 / SEC-OPS150-13
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Autheris.Api", "Autheris.Api.csproj"));

        foreach (string forbidden in new[]
                 {
                     "RuntimeFrameworkVersion", "Crossgen2PackVersion", "KnownCrossgen2Pack", "KnownRuntimePack",
                     "NuGetAudit", "RestoreSources", "RestoreAdditionalProjectSources", "SignatureValidationMode",
                     "RestorePackagesPath", "DisableImplicitNuGetFallbackFolder", "PackageDownload",
                 })
        {
            csproj.ShouldNotContain(forbidden, Case.Sensitive, $"csproj must not contain '{forbidden}'");
        }

        string props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        props.ShouldContain("<NuGetAudit>true</NuGetAudit>");
        props.ShouldContain("<NuGetAuditMode>all</NuGetAuditMode>");
        props.ShouldContain("<NuGetAuditLevel>low</NuGetAuditLevel>");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ApiLockFile_HasLinuxX64Target_RestoreLockedModeCondition_Unchanged()
    {
        // SEC-OPS150-13 / SEC-OPS150-19
        string root = FindRepoRoot();
        LockFileTargets().ShouldContain("net10.0");
        LockFileTargets().ShouldContain("net10.0/linux-x64");

        string props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        props.ShouldContain(
            "<RestoreLockedMode Condition=\"('$(ContinuousIntegrationBuild)' == 'true' Or '$(GITHUB_ACTIONS)' == 'true') And '$(RuntimeIdentifier)' == ''\">true</RestoreLockedMode>");
    }

    [Fact]
    public void ApiCsproj_KeepsTieredPgo()
    {
        XElement project = LoadApiCsproj();
        project.Descendants("TieredPGO").ShouldHaveSingleItem().Value.ShouldBe("true");
        project.Descendants("TieredCompilation").ShouldBeEmpty();
        project.Descendants("TieredCompilationQuickJit").ShouldBeEmpty();
        project.Descendants("TieredCompilationQuickJitForLoops").ShouldBeEmpty();
        project.Descendants("ServerGarbageCollection").ShouldHaveSingleItem().Value.ShouldBe("true");
    }

    [Fact]
    public void ApiCsproj_ReadyToRun_NotSelfContained_NotForSelfContainedRelease()
    {
        XElement project = LoadApiCsproj();

        project.Descendants("PublishReadyToRun").ShouldHaveSingleItem()
            .Attribute("Condition")!.Value.ShouldContain("'$(SelfContained)' != 'true'");
        project.Descendants("SelfContained").ShouldHaveSingleItem().Value.ShouldBe("false");
        project.Descendants("SelfContained").Single().Attribute("Condition")!.Value.ShouldContain("'$(SelfContained)' == ''");
        project.Descendants("PublishSingleFile").ShouldBeEmpty();
    }

    private static HashSet<string> LockFileTargets()
    {
        string path = Path.Combine(FindRepoRoot(), "src", "Autheris.Api", "packages.lock.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("dependencies").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static XElement LoadApiCsproj() =>
        XDocument.Load(Path.Combine(FindRepoRoot(), "src", "Autheris.Api", "Autheris.Api.csproj")).Root!;

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Autheris.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Autheris.sln not found");
    }

    [GeneratedRegex(@"uses:\s*[^@\s]+@(\S+)")]
    private static partial Regex UsesRegex();
}
