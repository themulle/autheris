namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Autheris.Tests.Unit.Cdc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// REQ-CDC-99 I1-B on a real SQL Server: activation against the declared primary key with stable codes under a least-privilege login
/// (AC-5, SEC-CDC99-22), the producer on real change tracking (AC-2), expired watermark (AC-4) and failover (AC-6). No sleeps and no waits: the producer is
/// driven by explicit poll calls. Needs Docker; without it each test returns early.
/// </summary>
public sealed class SqlServerChangeTrackingProducerTests : IAsyncLifetime
{
    private SqlServerChangeTrackingDatabase? _db;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await SqlServerChangeTrackingDatabase.CreateAsync();
        }
        catch (Exception ex)
        {
            ContainerStartup.FailIfRequired(ex, "The SQL Server change tracking test database");
        }
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private bool Available => _db is { IsAvailable: true };

    private sealed class Rig
    {
        public required FakeCdcStreamStore InnerStore { get; init; }
        public required FaultInjectingStreamStore Store { get; init; }
        public required TestCdcCrypto Crypto { get; init; }
        public required RecordingSqlConnectionFactory Factory { get; init; }
        public required CdcTableActivator Activator { get; init; }
        public required MssqlChangeTrackingPoller Poller { get; init; }
        public required GatewayOptions Options { get; init; }
        public required TableIdentifier Table { get; init; }
        public required Func<FaultInjectingStreamStore, MssqlChangeTrackingPoller> NewPoller { get; init; }
    }

    private static Rig NewRig(
        string connectionString,
        string tableName,
        Func<MssqlChangeTrackingOptions, MssqlChangeTrackingOptions>? configure = null,
        IReadOnlyList<string>? primaryKey = null,
        IEnumerable<string>? columns = null)
    {
        var table = new TableIdentifier("finance", "dbo", tableName);
        var cdc = new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = [$"finance.dbo.{tableName}"] };
        var options = new GatewayOptions
        {
            MssqlChangeTracking = configure?.Invoke(cdc) ?? cdc,
            DataSources = new SqlDataSourceOptions
            {
                Connections = { ["src"] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = connectionString } }
            }
        };
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<TableMetadata?>(new TableMetadata
        {
            Table = new Table { SourceType = "SqlServer", SourceName = "src", SchemaName = "dbo", TableName = tableName, IsActive = true },
            Identifier = table,
            Columns = (columns ?? ["Id", "Name", "TenantId"]).Select(c => new TableColumn { ColumnName = c }).ToList(),
            PrimaryKeyColumns = (primaryKey ?? ["Id"]).ToList()
        }));
        var inner = new FakeCdcStreamStore();
        var store = new FaultInjectingStreamStore(inner);
        var crypto = new TestCdcCrypto();
        var codec = new TestPayloadCodec();
        var factory = new RecordingSqlConnectionFactory();
        var monitor = new FixedMonitor(options);
        var time = TimeProvider.System;
        MssqlChangeTrackingPoller PollerFor(FaultInjectingStreamStore s)
        {
            var activator = new CdcTableActivator(metadata, factory, s, crypto, monitor, NullLogger<CdcTableActivator>.Instance, null, time);
            var builder = new CdcChangeEventBuilder(s, crypto, codec, monitor, time);
            return new MssqlChangeTrackingPoller(factory, activator, s, builder, new NoSignal(), monitor, NullLogger<MssqlChangeTrackingPoller>.Instance, time);
        }

        var activatorMain = new CdcTableActivator(metadata, factory, store, crypto, monitor, NullLogger<CdcTableActivator>.Instance, null, time);
        return new Rig
        {
            InnerStore = inner,
            Store = store,
            Crypto = crypto,
            Factory = factory,
            Activator = activatorMain,
            Poller = PollerFor(store),
            Options = options,
            Table = table,
            NewPoller = PollerFor
        };
    }

    private sealed class FixedMonitor(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue { get; } = value;

        public GatewayOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private sealed class NoSignal : Autheris.Application.Streaming.Interfaces.ICdcStreamSignal
    {
        public ValueTask NotifyAdvancedAsync(CdcStreamId id, long headOffset, CancellationToken ct = default) => ValueTask.CompletedTask;

        public IAsyncDisposable Subscribe(Func<CdcStreamId, long, ValueTask> onAdvanced) => throw new NotSupportedException();
    }

    private static string NewName() => "t_" + Guid.NewGuid().ToString("N")[..10];

    private static CdcTableSettings SettingsOf(Rig rig) => CdcTableSettings.Resolve(rig.Options.MssqlChangeTracking).Single();

    private async Task<List<(CdcStreamEventRecord Record, CdcChangePayload Payload)>> ChangesAsync(Rig rig)
    {
        var list = new List<(CdcStreamEventRecord, CdcChangePayload)>();
        foreach (var e in rig.InnerStore.Only.Events.Where(e => e.Kind == CdcStreamEventKind.Change))
        {
            list.Add((e, TestPayloadCodec.ReadChange(await rig.Crypto.UnprotectEventAsync(e))));
        }

        return list;
    }

    // ---- AC-5 / ST-14: activation

    [Fact]
    public async Task Activation_Succeeds_WithExactlyConnectSelectAndViewChangeTracking_AndReadsNoCatalogView()
    {
        if (!Available) return;
        var name = NewName();
        await _db!.CreateTableAsync(name);
        var login = await _db.CreateLeastPrivilegeLoginAsync([name]);
        var rig = NewRig(login, name);

        var result = await rig.Activator.EnsureActiveAsync(SettingsOf(rig));
        await rig.Poller.PollTableChangesAsync(rig.Table, 1);

        result.IsActive.ShouldBeTrue();
        rig.InnerStore.Only.Status.ShouldBe(CdcStreamStatus.Active);
        rig.Factory.Statements.ShouldNotBeEmpty();
        rig.Factory.Statements.ShouldAllBe(s => !s.Contains("sys.", StringComparison.OrdinalIgnoreCase) && !s.Contains("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Activation_RejectsWithStableCode_NoDeclaredPk()
    {
        if (!Available) return;
        var name = NewName();
        await _db!.CreateTableAsync(name);
        var rig = NewRig(await _db.CreateLeastPrivilegeLoginAsync([name]), name, primaryKey: []);

        (await rig.Activator.EnsureActiveAsync(SettingsOf(rig))).Code.ShouldBe(CdcActivationCodes.NoDeclaredPk);
        await Should.ThrowAsync<CdcSourceConfigurationException>(() => rig.Poller.PollTableChangesAsync(rig.Table, 1));
    }

    [Fact]
    public async Task Activation_RejectsWithStableCode_PkMismatch()
    {
        if (!Available) return;
        var name = NewName();
        await _db!.CreateTableAsync(name);
        var rig = NewRig(await _db.CreateLeastPrivilegeLoginAsync([name]), name, primaryKey: ["Name"]);

        (await rig.Activator.EnsureActiveAsync(SettingsOf(rig))).Code.ShouldBe(CdcActivationCodes.PkMismatch);
        rig.InnerStore.Only.ActivationCode.ShouldBe(CdcActivationCodes.PkMismatch);
    }

    [Fact]
    public async Task Activation_RejectsWithStableCode_TrackingOff()
    {
        if (!Available) return;
        var name = NewName();
        await _db!.CreateTableAsync(name, changeTracking: false);
        var rig = NewRig(await _db.CreateLeastPrivilegeLoginAsync([name]), name);

        (await rig.Activator.EnsureActiveAsync(SettingsOf(rig))).Code.ShouldBe(CdcActivationCodes.TrackingDisabled);
    }

    [Fact]
    public async Task Activation_RejectsWithStableCode_SnapshotIsolationOff()
    {
        if (!Available) return;
        await using var noSnapshot = await SqlServerChangeTrackingDatabase.CreateAsync(snapshotIsolation: false);
        var name = NewName();
        await noSnapshot.CreateTableAsync(name);
        var rig = NewRig(await noSnapshot.CreateLeastPrivilegeLoginAsync([name]), name);

        (await rig.Activator.EnsureActiveAsync(SettingsOf(rig))).Code.ShouldBe(CdcActivationCodes.SnapshotIsolationOff);

        // the documented opt-out reads at READ COMMITTED instead
        var optOut = NewRig(await noSnapshot.CreateLeastPrivilegeLoginAsync([name]), name, o => new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = [$"finance.dbo.{name}"], AllowNonSnapshotRead = true });
        (await optOut.Activator.EnsureActiveAsync(SettingsOf(optOut))).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task MissingViewChangeTracking_FailsActivationWithAStableCode_AndNeverPolls()
    {
        if (!Available) return;
        var name = NewName();
        await _db!.CreateTableAsync(name);
        var rig = NewRig(await _db.CreateLeastPrivilegeLoginAsync([name], withViewChangeTracking: false), name);

        var result = await rig.Activator.EnsureActiveAsync(SettingsOf(rig));

        result.IsActive.ShouldBeFalse();
        result.Code.ShouldBe(CdcActivationCodes.TrackingDisabled);
        await Should.ThrowAsync<CdcSourceConfigurationException>(() => rig.Poller.PollTableChangesAsync(rig.Table, 1));
        rig.InnerStore.Only.Events.ShouldBeEmpty();
        rig.InnerStore.Only.Watermark.ShouldBeNull();
    }

    // ---- AC-2: the producer on real change tracking

    [Fact]
    public async Task Producer_StartsAtTheCurrentVersion_ThenAppendsInsertUpdateDelete()
    {
        if (!Available) return;
        var name = NewName();
        await _db!.CreateTableAsync(name);
        await _db.ExecuteAdminAsync($"INSERT dbo.[{name}] (Id, Name) VALUES (1, N'history')");
        var rig = NewRig(await _db.CreateLeastPrivilegeLoginAsync([name]), name);

        (await rig.Poller.PollTableChangesAsync(rig.Table, 1)).ShouldBe(0); // no history is replayed
        rig.InnerStore.Only.Watermark.ShouldNotBeNull();

        await _db.ExecuteAdminAsync($"INSERT dbo.[{name}] (Id, Name) VALUES (2, N'two')");
        (await rig.Poller.PollTableChangesAsync(rig.Table, 1)).ShouldBe(1);
        await _db.ExecuteAdminAsync($"UPDATE dbo.[{name}] SET Name = N'two-b' WHERE Id = 2");
        (await rig.Poller.PollTableChangesAsync(rig.Table, 1)).ShouldBe(1);
        await _db.ExecuteAdminAsync($"DELETE dbo.[{name}] WHERE Id = 2");
        (await rig.Poller.PollTableChangesAsync(rig.Table, 1)).ShouldBe(1);

        var changes = await ChangesAsync(rig);
        changes.Select(c => c.Record.Operation).ShouldBe([CdcOperation.Insert, CdcOperation.Update, CdcOperation.Delete]);
        changes.Select(c => c.Record.Offset).ShouldBe([1L, 2, 3]);
        changes[0].Payload.After!["Name"].ShouldBe("two");
        changes[1].Payload.Before!["Name"].ShouldBe("two");
        changes[1].Payload.After!["Name"].ShouldBe("two-b");
        changes[2].Payload.Before!["Name"].ShouldBe("two-b");
        changes[2].Payload.After.ShouldBeNull();
        changes[2].Payload.Key["Id"].ShouldBe(2);
    }

    [Fact]
    public async Task Producer_SplitsBatchesOnVersionBoundaries_WithoutLossOrDuplicate()
    {
        if (!Available) return;
        var name = NewName();
        await _db!.CreateTableAsync(name);
        var rig = NewRig(await _db.CreateLeastPrivilegeLoginAsync([name]), name, o => new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = [$"finance.dbo.{name}"], BatchSize = 3 });
        await rig.Poller.PollTableChangesAsync(rig.Table, 1);

        // one multi-row statement = ONE change version with 5 rows (more than a batch), then single-row versions
        await _db.ExecuteAdminAsync($"INSERT dbo.[{name}] (Id, Name) VALUES (1,N'a'),(2,N'b'),(3,N'c'),(4,N'd'),(5,N'e')");
        for (var id = 6; id <= 8; id++)
        {
            await _db.ExecuteAdminAsync($"INSERT dbo.[{name}] (Id, Name) VALUES ({id}, N'x')");
        }

        var total = 0;
        for (var i = 0; i < 6; i++)
        {
            total += await rig.Poller.PollTableChangesAsync(rig.Table, 1);
        }

        total.ShouldBe(8);
        var ids = (await ChangesAsync(rig)).Select(c => (int)c.Payload.Key["Id"]!).ToList();
        ids.Order().ShouldBe([1, 2, 3, 4, 5, 6, 7, 8]);
        rig.InnerStore.Only.Events.Select(e => e.Offset).ShouldBe(Enumerable.Range(1, 8).Select(i => (long)i));
    }

    [Fact]
    public async Task TenantMove_ProducesTheNewTenantEvent_NamingThePriorTenant()
    {
        if (!Available) return;
        var name = NewName();
        await _db!.CreateTableAsync(name);
        var rig = NewRig(await _db.CreateLeastPrivilegeLoginAsync([name]), name);
        await rig.Poller.PollTableChangesAsync(rig.Table, 1);
        await _db.ExecuteAdminAsync($"INSERT dbo.[{name}] (Id, Name, TenantId) VALUES (1, N'r', N'acme')");
        await rig.Poller.PollTableChangesAsync(rig.Table, 1);
        await _db.ExecuteAdminAsync($"UPDATE dbo.[{name}] SET TenantId = N'globex' WHERE Id = 1");
        await rig.Poller.PollTableChangesAsync(rig.Table, 1);

        var moved = rig.InnerStore.Only.Events.Last();

        moved.TenantScope.ShouldBe(CdcTenantScope.Row);
        moved.TenantId.ShouldBe("globex");
        moved.PriorTenantId.ShouldBe("acme");
    }

    // ---- AC-4: expired watermark, produced deterministically

    private async Task<Rig> ExpiredRigAsync(CdcOnWatermarkExpired policy)
    {
        // change table B, then enable change tracking on table A: A's minimum valid version is now the current version (> 0)
        var other = NewName();
        await _db!.CreateTableAsync(other);
        await _db.ExecuteAdminAsync($"INSERT dbo.[{other}] (Id, Name) VALUES (1, N'bump')");
        var name = NewName();
        await _db.CreateTableAsync(name);
        var rig = NewRig(
            await _db.CreateLeastPrivilegeLoginAsync([name]),
            name,
            o => new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = [$"finance.dbo.{name}"], OnWatermarkExpired = policy });
        var stream = await rig.Store.EnsureStreamAsync(CdcTableActivator.StreamSource, rig.Table);
        await rig.Store.AppendAsync(new CdcAppendBatch(stream.Id, CdcTableActivator.StreamSource, rig.Table, 1, 0, 0, [], [])); // watermark 0 for A
        return rig;
    }

    [Fact]
    public async Task WatermarkExpired_Restart_WritesControlEventAndContinues()
    {
        if (!Available) return;
        var rig = await ExpiredRigAsync(CdcOnWatermarkExpired.Restart);

        (await rig.Poller.PollTableChangesAsync(rig.Table, 1)).ShouldBe(1);

        var control = rig.InnerStore.Only.Events.Single();
        control.Kind.ShouldBe(CdcStreamEventKind.WatermarkExpired);
        rig.InnerStore.Only.Watermark!.Value.ShouldBeGreaterThan(0);

        await _db!.ExecuteAdminAsync($"INSERT dbo.[{rig.Table.TableName}] (Id, Name) VALUES (1, N'after')");
        (await rig.Poller.PollTableChangesAsync(rig.Table, 1)).ShouldBe(1);
        rig.InnerStore.Only.Events.Select(e => e.Kind).ShouldBe([CdcStreamEventKind.WatermarkExpired, CdcStreamEventKind.Change]);
    }

    [Fact]
    public async Task WatermarkExpired_Halt_StopsUntilResync()
    {
        if (!Available) return;
        var rig = await ExpiredRigAsync(CdcOnWatermarkExpired.Halt);

        (await rig.Poller.PollTableChangesAsync(rig.Table, 1)).ShouldBe(0);

        rig.InnerStore.Only.Status.ShouldBe(CdcStreamStatus.Halted);
        rig.InnerStore.Only.ActivationCode.ShouldBe(CdcActivationCodes.WatermarkExpired);
        var connections = rig.Factory.Connections;
        (await rig.Poller.PollTableChangesAsync(rig.Table, 1)).ShouldBe(0);
        rig.Factory.Connections.ShouldBe(connections); // a halted stream opens no source connection
        rig.InnerStore.Only.Events.ShouldBeEmpty();
    }

    // ---- AC-6: failover on real change tracking

    [Fact]
    public async Task Failover_CrashBeforeCommit_SecondProducerAppendsEverythingOnce_AndTheStaleOneIsFenced()
    {
        if (!Available) return;
        var name = NewName();
        await _db!.CreateTableAsync(name);
        var rig = NewRig(await _db.CreateLeastPrivilegeLoginAsync([name]), name);
        await rig.Poller.PollTableChangesAsync(rig.Table, 5);
        for (var id = 1; id <= 4; id++)
        {
            await _db.ExecuteAdminAsync($"INSERT dbo.[{name}] (Id, Name) VALUES ({id}, N'r{id}')");
        }

        rig.Store.ThrowBeforeAppend = true;
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Poller.PollTableChangesAsync(rig.Table, 5));
        rig.InnerStore.Only.Events.ShouldBeEmpty();

        var second = rig.NewPoller(new FaultInjectingStreamStore(rig.InnerStore));
        (await second.PollTableChangesAsync(rig.Table, 6)).ShouldBe(4);

        rig.Store.ThrowBeforeAppend = false;
        await _db.ExecuteAdminAsync($"INSERT dbo.[{name}] (Id, Name) VALUES (5, N'late')"); // something for the stale producer to append
        await Should.ThrowAsync<FencingTokenRejectedException>(() => rig.Poller.PollTableChangesAsync(rig.Table, 5));
        var events = rig.InnerStore.Only.Events;
        events.Select(e => e.Offset).ShouldBe([1L, 2, 3, 4]);
        events.Select(e => e.ChangeVersion).Distinct().Count().ShouldBe(4);
    }
}
