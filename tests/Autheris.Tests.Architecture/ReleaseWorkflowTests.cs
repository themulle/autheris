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
}
