namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Autheris.Api.Hosting;
using Autheris.Infrastructure.Modules.Trust;
using Autheris.Tools.ModuleSigner;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ModuleSignerRoundTripTests : IDisposable
{
    private readonly string _tempRoot;

    public ModuleSignerRoundTripTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"autheris-modsign-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        ModulePackageBuilder.RestoreWritablePermissionsRecursively(_tempRoot);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Signed_manifest_verifies_in_ManifestSignatureVerifier()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifyOnlyKey = CreatePublicOnlyKey(signingKey);
        string moduleDir = CreateSyntheticModuleDir("sample");

        var result = ModuleSignerCli.SignDirectory(
            moduleDir,
            id: "sample",
            version: "1.2.3",
            hostVersion: "1.0.0",
            abstractionsVersion: "1.0.0",
            entryAssembly: "Autheris.Modules.Sample.dll",
            entryType: "Autheris.Modules.Sample.SampleModule",
            signingKey);

        File.Exists(result.ManifestPath).ShouldBeTrue();
        var parsed = ModuleManifest.Parse(File.ReadAllText(result.ManifestPath));

        parsed.Id.ShouldBe("sample");
        parsed.Version.ShouldBe("1.2.3");
        parsed.KeyId.ShouldBe(ManifestSignatureVerifier.ComputeKeyId(verifyOnlyKey));
        parsed.Files.Count.ShouldBe(2);

        ManifestSignatureVerifier.VerifyManifest(parsed, verifyOnlyKey, out var failureReason).ShouldBeTrue(failureReason);
    }

    [Fact]
    public void Tampered_file_rejected_after_signing()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string moduleDir = CreateSyntheticModuleDir("sample");

        var result = ModuleSignerCli.SignDirectory(
            moduleDir,
            id: "sample",
            version: "1.2.3",
            hostVersion: "1.0.0",
            abstractionsVersion: "1.0.0",
            entryAssembly: "Autheris.Modules.Sample.dll",
            entryType: "Autheris.Modules.Sample.SampleModule",
            signingKey);

        string payloadPath = Path.Combine(moduleDir, "sub", "data.json");
        File.WriteAllText(payloadPath, "{\"tampered\":true}");

        Should.Throw<System.Security.SecurityException>(() =>
            FileHashVerifier.VerifyAndLoad(moduleDir, result.Manifest, verifyHashes: true));
    }

    [Theory]
    [Trait("SecurityCondition", "SEC-50-15")]
    [InlineData("--key")]
    [InlineData("-k")]
    [InlineData("--private-key")]
    [InlineData("--signing-key")]
    [InlineData("--pem")]
    public void Cli_key_argument_rejected_without_logging_value(string flagName)
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = signingKey.ExportPkcs8PrivateKeyPem();
        const string secretSentinel = "SUPER_SECRET_SENTINEL_MARKER_98765";
        string poisonedValue = $"{pem}\n#{secretSentinel}";

        string moduleDir = CreateSyntheticModuleDir("sample");
        string manifestPath = Path.Combine(moduleDir, "module.json");

        string[][] invocations =
        [
            ["--dir", moduleDir, "--id", "sample", "--version", "1.0.0", flagName, poisonedValue],
            ["--dir", moduleDir, "--id", "sample", "--version", "1.0.0", $"{flagName}={poisonedValue}"],
            ["--dir", moduleDir, "--id", "sample", "--version", "1.0.0", poisonedValue],
        ];

        foreach (var args in invocations)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = ModuleSignerCli.Run(
                args,
                new StringReader(string.Empty),
                stdout,
                stderr,
                getEnvironmentVariable: _ => pem);

            exitCode.ShouldNotBe(0);
            File.Exists(manifestPath).ShouldBeFalse("Manifest must never be written when CLI key argument is rejected.");
            stdout.ToString().ShouldNotContain(secretSentinel);
            stderr.ToString().ShouldNotContain(secretSentinel);
            stdout.ToString().ShouldNotContain("PRIVATE KEY");
            stderr.ToString().ShouldNotContain("PRIVATE KEY");
        }
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-50-15")]
    public void Cli_accepts_key_from_env_var_and_stdin()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifyOnlyKey = CreatePublicOnlyKey(signingKey);
        string pem = signingKey.ExportPkcs8PrivateKeyPem();

        string envDir = CreateSyntheticModuleDir("sample-env", "Autheris.Modules.SampleEnv.dll");
        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            int envExit = ModuleSignerCli.Run(
                ["--dir", envDir, "--id", "sample-env", "--version", "1.0.0"],
                new StringReader(string.Empty),
                stdout,
                stderr,
                getEnvironmentVariable: name => string.Equals(name, ModuleSignerCli.SigningKeyEnvVar, StringComparison.Ordinal) ? pem : null);

            envExit.ShouldBe(0, stderr.ToString());
            stdout.ToString().ShouldNotContain("PRIVATE KEY");
        }

        var envManifest = ModuleManifest.Parse(File.ReadAllText(Path.Combine(envDir, "module.json")));
        ManifestSignatureVerifier.VerifyManifest(envManifest, verifyOnlyKey, out var envReason).ShouldBeTrue(envReason);

        string stdinDir = CreateSyntheticModuleDir("sample-stdin", "Autheris.Modules.SampleStdin.dll");
        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            int stdinExit = ModuleSignerCli.Run(
                ["--dir", stdinDir, "--id", "sample-stdin", "--version", "1.0.0", "--key-stdin"],
                new StringReader(pem),
                stdout,
                stderr,
                getEnvironmentVariable: _ => null);

            stdinExit.ShouldBe(0, stderr.ToString());
            stdout.ToString().ShouldNotContain("PRIVATE KEY");
        }

        var stdinManifest = ModuleManifest.Parse(File.ReadAllText(Path.Combine(stdinDir, "module.json")));
        ManifestSignatureVerifier.VerifyManifest(stdinManifest, verifyOnlyKey, out var stdinReason).ShouldBeTrue(stdinReason);
    }

    [Fact]
    public async Task Signed_package_verifies_with_module_loader()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifyOnlyKey = CreatePublicOnlyKey(signingKey);

        string modulesRoot = Path.Combine(_tempRoot, "modules");
        string sampleDir = Path.Combine(modulesRoot, "sample");
        Directory.CreateDirectory(sampleDir);
        File.WriteAllBytes(Path.Combine(sampleDir, "Sample.TestModule01.dll"), ModulePackageBuilder.GetValidModuleDllBytes());

        string hostVersion = ModuleLoader.ResolveHostVersion();
        string pem = signingKey.ExportPkcs8PrivateKeyPem();

        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            int exitCode = ModuleSignerCli.Run(
                [
                    "--dir", sampleDir,
                    "--id", "sample",
                    "--version", "1.0.0",
                    "--host-version", hostVersion,
                    "--entry-assembly", "Sample.TestModule01.dll",
                    "--entry-type", typeof(SampleValidModule).FullName!,
                ],
                new StringReader(string.Empty),
                stdout,
                stderr,
                getEnvironmentVariable: name => string.Equals(name, ModuleSignerCli.SigningKeyEnvVar, StringComparison.Ordinal) ? pem : null);

            exitCode.ShouldBe(0, stderr.ToString());
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Path"] = modulesRoot,
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "false",
                ["Gateway:Sample:Endpoint"] = "https://catalog.example.internal",
            })
            .Build();
        var prodEnv = new FakeHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthentication();
        services.AddAuthorization();

        var loader = new ModuleLoader(
            publicKeyOverride: verifyOnlyKey,
            hostVersion: hostVersion,
            isDirectoryWritableOverride: _ => false);

        var loadResult = loader.LoadAndConfigureServices(services, config, prodEnv);
        loadResult.LoadedModuleIds.ShouldBe(["sample"]);

        using var routeBuilder = new FakeEndpointRouteBuilder(services);
        loadResult.MapEndpoints(routeBuilder);

        var moduleEndpoint = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .First(e => e.Metadata.GetMetadata<ModuleEndpointMetadata>()?.ModuleId == "sample");

        var httpContext = new DefaultHttpContext
        {
            RequestServices = routeBuilder.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };
        httpContext.Response.Body = new MemoryStream();
        httpContext.SetEndpoint(moduleEndpoint);
        await moduleEndpoint.RequestDelegate!(httpContext);
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    private string CreateSyntheticModuleDir(string name, string entryDll = "Autheris.Modules.Sample.dll")
    {
        string dir = Path.Combine(_tempRoot, name);
        string subDir = Path.Combine(dir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllBytes(Path.Combine(dir, entryDll), [0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02]);
        File.WriteAllText(Path.Combine(subDir, "data.json"), "{\"ok\":true}");
        return dir;
    }

    private static ECDsa CreatePublicOnlyKey(ECDsa key)
    {
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
        return pub;
    }
    [Fact]
    [Trait("Category", "Security")]
    public void Signer_rejects_two_part_host_version()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = signingKey.ExportPkcs8PrivateKeyPem();
        string moduleDir = CreateSyntheticModuleDir("sample");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = ModuleSignerCli.Run(
            ["--dir", moduleDir, "--id", "sample", "--version", "1.0.0", "--host-version", "1.5"],
            new StringReader(string.Empty),
            stdout,
            stderr,
            getEnvironmentVariable: _ => pem);

        exitCode.ShouldNotBe(0);
        File.Exists(Path.Combine(moduleDir, "module.json")).ShouldBeFalse();
    }

}
