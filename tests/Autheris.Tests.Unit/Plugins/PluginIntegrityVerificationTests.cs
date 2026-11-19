namespace Autheris.Tests.Unit.Plugins;

using System;
using System.IO;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Streaming.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class PluginIntegrityVerificationTests
{
    [Fact]
    public void CRIT01_PluginIntegrityVerification_TamperedHash_ThrowsSecurityException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "plugin_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllPath = Path.Combine(tempDir, "TestPlugin.dll");
            File.WriteAllBytes(dllPath, [0x4D, 0x5A, 0x90, 0x00]);

            var manifestPath = Path.Combine(tempDir, "manifest.json");
            File.WriteAllText(manifestPath, "{\"plugins\": [{\"file\": \"TestPlugin.dll\", \"sha256\": \"0000000000000000000000000000000000000000000000000000000000000000\"}]}");

            // SEC M-27: the trust anchor is the configuration, not the manifest next to the DLL.
            var pluginOptions = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
            {
                Plugins = new Autheris.Domain.Options.PluginsOptions
                {
                    TrustedPluginHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["TestPlugin.dll"] = "0000000000000000000000000000000000000000000000000000000000000000"
                    }
                }
            });
            var manager = new Autheris.Infrastructure.Plugins.PluginManager(NullLogger<Autheris.Infrastructure.Plugins.PluginManager>.Instance, null, pluginOptions);
            var ex = Should.Throw<System.Security.SecurityException>(() => manager.LoadPluginsFromDirectory(tempDir));
            ex.Message.ShouldContain("Integrity check failed");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
