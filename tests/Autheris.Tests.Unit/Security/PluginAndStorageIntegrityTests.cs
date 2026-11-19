using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Common;
using Autheris.Domain.Security;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using NSubstitute.ExceptionExtensions;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Streaming.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class PluginAndStorageIntegrityTests
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

    [Fact]
    public void CRIT01_LocalStorageProvider_PrefixCollisionTraversal_ThrowsSecurityException()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "lakehouse_" + Guid.NewGuid().ToString("N"));
        var siblingDir = tempBase + "_secrets";
        Directory.CreateDirectory(tempBase);
        Directory.CreateDirectory(siblingDir);

        try
        {
            var options = Options.Create(new GatewayOptions
            {
                Lakehouse = new LakehouseOptions
                {
                    Storage = new LakehouseStorageOptions
                    {
                        LocalBasePath = tempBase
                    }
                }
            });

            var provider = new Autheris.Extensions.Lakehouse.Services.LocalStorageProvider(options);
            var attackLocation = Path.Combine(siblingDir, "secret.key");

            Should.Throw<System.Security.SecurityException>(() =>
                provider.ExistsAsync(attackLocation).AsTask().GetAwaiter().GetResult());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
            if (Directory.Exists(siblingDir)) Directory.Delete(siblingDir, true);
        }
    }
}
