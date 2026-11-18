using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using Autheris.Tests.Common;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>Issue #802: committed catalog writes rebuild the GraphQL schema promptly, and a client can see when it is current.</summary>
[Collection("CatalogRelation")]
public sealed class CatalogGraphQlChangeSignalTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public sealed class Query
    {
        public string Ping() => "pong";
    }

    private static TableMetadata MakeTable(string name, bool hidden = false) => new()
    {
        Identifier = new TableIdentifier("sales", "public", name),
        Table = new Table
        {
            SourceName = "pg_main",
            SourceType = "PostgreSql",
            SchemaName = "public",
            TableName = name,
            IsActive = true,
            DiscoveryHidden = hidden
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }],
        PrimaryKeyColumns = ["id"]
    };

    private static List<TableMetadata> Tables(int count) => Enumerable.Range(0, count).Select(i => MakeTable($"t{i}")).ToList();

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Time { get; } = new();
        public MetadataRepository Meta { get; }
        public VersionProvider Version { get; } = new(new CatalogVersion(1, 1));
        public SyncEventBus Bus { get; } = new();
        public CapturingLogger<CatalogGraphQlTypeModule> Log { get; } = new();
        public CatalogGraphQlTypeModule Module { get; }
        public IRequestExecutorProvider? Provider { get; private set; }

        public Harness(IReadOnlyList<TableMetadata> tables)
        {
            Meta = new MetadataRepository(tables);
            Module = new CatalogGraphQlTypeModule(
                Meta,
                new RelationRepository(),
                Log,
                refreshInterval: TimeSpan.Zero,
                versionProvider: Version,
                timeProvider: Time,
                eventBus: Bus);
        }

        public int BuildsStarted => Log.Entries.Count(e => e.Message.Contains("schema build started", StringComparison.Ordinal));

        public int BuildsFinished => Log.Entries.Count(e => e.Message.Contains("schema build finished", StringComparison.Ordinal));

        public async Task StartAsync(IRequestExecutorWarmupTask? warmup = null)
        {
            var builder = new ServiceCollection().AddGraphQLServer().AddQueryType<Query>().AddTypeModule(_ => Module)
                .AddDiagnosticEventListener(_ => new CatalogSchemaBuildDiagnosticListener(Module));
            if (warmup is not null)
            {
                builder.AddWarmupTask(_ => warmup, skipIf: false);
            }

            Provider = builder.Services.BuildServiceProvider().GetRequiredService<IRequestExecutorProvider>();
            await Provider.GetExecutorAsync();
            await WaitHelper.WaitUntil(() => BuildsFinished == 1, Wait);
        }

        public Task PublishAsync(long structure, long content) =>
            Bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new CatalogChangedEvent(structure, content, Time.GetUtcNow()));

        public async Task<IRequestExecutor> WaitForSchemaAsync(Func<IRequestExecutor, bool> predicate)
        {
            IRequestExecutor executor = await Provider!.GetExecutorAsync();
            await WaitHelper.WaitUntil(
                async () =>
                {
                    executor = await Provider!.GetExecutorAsync();
                    return predicate(executor);
                },
                Wait,
                pollInterval: TimeSpan.FromMilliseconds(25));
            return executor;
        }

        public void Dispose() => Module.Dispose();
    }

    [Fact]
    public async Task CatalogChangedEvent_RebuildsAfterDebounce_WithoutPeriodicTick()
    {
        using var h = new Harness([MakeTable("orders")]);
        await h.StartAsync();
        var reads = h.Meta.GetAllTablesCalls;

        // The version provider still returns the old (cached) version: the event-driven check must not exit on it.
        h.Meta.SetTables([MakeTable("orders"), MakeTable("extra")]);
        await h.PublishAsync(2, 1);

        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce - TimeSpan.FromMilliseconds(1));
        h.Meta.GetAllTablesCalls.ShouldBe(reads, "the check waits for the debounce");

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        var rebuilt = await h.WaitForSchemaAsync(e => e.Schema.Types.ContainsName("sales_public_extra"));
        rebuilt.Schema.Types.ContainsName("sales_public_orders").ShouldBeTrue();
    }

    [Fact]
    public async Task BurstOfEventsWithinDebounce_OneCheckOneRebuild()
    {
        using var h = new Harness(Tables(1));
        await h.StartAsync();
        var reads = h.Meta.GetAllTablesCalls;

        for (var i = 2; i <= 11; i++)
        {
            h.Meta.SetTables(Tables(i));
            await h.PublishAsync(i, 1);
            h.Time.Advance(TimeSpan.FromMilliseconds(50));
        }

        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce);
        await h.WaitForSchemaAsync(e => e.Schema.Types.ContainsName("sales_public_t10"));
        await WaitHelper.WaitUntil(() => h.BuildsFinished == 2, Wait);

        h.Meta.GetAllTablesCalls.ShouldBe(reads + 1, "one check for the whole write batch");
        h.BuildsStarted.ShouldBe(2, "startup plus one rebuild");
    }

    [Fact]
    public async Task ChangeWithoutSchemaEffect_NoRebuild_StatusClears()
    {
        using var h = new Harness(Tables(2));
        await h.StartAsync();
        (await h.Module.IsSchemaCurrentAsync()).ShouldBeTrue();
        var reads = h.Meta.GetAllTablesCalls;

        // A consent pattern grant bumps the content version only; the schema does not change.
        h.Version.Current = new CatalogVersion(1, 2);
        await h.PublishAsync(1, 2);
        (await h.Module.IsSchemaCurrentAsync()).ShouldBeFalse("the change is not checked yet");

        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce);
        await WaitHelper.WaitUntil(async () => await h.Module.IsSchemaCurrentAsync(), Wait);

        h.Meta.GetAllTablesCalls.ShouldBe(reads + 1);
        h.BuildsStarted.ShouldBe(1, "a write that does not change the schema never rebuilds it");
    }

    [Fact]
    public async Task EventStream_NoCheckUntilQuiet_ThenOneTrailingRebuild()
    {
        using var h = new Harness(Tables(1));
        await h.StartAsync();

        h.Meta.SetTables(Tables(2));
        await h.PublishAsync(2, 1);
        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce);
        await h.WaitForSchemaAsync(e => e.Schema.Types.ContainsName("sales_public_t1"));
        await WaitHelper.WaitUntil(() => h.BuildsFinished == 2, Wait);
        var reads = h.Meta.GetAllTablesCalls;

        for (var i = 3; i <= 22; i++)
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(500));
            h.Meta.SetTables(Tables(i));
            await h.PublishAsync(i, 1);
        }

        h.Time.Advance(CatalogGraphQlTypeModule.MinQuietPeriod - TimeSpan.FromMilliseconds(1));
        h.Meta.GetAllTablesCalls.ShouldBe(reads, "a write stream causes no check until it settles");

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await h.WaitForSchemaAsync(e => e.Schema.Types.ContainsName("sales_public_t21"));
        await WaitHelper.WaitUntil(() => h.BuildsFinished == 3, Wait);
        h.Meta.GetAllTablesCalls.ShouldBe(reads + 1);
        h.BuildsStarted.ShouldBe(3, "startup, leading edge and one trailing rebuild");
    }

    [Fact]
    public async Task SignalBeforeStartupBuild_NoCheck()
    {
        using var h = new Harness(Tables(1));
        await h.PublishAsync(2, 1);
        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce);
        await Task.Yield();

        h.Meta.GetAllTablesCalls.ShouldBe(0, "the startup build reads the current catalog anyway");
        (await h.Module.IsSchemaCurrentAsync()).ShouldBeFalse("no schema has been built yet");
    }

    [Fact]
    public async Task SchemaStatus_UpdatingUntilExecutorCreated_ThenCurrent()
    {
        using var h = new Harness(Tables(1));
        var warmup = new BlockingWarmup();
        await h.StartAsync(warmup);
        (await h.Module.IsSchemaCurrentAsync()).ShouldBeTrue();

        warmup.Block();
        h.Meta.SetTables(Tables(2));
        h.Version.Current = new CatalogVersion(2, 1);
        await h.PublishAsync(2, 1);
        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce);
        await warmup.Entered.Task.WaitAsync(Wait);

        (await h.Module.IsSchemaCurrentAsync()).ShouldBeFalse("the rebuilt executor is not created yet");

        warmup.Release();
        await h.WaitForSchemaAsync(e => e.Schema.Types.ContainsName("sales_public_t1"));
        await WaitHelper.WaitUntil(async () => await h.Module.IsSchemaCurrentAsync(), Wait);
    }

    [Fact]
    public async Task SchemaStatus_VersionNewerThanBuilt_ReportsUpdating()
    {
        // Another instance wrote the catalog and no event reached this one (in-process bus): the version shows it.
        using var h = new Harness(Tables(1));
        await h.StartAsync();
        h.Version.Current = new CatalogVersion(2, 1);

        (await h.Module.IsSchemaCurrentAsync()).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SchemaStatus_VersionReadFails_ReportsUpdating()
    {
        using var h = new Harness(Tables(1));
        await h.StartAsync();
        h.Version.Fail = true;

        (await h.Module.IsSchemaCurrentAsync()).ShouldBeFalse("an unknown catalog version must not be reported as current");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SchemaStatus_FailedBuild_ReportsUpdating()
    {
        using var h = new Harness(Tables(1));
        h.Meta.Fail = true;
        await h.StartAsync();

        (await h.Module.IsSchemaCurrentAsync()).ShouldBeFalse("the fail-closed schema without catalog tables is behind the catalog");
    }

    [Theory]
    [InlineData(true, 200, "Current")]
    [InlineData(false, 503, "Updating")]
    [Trait("Category", "Security")]
    public async Task HealthGraphQlSchema_BodyCarriesStatusOnly(bool current, int expectedStatus, string expectedText)
    {
        var (status, body, context) = await ExecuteStatusAsync(new FixedFreshness(current));

        status.ShouldBe(expectedStatus);
        context.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        if (!current)
        {
            context.Response.Headers.RetryAfter.ToString().ShouldBe("1");
        }

        using var doc = JsonDocument.Parse(body);
        string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))
            .ShouldBe("status", "no names, counts or versions to anonymous callers");
        doc.RootElement.GetProperty("status").GetString().ShouldBe(expectedText);
    }

    [Fact]
    public async Task HealthGraphQlSchema_NoModule_Returns404()
    {
        var (status, _, _) = await ExecuteStatusAsync(null);
        status.ShouldBe(404);
    }

    private static async Task<(int Status, string Body, HttpContext Context)> ExecuteStatusAsync(ICatalogSchemaFreshness? freshness)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var result = await HealthEndpoints.HandleGraphQlSchemaAsync(freshness, context, CancellationToken.None);
        var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        body.Seek(0, SeekOrigin.Begin);
        return (context.Response.StatusCode, await new StreamReader(body).ReadToEndAsync(), context);
    }

    private sealed class FixedFreshness(bool current) : ICatalogSchemaFreshness
    {
        public ValueTask<bool> IsSchemaCurrentAsync(CancellationToken ct = default) => ValueTask.FromResult(current);
    }

    private sealed class BlockingWarmup : IRequestExecutorWarmupTask
    {
        private volatile TaskCompletionSource? _release;

        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ApplyOnlyOnStartup => false;

        public void Block()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release() => _release?.TrySetResult();

        public async Task WarmupAsync(IRequestExecutor executor, CancellationToken cancellationToken)
        {
            var gate = _release;
            if (gate is null || gate.Task.IsCompleted)
            {
                return;
            }

            Entered.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Dispatches synchronously on the publisher's thread.</summary>
    private sealed class SyncEventBus : IEventBus
    {
        private readonly List<(string Channel, Func<object, Task> Handler)> _handlers = [];

        public event Action? ConnectionRestored { add { } remove { } }

        public async Task PublishAsync<T>(string channel, T message, CancellationToken ct = default)
        {
            foreach (var (c, handler) in _handlers.ToArray())
            {
                if (string.Equals(c, channel, StringComparison.Ordinal))
                {
                    await handler(message!);
                }
            }
        }

        public IDisposable Subscribe<T>(string channel, Func<T, Task> handler)
        {
            var entry = (channel, (Func<object, Task>)(o => o is T t ? handler(t) : Task.CompletedTask));
            _handlers.Add(entry);
            return new Unsubscribe(() => _handlers.Remove(entry));
        }

        public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);

        public Task<long> GetCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);

        private sealed class Unsubscribe(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }

    private sealed class MetadataRepository(IReadOnlyList<TableMetadata> initial) : ITableMetadataRepository
    {
        private IReadOnlyList<TableMetadata> _tables = initial;
        private int _calls;

        public bool Fail { get; set; }

        public int GetAllTablesCalls => Volatile.Read(ref _calls);

        public void SetTables(IReadOnlyList<TableMetadata> tables) => _tables = tables;

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Fail ? throw new InvalidOperationException("catalog unreachable") : Task.FromResult(_tables);
        }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(_tables.FirstOrDefault(t => t.Identifier.Equals(table)));

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
            => Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class RelationRepository : ITableRelationRepository
    {
        public bool BulkRelationsAuthoritative => true;

        public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TableRelation>>([]);

        public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TableRelation>>([]);

        public Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class VersionProvider(CatalogVersion initial) : ICatalogVersionProvider
    {
        public CatalogVersion Current { get; set; } = initial;

        public bool Fail { get; set; }

        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) =>
            Fail ? ValueTask.FromException<CatalogVersion>(new InvalidOperationException("store unreachable")) : ValueTask.FromResult(Current);

        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(Current.Formatted);
    }
}
