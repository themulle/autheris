using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class ReleaseWorkflowTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Release_passes_tag_version_to_publish_and_helm_package()
    {
        var releasePath = Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows", "release.yml");
        File.Exists(releasePath).ShouldBeTrue();
        var content = File.ReadAllText(releasePath);

        content.ShouldContain("-p:Version=");
        content.ShouldContain("--app-version \"$VERSION\"");
        content.ShouldContain("validate-release-version.sh");
        content.ShouldNotContain("generate_release_notes: true");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Docker_publish_passes_version_and_never_tags_prerelease_latest_or_getting_started()
    {
        var dockerPublishPath = Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows", "docker-publish.yml");
        File.Exists(dockerPublishPath).ShouldBeTrue();
        var content = File.ReadAllText(dockerPublishPath);

        // Both publish steps must specify -p:Version=
        var matches = Regex.Matches(content, @"dotnet publish[^\n]*\n(?:\s+[^\n]*\n)*?\s+-p:Version=");
        matches.Count.ShouldBeGreaterThanOrEqualTo(2, "Both dotnet publish calls in docker-publish.yml must pass -p:Version=");

        // Pre-release tags must not add :latest or :getting-started
        content.ShouldContain("[[ \"$VERSION\" != *-* ]]");
        content.ShouldContain("TAGS+=(\"${IMAGE}:latest\")");
        content.ShouldContain("TAGS+=(\"${IMAGE}:getting-started\")");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Default_rendered_image_tag_is_a_pushed_tag()
    {
        var dockerPublishPath = Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows", "docker-publish.yml");
        var releasePath = Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows", "release.yml");
        var helpersPath = Path.Combine(RepositoryScan.RepoRoot, "deploy", "helm", "autheris", "templates", "_helpers.tpl");
        var validateScriptPath = Path.Combine(RepositoryScan.RepoRoot, "scripts", "release", "validate-release-version.sh");

        var dockerContent = File.ReadAllText(dockerPublishPath);
        var releaseContent = File.ReadAllText(releasePath);
        var helpersContent = File.ReadAllText(helpersPath);
        var scriptContent = File.ReadAllText(validateScriptPath);

        // docker-publish pushes both ${IMAGE}:${REF_NAME} and ${IMAGE}:${VERSION}
        dockerContent.ShouldContain("TAGS+=(\"${IMAGE}:${REF_NAME}\")");
        dockerContent.ShouldContain("TAGS+=(\"${IMAGE}:${VERSION}\")");

        // release.yml packages chart with --app-version "$VERSION"
        releaseContent.ShouldContain("--app-version \"$VERSION\"");

        // _helpers.tpl falls back to .Chart.AppVersion
        helpersContent.ShouldContain("default .Chart.AppVersion .Values.image.tag");

        // validate-release-version.sh contains helm template image check and registry existence check
        scriptContent.ShouldContain("helm template");
        scriptContent.ShouldContain("docker buildx imagetools inspect");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Release_blocked_when_last_nightly_failed()
    {
        var releasePath = Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows", "release.yml");
        var content = File.ReadAllText(releasePath);

        content.ShouldContain("Nightly gate");
        content.ShouldContain("parity-nightly.yml");
        content.ShouldContain("sql-compiler-gate-nightly.yml");
        content.ShouldContain("conclusion");
        content.ShouldContain("success");

        // Verify Nightly gate step appears before the publish step
        var nightlyGateIndex = content.IndexOf("Nightly gate", StringComparison.Ordinal);
        var publishIndex = content.IndexOf("dotnet publish", StringComparison.Ordinal);

        nightlyGateIndex.ShouldBeGreaterThan(0);
        publishIndex.ShouldBeGreaterThan(0);
        nightlyGateIndex.ShouldBeLessThan(publishIndex, "Nightly gate must run before dotnet publish.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Release_workflow_permissions_stay_least_privilege()
    {
        var releasePath = Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows", "release.yml");
        var content = File.ReadAllText(releasePath);

        // Top-level permissions must be empty
        content.ShouldMatch(@"permissions:\s*\{\}");

        // No pull_request_target
        content.ShouldNotContain("pull_request_target");

        // Job-level permissions check
        var buildJobIndex = content.IndexOf("build:", StringComparison.Ordinal);
        var releaseJobIndex = content.IndexOf("release:", StringComparison.Ordinal);
        buildJobIndex.ShouldBeGreaterThan(0);
        releaseJobIndex.ShouldBeGreaterThan(0);

        var buildJobBlock = content.Substring(buildJobIndex, releaseJobIndex - buildJobIndex);
        var releaseJobBlock = content.Substring(releaseJobIndex);

        // build job permissions: read contents and actions, token/attestation write for provenance
        buildJobBlock.ShouldContain("actions: read");
        buildJobBlock.ShouldNotContain("contents: write");
        buildJobBlock.ShouldNotContain("packages: write");

        // contents: write only in release job
        releaseJobBlock.ShouldContain("contents: write");
    }

    [Fact]
    public void Changelog_exists_with_unreleased_section()
    {
        var changelogPath = Path.Combine(RepositoryScan.RepoRoot, "CHANGELOG.md");
        File.Exists(changelogPath).ShouldBeTrue();
        var content = File.ReadAllText(changelogPath);

        content.ShouldContain("# Changelog");
        content.ShouldContain("## [Unreleased]");
        content.ShouldContain("### BREAKING");
        content.ShouldContain("### Removed");
        content.ShouldContain("### Moved to modules");
        content.ShouldContain("### Fixed");
        content.ShouldContain("### Security");

        // Backfill sections 1.0 to 1.7 must exist
        string[] versions = ["1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.4.0", "1.5.0", "1.6.0", "1.7.0"];
        foreach (var v in versions)
        {
            content.ShouldContain($"## [{v}]");
        }

        // 1.0.0 through 1.4.0 must mark unsupported upgrade base
        string[] unsupportedVersions = ["1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.4.0"];
        foreach (var uv in unsupportedVersions)
        {
            var sectionStart = content.IndexOf($"## [{uv}]", StringComparison.Ordinal);
            sectionStart.ShouldBeGreaterThan(0);
            var sectionChunk = content.Substring(sectionStart, Math.Min(300, content.Length - sectionStart));
            sectionChunk.ShouldContain("> Unsupported upgrade base. Oldest supported migration base: v1.5.0.");
        }
    }

    private const string SigningKeySecret = "AUTHERIS_MODULE_SIGNING_KEY_PEM";

    [Fact]
    [Trait("Category", "Security")]
    public void Image_build_rejects_unsigned_module()
    {
        var dockerfile = ReadRepoFile("Dockerfile");

        dockerfile.ShouldContain("ARG FLAVOR=core");
        dockerfile.ShouldContain("FROM dotnet-base AS modules-core");
        dockerfile.ShouldContain("FROM dotnet-base AS modules-full");
        dockerfile.ShouldContain("FROM modules-${FLAVOR} AS modules-selected");
        dockerfile.ShouldContain("core|full) ;; *) echo \"Error: FLAVOR must be core or full\"");

        var core = StageBlock(dockerfile, "modules-core");
        core.ShouldNotContain("dist/modules");
        core.ShouldNotContain("/payload/modules");

        // The full stage verifies every module and aborts the build on a non-zero verify, on an empty set and on any unsigned module.
        var full = StageBlock(dockerfile, "modules-full");
        full.ShouldContain("COPY dist/modules/ /payload/modules/");
        full.ShouldContain("set -eu");
        full.ShouldContain("Autheris.ModuleSigner.dll verify --pub /tools/module-release-pub.pem --dir \"$module_dir\" || exit 1");
        full.ShouldContain("[ \"$count\" -gt 0 ]");
        full.IndexOf("verify --pub", StringComparison.Ordinal).ShouldBeLessThan(full.IndexOf("chmod 0555", StringComparison.Ordinal));
        full.ShouldContain("chmod 0555");
        full.ShouldContain("chmod 0444");

        // Only the selected flavor payload reaches the runtime image, root-owned; no unconditional module copy remains.
        dockerfile.ShouldContain("COPY --from=modules-selected --chown=0:0 /payload/ /app/");
        dockerfile.ShouldNotContain("COPY dist/modules/ /app/modules/");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Image_verify_uses_embedded_release_key()
    {
        var dockerfile = ReadRepoFile("Dockerfile");
        var full = StageBlock(dockerfile, "modules-full");

        // The pinned key is the file embedded into Autheris.Api as the Keys/module-release-*.pem resource.
        var apiProject = ReadRepoFile("src/Autheris.Api/Autheris.Api.csproj");
        apiProject.ShouldContain("Hosting\\Keys\\module-release-");
        File.Exists(Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Api", "Hosting", "Keys", "module-release-pub.pem")).ShouldBeTrue();
        full.ShouldContain("COPY src/Autheris.Api/Hosting/Keys/module-release-pub.pem /tools/module-release-pub.pem");

        // No key material or key location can be injected through build args, environment or secrets.
        foreach (var line in dockerfile.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("ARG ", StringComparison.Ordinal) || trimmed.StartsWith("ENV ", StringComparison.Ordinal))
            {
                trimmed.ShouldNotContain("PEM", Case.Insensitive);
                trimmed.ShouldNotContain("KEY", Case.Insensitive);
                trimmed.ShouldNotContain("PUB", Case.Insensitive);
            }
        }

        dockerfile.ShouldNotContain("--mount=type=secret");
        dockerfile.ShouldNotContain("--mount=type=bind");
        full.ShouldNotContain("$PUB");
        full.ShouldNotContain("${PUB");
        ReadRepoFile(".github/workflows/docker-publish.yml").ShouldNotContain("build-args: |\n            PUB");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Signing_key_secret_only_in_protected_environment_jobs()
    {
        string[] workflows = ["docker-publish.yml", "release.yml"];
        var referencing = 0;
        foreach (var workflow in workflows)
        {
            var content = ReadRepoFile(".github/workflows/" + workflow);
            foreach (var (name, block) in JobBlocks(content))
            {
                if (!block.Contains($"secrets.{SigningKeySecret}", StringComparison.Ordinal))
                {
                    continue;
                }

                referencing++;
                Regex.IsMatch(block, @"(?m)^    environment:\s*(release|module-release)\s*$")
                    .ShouldBeTrue($"{workflow} job '{name}' references the module signing key without a protected environment");
            }
        }

        referencing.ShouldBe(2);

        var dockerJobs = JobBlocks(ReadRepoFile(".github/workflows/docker-publish.yml")).ToDictionary(j => j.Name, j => j.Block);
        dockerJobs.TryGetValue("module-e2e-branch", out var branchE2eBlock).ShouldBeTrue();
        branchE2eBlock!.ShouldNotContain($"secrets.{SigningKeySecret}");
        branchE2eBlock.ShouldNotContain("environment: module-release");

        // No other workflow, script or composite action may read the signing key.
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepositoryScan.RepoRoot, ".github"), "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (name is "docker-publish.yml" or "release.yml")
            {
                continue;
            }

            File.ReadAllText(path).ShouldNotContain($"secrets.{SigningKeySecret}", customMessage: path);
        }

        // Only tag, dispatch or main-bound triggers: no pull_request_target and no fork-reachable trigger carries the secret.
        ReadRepoFile(".github/workflows/release.yml").ShouldNotContain("pull_request_target");
        ReadRepoFile(".github/workflows/docker-publish.yml").ShouldNotContain("pull_request_target");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Ghsa_ff36_36r5_regression()
    {
        string[] workflows = ["docker-publish.yml", "release.yml"];
        var secretSteps = 0;

        foreach (var workflow in workflows)
        {
            var content = ReadRepoFile(".github/workflows/" + workflow);
            foreach (var (jobName, jobBlock) in JobBlocks(content))
            {
                foreach (var stepBlock in StepBlocks(jobBlock))
                {
                    if (!stepBlock.Contains($"secrets.{SigningKeySecret}", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    secretSteps++;
                    stepBlock.Contains("Autheris.ModuleSigner.dll", StringComparison.Ordinal).ShouldBeTrue(
                        $"{workflow} job '{jobName}' must execute only the prebuilt signer DLL inside steps with the signing secret");
                    stepBlock.ShouldNotContain("dotnet build");
                    stepBlock.ShouldNotContain("dotnet publish");
                    stepBlock.ShouldNotContain("dotnet restore");
                    stepBlock.ShouldNotContain("dotnet test");
                    stepBlock.ShouldNotContain("dotnet run");
                }
            }
        }

        secretSteps.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Docker_publish_and_release_package_all_modules_and_trim_runtimes()
    {
        var dockerPublish = ReadRepoFile(".github/workflows/docker-publish.yml");
        dockerPublish.ShouldNotContain("modules/Autheris.Modules.Lakehouse/Autheris.Modules.Lakehouse.csproj");
        Regex.Count(dockerPublish, Regex.Escape("for csproj in modules/*/*.csproj; do"))
            .ShouldBe(2, "Both build-and-push and module-e2e-branch in docker-publish.yml must package all non-test modules");
        dockerPublish.ShouldContain("! -name linux-x64");

        var releaseWorkflow = ReadRepoFile(".github/workflows/release.yml");
        releaseWorkflow.ShouldContain("for csproj in modules/*/*.csproj; do");
        releaseWorkflow.ShouldContain("! -name linux-x64 ! -name linux-arm64");
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryScan.RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string StageBlock(string dockerfile, string stage)
    {
        var start = dockerfile.IndexOf($" AS {stage}\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0, $"stage {stage} not found");
        var next = dockerfile.IndexOf("\nFROM ", start, StringComparison.Ordinal);
        return next < 0 ? dockerfile[start..] : dockerfile[start..next];
    }

    private static IEnumerable<(string Name, string Block)> JobBlocks(string workflow)
    {
        var jobsStart = workflow.IndexOf("\njobs:\n", StringComparison.Ordinal);
        jobsStart.ShouldBeGreaterThan(0);
        var matches = Regex.Matches(workflow[jobsStart..], @"(?m)^  ([A-Za-z0-9_-]+):\s*$");
        for (var i = 0; i < matches.Count; i++)
        {
            var begin = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : workflow.Length - jobsStart;
            yield return (matches[i].Groups[1].Value, workflow.Substring(jobsStart + begin, end - begin));
        }
    }

    private static IEnumerable<string> StepBlocks(string jobBlock)
    {
        var stepsStart = jobBlock.IndexOf("\n    steps:\n", StringComparison.Ordinal);
        if (stepsStart < 0)
        {
            yield break;
        }

        var matches = Regex.Matches(jobBlock[stepsStart..], @"(?m)^      - (?:name|uses|run):");
        for (var i = 0; i < matches.Count; i++)
        {
            var begin = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : jobBlock.Length - stepsStart;
            yield return jobBlock.Substring(stepsStart + begin, end - begin);
        }
    }
}
