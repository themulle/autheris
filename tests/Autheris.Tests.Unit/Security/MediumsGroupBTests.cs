namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.FinOps.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Autheris.Application.State;
using Autheris.Infrastructure.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Review E-14 (shared FinOps budget), E-15 (row-level lakehouse isolation), R3-4 (cached anonymous health probe).</summary>
public sealed class MediumsGroupBTests
{
    // ---------------------------------------------------------------- E-14

    private static FocusCostAccountingService NewFinOps(InMemoryClusterStateProvider shared, decimal budget) =>
        new(
            Options.Create(new GatewayOptions
            {
                FinOps = new FinOpsOptions { Enabled = true, DefaultMonthlyBudget = budget, PricePerComputeSecond = 1m }
            }),
            NullLogger<FocusCostAccountingService>.Instance,
            shared);

    [Fact]
    public async Task E14_BudgetIsSharedAcrossReplicas()
    {
        var shared = new InMemoryClusterStateProvider();
        var replicaA = NewFinOps(shared, budget: 10m);
        var replicaB = NewFinOps(shared, budget: 10m);

        await replicaA.RecordUsageAsync("t1", "p", "op", "HttpCompute", 0, 0, computeMs: 6000);
        (await replicaB.CheckBudgetAsync("t1")).IsExceeded.ShouldBeFalse();

        await replicaB.RecordUsageAsync("t1", "p", "op", "HttpCompute", 0, 0, computeMs: 6000);

        // 6 + 6 = 12 > 10: both replicas see the cluster-wide total, neither only its own 6.
        (await replicaA.CheckBudgetAsync("t1")).IsExceeded.ShouldBeTrue();
        (await replicaB.CheckBudgetAsync("t1")).CurrentSpend.ShouldBe(12m);
    }

    [Fact]
    public async Task E14_ResetClearsSharedSpend()
    {
        var shared = new InMemoryClusterStateProvider();
        var replicaA = NewFinOps(shared, budget: 5m);
        var replicaB = NewFinOps(shared, budget: 5m);

        await replicaA.RecordUsageAsync("t1", "p", "op", "HttpCompute", 0, 0, computeMs: 6000);
        (await replicaB.CheckBudgetAsync("t1")).IsExceeded.ShouldBeTrue();

        await replicaB.ResetSpendAsync("t1");
        (await replicaA.CheckBudgetAsync("t1")).IsExceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task E14_FallsBackToLocalAccountingWhenSharedStoreIsUnreachable()
    {
        var shared = Substitute.For<Autheris.Application.State.IDistributedClusterStateProvider>();
        shared.IncrementAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<long?>((long?)null));
        shared.GetAsync<long>(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<long>(0L));

        var service = new FocusCostAccountingService(
            Options.Create(new GatewayOptions { FinOps = new FinOpsOptions { Enabled = true, DefaultMonthlyBudget = 5m, PricePerComputeSecond = 1m } }),
            NullLogger<FocusCostAccountingService>.Instance,
            shared);

        await service.RecordUsageAsync("t1", "p", "op", "HttpCompute", 0, 0, computeMs: 6000);

        (await service.CheckBudgetAsync("t1")).IsExceeded.ShouldBeTrue();
    }

    [Fact]
    public async Task E14_InMemoryIncrementIsAtomic()
    {
        var shared = new InMemoryClusterStateProvider();
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => shared.IncrementAsync("k", 1, TimeSpan.FromMinutes(1)).AsTask()));
        (await shared.GetAsync<long>("k")).ShouldBe(200);
    }

    // ---------------------------------------------------------------- E-15

    private static (DeltaLakeDataSourceExecutor Executor, DataSourceExecutionContext Context) NewDelta(
        IReadOnlyDictionary<string, object?> arguments,
        IReadOnlyList<DeltaDataFile> files)
    {
        var reader = Substitute.For<IDeltaMetadataReader>();
        var snapshot = new DeltaSnapshot(
            "t", 1, 0,
            new DeltaTableMetadata("id", "t", null, "parquet",
                new DeltaSchema("struct", [new DeltaField("id", "string"), new DeltaField("tenantId", "string")]),
                ["tenantId"], 0, new Dictionary<string, string>()),
            files,
            new DeltaProtocol(1, 2));
        reader.LoadSnapshotAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DeltaSnapshot>(snapshot));

        // Pruner that does not prune: the row-level check must hold on its own.
        var pruner = Substitute.For<IDeltaPartitionPruner>();
        pruner.PruneDataFiles(Arg.Any<IReadOnlyList<DeltaDataFile>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(call => call.Arg<IReadOnlyList<DeltaDataFile>>());

        var executor = new DeltaLakeDataSourceExecutor(
            reader, pruner, Substitute.For<IColumnMaskingProvider>(),
            Options.Create(new GatewayOptions()), NullLogger<DeltaLakeDataSourceExecutor>.Instance, new DemoDataSwitch(true));

        var tableId = new TableIdentifier("lake", "default", "delta_table");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "default", TableName = "delta_table", DataSourceType = DataSourceType.LakehouseDelta },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "string" },
                new TableColumn { ColumnName = "tenantId", DataType = "string" }
            ]
        };
        var context = new DataSourceExecutionContext(
            "lake", metadata, new ClaimsPrincipal(new ClaimsIdentity()),
            TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["tenantId"] = ColumnAccessLevel.Clear
            }),
            arguments, new List<string> { "id", "tenantId" }, null, 1000, 0, new TenantId("tenant-1"));
        return (executor, context);
    }

    private static DeltaDataFile File(string path, string tenant) =>
        new(path, new Dictionary<string, string> { ["tenantId"] = tenant }, 1, 0, true);

    [Fact]
    public async Task E15_Delta_ForeignTenantFilesYieldNoRowsEvenIfPruningLetsThemThrough()
    {
        var (executor, context) = NewDelta(
            new Dictionary<string, object?>(),
            [File("a.parquet", "tenant-1"), File("b.parquet", "tenant-2")]);

        var rows = await executor.ExecuteAsync(context);

        rows.Count.ShouldBe(1);
        rows[0]["tenantId"].ShouldBe("tenant-1");
    }

    [Fact]
    public async Task E15_Delta_CallerSuppliedForeignTenantPredicateIsRejected()
    {
        var (executor, context) = NewDelta(
            new Dictionary<string, object?> { ["tenantId"] = "tenant-2" },
            [File("b.parquet", "tenant-2")]);

        await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewaySecurityException>(() => executor.ExecuteAsync(context));
    }

    [Fact]
    public void E15_RowBelongsToTenant_IsStrictAndCaseSensitive()
    {
        var row = new Dictionary<string, object?> { ["TenantId"] = "Tenant-1" };
        LakehouseLocationGuard_RowBelongs(row, "Tenant-1").ShouldBeTrue();
        LakehouseLocationGuard_RowBelongs(row, "tenant-1").ShouldBeFalse();
        LakehouseLocationGuard_RowBelongs(new Dictionary<string, object?> { ["tenantId"] = null }, "tenant-1").ShouldBeFalse();
    }

    private static bool LakehouseLocationGuard_RowBelongs(IReadOnlyDictionary<string, object?> row, string tenant)
    {
        // LakehouseLocationGuard is internal to the extensions assembly; reach it through reflection.
        var type = typeof(LakehouseDataSourceExecutor).Assembly.GetType("Autheris.Extensions.Lakehouse.Services.LakehouseLocationGuard")!;
        var method = type.GetMethod("RowBelongsToTenant", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (bool)method.Invoke(null, [row, "tenantId", tenant])!;
    }

    // ---------------------------------------------------------------- R3-4

    [Fact]
    public async Task R34_ConcurrentAnonymousHealthRequestsShareOneProbe()
    {
        var inner = Substitute.For<IGatewayHealthCheckService>();
        var gate = new TaskCompletionSource<GatewayHealthReport>();
        var calls = 0;
        inner.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        });
        var cached = new CachedGatewayHealthCheckService(inner, TimeSpan.FromMinutes(1));

        var requests = Enumerable.Range(0, 50).Select(_ => cached.CheckHealthAsync()).ToArray();
        gate.SetResult(new GatewayHealthReport(true, []));
        await Task.WhenAll(requests);
        await cached.CheckHealthAsync();

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task R34_ReportIsProbedAgainAfterTtl()
    {
        var inner = Substitute.For<IGatewayHealthCheckService>();
        inner.CheckHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GatewayHealthReport(true, [])));
        var time = new ManualTime();
        var cached = new CachedGatewayHealthCheckService(inner, TimeSpan.FromSeconds(5), time);

        await cached.CheckHealthAsync();
        await cached.CheckHealthAsync();
        await inner.Received(1).CheckHealthAsync(Arg.Any<CancellationToken>());

        time.Advance(TimeSpan.FromSeconds(6));
        await cached.CheckHealthAsync();
        await inner.Received(2).CheckHealthAsync(Arg.Any<CancellationToken>());
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
