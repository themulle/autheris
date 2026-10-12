using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class SupplyChainAndDeploymentSecurityTests
{
    private static string? FindRepoRoot()
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
        return null;
    }

    // =========================================================================
    // Phase 1 & Phase 4: Workflow Security, Pinning, Minimal Permissions
    // =========================================================================

    [Fact]
    public void AllWorkflows_MustUsePinnedCommitShas_NoUnpinnedTags()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var workflowFiles = Directory.GetFiles(Path.Combine(root!, ".github", "workflows"), "*.yml");
        workflowFiles.Length.ShouldBeGreaterThan(0, "No workflow files found");

        var unpinnedRegex = new Regex(@"^\s*-\s+(?:name:\s+.*?\n\s+)?uses:\s+([^@\s]+)@([^\s#]+)", RegexOptions.Multiline);
        var sha40Regex = new Regex(@"^[0-9a-fA-F]{40}$");

        var violations = new List<string>();

        foreach (var file in workflowFiles)
        {
            var content = File.ReadAllText(file);
            var matches = unpinnedRegex.Matches(content);
            foreach (Match match in matches)
            {
                var action = match.Groups[1].Value.Trim();
                var refStr = match.Groups[2].Value.Trim();

                if (action.StartsWith("./")) continue; // Local actions

                if (!sha40Regex.IsMatch(refStr))
                {
                    violations.Add($"{Path.GetFileName(file)}: Action '{action}' uses unpinned ref '{refStr}' (must be 40-char SHA)");
                }
            }
        }

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void CI_Workflow_HasMinimalPermissions_AndRobustAuditGate()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var ciContent = File.ReadAllText(Path.Combine(root!, ".github", "workflows", "ci.yml"));

        // Permissions check
        ciContent.ShouldNotContain("permissions: read-all");
        ciContent.ShouldNotContain("permissions: write-all");
        ciContent.ShouldContain("contents: read");

        // SC-17: Robust JSON package audit
        ciContent.ShouldContain("--format json --output-version 1");
        ciContent.ShouldContain("jq");
    }

    [Fact]
    public void DockerPublish_Workflow_HasTrivyGate_CosignSigning_AndProvenance()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var publishContent = File.ReadAllText(Path.Combine(root!, ".github", "workflows", "docker-publish.yml"));

        // Minimal permissions
        publishContent.ShouldContain("permissions:");
        publishContent.ShouldContain("contents: read");
        publishContent.ShouldContain("packages: write");
        publishContent.ShouldContain("id-token: write");
        publishContent.ShouldContain("attestations: write");

        // Trivy scan gate before push
        publishContent.ShouldContain("aquasecurity/trivy-action");
        publishContent.ShouldContain("image-ref:");
        publishContent.ShouldContain("severity: CRITICAL,HIGH");

        // Cosign sign & verify
        publishContent.ShouldContain("sigstore/cosign-installer");
        publishContent.ShouldContain("cosign sign --yes");
        publishContent.ShouldContain("cosign verify");

        // SLSA provenance & SBOM
        publishContent.ShouldContain("provenance: mode=max");
        publishContent.ShouldContain("sbom: true");

        // Tag policy: latest and getting-started only on release tags or manual dispatch
        publishContent.ShouldNotContain("# Push on main\n            TAGS+=(\"${IMAGE}:latest\")");
    }

    [Fact]
    public void Release_Workflow_SeparatesBuildAndRelease_AndAttestsProvenance()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var releaseContent = File.ReadAllText(Path.Combine(root!, ".github", "workflows", "release.yml"));

        // Two jobs: build and release
        releaseContent.ShouldContain("build:");
        releaseContent.ShouldContain("release:");
        releaseContent.ShouldContain("needs: build");

        // Attestation & signing
        releaseContent.ShouldContain("actions/attest-build-provenance");
        releaseContent.ShouldContain("cosign sign-blob");
    }

    [Fact]
    public void Trivy_Workflow_Exists_And_Scans()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var trivyWorkflow = Path.Combine(root!, ".github", "workflows", "trivy.yml");
        File.Exists(trivyWorkflow).ShouldBeTrue("trivy.yml should exist");

        var content = File.ReadAllText(trivyWorkflow);
        content.ShouldContain("aquasecurity/trivy-action");
        content.ShouldContain("upload-sarif");
    }

    // =========================================================================
    // Phase 2: Signatures, Attestations, Dependabot, Security Docs
    // =========================================================================

    [Fact]
    public void PolicyAndAdmissionConfigs_Exist_AndAreConfigured()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        File.Exists(Path.Combine(root!, "deploy", "policy", "podman", "policy.json")).ShouldBeTrue();
        File.Exists(Path.Combine(root!, "deploy", "policy", "podman", "registries.d", "ghcr.yaml")).ShouldBeTrue();
        File.Exists(Path.Combine(root!, ".trivyignore")).ShouldBeTrue();
        File.Exists(Path.Combine(root!, "SECURITY.md")).ShouldBeTrue();
        File.Exists(Path.Combine(root!, ".github", "CODEOWNERS")).ShouldBeTrue();
        File.Exists(Path.Combine(root!, ".gitleaks.toml")).ShouldBeTrue();
        File.Exists(Path.Combine(root!, "global.json")).ShouldBeTrue();
    }

    [Fact]
    public void Dependabot_CoversAllRequiredEcosystems()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var dependabot = File.ReadAllText(Path.Combine(root!, ".github", "dependabot.yml"));
        dependabot.ShouldContain("package-ecosystem: \"github-actions\"");
        dependabot.ShouldContain("package-ecosystem: \"nuget\"");
        dependabot.ShouldContain("package-ecosystem: \"docker\"");
    }

    // =========================================================================
    // Phase 3: Docker Hardening (Non-Root-Drop 'USER 10001:10001', Read-Only, Seccomp)
    // =========================================================================

    [Fact]
    public void Dockerfiles_EnforceNonRoot_User10001_AndHealthcheck()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var dockerfiles = new[]
        {
            Path.Combine(root!, "Dockerfile"),
            Path.Combine(root!, "Dockerfile.production"),
            Path.Combine(root!, "Dockerfile.staging")
        };

        foreach (var file in dockerfiles)
        {
            File.Exists(file).ShouldBeTrue($"Expected dockerfile {Path.GetFileName(file)} to exist.");
            var content = File.ReadAllText(file);

            content.Contains("USER 10001:10001").ShouldBeTrue($"File {Path.GetFileName(file)} must specify 'USER 10001:10001'");
            content.Contains("HEALTHCHECK").ShouldBeTrue($"File {Path.GetFileName(file)} must define a HEALTHCHECK");
            content.Contains("@sha256:").ShouldBeTrue($"File {Path.GetFileName(file)} must use digest-pinned base images");
        }

        var prodContent = File.ReadAllText(Path.Combine(root!, "Dockerfile.production"));
        prodContent.ShouldContain("ASPNETCORE_ENVIRONMENT=Production");

        var stagingContent = File.ReadAllText(Path.Combine(root!, "Dockerfile.staging"));
        stagingContent.ShouldContain("ASPNETCORE_ENVIRONMENT=Staging");
    }

    [Fact]
    public void DockerCompose_AppliesSecurityPosture_AndHealthcheck()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var compose = File.ReadAllText(Path.Combine(root!, "docker-compose.yml"));
        compose.ShouldContain("read_only: true");
        compose.ShouldContain("no-new-privileges:true");
        compose.ShouldContain("cap_drop:");
        compose.ShouldContain("ALL");
        compose.ShouldContain("healthcheck:");
    }

    [Fact]
    public void BenchmarkDockerfiles_AreHardened()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var benchGql = File.ReadAllText(Path.Combine(root!, "benchmarks", "load", "docker", "Dockerfile.gql"));
        benchGql.ShouldContain("USER 10001:10001");
        benchGql.ShouldContain("ASPNETCORE_ENVIRONMENT=Benchmark");
        benchGql.ShouldNotContain("ASPNETCORE_ENVIRONMENT=Development");
        benchGql.ShouldContain("appsettings.Benchmark.json");

        var podmanApi = File.ReadAllText(Path.Combine(root!, "deploy", "containers", "gqlgateway-api", "Containerfile"));
        podmanApi.ShouldContain("--locked-mode");
        podmanApi.ShouldNotContain("/p:TreatWarningsAsErrors=false");
        podmanApi.ShouldNotContain("appsettings.Development.json");
    }

    [Fact]
    public void SeccompProfile_Exists_AndIsValidJson()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var seccompPath = Path.Combine(root!, "deploy", "security", "seccomp-default.json");
        File.Exists(seccompPath).ShouldBeTrue("seccomp-default.json must exist");

        var json = File.ReadAllText(seccompPath);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("defaultAction", out var action).ShouldBeTrue();
    }

    // =========================================================================
    // Phase 5: YAML Syntax Validation
    // =========================================================================

    [Fact]
    public void AllYamlFiles_HaveBasicValidStructure()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repo root not found");

        var yamlFiles = Directory.GetFiles(Path.Combine(root!, ".github"), "*.yml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(root!, "*.yml", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(Path.Combine(root!, "deploy"), "*.yaml", SearchOption.AllDirectories))
            .ToList();

        yamlFiles.Count.ShouldBeGreaterThan(0);

        foreach (var file in yamlFiles)
        {
            var lines = File.ReadAllLines(file);
            // Basic structure check: lines should not contain invalid tabs or unbalanced basic quotes
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#') || string.IsNullOrWhiteSpace(trimmed)) continue;

                // Indentation should not use tabs in YAML
                line.TakeWhile(char.IsWhiteSpace).Contains('\t').ShouldBeFalse($"YAML file {Path.GetFileName(file)} line {i + 1} contains tab indentation.");
            }
        }
    }

    // =========================================================================
    // SC-07: DefaultEnvironmentSecretProvider file: reference support
    // =========================================================================

    [Fact]
    public void DefaultEnvironmentSecretProvider_FileReference_OutsideAllowlist_ThrowsException()
    {
        var config = new ConfigurationBuilder().Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var provider = new DefaultEnvironmentSecretProvider(config, env);

        Should.Throw<InvalidOperationException>(() =>
            provider.GetSecretBytes("file:/etc/passwd"));
    }

    [Fact]
    public void DefaultEnvironmentSecretProvider_FileReference_InsideAllowlist_ReadsAndTrims()
    {
        var config = new ConfigurationBuilder().Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        // Allow test directory override
        var tempFile = Path.GetTempFileName();
        try
        {
            var rawSecret = "super-secret-key-that-is-at-least-32-bytes-long-12345\n\r";
            File.WriteAllText(tempFile, rawSecret);

            var provider = new DefaultEnvironmentSecretProvider(config, env, secretsDirectory: Path.GetDirectoryName(tempFile));
            var bytes = provider.GetSecretBytes($"file:{tempFile}");

            Encoding.UTF8.GetString(bytes).ShouldBe("super-secret-key-that-is-at-least-32-bytes-long-12345");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
