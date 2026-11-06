namespace Autheris.Tests.Unit.DataSources;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.DataSources.Changes;
using Autheris.Application.DataSources;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.OData.Services;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Extensions.OData;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Catalog;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #447 review round: change detection is gated on the table's source (SEC-ATR-04), discovery caches follow a hot reload
/// (SEC-ATR-07, REQ-ATR-10) and a scope without an ingress stamp is denied (SEC-ATR-14).
/// </summary>
[Trait("Category", "Security")]
public sealed class TransportGateHardeningTests
{
    private const string SecretSource = "secret_src";

    private static readonly TenantId Tenant = new("corp");
    private static readonly TableIdentifier Table = new("corp", "sales", "hidden_t");

    private static TableMetadata Row(string name = "hidden_t")
    {
        var row = Wp0.Table("corp", name, "Short " + name, ["id"]);
        return row with { Table = row.Table with { SourceName = SecretSource, SchemaName = "sales" }, Identifier = new TableIdentifier("corp", "sales", name) };
    }

    private static DataSourceConfigurationOptions Options_(DataSourceTransportFlags flags) => new()
    {
        Profiles = { [SecretSource] = new DataSourceProfileOptions { AllowedTransports = flags } }
    };

    private sealed class ReloadableMonitor<T>(T value) : IOptionsMonitor<T>
    {
        private readonly List<Action<T, string?>> _listeners = [];
        private T _value = value;

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return null;
        }

        public void Reload(T next)
        {
            _value = next;
            foreach (var listener in _listeners)
            {
                listener(next, null);
            }
        }
    }

    // ----- SEC-ATR-04: change detection reads the source, so it is gated -----

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1"), new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-1")], "test"));

    private static (GovernedChangeDetectionService Service, IChangeVersionProvider Provider, IDataSourceTransportGate Gate) ChangeService(bool allowed)
    {
        var metadata = new TableMetadata
        {
            Identifier = Table,
            PrimaryKeyColumns = ["id"],
            Table = new Table { SourceName = SecretSource, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql, SchemaName = "sales", TableName = "hidden_t", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Table, Arg.Any<CancellationToken>()).Returns(metadata);
        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Table, Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(
                Table,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["id"] = ColumnAccessLevel.Clear },
                hasUnconstrainedColumnAllow: true)));
        var provider = Substitute.For<IChangeVersionProvider>();
        var gate = Substitute.For<IDataSourceTransportGate>();
        gate.EnsureCatalogSourceAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => allowed ? Task.CompletedTask : Task.FromException(new TransportNotAllowedException(SecretSource, DataSourceTransportFlags.Rest)));
        var service = new GovernedChangeDetectionService(
            repo, pdp, provider, Substitute.For<IColumnMaskingProvider>(), Substitute.For<IAuditLogRepository>(),
            Options.Create(new ChangeDetectionOptions()), gate);
        return (service, provider, gate);
    }

    [Fact]
    public async Task SEC_ATR_04_ChangeToken_DeniedSource_NoVersionRead()
    {
        var (service, provider, gate) = ChangeService(allowed: false);

        await Should.ThrowAsync<TransportNotAllowedException>(() => service.CheckTableModifiedAsync(Table, User(), Tenant));

        await gate.Received(1).EnsureCatalogSourceAsync(SecretSource, Arg.Any<CancellationToken>());
        provider.ReceivedCalls().ShouldBeEmpty("a denied transport must not reach the version provider");
    }

    [Fact]
    public async Task SEC_ATR_04_ChangesSince_DeniedSource_NoChangeOrRowRead()
    {
        var (service, provider, _) = ChangeService(allowed: false);
        var cursor = service.IssueCursor(Table, User(), Tenant, syncVersion: 1L);
        provider.ClearReceivedCalls();

        await Should.ThrowAsync<TransportNotAllowedException>(
            () => service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant)));

        provider.ReceivedCalls().ShouldBeEmpty("a denied transport must not read changes or current rows");
    }

    [Fact]
    public async Task SEC_ATR_04_ChangeToken_AllowedSource_ReachesProvider()
    {
        var (service, provider, _) = ChangeService(allowed: true);
        provider.GetCurrentVersionAsync(Table, Arg.Any<CancellationToken>()).Returns(new ValueTask<long?>(3L));

        var result = await service.CheckTableModifiedAsync(Table, User(), Tenant);

        result.SupportsChangeTracking.ShouldBeTrue();
    }

    // ----- SEC-ATR-07 / REQ-ATR-10: discovery caches follow a hot reload -----

    [Fact]
    public async Task SEC_ATR_07_OpenApiCache_ReloadThatDeniesASource_DropsItFromTheDocument()
    {
        var monitor = new ReloadableMonitor<DataSourceConfigurationOptions>(Options_(DataSourceTransportFlags.OData));
        using var resolver = new HierarchicalDataSourceOptionsResolver(monitor);
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Row()]));
        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance, transportResolver: resolver);
        var cache = new OpenApiCacheManager(resolver);

        async Task<string> DocumentAsync() =>
            System.Text.Encoding.UTF8.GetString(await cache.GetOrAddAsync(null, isYaml: false, ct => generator.GenerateOpenApiJsonAsync(null, false, ct)));

        (await DocumentAsync()).ShouldContain("hidden_t");
        (await DocumentAsync()).ShouldContain("hidden_t");

        monitor.Reload(Options_(DataSourceTransportFlags.WebSql));

        (await DocumentAsync()).ShouldNotContain("hidden_t");
    }

    [Fact]
    public async Task SEC_ATR_07_ODataAuthorizedTablesCache_ReloadThatDeniesASource_DropsItFromDiscovery()
    {
        var monitor = new ReloadableMonitor<DataSourceConfigurationOptions>(Options_(DataSourceTransportFlags.OData));
        using var resolver = new HierarchicalDataSourceOptionsResolver(monitor);
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Row()]));
        var exec = Substitute.For<IGatewayExecutionService>();
        exec.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(TableAccessDecision.Allowed(
                ci.ArgAt<TableMetadata>(1).Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));
        var ingress = new IngressTransportContext();
        ingress.Set(DataSourceTransportFlags.OData);
        using var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000_000 });
        var handler = new ODataHandler(
            repo, exec, NullLogger<ODataHandler>.Instance, memoryCache: memory, transportResolver: resolver, ingress: ingress);
        var user = Wp0.User("S-USER", "corp");

        (await handler.GetMetadataCsdlAsync(user)).ShouldContain("hidden_t");
        (await handler.GetMetadataCsdlAsync(user)).ShouldContain("hidden_t");

        monitor.Reload(Options_(DataSourceTransportFlags.WebSql));

        (await handler.GetMetadataCsdlAsync(user)).ShouldNotContain("hidden_t");
        System.Text.Json.JsonSerializer.Serialize(await handler.GetServiceDocumentAsync("http://localhost/odata/v4", user)).ShouldNotContain("hidden_t");
    }

    [Fact]
    public void Resolver_Generation_IncreasesOnReload()
    {
        var monitor = new ReloadableMonitor<DataSourceConfigurationOptions>(Options_(DataSourceTransportFlags.OData));
        using var resolver = new HierarchicalDataSourceOptionsResolver(monitor);
        var before = resolver.Generation;

        monitor.Reload(Options_(DataSourceTransportFlags.WebSql));

        resolver.Generation.ShouldBeGreaterThan(before);
    }

    // ----- SEC-ATR-14: a system job without an ingress stamp is denied -----

    [Fact]
    public async Task SEC_ATR_14_BackgroundScopeWithoutIngressStamp_IsDeniedAtTheGate()
    {
        using var resolver = new HierarchicalDataSourceOptionsResolver(
            new ReloadableMonitor<DataSourceConfigurationOptions>(Options_(DataSourceTransportFlags.All)));
        var services = new ServiceCollection();
        services.AddSingleton<IDataSourceOptionsResolver>(resolver);
        services.AddScoped<IIngressTransportContext, IngressTransportContext>();
        services.AddSingleton(Substitute.For<ITransportDenialRecorder>());
        services.AddScoped<IDataSourceTransportGate, DataSourceTransportGate>();
        await using var provider = services.BuildServiceProvider();

        // a scheduled job opens its own scope and never copies an ingress: the source allows every transport, yet the call is refused
        using var scope = provider.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IDataSourceTransportGate>();

        gate.Ingress.ShouldBe(DataSourceTransportFlags.None);
        await Should.ThrowAsync<TransportNotAllowedException>(() => gate.EnsureAsync(SecretSource));
        await Should.ThrowAsync<TransportNotAllowedException>(() => gate.EnsureCatalogSourceAsync(SecretSource));
        gate.IsAllowed(SecretSource).ShouldBeFalse();
    }
}
