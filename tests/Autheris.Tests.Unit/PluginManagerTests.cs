using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Plugins;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public sealed class PluginManagerTests
{
    private sealed class TestBillingPlugin : IHttpDataSourcePlugin
    {
        public string Name => "TestBillingPlugin";

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            PluginExecutionContext context,
            CancellationToken ct = default)
        {
            IReadOnlyList<IReadOnlyDictionary<string, object?>> result =
            [
                new Dictionary<string, object?>
                {
                    ["invoice_id"] = "INV-1001",
                    ["amount"] = 250.75m,
                    ["customer_id"] = context.Arguments.TryGetValue("customer_id", out var c) ? c : "CUST-DEFAULT"
                }
            ];

            return Task.FromResult(result);
        }
    }

    private static TableMetadata CreatePluginMetadata(string? pluginName, string? sourceName = "billing")
    {
        var id = new TableIdentifier("billing", "public", "invoices");
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = sourceName ?? string.Empty,
                SchemaName = "public",
                TableName = "invoices",
                DataSourceType = DataSourceType.HttpPlugin,
                PluginName = pluginName
            },
            Columns =
            [
                new TableColumn { ColumnName = "invoice_id", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "customer_id", DataType = "varchar" }
            ]
        };
    }

    private static DataSourceExecutionContext CreateContext(
        TableMetadata metadata,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var userSid = new Sid("S-1-5-21-1");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, userSid.Value)], "Test"));
        arguments ??= new Dictionary<string, object?>();

        var decision = TableAccessDecision.Allowed(
            metadata.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            null,
            hasUnconstrainedColumnAllow: true);

        return new DataSourceExecutionContext(
            SourceName: metadata.Table.SourceName,
            Metadata: metadata,
            Principal: principal,
            AccessDecision: decision,
            Arguments: arguments,
            RequestedFields: ["invoice_id", "amount", "customer_id"],
            RequestHeaders: null
        );
    }

    [Fact]
    public void RegisterPlugin_AddsPluginToRegistry()
    {
        using var manager = new PluginManager(NullLogger<PluginManager>.Instance);
        var plugin = new TestBillingPlugin();

        manager.RegisterPlugin(plugin);

        var retrieved = manager.GetPlugin("TestBillingPlugin");
        retrieved.ShouldNotBeNull();
        retrieved.Name.ShouldBe("TestBillingPlugin");

        var all = manager.GetAllPlugins();
        all.Count.ShouldBe(1);
        all.ShouldContain(plugin);
    }

    [Fact]
    public async Task PluginHttpDataSourceExecutor_ExecutesRegisteredPlugin()
    {
        using var manager = new PluginManager(NullLogger<PluginManager>.Instance);
        var plugin = new TestBillingPlugin();
        manager.RegisterPlugin(plugin);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var executor = new PluginHttpDataSourceExecutor(
            manager,
            httpClientFactory,
            NullLogger<PluginHttpDataSourceExecutor>.Instance);

        var metadata = CreatePluginMetadata("TestBillingPlugin");
        var context = CreateContext(metadata, new Dictionary<string, object?>
        {
            ["customer_id"] = "CUST-42"
        });

        var rows = await executor.ExecuteAsync(context);

        rows.Count.ShouldBe(1);
        rows[0]["invoice_id"].ShouldBe("INV-1001");
        rows[0]["amount"].ShouldBe(250.75m);
        rows[0]["customer_id"].ShouldBe("CUST-42");
    }

    [Fact]
    public async Task PluginHttpDataSourceExecutor_FallsBackToTableSourceName_WhenPluginNameIsNull()
    {
        using var manager = new PluginManager(NullLogger<PluginManager>.Instance);
        var plugin = new TestBillingPlugin();
        manager.RegisterPlugin(plugin);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var executor = new PluginHttpDataSourceExecutor(
            manager,
            httpClientFactory,
            NullLogger<PluginHttpDataSourceExecutor>.Instance);

        var metadata = CreatePluginMetadata(null, sourceName: "TestBillingPlugin");
        var context = CreateContext(metadata);

        var rows = await executor.ExecuteAsync(context);

        rows.Count.ShouldBe(1);
        rows[0]["invoice_id"].ShouldBe("INV-1001");
    }

    [Fact]
    public async Task PluginHttpDataSourceExecutor_ThrowsInvalidOperationException_WhenPluginNotFound()
    {
        using var manager = new PluginManager(NullLogger<PluginManager>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var executor = new PluginHttpDataSourceExecutor(
            manager,
            httpClientFactory,
            NullLogger<PluginHttpDataSourceExecutor>.Instance);

        var metadata = CreatePluginMetadata("NonExistentPlugin");
        var context = CreateContext(metadata);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("NonExistentPlugin");
    }

    [Fact]
    public void LoadPluginsFromDirectory_ReturnsZero_WhenDirectoryDoesNotExist()
    {
        using var manager = new PluginManager(NullLogger<PluginManager>.Instance);
        var count = manager.LoadPluginsFromDirectory("/non/existent/plugins/dir");
        count.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Signed_module_dll_is_not_loaded_as_plugin()
    {
        var tempPluginDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"autheris-plugin-mod-{Guid.NewGuid():N}");
        using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

        try
        {
            System.IO.Directory.CreateDirectory(tempPluginDir);
            var dllBytes = Autheris.Tests.Unit.Hosting.ModulePackageBuilder.GetValidModuleDllBytes();
            var dllSha = Autheris.Infrastructure.Modules.Trust.IntegrityHashHelper.ComputeSha256HexLower(dllBytes);
            var dllPath = System.IO.Path.Combine(tempPluginDir, "Sample.TestModule01.dll");
            System.IO.File.WriteAllBytes(dllPath, dllBytes);

            var manifest = Autheris.Tests.Unit.Hosting.ModulePackageBuilder.CreateSignedManifest(
                ecdsa,
                files: [new Autheris.Infrastructure.Modules.Trust.ModuleManifestFile("Sample.TestModule01.dll", dllSha)]);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(tempPluginDir, "module.json"),
                Autheris.Tests.Unit.Hosting.ModulePackageBuilder.SerializeManifestJson(manifest));

            var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
            {
                Plugins = new Autheris.Domain.Options.PluginsOptions
                {
                    TrustedPluginHashes =
                    {
                        ["Sample.TestModule01.dll"] = dllSha,
                    },
                },
            });

            using var manager = new PluginManager(NullLogger<PluginManager>.Instance, options: options);
            var loaded = manager.LoadPluginsFromDirectory(tempPluginDir);

            loaded.ShouldBe(0);
            manager.GetAllPlugins().ShouldBeEmpty();
        }
        finally
        {
            if (System.IO.Directory.Exists(tempPluginDir))
            {
                System.IO.Directory.Delete(tempPluginDir, recursive: true);
            }
        }
    }
}
