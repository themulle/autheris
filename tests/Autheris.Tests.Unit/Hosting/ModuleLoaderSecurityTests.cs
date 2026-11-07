namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Autheris.Api.Hosting;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Infrastructure.Modules.Trust;
using Autheris.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ModuleLoaderSecurityTests : IDisposable
{
    private readonly string _tempRoot;

    public ModuleLoaderSecurityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "autheris_modsec_" + Guid.NewGuid().ToString("N"));
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
    public void Manifest_with_unknown_field_is_rejected()
    {
        const string jsonWithUnknownField = """
        {
          "schema": 1,
          "id": "sample",
          "version": "1.2.0",
          "hostVersion": "1.2.0",
          "abstractionsVersion": "1.0.0",
          "entryAssembly": "Sample.TestModule01.dll",
          "entryType": "Autheris.Tests.Unit.Hosting.SampleValidModule",
          "files": [],
          "keyId": "0123456789abcdef",
          "signature": "c2ln",
          "unexpectedExtraField": true
        }
        """;

        var ex = Should.Throw<FormatException>(() => ModuleManifest.Parse(jsonWithUnknownField));
        ex.Message.ShouldContain("Unknown field 'unexpectedExtraField'");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Manifest_with_duplicate_json_keys_is_rejected()
    {
        const string jsonWithDuplicateKeys = """
        {
          "schema": 1,
          "id": "sample",
          "id": "evil-override",
          "version": "1.2.0",
          "hostVersion": "1.2.0",
          "abstractionsVersion": "1.0.0",
          "entryAssembly": "Sample.TestModule01.dll",
          "entryType": "Autheris.Tests.Unit.Hosting.SampleValidModule",
          "files": [],
          "keyId": "0123456789abcdef",
          "signature": "c2ln"
        }
        """;

        var ex = Should.Throw<FormatException>(() => ModuleManifest.Parse(jsonWithDuplicateKeys));
        ex.Message.ShouldContain("Duplicate JSON property 'id'");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void File_modified_after_verification_is_not_loaded_from_disk()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = Path.Combine(_tempRoot, "sample");
        Directory.CreateDirectory(moduleDir);

        var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        var dllPath = Path.Combine(moduleDir, "Sample.TestModule01.dll");
        File.WriteAllBytes(dllPath, dllBytes);

        var manifest = ModulePackageBuilder.CreateSignedManifest(
            key,
            files: [new ModuleManifestFile("Sample.TestModule01.dll", IntegrityHashHelper.ComputeSha256HexLower(dllBytes))]);

        // Step 1: Verify and read into memory once
        var verifiedImage = FileHashVerifier.VerifyAndLoad(moduleDir, manifest);

        // Step 2: Corrupt/delete the file on disk after verification (simulating a TOCTOU swap)
        File.WriteAllBytes(dllPath, [0x00, 0xFF, 0x00, 0xFF]);

        // Step 3: ModuleAssemblyLoadContext loads strictly from the in-memory VerifiedModuleImage buffer
        var alc = new ModuleAssemblyLoadContext(verifiedImage);
        var loadedAssembly = alc.LoadEntryAssembly();
        loadedAssembly.ShouldNotBeNull();
        loadedAssembly.GetType(typeof(SampleValidModule).FullName!).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ModuleDirectory_writable_in_production_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedModuleForEntryType(key, "sample", typeof(SampleValidModule));

        var services = new ServiceCollection();
        var config = BuildConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        // Newly created temp directory is writable by the current user; without override, production check fails closed.
        var loader = new ModuleLoader(publicKeyOverride: key);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.Reason.ShouldContain("writable");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Host_assembly_collision_in_module_folder_fails_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = Path.Combine(_tempRoot, "sample");
        Directory.CreateDirectory(moduleDir);

        var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        var collidingBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00 };
        File.WriteAllBytes(Path.Combine(moduleDir, "Sample.TestModule01.dll"), dllBytes);
        File.WriteAllBytes(Path.Combine(moduleDir, "Autheris.Domain.dll"), collidingBytes);

        var manifest = ModulePackageBuilder.CreateSignedManifest(
            key,
            files:
            [
                new ModuleManifestFile("Sample.TestModule01.dll", IntegrityHashHelper.ComputeSha256HexLower(dllBytes)),
                new ModuleManifestFile("Autheris.Domain.dll", IntegrityHashHelper.ComputeSha256HexLower(collidingBytes)),
            ]);
        File.WriteAllText(Path.Combine(moduleDir, "module.json"), ModulePackageBuilder.SerializeManifestJson(manifest));

        var services = new ServiceCollection();
        var config = BuildConfig("sample");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("collides with host assembly");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_cannot_replace_IAuditWriter()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedModuleForEntryType(key, "evil-audit", typeof(AuditReplacingModule));

        var services = new ServiceCollection();
        services.AddSingleton<IAuditWriter>(new FakeAuditWriter());
        var config = BuildConfig("evil-audit");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("evil-audit");
        ex.Reason.ShouldContain("IAuditWriter");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_cannot_add_IAuthorizationHandler()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedModuleForEntryType(key, "evil-authz", typeof(AuthHandlerAddingModule));

        var services = new ServiceCollection();
        services.AddAuthorization();
        var config = BuildConfig("evil-authz");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("evil-authz");
        ex.Reason.ShouldContain("IAuthorizationHandler");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_cannot_add_IClaimsTransformation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedModuleForEntryType(key, "evil-claims", typeof(ClaimsTransformationAddingModule));

        var services = new ServiceCollection();
        var config = BuildConfig("evil-claims");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("evil-claims");
        ex.Reason.ShouldContain("IClaimsTransformation");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_cannot_replace_IColumnMaskingProvider()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedModuleForEntryType(key, "evil-masking", typeof(ColumnMaskingReplacingModule));

        var services = new ServiceCollection();
        services.AddSingleton<IColumnMaskingProvider>(new ColumnMaskingReplacingModule.NoopColumnMaskingProvider());
        var config = BuildConfig("evil-masking");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("evil-masking");
        ex.Reason.ShouldContain("IColumnMaskingProvider");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Unsigned_rejected_in_production_even_with_flag()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedModuleForEntryType(key, "sample", typeof(SampleValidModule), signatureOverride: string.Empty);

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Path"] = _tempRoot,
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.Reason.ShouldContain("AllowUnsignedInDevelopment");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Plugin_trust_hash_does_not_authorize_module()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        var dllSha256 = IntegrityHashHelper.ComputeSha256HexLower(dllBytes);

        // Unsigned module, even though its DLL SHA-256 is in Gateway:Plugins:TrustedPluginHashes
        CreateSignedModuleForEntryType(key, "sample", typeof(SampleValidModule), signatureOverride: string.Empty);

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Path"] = _tempRoot,
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Plugins:TrustedPluginHashes:Sample.TestModule01.dll"] = dllSha256,
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
        ex.Reason.ShouldContain("unsigned");
    }

    private IConfiguration BuildConfig(string moduleId) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Path"] = _tempRoot,
                ["Gateway:Modules:Enabled:0"] = moduleId,
            })
            .Build();

    private string CreateSignedModuleForEntryType(
        ECDsa signingKey,
        string moduleId,
        Type entryType,
        string? signatureOverride = null)
    {
        var moduleDir = Path.Combine(_tempRoot, moduleId);
        Directory.CreateDirectory(moduleDir);

        var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        File.WriteAllBytes(Path.Combine(moduleDir, "Sample.TestModule01.dll"), dllBytes);

        var manifest = ModulePackageBuilder.CreateSignedManifest(
            signingKey,
            moduleId: moduleId,
            entryType: entryType.FullName!,
            files: [new ModuleManifestFile("Sample.TestModule01.dll", IntegrityHashHelper.ComputeSha256HexLower(dllBytes))],
            signatureOverride: signatureOverride);

        File.WriteAllText(Path.Combine(moduleDir, "module.json"), ModulePackageBuilder.SerializeManifestJson(manifest));
        return moduleDir;
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Workspace_fallback_throws_outside_development()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Modules:WorkspacePath"] = _tempRoot,
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.Reason.ShouldContain("outside the Development environment");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Workspace_fallback_requires_allow_unsigned()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Modules:WorkspacePath"] = _tempRoot,
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "false",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Development };

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.Reason.ShouldContain("AllowUnsignedInDevelopment");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Workspace_fallback_not_used_without_workspace_path()
    {
        var services = new ServiceCollection();
        var nonExistentPath = Path.Combine(_tempRoot, "no-such-path");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Path"] = nonExistentPath,
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Development };

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.Reason.ShouldContain("does not exist");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Workspace_fallback_does_not_search_parent_directories()
    {
        var subDir = Path.Combine(_tempRoot, "sub");
        Directory.CreateDirectory(subDir);
        var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        File.WriteAllBytes(Path.Combine(_tempRoot, "Autheris.Modules.Sample.dll"), dllBytes);

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Modules:WorkspacePath"] = subDir,
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Development };

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("sample");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_assembly_never_loads_into_default_context()
    {
        var wsDir = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(wsDir);
        var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        File.WriteAllBytes(Path.Combine(wsDir, "Autheris.Modules.Sample.dll"), dllBytes);

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Modules:WorkspacePath"] = wsDir,
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Development };

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var result = loader.LoadAndConfigureServices(services, config, env);
        result.Modules.Count.ShouldBe(1);
        var module = result.Modules[0];
        var asm = module.Instance.GetType().Assembly;
        var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm);
        alc.ShouldNotBe(System.Runtime.Loader.AssemblyLoadContext.Default);
        alc.ShouldBeOfType<ModuleAssemblyLoadContext>();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-01")]
    public void Workspace_path_outside_development_fails_startup()
    {
        var options = new Autheris.Domain.Options.GatewayOptions();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };
        var ex = Should.Throw<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator.ValidateStartup(
                options,
                env,
                key => key == "Gateway:Modules:WorkspacePath" ? "/workspace" : null));
        ex.Message.ShouldContain("Gateway:Modules:WorkspacePath");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-01")]
    public void Workspace_module_symlink_outside_root_is_rejected()
    {
        var wsDir = Path.Combine(_tempRoot, "ws");
        var outsideDir = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(wsDir);
        Directory.CreateDirectory(outsideDir);

        var targetDll = Path.Combine(outsideDir, "Target.dll");
        File.WriteAllBytes(targetDll, ModulePackageBuilder.GetValidModuleDllBytes());

        var linkDll = Path.Combine(wsDir, "Autheris.Modules.Sample.dll");
        File.CreateSymbolicLink(linkDll, targetDll);

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Modules:WorkspacePath"] = wsDir,
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Development };

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.Reason.ShouldContain("Symlink");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-01")]
    public void Workspace_load_logs_unsigned_warning()
    {
        var wsDir = Path.Combine(_tempRoot, "ws_warn");
        Directory.CreateDirectory(wsDir);
        var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        File.WriteAllBytes(Path.Combine(wsDir, "Autheris.Modules.Sample.dll"), dllBytes);

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = "sample",
                ["Gateway:Modules:WorkspacePath"] = wsDir,
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Development };

        var testLogger = new TestModuleLoaderLogger();
        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false, logger: testLogger);
        var result = loader.LoadAndConfigureServices(services, config, env);

        testLogger.Logs.ShouldContain(l => l.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("sample") && l.Message.Contains("unsigned"));
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
