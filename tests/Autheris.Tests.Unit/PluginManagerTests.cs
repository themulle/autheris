using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Events;
using Autheris.Application.Plugins;
using Autheris.Domain.Common;
using Autheris.Domain.Events;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Modules.Trust;
using Autheris.Infrastructure.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingLogger : ILogger<PluginManager>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class SimpleActionHandler(string actionName) : IPluginActionHandler
    {
        public string ActionName => actionName;

        public Task<PluginActionResult> ExecuteActionAsync(PluginActionContext context, CancellationToken ct = default) =>
            Task.FromResult(PluginActionResult.Succeeded());
    }

    private sealed class SpyEventBus : IGatewayEventBus
    {
        public int SubscriptionCount { get; private set; }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IGatewayEvent
        {
            SubscriptionCount++;
            return new DummyDisposable();
        }

        public IDisposable Subscribe(Type eventType, Func<IGatewayEvent, CancellationToken, Task> handler)
        {
            SubscriptionCount++;
            return new DummyDisposable();
        }

        public Task PublishAsync(IGatewayEvent @event, CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : IGatewayEvent => Task.CompletedTask;

        private sealed class DummyDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static byte[] BuildPluginDllBytes(
        string assemblyName,
        string? httpPluginName = null,
        string? actionHandlerName = null,
        bool includeEventListener = false,
        bool referenceMissingAssembly = false)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule(assemblyName + ".dll");

        if (httpPluginName != null)
        {
            var tb = mb.DefineType($"{assemblyName}.DynamicHttpPlugin", TypeAttributes.Public | TypeAttributes.Class);
            tb.AddInterfaceImplementation(typeof(IHttpDataSourcePlugin));
            DefineDefaultCtor(tb);
            DefineStringGetter(tb, typeof(IHttpDataSourcePlugin).GetProperty(nameof(IHttpDataSourcePlugin.Name))!, httpPluginName);

            var execImpl = tb.DefineMethod(
                nameof(IHttpDataSourcePlugin.ExecuteAsync),
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                typeof(Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>),
                [typeof(PluginExecutionContext), typeof(CancellationToken)]);
            var il = execImpl.GetILGenerator();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(typeof(IReadOnlyList<IReadOnlyDictionary<string, object?>>)));
            il.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(execImpl, typeof(IHttpDataSourcePlugin).GetMethod(nameof(IHttpDataSourcePlugin.ExecuteAsync))!);
            tb.CreateType();
        }

        if (actionHandlerName != null)
        {
            var tb = mb.DefineType($"{assemblyName}.DynamicActionHandler", TypeAttributes.Public | TypeAttributes.Class);
            tb.AddInterfaceImplementation(typeof(IPluginActionHandler));
            DefineDefaultCtor(tb);
            DefineStringGetter(tb, typeof(IPluginActionHandler).GetProperty(nameof(IPluginActionHandler.ActionName))!, actionHandlerName);

            var execImpl = tb.DefineMethod(
                nameof(IPluginActionHandler.ExecuteActionAsync),
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                typeof(Task<PluginActionResult>),
                [typeof(PluginActionContext), typeof(CancellationToken)]);
            var il = execImpl.GetILGenerator();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(typeof(PluginActionResult)));
            il.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(execImpl, typeof(IPluginActionHandler).GetMethod(nameof(IPluginActionHandler.ExecuteActionAsync))!);
            tb.CreateType();
        }

        if (includeEventListener)
        {
            var listenerIface = typeof(IPluginEventListener<AccessPlanCreatedEvent>);
            var tb = mb.DefineType($"{assemblyName}.DynamicEventListener", TypeAttributes.Public | TypeAttributes.Class);
            tb.AddInterfaceImplementation(listenerIface);
            DefineDefaultCtor(tb);

            var handleImpl = tb.DefineMethod(
                nameof(IPluginEventListener<AccessPlanCreatedEvent>.HandleEventAsync),
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                typeof(Task),
                [typeof(AccessPlanCreatedEvent), typeof(CancellationToken)]);
            var il = handleImpl.GetILGenerator();
            il.Emit(OpCodes.Call, typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetGetMethod()!);
            il.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(handleImpl, listenerIface.GetMethod(nameof(IPluginEventListener<AccessPlanCreatedEvent>.HandleEventAsync))!);
            tb.CreateType();
        }

        if (referenceMissingAssembly)
        {
            var missingAb = new PersistedAssemblyBuilder(new AssemblyName("NonExistentPluginDependency_" + Guid.NewGuid().ToString("N")), typeof(object).Assembly);
            var missingMb = missingAb.DefineDynamicModule("Missing.dll");
            var missingBase = missingMb.DefineType("Missing.BaseClass", TypeAttributes.Public | TypeAttributes.Class).CreateType();

            var brokenTb = mb.DefineType($"{assemblyName}.BrokenType", TypeAttributes.Public | TypeAttributes.Class, missingBase);
            brokenTb.CreateType();
        }

        using var ms = new MemoryStream();
        ab.Save(ms);
        return ms.ToArray();
    }

    private static void DefineDefaultCtor(TypeBuilder tb)
    {
        var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
    }

    private static void DefineStringGetter(TypeBuilder tb, PropertyInfo ifaceProp, string value)
    {
        var getter = tb.DefineMethod(
            "get_" + ifaceProp.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.SpecialName,
            typeof(string),
            Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldstr, value);
        il.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(getter, ifaceProp.GetGetMethod()!);
    }

    private static (PluginManager Manager, RecordingLogger Logger, SpyEventBus Bus) CreateTrustedManager(
        string environmentName,
        params (string FileName, byte[] Bytes)[] trustedFiles)
    {
        var logger = new RecordingLogger();
        var bus = new SpyEventBus();
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(environmentName));
        services.AddSingleton<IGatewayEventBus>(bus);

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fileName, bytes) in trustedFiles)
        {
            hashes[fileName] = IntegrityHashHelper.ComputeSha256HexLower(bytes);
        }

        var options = Options.Create(new GatewayOptions
        {
            Plugins = new PluginsOptions
            {
                RequireIntegrityManifest = true,
                TrustedPluginHashes = hashes
            }
        });

        var manager = new PluginManager(logger, services.BuildServiceProvider(), options);
        return (manager, logger, bus);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Duplicate_name_with_builtin_rejects_the_dll()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-dp10-builtin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllBytes = BuildPluginDllBytes("CollidingPlugin", httpPluginName: "TestBillingPlugin", actionHandlerName: "rogue.action", includeEventListener: true);
            File.WriteAllBytes(Path.Combine(tempDir, "CollidingPlugin.dll"), dllBytes);

            var (manager, logger, bus) = CreateTrustedManager(Environments.Production, ("CollidingPlugin.dll", dllBytes));
            using (manager)
            {
                var builtin = new TestBillingPlugin();
                manager.RegisterPlugin(builtin);

                var loaded = manager.LoadPluginsFromDirectory(tempDir);

                loaded.ShouldBe(0);
                manager.GetPlugin("TestBillingPlugin").ShouldBeSameAs(builtin);
                manager.GetActionHandler("rogue.action").ShouldBeNull();
                manager.GetAllListeners().ShouldBeEmpty();
                bus.SubscriptionCount.ShouldBe(0);
                logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("TestBillingPlugin"));
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Duplicate_action_handler_rejects_both()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-dp10-action-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var firstBytes = BuildPluginDllBytes("ActionA", actionHandlerName: "sync.roles");
            var secondBytes = BuildPluginDllBytes("ActionB", actionHandlerName: "SYNC.ROLES");
            var thirdBytes = BuildPluginDllBytes("GoodPlugin", httpPluginName: "HealthyPlugin");

            File.WriteAllBytes(Path.Combine(tempDir, "ActionA.dll"), firstBytes);
            File.WriteAllBytes(Path.Combine(tempDir, "ActionB.dll"), secondBytes);
            File.WriteAllBytes(Path.Combine(tempDir, "GoodPlugin.dll"), thirdBytes);

            var (manager, logger, _) = CreateTrustedManager(
                Environments.Production,
                ("ActionA.dll", firstBytes),
                ("ActionB.dll", secondBytes),
                ("GoodPlugin.dll", thirdBytes));

            using (manager)
            {
                var loaded = manager.LoadPluginsFromDirectory(tempDir);

                loaded.ShouldBe(1);
                manager.GetActionHandler("sync.roles").ShouldBeNull();
                manager.GetAllActionHandlers().ShouldBeEmpty();
                manager.GetPlugin("HealthyPlugin").ShouldNotBeNull();
                logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("sync.roles", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rejected_assembly_listener_is_not_bound()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-dp10-listener-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllABytes = BuildPluginDllBytes("PluginA", httpPluginName: "SharedName", includeEventListener: true);
            var dllBBytes = BuildPluginDllBytes("PluginB", httpPluginName: "sharedname", includeEventListener: true);

            File.WriteAllBytes(Path.Combine(tempDir, "PluginA.dll"), dllABytes);
            File.WriteAllBytes(Path.Combine(tempDir, "PluginB.dll"), dllBBytes);

            var (manager, _, bus) = CreateTrustedManager(
                Environments.Production,
                ("PluginA.dll", dllABytes),
                ("PluginB.dll", dllBBytes));

            using (manager)
            {
                var loaded = manager.LoadPluginsFromDirectory(tempDir);

                loaded.ShouldBe(0);
                manager.GetAllPlugins().ShouldBeEmpty();
                manager.GetAllListeners().ShouldBeEmpty();
                bus.SubscriptionCount.ShouldBe(0);
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Integrity_failure_is_fatal_in_non_development()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-dp10-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var validBytes = BuildPluginDllBytes("ValidPlugin", httpPluginName: "ValidPlugin");
            var tamperedBytes = BuildPluginDllBytes("TamperedPlugin", httpPluginName: "TamperedPlugin");

            File.WriteAllBytes(Path.Combine(tempDir, "TamperedPlugin.dll"), tamperedBytes);

            // Configure expected hash for ValidPlugin bytes, while TamperedPlugin.dll has mismatched bytes on disk.
            var (manager, _, _) = CreateTrustedManager(
                Environments.Production,
                ("TamperedPlugin.dll", validBytes));

            using (manager)
            {
                Should.Throw<SecurityException>(() => manager.LoadPluginsFromDirectory(tempDir));
                manager.GetAllPlugins().ShouldBeEmpty();
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Load_order_is_ordinal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-dp10-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Ordinal: "B_plugin.dll" (66) precedes "a_plugin.dll" (97); OrdinalIgnoreCase would put 'a' first.
            var lowerBytes = BuildPluginDllBytes("a_plugin", httpPluginName: "PluginLowerA");
            var upperBytes = BuildPluginDllBytes("B_plugin", httpPluginName: "PluginUpperB");

            File.WriteAllBytes(Path.Combine(tempDir, "a_plugin.dll"), lowerBytes);
            File.WriteAllBytes(Path.Combine(tempDir, "B_plugin.dll"), upperBytes);

            var (manager, logger, _) = CreateTrustedManager(
                Environments.Production,
                ("a_plugin.dll", lowerBytes),
                ("B_plugin.dll", upperBytes));

            using (manager)
            {
                var loaded = manager.LoadPluginsFromDirectory(tempDir);
                loaded.ShouldBe(2);

                var loadMessages = logger.Entries
                    .Where(e => e.Message.Contains("Successfully loaded HTTP data source plugin", StringComparison.Ordinal))
                    .Select(e => e.Message)
                    .ToList();

                loadMessages.Count.ShouldBe(2);
                loadMessages[0].ShouldContain("PluginUpperB");
                loadMessages[1].ShouldContain("PluginLowerA");
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Unlisted_dll_outside_development_aborts_plugin_loading()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-dp10-unlisted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var listedBytes = BuildPluginDllBytes("A_Listed", httpPluginName: "ListedPlugin");
            var unlistedBytes = BuildPluginDllBytes("B_Unlisted", httpPluginName: "UnlistedPlugin");

            File.WriteAllBytes(Path.Combine(tempDir, "A_Listed.dll"), listedBytes);
            File.WriteAllBytes(Path.Combine(tempDir, "B_Unlisted.dll"), unlistedBytes);

            var (manager, _, _) = CreateTrustedManager(
                Environments.Production,
                ("A_Listed.dll", listedBytes));

            using (manager)
            {
                Should.Throw<SecurityException>(() => manager.LoadPluginsFromDirectory(tempDir));
                manager.GetAllPlugins().ShouldBeEmpty();
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Type_load_failure_does_not_abort_other_plugins()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-dp10-typeload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brokenBytes = BuildPluginDllBytes("A_Broken", referenceMissingAssembly: true);
            var healthyBytes = BuildPluginDllBytes("B_Healthy", httpPluginName: "HealthyPlugin");

            File.WriteAllBytes(Path.Combine(tempDir, "A_Broken.dll"), brokenBytes);
            File.WriteAllBytes(Path.Combine(tempDir, "B_Healthy.dll"), healthyBytes);

            var (manager, _, _) = CreateTrustedManager(
                Environments.Production,
                ("A_Broken.dll", brokenBytes),
                ("B_Healthy.dll", healthyBytes));

            using (manager)
            {
                var loaded = manager.LoadPluginsFromDirectory(tempDir);

                loaded.ShouldBe(1);
                manager.GetPlugin("HealthyPlugin").ShouldNotBeNull();
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
