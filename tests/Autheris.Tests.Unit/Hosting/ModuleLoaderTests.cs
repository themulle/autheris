namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autheris.Api.Hosting;
using Autheris.Infrastructure.Modules.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ModuleLoaderTests : IDisposable
{
    private readonly string _tempRoot;

    public ModuleLoaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "autheris_modloader_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        ModulePackageBuilder.RestoreWritablePermissionsRecursively(_tempRoot);
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void No_modules_enabled_loads_nothing()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Path"] = _tempRoot,
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var result = loader.LoadAndConfigureServices(services, config, env);

        result.Modules.ShouldBeEmpty();
        result.LoadedModuleIds.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Valid_signed_module_loads_and_maps_endpoints()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = CreateSignedSampleModuleDirectory(key);

        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthentication();
        services.AddAuthorization();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Path"] = _tempRoot,
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Sample:Endpoint"] = "https://catalog.example.internal",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var result = loader.LoadAndConfigureServices(services, config, env);

        result.LoadedModuleIds.ShouldBe(["sample"]);
        var markerDescriptor = services.Single(d => d.ServiceType.FullName == typeof(ISampleModuleMarker).FullName);
        var marker = markerDescriptor.ImplementationInstance.ShouldNotBeNull();
        marker.GetType().GetProperty(nameof(ISampleModuleMarker.ModuleId))!.GetValue(marker).ShouldBe("sample");
        marker.GetType().GetProperty(nameof(ISampleModuleMarker.ConfiguredEndpoint))!.GetValue(marker).ShouldBe("https://catalog.example.internal");

        using var app = new FakeEndpointRouteBuilder(services);
        result.MapEndpoints(app);

        var endpoints = app.DataSources
            .SelectMany(ds => ds.Endpoints)
            .ToList();
        endpoints.ShouldContain(e => e.Metadata.GetMetadata<ModuleEndpointMetadata>() != null);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Unsigned_module_rejected_outside_development()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key, signatureOverride: string.Empty);

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("unsigned");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AllowUnsigned_fails_startup_outside_development()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Path"] = _tempRoot,
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Staging };

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.Reason.ShouldContain("AllowUnsignedInDevelopment");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Tampered_dll_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = CreateSignedSampleModuleDirectory(key);

        var dllPath = Path.Combine(moduleDir, "Sample.TestModule01.dll");
        var bytes = File.ReadAllBytes(dllPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(dllPath, bytes);

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("SHA-256");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Tampered_manifest_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = CreateSignedSampleModuleDirectory(key);

        var manifestPath = Path.Combine(moduleDir, "module.json");
        var json = File.ReadAllText(manifestPath).Replace("\"1.2.0\"", "\"1.2.9\"");
        File.WriteAllText(manifestPath, json);

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("signature");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Wrong_signature_fails_startup()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var expectedKeyId = ManifestSignatureVerifier.ComputeKeyId(otherKey);

        CreateSignedSampleModuleDirectory(signingKey, keyIdOverride: expectedKeyId);

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: otherKey, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("signature");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Unknown_keyId_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key, keyIdOverride: "deadbeefcafebabe");

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("keyId");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Unlisted_file_in_module_folder_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = CreateSignedSampleModuleDirectory(key);
        File.WriteAllBytes(Path.Combine(moduleDir, "ExtraUnlisted.dll"), [0x01, 0x02, 0x03]);

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("unlisted file");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Missing_listed_file_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = Path.Combine(_tempRoot, "sample");
        Directory.CreateDirectory(moduleDir);

        var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        File.WriteAllBytes(Path.Combine(moduleDir, "Sample.TestModule01.dll"), dllBytes);

        var manifest = ModulePackageBuilder.CreateSignedManifest(
            key,
            files:
            [
                new ModuleManifestFile("Sample.TestModule01.dll", IntegrityHashHelper.ComputeSha256HexLower(dllBytes)),
                new ModuleManifestFile("MissingDependency.dll", new string('a', 64)),
            ]);
        File.WriteAllText(Path.Combine(moduleDir, "module.json"), ModulePackageBuilder.SerializeManifestJson(manifest));

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("missing listed file");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Path_traversal_in_manifest_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = Path.Combine(_tempRoot, "sample");
        Directory.CreateDirectory(moduleDir);

        const string traversalJson = """
        {
          "schema": 1,
          "id": "sample",
          "version": "1.2.0",
          "hostVersion": "1.2.0",
          "abstractionsVersion": "1.0.0",
          "entryAssembly": "Sample.TestModule01.dll",
          "entryType": "Autheris.Tests.Unit.Hosting.SampleValidModule",
          "files": [
            { "path": "../outside.dll", "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }
          ],
          "keyId": "0123456789abcdef",
          "signature": "c2ln"
        }
        """;
        File.WriteAllText(Path.Combine(moduleDir, "module.json"), traversalJson);

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("Invalid manifest file path");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void HostVersion_mismatch_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key, hostVersion: "9.9.0");

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, hostVersion: "1.2.0", isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("hostVersion");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AbstractionsVersion_mismatch_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key, abstractionsVersion: "4.0.0");

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, abstractionsVersion: "1.0.0", isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("abstractionsVersion");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void HostVersion_patch_mismatch_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key, hostVersion: "1.2.0");
        var loader = new ModuleLoader(publicKeyOverride: key, hostVersion: "1.2.1", isDirectoryWritableOverride: _ => false);

        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(
            new ServiceCollection(), BuildEnabledConfig("sample"), new FakeHostEnvironment { EnvironmentName = Environments.Production }));
        ex.Reason.ShouldContain("hostVersion");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void HostVersion_prerelease_mismatch_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key, hostVersion: "1.2.0-rc.1");
        var loader = new ModuleLoader(publicKeyOverride: key, hostVersion: "1.2.0", isDirectoryWritableOverride: _ => false);

        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(
            new ServiceCollection(), BuildEnabledConfig("sample"), new FakeHostEnvironment { EnvironmentName = Environments.Production }));
        ex.Reason.ShouldContain("hostVersion");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void HostVersion_build_metadata_is_ignored()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key, hostVersion: "1.2.0+abc1234");
        var loader = new ModuleLoader(publicKeyOverride: key, hostVersion: "1.2.0+def5678", isDirectoryWritableOverride: _ => false);

        var result = loader.LoadAndConfigureServices(
            new ServiceCollection(), BuildEnabledConfig("sample"), new FakeHostEnvironment { EnvironmentName = Environments.Production });
        result.LoadedModuleIds.ShouldBe(["sample"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ModuleDirectory_not_root_owned_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key);
        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false, ownerUidProbe: _ => 1000u);

        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(
            new ServiceCollection(), BuildEnabledConfig("sample"), new FakeHostEnvironment { EnvironmentName = Environments.Production }));
        ex.Reason.ShouldContain("not owned by root");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ModuleDirectory_symlink_root_fails_startup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key);
        var linkRoot = Path.Combine(Path.GetTempPath(), "autheris_modlink_" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(linkRoot, _tempRoot);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Modules:Path"] = linkRoot,
                    ["Gateway:Modules:Enabled:0"] = "sample",
                })
                .Build();
            var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);

            var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(
                new ServiceCollection(), config, new FakeHostEnvironment { EnvironmentName = Environments.Production }));
            ex.Reason.ShouldContain("symbolic link");
        }
        finally
        {
            Directory.Delete(linkRoot);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ModuleFile_group_or_other_writable_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key);
        var loader = new ModuleLoader(
            publicKeyOverride: key,
            isDirectoryWritableOverride: path => path.EndsWith("Sample.TestModule01.dll", StringComparison.Ordinal));

        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(
            new ServiceCollection(), BuildEnabledConfig("sample"), new FakeHostEnvironment { EnvironmentName = Environments.Production }));
        ex.Reason.ShouldContain("Sample.TestModule01.dll");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ModuleFile_world_writable_bit_fails_startup_on_real_filesystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = CreateSignedSampleModuleDirectory(key);
        File.SetUnixFileMode(Path.Combine(moduleDir, "Sample.TestModule01.dll"), (UnixFileMode)0b110_100_110);

        var ex = Should.Throw<ModuleLoadException>(() => ModuleDirectoryPermissionCheck.VerifyFiles(
            moduleDir, ["Sample.TestModule01.dll"], isProduction: true, moduleId: "sample"));
        ex.Reason.ShouldContain("group or others");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Config_cannot_add_trusted_key()
    {
        var keyConstants = typeof(ModuleLoader)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && ((string)f.GetRawConstantValue()!).StartsWith("Gateway:Modules:", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        keyConstants.ShouldNotBeEmpty();
        foreach (var key in keyConstants)
        {
            key.ShouldNotContain("Trusted", Case.Insensitive);
            key.ShouldNotContain("PublicKey", Case.Insensitive);
            key.ShouldNotContain("Keyring", Case.Insensitive);
        }

        // A configured foreign key id or PEM must not make a module signed by another key load.
        using var embedded = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var foreign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(foreign);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Path"] = _tempRoot,
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Modules:TrustedKeys:0"] = Convert.ToBase64String(foreign.ExportSubjectPublicKeyInfo()),
                ["Gateway:Modules:PublicKey"] = foreign.ExportSubjectPublicKeyInfoPem(),
            })
            .Build();
        var loader = new ModuleLoader(publicKeyOverride: embedded, isDirectoryWritableOverride: _ => false);

        Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(
            new ServiceCollection(), config, new FakeHostEnvironment { EnvironmentName = Environments.Production }));
    }

    private IConfiguration BuildEnabledConfig(string moduleId) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Path"] = _tempRoot,
                ["Gateway:Modules:Enabled:0"] = moduleId,
            })
            .Build();

    private string CreateSignedSampleModuleDirectory(
        ECDsa signingKey,
        string moduleId = "sample",
        string? hostVersion = null,
        string abstractionsVersion = "1.0.0",
        string? keyIdOverride = null,
        string? signatureOverride = null)
    {
        var moduleDir = Path.Combine(_tempRoot, moduleId);
        Directory.CreateDirectory(moduleDir);

        var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        File.WriteAllBytes(Path.Combine(moduleDir, "Sample.TestModule01.dll"), dllBytes);

        var manifest = ModulePackageBuilder.CreateSignedManifest(
            signingKey,
            moduleId: moduleId,
            files: [new ModuleManifestFile("Sample.TestModule01.dll", IntegrityHashHelper.ComputeSha256HexLower(dllBytes))],
            hostVersion: hostVersion,
            abstractionsVersion: abstractionsVersion,
            keyIdOverride: keyIdOverride,
            signatureOverride: signatureOverride);

        File.WriteAllText(Path.Combine(moduleDir, "module.json"), ModulePackageBuilder.SerializeManifestJson(manifest));
        return moduleDir;
    }

    [Fact]
    public void Loaded_module_emits_startup_event()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedSampleModuleDirectory(key);

        var services = new ServiceCollection();
        var config = BuildEnabledConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var testLogger = new TestModuleLoaderLogger();
        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false, logger: testLogger);
        var result = loader.LoadAndConfigureServices(services, config, env);

        result.LoadedModuleIds.ShouldBe(["sample"]);
        testLogger.Logs.ShouldContain(l => l.LogLevel == Microsoft.Extensions.Logging.LogLevel.Information && l.Message.Contains("ModuleLoaded") && l.Message.Contains("sample"));
    }

    private sealed class TestModuleLoaderLogger : Microsoft.Extensions.Logging.ILogger<ModuleLoader>
    {
        public readonly List<(Microsoft.Extensions.Logging.LogLevel LogLevel, string Message)> Logs = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }
}
