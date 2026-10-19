namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Autheris.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using static CdcProducerHarness;

/// <summary>REQ-CDC-99 D-A2: one fenced lease per tracked table, per-table settings, durable store requirement, no bus publication.</summary>
public sealed class CdcProducerServiceTests
{
    private sealed class FakeLease(string key, long token) : IDistributedLease
    {
        public string ResourceKey => key;

        public long Token => token;

        public CancellationToken LeaseLost => CancellationToken.None;

        public DateTimeOffset LastConfirmedAt => DateTimeOffset.UnixEpoch;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IDistributedClusterStateProvider Cluster(Dictionary<string, long> tokens, List<(string Key, TimeSpan Ttl)>? acquired = null)
    {
        var state = Substitute.For<IDistributedClusterStateProvider>();
        state.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = call.ArgAt<string>(0);
                acquired?.Add((key, call.ArgAt<TimeSpan>(1)));
                return new ValueTask<IDistributedLease?>(tokens.TryGetValue(key, out var t) ? new FakeLease(key, t) : null);
            });
        return state;
    }

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static CdcProducerService Service(
        IMssqlChangeTrackingPoller poller, ICdcStreamStore store, GatewayOptions options, IDistributedClusterStateProvider state, string env = "Development", CdcPollFailureTracker? tracker = null, FakeTimeProvider? time = null) =>
        new(poller, store, new StaticOptionsMonitor<GatewayOptions>(options), state, Env(env), NullLogger<CdcProducerService>.Instance, tracker, time);

    private static GatewayOptions Options(params string[] tables) =>
        new() { MssqlChangeTracking = new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = [.. tables] } };

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDurableStore_OutsideDevelopment_FailsStart(string environment)
    {
        var store = Substitute.For<ICdcStreamStore>();
        store.IsDurable.Returns(false);
        var service = Service(Substitute.For<IMssqlChangeTrackingPoller>(), store, Options("a.dbo.t"), Cluster([]), environment);

        await Should.ThrowAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NonDurableStore_InDevelopment_Starts()
    {
        var store = Substitute.For<ICdcStreamStore>();
        store.IsDurable.Returns(false);
        var service = Service(Substitute.For<IMssqlChangeTrackingPoller>(), store, Options("a.dbo.t"), Cluster([]));

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DurableStore_InProduction_Starts()
    {
        var store = Substitute.For<ICdcStreamStore>();
        store.IsDurable.Returns(true);
        var service = Service(Substitute.For<IMssqlChangeTrackingPoller>(), store, Options("a.dbo.t"), Cluster([]), "Production");

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RunOnce_PollsEveryTableUnderItsOwnLease_AndPassesItsFencingToken()
    {
        var poller = Substitute.For<IMssqlChangeTrackingPoller>();
        poller.PollTableChangesAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(1);
        var acquired = new List<(string, TimeSpan)>();
        var service = Service(
            poller,
            Substitute.For<ICdcStreamStore>(),
            Options("fin.dbo.Orders", "fin.dbo.Invoices"),
            Cluster(new() { ["cdc:mssql:poll:fin.dbo.orders"] = 11, ["cdc:mssql:poll:fin.dbo.invoices"] = 22 }, acquired));

        await service.RunOnceAsync();

        acquired.Select(a => a.Item1).Order().ToArray().ShouldBe(["cdc:mssql:poll:fin.dbo.invoices", "cdc:mssql:poll:fin.dbo.orders"]);
        await poller.Received(1).PollTableChangesAsync(new TableIdentifier("fin", "dbo", "Orders"), 11, Arg.Any<CancellationToken>());
        await poller.Received(1).PollTableChangesAsync(new TableIdentifier("fin", "dbo", "Invoices"), 22, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnce_TableLeaseHeldElsewhere_IsNotPolledHere()
    {
        var poller = Substitute.For<IMssqlChangeTrackingPoller>();
        var service = Service(poller, Substitute.For<ICdcStreamStore>(), Options("fin.dbo.Orders", "fin.dbo.Invoices"), Cluster(new() { ["cdc:mssql:poll:fin.dbo.invoices"] = 22 }));

        await service.RunOnceAsync();

        await poller.DidNotReceive().PollTableChangesAsync(new TableIdentifier("fin", "dbo", "Orders"), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await poller.Received(1).PollTableChangesAsync(new TableIdentifier("fin", "dbo", "Invoices"), 22, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Partitions_UsePerTableIntervalAndLeaseTtl_AndDisabledMeansNone()
    {
        var options = new GatewayOptions
        {
            MssqlChangeTracking = new MssqlChangeTrackingOptions
            {
                Enabled = true,
                PollingIntervalMilliseconds = 1000,
                TrackedTables = ["fin.dbo.slow"],
                Tables = [new CdcTrackedTableOptions { Table = "fin.dbo.fast", PollingIntervalMilliseconds = 250 }, new CdcTrackedTableOptions { Table = "fin.dbo.rare", PollingIntervalMilliseconds = 20_000 }]
            }
        };
        var service = Service(Substitute.For<IMssqlChangeTrackingPoller>(), Substitute.For<ICdcStreamStore>(), options, Cluster([]));

        var partitions = (IReadOnlyList<ClusterPartitionView>)Partitions(service);

        partitions.Single(p => p.Key.EndsWith("slow")).Interval.ShouldBe(TimeSpan.FromSeconds(1));
        partitions.Single(p => p.Key.EndsWith("slow")).LeaseTtl.ShouldBe(TimeSpan.FromSeconds(15));
        partitions.Single(p => p.Key.EndsWith("fast")).Interval.ShouldBe(TimeSpan.FromMilliseconds(250));
        partitions.Single(p => p.Key.EndsWith("rare")).LeaseTtl.ShouldBe(TimeSpan.FromSeconds(60)); // 3 x interval

        var disabled = Service(Substitute.For<IMssqlChangeTrackingPoller>(), Substitute.For<ICdcStreamStore>(), new GatewayOptions(), Cluster([]));
        Partitions(disabled).ShouldBeEmpty();
    }

    private sealed record ClusterPartitionView(string Key, TimeSpan Interval, TimeSpan LeaseTtl);

    private static IReadOnlyList<ClusterPartitionView> Partitions(CdcProducerService service)
    {
        var method = typeof(CdcProducerService).GetMethod("GetPartitions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var list = (System.Collections.IEnumerable)method.Invoke(service, null)!;
        return list.Cast<Autheris.Application.Hosting.ClusterPartition>().Select(p => new ClusterPartitionView(p.Key, p.Schedule.Interval, p.Schedule.LeaseTtl)).ToList();
    }

    [Fact]
    public async Task PersistentFailure_BacksOffExponentially_AndIsAttemptedAgainAfterwards()
    {
        var poller = Substitute.For<IMssqlChangeTrackingPoller>();
        poller.PollTableChangesAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new CdcSourceConfigurationException(CdcActivationCodes.TrackingDisabled));
        var time = new FakeTimeProvider();
        var service = Service(
            poller, Substitute.For<ICdcStreamStore>(), Options("fin.dbo.Orders"), Cluster(new() { ["cdc:mssql:poll:fin.dbo.orders"] = 1 }),
            tracker: new CdcPollFailureTracker(time), time: time);

        await service.RunOnceAsync();
        await service.RunOnceAsync();
        await service.RunOnceAsync();
        await poller.Received(1).PollTableChangesAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<CancellationToken>());

        time.Advance(TimeSpan.FromMinutes(6));
        await service.RunOnceAsync();

        await poller.Received(2).PollTableChangesAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailureLog_CarriesOnlyTheStableCode()
    {
        var logger = new CapturingLogger<CdcProducerService>();
        var poller = Substitute.For<IMssqlChangeTrackingPoller>();
        poller.PollTableChangesAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new CdcSourceConfigurationException(CdcActivationCodes.PkMismatch));
        var service = new CdcProducerService(
            poller, Substitute.For<ICdcStreamStore>(), new StaticOptionsMonitor<GatewayOptions>(Options("fin.dbo.Orders")),
            Cluster(new() { ["cdc:mssql:poll:fin.dbo.orders"] = 1 }), Env("Development"), logger);

        await service.RunOnceAsync();

        logger.Messages.ShouldContain(m => m.Contains(CdcActivationCodes.PkMismatch, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FencedAndTransientFailures_AreContained()
    {
        var poller = Substitute.For<IMssqlChangeTrackingPoller>();
        var calls = 0;
        poller.PollTableChangesAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => ++calls == 1 ? throw new InvalidOperationException("transient") : throw new FencingTokenRejectedException("t", 1));
        var service = Service(poller, Substitute.For<ICdcStreamStore>(), Options("fin.dbo.Orders"), Cluster(new() { ["cdc:mssql:poll:fin.dbo.orders"] = 1 }));

        await service.RunOnceAsync();
        await service.RunOnceAsync();

        calls.ShouldBe(2);
    }

    [Fact]
    public async Task TableRemovedFromOptions_IsNotPolled()
    {
        var poller = Substitute.For<IMssqlChangeTrackingPoller>();
        var service = Service(poller, Substitute.For<ICdcStreamStore>(), Options("fin.dbo.Orders"), Cluster(new() { ["cdc:mssql:poll:fin.dbo.gone"] = 1 }));
        var partition = new Autheris.Application.Hosting.ClusterPartition(
            "cdc:mssql:poll:fin.dbo.gone", new Autheris.Application.Hosting.ClusterSchedule(true, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(15), TimeSpan.Zero));

        await service.RunPartitionCycleAsync(partition, CancellationToken.None);

        await poller.DidNotReceiveWithAnyArgs().PollTableChangesAsync(default, 0, default);
    }

    // ---- AC-14: MSSQL changes never travel over the broadcast channel

    private sealed class RecordingChannel : ICdcEventChannel
    {
        public List<CdcEvent> Published { get; } = [];

        public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default)
        {
            Published.Add(cdcEvent);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task NeverPublishesOnCdcEventChannel()
    {
        var h = new CdcProducerHarness();
        await h.SeedWatermarkAsync(5);
        h.Source.Changes.Add(Change(10, "I", 1));
        var channel = new RecordingChannel();
        var services = new ServiceCollection();
        services.AddSingleton<ICdcEventChannel>(channel);
        services.AddSingleton(h.Metadata);
        services.AddSingleton<ISqlConnectionFactory>(new FakeSourceConnectionFactory(h.Source));
        services.AddSingleton<ICdcStreamStore>(h.Store);
        services.AddSingleton<ICdcStreamCrypto>(h.Crypto);
        services.AddSingleton<ICdcPayloadCodec>(h.Codec);
        services.AddSingleton<ICdcStreamSignal>(h.Signal);
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>(h.Monitor);
        services.AddSingleton(Substitute.For<IDistributedClusterStateProvider>());
        services.AddSingleton(Env("Development"));
        services.AddLogging();
        services.AddCdcSourceIntegration(h.Options);
        using var provider = services.BuildServiceProvider();

        var poller = provider.GetRequiredService<IMssqlChangeTrackingPoller>();
        (await poller.PollTableChangesAsync(TableId, 1)).ShouldBe(1);

        channel.Published.ShouldBeEmpty();
        provider.GetServices<IHostedService>().OfType<CdcProducerService>().ShouldHaveSingleItem();
        new[] { typeof(MssqlChangeTrackingPoller), typeof(CdcChangeEventBuilder), typeof(CdcTableActivator), typeof(CdcProducerService) }
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()))
            .ShouldNotContain(p => p.ParameterType == typeof(ICdcEventChannel));
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
