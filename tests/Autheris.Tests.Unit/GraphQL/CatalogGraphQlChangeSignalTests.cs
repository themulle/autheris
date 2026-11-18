using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using Autheris.Tests.Common;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>Issue #802: committed catalog writes rebuild the GraphQL schema promptly, and a client can see when it is current.</summary>
[Collection("CatalogRelation")]
public sealed class CatalogGraphQlChangeSignalTests
{
    private const string StatusRoute = "/api/v1/catalog/graphql-schema-status";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static TableMetadata MakeTable(string name) => new()
    {
        Identifier = new TableIdentifier("sales", "public", name),
        Table = new Table
        {
            SourceName = "pg_main",
            SourceType = "PostgreSql",
            SchemaName = "public",
            TableName = name,
            IsActive = true
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }],
        PrimaryKeyColumns = ["id"]
    };

    private static List<TableMetadata> Tables(int count) => Enumerable.Range(0, count).Select(i => MakeTable($"t{i}")).ToList();

    [Fact]
    public async Task CatalogChangedEvent_RebuildsAfterDebounce_WithoutPeriodicTick()
    {
        using var h = new Harness([MakeTable("orders")]);
        await h.StartAsync();
        var reads = h.Meta.GetAllTablesCalls;

        // The version provider still returns the old (cached) version: the event-driven check must not exit on it.
        h.Meta.SetTables([MakeTable("orders"), MakeTable("extra")]);
        await h.PublishAsync();

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
            await h.PublishAsync();
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
        (await h.StatusAsync()).ShouldBeTrue();
        var reads = h.Meta.GetAllTablesCalls;

        // A consent pattern grant bumps the content version only; the schema does not change.
        h.Meta.Version = new CatalogVersion(1, 2);
        await h.PublishAsync();
        (await h.StatusAsync()).ShouldBeFalse("the change is not checked yet");

        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce);
        await WaitHelper.WaitUntil(h.StatusAsync, Wait);

        h.Meta.GetAllTablesCalls.ShouldBe(reads + 1);
        h.BuildsStarted.ShouldBe(1, "a write that does not change the schema never rebuilds it");
    }

    [Fact]
    public async Task EventStream_NoCheckUntilQuiet_ThenOneTrailingRebuild()
    {
        using var h = new Harness(Tables(1));
        await h.StartAsync();

        h.Meta.SetTables(Tables(2));
        await h.PublishAsync();
        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce);
        await h.WaitForSchemaAsync(e => e.Schema.Types.ContainsName("sales_public_t1"));
        await WaitHelper.WaitUntil(() => h.BuildsFinished == 2, Wait);
        var reads = h.Meta.GetAllTablesCalls;

        for (var i = 3; i <= 22; i++)
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(500));
            h.Meta.SetTables(Tables(i));
            await h.PublishAsync();
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
    public async Task ContinuousEventStream_ChecksAtTheDeferralCap()
    {
        using var h = new Harness(Tables(1));
        await h.StartAsync();
        var reads = h.Meta.GetAllTablesCalls;

        // One event per second for six minutes: the quiet period (2 s) never passes.
        for (var second = 0; second <= 360; second++)
        {
            await h.PublishAsync();
            h.Time.Advance(TimeSpan.FromSeconds(1));
            if (second == 0)
            {
                await WaitHelper.WaitUntil(() => h.Meta.GetAllTablesCalls == reads + 1, Wait, "leading-edge check");
            }
            else if (second == 200)
            {
                h.Meta.GetAllTablesCalls.ShouldBe(reads + 1, "no check while the stream continues");
            }
        }

        await WaitHelper.WaitUntil(() => h.Meta.GetAllTablesCalls >= reads + 2, Wait, "check at the deferral cap");
        h.Meta.GetAllTablesCalls.ShouldBe(reads + 2, "exactly one check after five minutes of continuous change");
    }

    [Fact]
    public async Task DeferredRebuild_FollowUpCheckEndsTheBurst()
    {
        using var h = new Harness(Tables(1));
        await h.DeferWithoutRebuildAsync();
        h.Module.HasScheduledSignalCheck.ShouldBeTrue("a deferred rebuild schedules one follow-up check");
        var reads = h.Meta.GetAllTablesCalls;

        h.Time.Advance(CatalogGraphQlTypeModule.MinQuietPeriod - TimeSpan.FromMilliseconds(1));
        h.Meta.GetAllTablesCalls.ShouldBe(reads);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitHelper.WaitUntil(() => h.Meta.GetAllTablesCalls == reads + 1, Wait);
        await WaitHelper.WaitUntil(
            () => h.Log.Entries.Any(e => e.Message.Contains("rebuild request coalesced", StringComparison.Ordinal)),
            Wait,
            "the quiet follow-up check ends the burst (the request is already outstanding, so it coalesces)");
        h.Module.HasScheduledSignalCheck.ShouldBeFalse("one follow-up, not a chain");
    }

    [Fact]
    public async Task DeferredRebuild_PeriodicTickDueFirst_NoFollowUpCheck()
    {
        using var h = new Harness(Tables(1), refreshInterval: TimeSpan.FromSeconds(1));
        await h.DeferWithoutRebuildAsync();

        h.Module.HasScheduledSignalCheck.ShouldBeFalse("the periodic tick ends the burst before a follow-up would run");
    }

    [Fact]
    public async Task SignalBeforeStartupBuild_NoCheck()
    {
        using var h = new Harness(Tables(1));
        await h.PublishAsync();
        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce);
        await Task.Yield();

        h.Meta.GetAllTablesCalls.ShouldBe(0, "the startup build reads the current catalog anyway");
        (await h.Module.IsSchemaCurrentAsync()).ShouldBeFalse("no schema has been built yet");
    }

    [Fact]
    public async Task Dispose_ReleasesSubscriptionAndTimer_NoCheckAfterwards()
    {
        using var h = new Harness(Tables(1));
        await h.StartAsync();
        var reads = h.Meta.GetAllTablesCalls;

        await h.PublishAsync(); // arms the debounce timer
        h.Module.Dispose();
        h.Bus.HandlerCount.ShouldBe(0);

        h.Meta.SetTables(Tables(2));
        await h.PublishAsync();
        h.Time.Advance(CatalogGraphQlTypeModule.MaxQuietPeriod); // a disposed timer never fires

        h.Meta.GetAllTablesCalls.ShouldBe(reads, "a disposed module runs no check");
    }

    [Fact]
    public async Task SchemaStatus_UpdatingUntilExecutorCreated_ThenCurrent()
    {
        using var h = new Harness(Tables(1));
        var warmup = new BlockingWarmup();
        await h.StartAsync(warmup);
        (await h.StatusAsync()).ShouldBeTrue();

        warmup.Block();
        h.Meta.SetTables(Tables(2));
        h.Meta.Version = new CatalogVersion(2, 1);
        await h.PublishAsync();
        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce);
        await warmup.Entered.Task.WaitAsync(Wait);

        (await h.StatusAsync()).ShouldBeFalse("the rebuilt executor is not created yet");

        warmup.Release();
        await h.WaitForSchemaAsync(e => e.Schema.Types.ContainsName("sales_public_t1"));
        await WaitHelper.WaitUntil(h.StatusAsync, Wait);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SchemaStatus_EventNotDelivered_StoreVersionDecides()
    {
        using var h = new Harness(Tables(1));
        await h.StartAsync();

        // A committed write whose event is still queued (or dropped, or sent by another instance); the provider cache is warm.
        h.Meta.Version = new CatalogVersion(2, 1);
        (await h.StatusAsync()).ShouldBeFalse("the store is ahead of the served schema");

        await h.PublishAsync();
        h.Time.Advance(CatalogGraphQlTypeModule.ChangeSignalDebounce);
        await WaitHelper.WaitUntil(h.StatusAsync, Wait);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SchemaStatus_ReadStartedBeforeWrite_IsNotJoined()
    {
        using var h = new Harness(Tables(1));
        await h.StartAsync();
        h.Time.Advance(CatalogGraphQlTypeModule.StatusReadSpacing);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Meta.BlockNextVersionRead = gate;
        var before = h.Module.IsSchemaCurrentAsync().AsTask();
        await WaitHelper.WaitUntil(() => h.Meta.VersionReads == 1, Wait);

        h.Meta.Version = new CatalogVersion(2, 1);
        var after = h.Module.IsSchemaCurrentAsync().AsTask();
        h.Time.Advance(CatalogGraphQlTypeModule.StatusReadSpacing);
        (await after).ShouldBeFalse("a request after the write needs a read that started after it");
        h.Meta.VersionReads.ShouldBe(2);

        gate.SetResult();
        (await before).ShouldBeTrue("the earlier request saw the catalog before the write");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SchemaStatus_ReadsAreSpaced()
    {
        using var h = new Harness(Tables(1));
        await h.StartAsync();
        h.Time.Advance(CatalogGraphQlTypeModule.StatusReadSpacing);
        (await h.Module.IsSchemaCurrentAsync()).ShouldBeTrue();
        var reads = h.Meta.VersionReads;

        var burst = Enumerable.Range(0, 50).Select(_ => h.Module.IsSchemaCurrentAsync().AsTask()).ToList();
        h.Meta.VersionReads.ShouldBe(reads, "no read starts before the spacing has passed");
        h.Time.Advance(CatalogGraphQlTypeModule.StatusReadSpacing);
        (await Task.WhenAll(burst)).ShouldAllBe(current => current);
        h.Meta.VersionReads.ShouldBe(reads + 1, "requests that arrive together share one read");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SchemaStatus_VersionReadFails_ReportsUpdating()
    {
        using var h = new Harness(Tables(1));
        await h.StartAsync();
        h.Meta.FailVersionRead = true;

        (await h.StatusAsync()).ShouldBeFalse("an unknown catalog version must not be reported as current");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SchemaStatus_FailedBuild_ReportsUpdating()
    {
        using var h = new Harness(Tables(1));
        h.Meta.Fail = true;
        await h.StartAsync();

        (await h.StatusAsync()).ShouldBeFalse("the fail-closed schema without catalog tables is behind the catalog");
    }

    [Theory]
    [InlineData(true, 200, "Current")]
    [InlineData(false, 503, "Updating")]
    [Trait("Category", "Security")]
    public async Task StatusEndpoint_BodyCarriesStatusOnly(bool current, int expectedStatus, string expectedText)
    {
        var (status, body, context) = await ExecuteStatusAsync(new FixedFreshness(current));

        status.ShouldBe(expectedStatus);
        context.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        context.Response.Headers.RetryAfter.ToString().ShouldBe(current ? string.Empty : "1");

        using var doc = JsonDocument.Parse(body);
        string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name))
            .ShouldBe("status", "no names, counts or versions");
        doc.RootElement.GetProperty("status").GetString().ShouldBe(expectedText);
    }

    [Fact]
    public async Task StatusEndpoint_NoModule_Returns404()
    {
        var (status, _, _) = await ExecuteStatusAsync(null);
        status.ShouldBe(404);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void StatusRoute_RefusesAnonymousCallers()
    {
        using var routes = new Autheris.Tests.Common.Fakes.TestEndpointRouteBuilder(permissiveServices: true);
        routes.MapCatalogEndpoints();

        var endpoint = routes.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == StatusRoute && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));

        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty("the status reveals write timing");
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
    }

    public sealed class Query
    {
        public string Ping() => "pong";
    }

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Time { get; } = new();
        public MetadataRepository Meta { get; }
        public VersionProvider Version { get; } = new(new CatalogVersion(1, 1));
        public SyncEventBus Bus { get; } = new();
        public CapturingLogger<CatalogGraphQlTypeModule> Log { get; } = new();
        public CatalogGraphQlTypeModule Module { get; }
        public IRequestExecutorProvider? Provider { get; private set; }

        public Harness(IReadOnlyList<TableMetadata> tables, TimeSpan? refreshInterval = null)
        {
            Meta = new MetadataRepository(tables);
            Module = new CatalogGraphQlTypeModule(
                Meta,
                new RelationRepository(),
                Log,
                refreshInterval: refreshInterval ?? TimeSpan.Zero,
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

        public Task PublishAsync() =>
            Bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new CatalogChangedEvent(0, 0, Time.GetUtcNow()));

        /// <summary>Status reads are spaced (fake clock): advance past the spacing first.</summary>
        public async Task<bool> StatusAsync()
        {
            Time.Advance(CatalogGraphQlTypeModule.StatusReadSpacing);
            return await Module.IsSchemaCurrentAsync();
        }

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

        /// <summary>
        /// The startup build without Hot Chocolate, plus a subscriber that never rebuilds: the first change stays requested, so
        /// the next change is deferred (#786 quiet-period rule) and schedules the follow-up check.
        /// </summary>
        public async Task DeferWithoutRebuildAsync()
        {
            await Module.CreateTypesAsync(null!, CancellationToken.None);
            Module.TypesChanged += (_, _) => { };

            Meta.SetTables(Tables(2));
            Version.Current = Meta.Version = new CatalogVersion(2, 1);
            (await Module.CheckForCatalogChangesAsync()).ShouldBeTrue(); // leading edge: requested
            Meta.SetTables(Tables(3));
            Version.Current = Meta.Version = new CatalogVersion(3, 1);
            (await Module.CheckForCatalogChangesAsync()).ShouldBeTrue(); // further change: deferred
        }

        public void Dispose() => Module.Dispose();
    }

    private static async Task<(int Status, string Body, HttpContext Context)> ExecuteStatusAsync(ICatalogSchemaFreshness? freshness)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var result = await CatalogApiEndpoints.GetGraphQlSchemaStatusAsync(freshness, context, CancellationToken.None);
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

        public int HandlerCount => _handlers.Count;

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
        private int _versionReads;

        public bool Fail { get; set; }

        public bool FailVersionRead { get; set; }

        /// <summary>The committed catalog version in the store (read uncached).</summary>
        public CatalogVersion Version { get; set; } = new(1, 1);

        /// <summary>Blocks the next version read after it has taken its value.</summary>
        public TaskCompletionSource? BlockNextVersionRead { get; set; }

        public int GetAllTablesCalls => Volatile.Read(ref _calls);

        public int VersionReads => Volatile.Read(ref _versionReads);

        public void SetTables(IReadOnlyList<TableMetadata> tables) => _tables = tables;

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Fail ? throw new InvalidOperationException("catalog unreachable") : Task.FromResult(_tables);
        }

        public async Task<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _versionReads);
            if (FailVersionRead)
            {
                throw new InvalidOperationException("store unreachable");
            }

            var value = Version;
            var gate = BlockNextVersionRead;
            if (gate is not null)
            {
                BlockNextVersionRead = null;
                await gate.Task.ConfigureAwait(false);
            }

            return value;
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

    /// <summary>The cached provider view; tests leave it behind the store to prove the store decides.</summary>
    private sealed class VersionProvider(CatalogVersion initial) : ICatalogVersionProvider
    {
        public CatalogVersion Current { get; set; } = initial;

        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(Current);

        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(Current.Formatted);
    }
}
