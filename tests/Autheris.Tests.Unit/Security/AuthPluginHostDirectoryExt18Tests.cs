namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Plugins;
using Autheris.Infrastructure.Plugins.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>EXT-18: the auth plugin host tolerates non-.NET DLLs and verifies "plugins"-style manifests.</summary>
public sealed class AuthPluginHostDirectoryExt18Tests
{
    private sealed class DevEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static AuthPluginHost DevHost(string dir)
    {
        var options = new GatewayOptions { Plugins = new PluginsOptions { Directory = dir, RequireIntegrityManifest = false } };
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new DevEnvironment());
        return new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance, serviceProvider: services.BuildServiceProvider());
    }

    [Fact]
    public void NonDotNetDll_IsSkipped_AndDoesNotAbortStartup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autheris_ext18_native_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "native_driver.dll"), [0x4d, 0x5a, 0x90, 0x00, 0x03, 0x00]);

            DevHost(dir).LoadPluginsFromDirectory(dir).ShouldBe(0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PluginsStyleManifest_IsVerified_HashMismatchIsFatal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autheris_ext18_manifest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Plugin.dll"), [0x4d, 0x5a, 0x90, 0x00, 0x01]);
            File.WriteAllText(
                Path.Combine(dir, "manifest.json"),
                "{\"plugins\":[{\"file\":\"Plugin.dll\",\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}]}");

            Should.Throw<SecurityException>(() => DevHost(dir).LoadPluginsFromDirectory(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void UnparsableManifest_IsFatal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autheris_ext18_badmanifest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Plugin.dll"), [0x4d, 0x5a, 0x90, 0x00, 0x01]);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{ not json");

            Should.Throw<SecurityException>(() => DevHost(dir).LoadPluginsFromDirectory(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
