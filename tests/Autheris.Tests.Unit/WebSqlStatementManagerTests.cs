namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebSqlStatementManagerTests : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceScope _scope;
    private readonly IServiceProvider _serviceProvider;
    private readonly IGovernedSqlExecutionService _sqlExecutionService;
    private readonly WebSqlStatementManager _manager;

    private static readonly TenantId TestTenant = new("tenant-123");
    private static readonly ClaimsPrincipal TestUser = new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.PrimarySid, "user-abc"),
        new Claim(ClaimTypes.Name, "Alice")
    }, "TestAuth"));

    public WebSqlStatementManagerTests()
    {
        _sqlExecutionService = Substitute.For<IGovernedSqlExecutionService>();
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scope = Substitute.For<IServiceScope>();
        _serviceProvider = Substitute.For<IServiceProvider>();

        _scopeFactory.CreateScope().Returns(_scope);
        _scope.ServiceProvider.Returns(_serviceProvider);
        _serviceProvider.GetService(typeof(IGovernedSqlExecutionService)).Returns(_sqlExecutionService);

        _manager = new WebSqlStatementManager(
            _scopeFactory,
            NullLogger<WebSqlStatementManager>.Instance,
            retentionPeriod: TimeSpan.FromMinutes(5));
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    [Fact]
    public async Task SubmitOrWaitAsync_FastQuery_CompletesSynchronously_WithStateFinished()
    {
        // Arrange
        var request = new GovernedSqlQueryRequest("SELECT id, name FROM default.dbo.users");
        var result = new GovernedSqlResult(
            OriginalSql: request.Sql,
            RewrittenSql: "SELECT [id], [name] FROM [dbo].[users]",
            Columns: new[] { "id", "name" },
            Rows: new[]
            {
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
                new Dictionary<string, object?> { ["id"] = 2, ["name"] = "Bob" }
            },
            RowCount: 2,
            ElapsedMilliseconds: 15);

        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

        // Act
        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestTenant, TimeSpan.FromSeconds(2));

        // Assert
        status.ShouldNotBeNull();
        status.State.ShouldBe("FINISHED");
        status.Columns.ShouldBe(new[] { "id", "name" });
        status.Data.ShouldNotBeNull();
        status.Data.Count.ShouldBe(2);
        status.Data[0][0].ShouldBe(1);
        status.Data[0][1].ShouldBe("Alice");
        status.Data[1][0].ShouldBe(2);
        status.Data[1][1].ShouldBe("Bob");
        status.NextUri.ShouldBeNull();
        status.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task SubmitOrWaitAsync_QueryExceedsWaitTimeout_ReturnsRunning_WithNextUri()
    {
        // Arrange
        var request = new GovernedSqlQueryRequest("SELECT * FROM default.dbo.big_table");
        var tcs = new TaskCompletionSource<GovernedSqlResult>();

        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        // Act: wait timeout of 50ms while query is still running
        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestTenant, TimeSpan.FromMilliseconds(50));

        // Assert
        status.ShouldNotBeNull();
        status.State.ShouldBe("RUNNING");
        status.Columns.ShouldBeNull();
        status.Data.ShouldBeNull();
        status.NextUri.ShouldBe($"/v1/statement/queued/{status.StatementId}");

        // Now complete the query and poll via statement ID
        var result = new GovernedSqlResult(
            OriginalSql: request.Sql,
            RewrittenSql: "SELECT * FROM [dbo].[big_table]",
            Columns: new[] { "val" },
            Rows: new[] { new Dictionary<string, object?> { ["val"] = 42 } },
            RowCount: 1,
            ElapsedMilliseconds: 120);
        tcs.SetResult(result);

        var polledStatus = await _manager.GetStatusOrWaitAsync(status.StatementId, TestUser, TestTenant, TimeSpan.FromSeconds(2));
        polledStatus.ShouldNotBeNull();
        polledStatus.State.ShouldBe("FINISHED");
        polledStatus.Columns.ShouldBe(new[] { "val" });
        polledStatus.Data.ShouldNotBeNull();
        polledStatus.Data[0][0].ShouldBe(42);
        polledStatus.NextUri.ShouldBeNull();
    }

    [Fact]
    public async Task GetStatusOrWaitAsync_WrongTenantOrUser_ThrowsSecurityException()
    {
        // Arrange
        var request = new GovernedSqlQueryRequest("SELECT 1");
        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GovernedSqlResult("SELECT 1", "SELECT 1", new[] { "1" }, Array.Empty<IReadOnlyDictionary<string, object?>>(), 0, 5)));

        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestTenant, TimeSpan.FromMilliseconds(10));

        var attacker = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, "attacker-sid")
        }, "TestAuth"));

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(() =>
            _manager.GetStatusOrWaitAsync(status.StatementId, attacker, TestTenant, TimeSpan.FromSeconds(1)));

        var otherTenant = new TenantId("tenant-other");
        await Should.ThrowAsync<SecurityException>(() =>
            _manager.GetStatusOrWaitAsync(status.StatementId, TestUser, otherTenant, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CancelStatementAsync_CancelsRunningStatement()
    {
        // Arrange
        var request = new GovernedSqlQueryRequest("SELECT * FROM default.dbo.infinite_loop");
        var tcs = new TaskCompletionSource<GovernedSqlResult>();

        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestTenant, TimeSpan.FromMilliseconds(30));
        status.State.ShouldBe("RUNNING");

        // Act
        bool canceled = await _manager.CancelStatementAsync(status.StatementId, TestUser, TestTenant);
        canceled.ShouldBeTrue();

        // Assert
        var polled = await _manager.GetStatusOrWaitAsync(status.StatementId, TestUser, TestTenant, TimeSpan.FromMilliseconds(10));
        polled.State.ShouldBe("CANCELED");
    }

    [Fact]
    public async Task SubmitOrWaitAsync_WhenExecutionFaultsWithDatabaseError_SanitizesErrorMessage()
    {
        // Arrange
        var request = new GovernedSqlQueryRequest("SELECT secret_iban FROM default.dbo.users");
        var tcs = new TaskCompletionSource<GovernedSqlResult>();
        tcs.SetException(new InvalidOperationException("Conversion failed when converting the varchar value 'SECRET_IBAN_99999' to data type int"));

        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        // Act
        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestTenant, TimeSpan.FromSeconds(2));

        // Assert
        status.ShouldNotBeNull();
        status.State.ShouldBe("FAILED");
        status.ErrorMessage.ShouldNotBeNull();
        status.ErrorMessage.ShouldNotContain("SECRET_IBAN");
        status.ErrorMessage.ShouldBe("The SQL statement could not be executed. Contact support with the trace id.");
    }
}
