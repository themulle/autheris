using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// O1/O2/O13 and O10 (docs/plans/rls-subquery-in-strategy.md, OData-Härtung): requested columns are validated against the
/// catalog and the access decision instead of being dropped silently, and concurrent reads per user and table are bounded.
/// </summary>
public sealed class GatewayExecutionRequestValidationTests
{
    private static readonly TableIdentifier Table = new("lwetem_prod", "fms", "air1");

    private sealed class CapturingExecutor : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.Sql;
        public List<IReadOnlyList<string>> RequestedFields { get; } = [];
        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
        {
            RequestedFields.Add(context.RequestedFields);
            context.Items["RlsPushdownExecuted"] = true;
            Entered.TrySetResult();
            if (Gate != null)
            {
                await Gate.Task.ConfigureAwait(false);
            }
            return [];
        }
    }

    private static TableMetadata CreateMetadata() => new()
    {
        Identifier = Table,
        Table = new Table { SourceName = "lwetem_prod", SchemaName = "fms", TableName = "air1", SourceType = "SqlServer" },
        PrimaryKeyColumns = ["ts", "client_id"],
        Columns =
        [
            new TableColumn { ColumnName = "ts", DataType = "datetime2" },
            new TableColumn { ColumnName = "client_id", DataType = "int" },
            new TableColumn { ColumnName = "serv_break_press1", DataType = "float" },
            new TableColumn { ColumnName = "secret_col", DataType = "varchar" }
        ]
    };

    private static ClaimsPrincipal CreateUser(string sid = "S-1-5-21-LWE-DAVID") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, sid),
            new Claim(ClaimTypes.NameIdentifier, sid),
            new Claim("objectSid", sid),
            new Claim(ClaimTypes.Name, "david")
        ], "TestAuth", ClaimTypes.Name, ClaimTypes.Role));

    private static GatewayExecutionService CreateService(
        CapturingExecutor executor,
        GatewayOptions? options = null,
        ITableReadConcurrencyGate? gate = null)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(CreateMetadata());

        var decision = TableAccessDecision.Allowed(
            Table,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["secret_col"] = ColumnAccessLevel.Deny },
            hasUnconstrainedColumnAllow: true);
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(decision);

        return new GatewayExecutionService(
            metadataRepo,
            Substitute.For<IConsentRepository>(),
            Substitute.For<IAuditLogRepository>(),
            Substitute.For<IConsentResolutionService>(),
            cache,
            Substitute.For<IColumnMaskingProvider>(),
            options: Options.Create(options ?? new GatewayOptions()),
            dataSourceExecutors: [executor],
            concurrencyGate: gate);
    }

    [Fact]
    public async Task UnknownRequestedColumn_IsRejected_InsteadOfReturningAllColumns()
    {
        var executor = new CapturingExecutor();
        var service = CreateService(executor);

        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            service.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, ["amount"], null));

        ex.Message.ShouldContain("amount");
        executor.RequestedFields.ShouldBeEmpty();
    }

    [Fact]
    public async Task MixedValidAndUnknownColumns_AreRejected_NoPartialProjection()
    {
        var executor = new CapturingExecutor();
        var service = CreateService(executor);

        await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            service.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, ["ts", "amount"], null));

        executor.RequestedFields.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeniedColumn_IsRejected_WithTheSameMessageAsAnUnknownColumn()
    {
        var service = CreateService(new CapturingExecutor());

        var denied = await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            service.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, ["secret_col"], null));
        var unknown = await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            service.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, ["no_such_col"], null));

        // No existence oracle: only the echoed column name differs.
        denied.Message.Replace("secret_col", "X").ShouldBe(unknown.Message.Replace("no_such_col", "X"));
    }

    [Fact]
    public async Task RequestedColumns_AreNormalizedToCatalogSpelling_AndDeduplicated()
    {
        var executor = new CapturingExecutor();
        var service = CreateService(executor);

        await service.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, ["TS", "Client_Id", "ts"], null);

        executor.RequestedFields.Single().ShouldBe(new[] { "ts", "client_id" }, ignoreOrder: false);
    }

    [Fact]
    public async Task NoRequestedColumns_SelectsAllAuthorizedColumns()
    {
        var executor = new CapturingExecutor();
        var service = CreateService(executor);

        await service.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, null, null);

        executor.RequestedFields.Single().ShouldBe(new[] { "ts", "client_id", "serv_break_press1" }, ignoreOrder: false);
    }

    [Fact]
    public async Task ConcurrentReads_AboveLimitPerUserAndTable_AreThrottled()
    {
        var gate = new TableReadConcurrencyGate();
        var options = new GatewayOptions { DataSources = new SqlDataSourceOptions { MaxConcurrentReadsPerUserAndTable = 1 } };
        var blocking = new CapturingExecutor { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var first = CreateService(blocking, options, gate);
        var second = CreateService(new CapturingExecutor(), options, gate);

        var running = first.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, null, null);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var ex = await Should.ThrowAsync<GatewayThrottledException>(() =>
            second.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, null, null));
        ex.RetryAfterSeconds.ShouldBeGreaterThan(0);

        // Another user is not affected by david's running query.
        await Should.NotThrowAsync(() => second.ExecuteTableQueryAsync(CreateUser("S-1-5-21-OTHER"), Table, 100, 0, null, null, null));

        blocking.Gate!.SetResult();
        await running;

        // The slot is released after completion.
        await Should.NotThrowAsync(() => second.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, null, null));
    }

    [Fact]
    public async Task ConcurrencyLimitZero_DisablesThrottling()
    {
        var gate = new TableReadConcurrencyGate();
        var options = new GatewayOptions { DataSources = new SqlDataSourceOptions { MaxConcurrentReadsPerUserAndTable = 0 } };
        var blocking = new CapturingExecutor { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var first = CreateService(blocking, options, gate);
        var second = CreateService(new CapturingExecutor(), options, gate);

        var running = first.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, null, null);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Should.NotThrowAsync(() => second.ExecuteTableQueryAsync(CreateUser(), Table, 100, 0, null, null, null));

        blocking.Gate!.SetResult();
        await running;
    }
}

public sealed class TableReadConcurrencyGateTests
{
    [Fact]
    public void TryEnter_RespectsLimitPerKey_AndReleasesOnDispose()
    {
        var gate = new TableReadConcurrencyGate();

        var a1 = gate.TryEnter("t|u|a", 2);
        var a2 = gate.TryEnter("t|u|a", 2);
        var a3 = gate.TryEnter("t|u|a", 2);
        var b1 = gate.TryEnter("t|u|b", 2);

        a1.ShouldNotBeNull();
        a2.ShouldNotBeNull();
        a3.ShouldBeNull();
        b1.ShouldNotBeNull();

        a1!.Dispose();
        a1.Dispose(); // idempotent: must not free a second slot
        gate.TryEnter("t|u|a", 2).ShouldNotBeNull();
        gate.TryEnter("t|u|a", 2).ShouldBeNull();
    }

    [Fact]
    public void TryEnter_LimitZeroOrNegative_IsUnlimited()
    {
        var gate = new TableReadConcurrencyGate();

        for (var i = 0; i < 100; i++)
        {
            gate.TryEnter("k", 0).ShouldNotBeNull();
        }
    }
}
