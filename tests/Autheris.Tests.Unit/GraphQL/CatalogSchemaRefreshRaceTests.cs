namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Lane A, A2: a catalog check that starts after a completed catalog write must observe that write
/// (no dropped check while another one runs), and the served schema converges to the catalog even when the
/// change lands while an executor build is in flight. All gates are explicit; nothing relies on timing.
/// </summary>
public sealed class CatalogSchemaRefreshRaceTests
{
    public sealed class PingQuery
    {
        public string Ping() => "pong";
    }

    private static TableMetadata Table(string name) => new()
    {
        Identifier = new TableIdentifier("sales", "public", name),
        Table = new Table { TableName = name, SchemaName = "public", SourceType = "PostgreSql", IsActive = true },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    /// <summary>A metadata repository whose reads are snapshot-at-call and can be held after the snapshot is taken.</summary>
    private sealed class GatedCatalog
    {
        private readonly object _lock = new();
        private IReadOnlyList<TableMetadata> _current;
        private int _holdNext;
        private int _concurrent;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxConcurrentReads { get; private set; }
        public bool Throw { get; set; }

        public GatedCatalog(params string[] tables) => _current = tables.Select(Table).ToList();

        public void Write(params string[] tables)
        {
            lock (_lock)
            {
                _current = tables.Select(Table).ToList();
            }
        }

        public void HoldNextRead() => Interlocked.Exchange(ref _holdNext, 1);

        public ITableMetadataRepository Repository()
        {
            var meta = Substitute.For<ITableMetadataRepository>();
            meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(_ => ReadAsync());
            return meta;
        }

        private async Task<IReadOnlyList<TableMetadata>> ReadAsync()
        {
            IReadOnlyList<TableMetadata> snapshot;
            lock (_lock)
            {
                snapshot = _current;
                MaxConcurrentReads = Math.Max(MaxConcurrentReads, Interlocked.Increment(ref _concurrent));
            }

            try
            {
                if (Throw)
                {
                    throw new TimeoutException("governance db down");
                }

                if (Interlocked.Exchange(ref _holdNext, 0) == 1)
                {
                    Started.TrySetResult();
                    await Gate.Task.ConfigureAwait(false);
                }

                return snapshot;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }

    private static ITableRelationRepository Relations()
    {
        var rel = Substitute.For<ITableRelationRepository>();
        rel.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);
        return rel;
    }

    private static async Task<(CatalogGraphQlTypeModule Module, ServiceProvider Provider)> StartAsync(GatedCatalog catalog)
    {
        var module = new CatalogGraphQlTypeModule(catalog.Repository(), Relations());
        var provider = new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).Services.BuildServiceProvider();
        await provider.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();
        return (module, provider);
    }

    [Fact]
    public async Task CheckForCatalogChanges_WhileAnotherCheckRuns_ObservesTheLaterWrite()
    {
        var catalog = new GatedCatalog("orders");
        var (module, provider) = await StartAsync(catalog);
        using var _ = module;
        await using var __ = provider;

        catalog.HoldNextRead();
        var first = module.CheckForCatalogChangesAsync();
        await catalog.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The first check already read the old catalog; this write completes before the second check starts.
        catalog.Write("orders", "invoices");
        var second = module.CheckForCatalogChangesAsync();

        catalog.Gate.SetResult();
        (await first.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeFalse();
        (await second.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();
    }

    [Fact]
    public async Task CheckForCatalogChanges_RepositoryThrows_KeepsPreviousSchema_ReleasesSemaphore()
    {
        var catalog = new GatedCatalog("orders");
        var (module, provider) = await StartAsync(catalog);
        using var _ = module;
        await using var __ = provider;
        var fingerprint = module.BuiltFingerprint;
        fingerprint.ShouldNotBeNull();

        catalog.Write("orders", "invoices");
        catalog.Throw = true;
        (await module.CheckForCatalogChangesAsync().WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeFalse();
        module.BuiltFingerprint.ShouldBe(fingerprint);

        // The semaphore was released: the next check runs and sees the change.
        catalog.Throw = false;
        (await module.CheckForCatalogChangesAsync().WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();
    }

    [Fact]
    public async Task CheckForCatalogChanges_CancelledWaiter_DoesNotReleaseSemaphore()
    {
        var catalog = new GatedCatalog("orders");
        var (module, provider) = await StartAsync(catalog);
        using var _ = module;
        await using var __ = provider;

        catalog.HoldNextRead();
        var first = module.CheckForCatalogChangesAsync();
        await catalog.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        var cancelled = module.CheckForCatalogChangesAsync(cts.Token);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(cancelled.WaitAsync(TimeSpan.FromSeconds(30)));

        // A semaphore released by the cancelled waiter would let this check read while the first one is held.
        var third = module.CheckForCatalogChangesAsync();
        catalog.Gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30));
        await third.WaitAsync(TimeSpan.FromSeconds(30));
        catalog.MaxConcurrentReads.ShouldBe(1);
    }

    [Fact]
    public async Task CatalogChange_DuringInFlightExecutorBuild_ServedSchemaConvergesToNewCatalog()
    {
        var catalog = new GatedCatalog("orders");
        using var module = new CatalogGraphQlTypeModule(catalog.Repository(), Relations());
        await using var provider = new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).Services.BuildServiceProvider();
        var executors = provider.GetRequiredService<IRequestExecutorProvider>();
        var events = provider.GetRequiredService<IRequestExecutorEvents>();

        var created = new SemaphoreSlim(0);
        using var subscription = events.Subscribe(new CreatedObserver(created));

        // The executor build reads the old catalog and is held; the catalog changes and a check fires meanwhile.
        catalog.HoldNextRead();
        var build = executors.GetExecutorAsync().AsTask();
        await catalog.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        catalog.Write("orders", "invoices");
        await module.CheckForCatalogChangesAsync().WaitAsync(TimeSpan.FromSeconds(30));
        catalog.Gate.SetResult();
        await build.WaitAsync(TimeSpan.FromSeconds(30));

        // Convergence is event driven: wait for each Created event until the served schema has the new table.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await executors.GetExecutorAsync()).Schema.Types.ContainsName("sales_public_invoices"))
        {
            var remaining = deadline - DateTime.UtcNow;
            (remaining > TimeSpan.Zero && await created.WaitAsync(remaining))
                .ShouldBeTrue("the served schema never converged to the new catalog");
        }
    }

    private sealed class CreatedObserver(SemaphoreSlim created) : IObserver<RequestExecutorEvent>
    {
        public void OnNext(RequestExecutorEvent value)
        {
            if (value.Type == RequestExecutorEventType.Created)
            {
                created.Release();
            }
        }

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}
