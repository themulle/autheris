namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Hosting;
using Autheris.Infrastructure.Modules.Trust;
using Autheris.Modules.OpenMetadata;
using Autheris.Tests.Integration.Modules;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class ModuleTestServiceBridgeTests
{
    private static Assembly LoadIsolatedOpenMetadataAssembly()
    {
        var location = typeof(IOpenMetadataClient).Assembly.Location;
        var dllBytes = File.ReadAllBytes(location);
        var fileName = Path.GetFileName(location);
        var manifest = new ModuleManifest(
            Schema: 1,
            Id: "openmetadata",
            Version: "1.0.0",
            HostVersion: ModuleLoader.ResolveHostVersion(),
            AbstractionsVersion: "1.0.0",
            EntryAssembly: fileName,
            EntryType: typeof(OpenMetadataModule).FullName!,
            Files: [new ModuleManifestFile(fileName, IntegrityHashHelper.ComputeSha256HexLower(dllBytes))],
            KeyId: "test",
            Signature: string.Empty);

        var verifiedImage = new VerifiedModuleImage(
            "openmetadata",
            Path.GetDirectoryName(location)!,
            manifest,
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { [fileName] = dllBytes });

        var alc = new ModuleAssemblyLoadContext(verifiedImage);
        return alc.LoadEntryAssembly();
    }

    [Fact]
    public async Task ReplaceModuleService_BridgesCallsAcrossMultipleIsolatedModuleLoadContexts()
    {
        var fake = new RecordingClient();

        // First isolated ALC
        var asm1 = LoadIsolatedOpenMetadataAssembly();
        var clientType1 = asm1.GetType(typeof(IOpenMetadataClient).FullName!, throwOnError: true)!;
        clientType1.ShouldNotBe(typeof(IOpenMetadataClient));

        var services1 = new ServiceCollection();
        services1.AddSingleton(clientType1, new object());
        services1.ReplaceModuleService<IOpenMetadataClient>(fake);
        using var provider1 = services1.BuildServiceProvider();
        var bridged1 = provider1.GetRequiredService(clientType1);

        // Second isolated ALC in the same process (simulates multiple integration test hosts)
        var asm2 = LoadIsolatedOpenMetadataAssembly();
        var clientType2 = asm2.GetType(typeof(IOpenMetadataClient).FullName!, throwOnError: true)!;
        clientType2.ShouldNotBe(typeof(IOpenMetadataClient));
        clientType2.ShouldNotBe(clientType1);

        var services2 = new ServiceCollection();
        services2.AddSingleton(clientType2, new object());
        services2.ReplaceModuleService<IOpenMetadataClient>(fake);
        using var provider2 = services2.BuildServiceProvider();
        var bridged2 = provider2.GetRequiredService(clientType2);

        // Invoke GetTableByFqnAsync and GetTablesAsync on both bridged instances
        var getTable1 = clientType1.GetMethod(nameof(IOpenMetadataClient.GetTableByFqnAsync))!;
        var task1 = (Task)getTable1.Invoke(bridged1, ["svc.db.sch.first", CancellationToken.None])!;
        await task1;
        var table1 = task1.GetType().GetProperty("Result")!.GetValue(task1);
        table1.ShouldNotBeNull();
        clientType1.Assembly.GetType(typeof(OpenMetadataTable).FullName!)!
            .GetProperty(nameof(OpenMetadataTable.FullyQualifiedName))!
            .GetValue(table1)
            .ShouldBe("svc.db.sch.first");

        var getTables2 = clientType2.GetMethod(nameof(IOpenMetadataClient.GetTablesAsync))!;
        var task2 = (Task)getTables2.Invoke(bridged2, [null, CancellationToken.None])!;
        await task2;
        var list2 = task2.GetType().GetProperty("Result")!.GetValue(task2) as System.Collections.IEnumerable;
        list2.ShouldNotBeNull();
        list2.Cast<object>().Count().ShouldBe(1);

        fake.RequestedFqns.ShouldBe(["svc.db.sch.first"]);
    }

    [Fact]
    public async Task ReplaceModuleService_BridgesNSubstituteMockAndNullResultsAcrossLoadContext()
    {
        var mock = NSubstitute.Substitute.For<IOpenMetadataClient>();
        NSubstitute.SubstituteExtensions.Returns(
            mock.GetTableByFqnAsync("svc.db.sch.missing", NSubstitute.Arg.Any<CancellationToken>()),
            Task.FromResult<OpenMetadataTable?>(null));
        NSubstitute.SubstituteExtensions.Returns(
            mock.GetTablesAsync(NSubstitute.Arg.Any<string?>(), NSubstitute.Arg.Any<CancellationToken>()),
            Task.FromResult<IReadOnlyList<OpenMetadataTable>>(
            [
                new OpenMetadataTable
                {
                    Id = Guid.NewGuid(),
                    Name = "orders",
                    FullyQualifiedName = "svc.db.sch.orders",
                    Columns = [new OpenMetadataColumn { Name = "email", DataType = "VARCHAR" }],
                    Tags = [new OpenMetadataTag { TagFQN = "PII.Sensitive" }]
                }
            ]));

        var asm = LoadIsolatedOpenMetadataAssembly();
        var clientType = asm.GetType(typeof(IOpenMetadataClient).FullName!, throwOnError: true)!;
        var services = new ServiceCollection();
        services.AddSingleton(clientType, new object());
        services.ReplaceModuleService(mock);

        using var provider = services.BuildServiceProvider();
        var bridged = provider.GetRequiredService(clientType);

        var missingTask = (Task)clientType.GetMethod(nameof(IOpenMetadataClient.GetTableByFqnAsync))!
            .Invoke(bridged, ["svc.db.sch.missing", CancellationToken.None])!;
        await missingTask;
        missingTask.GetType().GetProperty("Result")!.GetValue(missingTask).ShouldBeNull();

        var tablesTask = (Task)clientType.GetMethod(nameof(IOpenMetadataClient.GetTablesAsync))!
            .Invoke(bridged, [null, CancellationToken.None])!;
        await tablesTask;
        var list = (tablesTask.GetType().GetProperty("Result")!.GetValue(tablesTask) as System.Collections.IEnumerable)!
            .Cast<object>()
            .ToList();
        list.Count.ShouldBe(1);
        clientType.Assembly.GetType(typeof(OpenMetadataTable).FullName!)!
            .GetProperty(nameof(OpenMetadataTable.FullyQualifiedName))!
            .GetValue(list[0])
            .ShouldBe("svc.db.sch.orders");
    }

    private sealed class RecordingClient : IOpenMetadataClient
    {
        public List<string> RequestedFqns { get; } = [];

        public Task<IReadOnlyList<OpenMetadataTable>> GetTablesAsync(string? service = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenMetadataTable>>(
            [
                new OpenMetadataTable { Id = Guid.NewGuid(), Name = "t1", FullyQualifiedName = "svc.db.sch.t1" }
            ]);

        public Task<OpenMetadataTable?> GetTableByFqnAsync(string fqn, CancellationToken ct = default)
        {
            RequestedFqns.Add(fqn);
            return Task.FromResult<OpenMetadataTable?>(
                new OpenMetadataTable { Id = Guid.NewGuid(), Name = "first", FullyQualifiedName = fqn });
        }

        public Task<IReadOnlyList<OpenMetadataPolicy>> GetPoliciesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([]);

        public Task<IReadOnlyList<OpenMetadataRole>> GetRolesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenMetadataRole>>([]);

        public Task<IReadOnlyList<OpenMetadataTeam>> GetTeamsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]);

        public Task<IReadOnlyList<OpenMetadataUser>> GetUsersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]);
    }
}
