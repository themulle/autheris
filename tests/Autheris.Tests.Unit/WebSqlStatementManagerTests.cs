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
using Microsoft.AspNetCore.Http;
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
    private static readonly Sid TestOwner = new("user-abc");
    private static readonly ClaimsPrincipal TestUser = new(new ClaimsIdentity(new[]
    { DefaultTenantCaller.Claim,
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
        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));

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
        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromMilliseconds(50));

        // Assert
        status.ShouldNotBeNull();
        status.State.ShouldBe("RUNNING");
        status.Columns.ShouldBeNull();
        status.Data.ShouldBeNull();
        status.NextUri.ShouldBe($"/v1/statement/queued/{status.StatementId}");

        // Now complete the query and poll via statement ID
        var result = new GovernedSqlResult(
            OriginalSql: request.Sql,
            Columns: new[] { "val" },
            Rows: new[] { new Dictionary<string, object?> { ["val"] = 42 } },
            RowCount: 1,
            ElapsedMilliseconds: 120);
        tcs.SetResult(result);

        var polledStatus = await _manager.GetStatusOrWaitAsync(status.StatementId, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));
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
            .Returns(Task.FromResult(new GovernedSqlResult("SELECT 1", new[] { "1" }, Array.Empty<IReadOnlyDictionary<string, object?>>(), 0, 5)));

        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromMilliseconds(10));

        var attacker = new ClaimsPrincipal(new ClaimsIdentity(new[]
        { DefaultTenantCaller.Claim,
            new Claim(ClaimTypes.PrimarySid, "attacker-sid")
        }, "TestAuth"));
        var attackerOwner = new Sid("attacker-sid");

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(() =>
            _manager.GetStatusOrWaitAsync(status.StatementId, attacker, attackerOwner, TestTenant, TimeSpan.FromSeconds(1)));

        var otherTenant = new TenantId("tenant-other");
        await Should.ThrowAsync<SecurityException>(() =>
            _manager.GetStatusOrWaitAsync(status.StatementId, TestUser, TestOwner, otherTenant, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CancelStatementAsync_CancelsRunningStatement()
    {
        // Arrange
        var request = new GovernedSqlQueryRequest("SELECT * FROM default.dbo.infinite_loop");
        var tcs = new TaskCompletionSource<GovernedSqlResult>();

        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromMilliseconds(30));
        status.State.ShouldBe("RUNNING");

        // Act
        bool canceled = await _manager.CancelStatementAsync(status.StatementId, TestUser, TestOwner, TestTenant);
        canceled.ShouldBeTrue();

        // Assert
        var polled = await _manager.GetStatusOrWaitAsync(status.StatementId, TestUser, TestOwner, TestTenant, TimeSpan.FromMilliseconds(10));
        polled.State.ShouldBe("CANCELED");
    }

    [Fact]
    public async Task SubmitOrWaitAsync_WhenStatementDoesNotParse_IsFailedWithTheSyntaxMessage_NotCanceled()
    {
        var request = new GovernedSqlQueryRequest("SELEC 1");
        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GovernedSqlResult>(new Antlr4.Runtime.Misc.ParseCanceledException("line 1:0: mismatched input 'SELEC'")));

        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));

        status.State.ShouldBe("FAILED");
        status.ErrorMessage.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.SyntaxError));
    }

    [Fact]
    public async Task SubmitOrWaitAsync_WhenParserTimesOut_IsFailedWithTheLimitMessage_NotCanceled()
    {
        var request = new GovernedSqlQueryRequest("SELECT 1");
        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GovernedSqlResult>(
                new Antlr4.Runtime.Misc.ParseCanceledException("SQL parsing exceeded the time budget.", new TimeoutException("budget"))));

        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));

        status.State.ShouldBe("FAILED");
        status.ErrorMessage.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.LimitExceeded));
    }

    [Fact]
    public async Task SubmitOrWaitAsync_WhenExecutionIsCanceledWithoutAGovernedError_StaysCanceled()
    {
        var request = new GovernedSqlQueryRequest("SELECT 1");
        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GovernedSqlResult>(new OperationCanceledException()));

        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));

        status.State.ShouldBe("CANCELED");
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
        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));

        // Assert
        status.ShouldNotBeNull();
        status.State.ShouldBe("FAILED");
        status.ErrorMessage.ShouldNotBeNull();
        status.ErrorMessage.ShouldNotContain("SECRET_IBAN");
        status.ErrorMessage.ShouldBe("The SQL statement could not be executed. Contact support with the trace id.");
    }

    [Fact]
    public async Task SubmitOrWaitAsync_ExceedingMaxConcurrentSessionsPerUser_ThrowsGatewayThrottledException()
    {
        using var smallManager = new WebSqlStatementManager(
            _scopeFactory,
            NullLogger<WebSqlStatementManager>.Instance,
            options: null,
            retentionPeriod: TimeSpan.FromMinutes(1),
            maxSessionsPerUser: 2,
            maxTotalSessions: 10);

        var tcs1 = new TaskCompletionSource<GovernedSqlResult>();
        var tcs2 = new TaskCompletionSource<GovernedSqlResult>();
        var tcs3 = new TaskCompletionSource<GovernedSqlResult>();

        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(tcs1.Task, tcs2.Task, tcs3.Task);

        var s1 = await smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), TestUser, TestOwner, TestTenant, TimeSpan.FromMilliseconds(5));
        var s2 = await smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 2"), TestUser, TestOwner, TestTenant, TimeSpan.FromMilliseconds(5));

        s1.State.ShouldBe("RUNNING");
        s2.State.ShouldBe("RUNNING");

        var ex = await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewayThrottledException>(() =>
            smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 3"), TestUser, TestOwner, TestTenant, TimeSpan.FromMilliseconds(5)));

        ex.Message.ShouldContain("limit of 2 concurrent active statements");
        ex.RetryAfterSeconds.ShouldBe(5);
    }

    [Fact]
    public async Task SubmitOrWaitAsync_EvictsOldestCompletedSession_WhenUserCapacityExceeded()
    {
        using var smallManager = new WebSqlStatementManager(
            _scopeFactory,
            NullLogger<WebSqlStatementManager>.Instance,
            options: null,
            retentionPeriod: TimeSpan.FromMinutes(5),
            maxSessionsPerUser: 2,
            maxTotalSessions: 10);

        var fastResult = new GovernedSqlResult("SELECT 1", ["id"], [new Dictionary<string, object?> { ["id"] = 1 }], 1, 5);
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(fastResult));

        var s1 = await smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(1));
        var s2 = await smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 2"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(1));

        s1.State.ShouldBe("FINISHED");
        s2.State.ShouldBe("FINISHED");

        var s3 = await smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 3"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(1));
        s3.State.ShouldBe("FINISHED");

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            smallManager.GetStatusOrWaitAsync(s1.StatementId, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(1)));

        var p2 = await smallManager.GetStatusOrWaitAsync(s2.StatementId, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(1));
        p2.State.ShouldBe("FINISHED");
    }

    [Fact]
    public async Task TwoOidcUsersWithoutPrimarySid_CannotReadEachOthersStatement()
    {
        var u1 = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim("sub", "oidc-user-1")], "TestAuth"));
        var owner1 = new Sid("oidc-user-1");
        var u2 = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim("sub", "oidc-user-2")], "TestAuth"));
        var owner2 = new Sid("oidc-user-2");

        var tcs = new TaskCompletionSource<GovernedSqlResult>();
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        var status = await _manager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), u1, owner1, TestTenant, TimeSpan.FromMilliseconds(10));

        await Should.ThrowAsync<SecurityException>(() =>
            _manager.GetStatusOrWaitAsync(status.StatementId, u2, owner2, TestTenant, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task TwoOidcUsersWithoutPrimarySid_HaveSeparateQuotas()
    {
        using var smallManager = new WebSqlStatementManager(
            _scopeFactory,
            NullLogger<WebSqlStatementManager>.Instance,
            options: null,
            retentionPeriod: TimeSpan.FromMinutes(1),
            maxSessionsPerUser: 1,
            maxTotalSessions: 10);

        var u1 = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim("sub", "oidc-user-1")], "TestAuth"));
        var owner1 = new Sid("oidc-user-1");
        var u2 = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim("sub", "oidc-user-2")], "TestAuth"));
        var owner2 = new Sid("oidc-user-2");

        var tcs1 = new TaskCompletionSource<GovernedSqlResult>();
        var tcs2 = new TaskCompletionSource<GovernedSqlResult>();

        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(tcs1.Task, tcs2.Task);

        var s1 = await smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), u1, owner1, TestTenant, TimeSpan.FromMilliseconds(5));
        s1.State.ShouldBe("RUNNING");

        // u2 must not be blocked by u1's active statement limit
        var s2 = await smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 2"), u2, owner2, TestTenant, TimeSpan.FromMilliseconds(5));
        s2.State.ShouldBe("RUNNING");
    }

    [Fact]
    public async Task Cancel_ByOtherCanonicalSid_Throws()
    {
        var u1 = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "user-1")], "TestAuth"));
        var owner1 = new Sid("user-1");
        var u2 = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "user-2")], "TestAuth"));
        var owner2 = new Sid("user-2");

        var tcs = new TaskCompletionSource<GovernedSqlResult>();
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        var status = await _manager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), u1, owner1, TestTenant, TimeSpan.FromMilliseconds(10));

        await Should.ThrowAsync<SecurityException>(() =>
            _manager.CancelStatementAsync(status.StatementId, u2, owner2, TestTenant));
    }

    [Fact]
    public async Task StatementId_Has128BitRandomSuffix()
    {
        var fastResult = new GovernedSqlResult("SELECT 1", ["id"], [new Dictionary<string, object?> { ["id"] = 1 }], 1, 5);
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(fastResult));

        var status = await _manager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(1));
        var parts = status.StatementId.Split('_');
        parts.Length.ShouldBe(4);
        // Suffix must be 32 hex chars (128 bits)
        parts[3].Length.ShouldBe(32);
        System.Text.RegularExpressions.Regex.IsMatch(parts[3], "^[0-9a-f]{32}$").ShouldBeTrue();
    }

    [Fact]
    public async Task TrinoStatement_WithoutCanonicalIdentity_Returns401()
    {
        var anonymousContext = new DefaultHttpContext();
        anonymousContext.User = new ClaimsPrincipal(new ClaimsIdentity()); // no SID claims

        var statementManager = Substitute.For<IWebSqlStatementManager>();

        await Autheris.Api.Endpoints.WebSqlEndpoints.HandleTrinoQueuedStatementRequest("stmt-1", anonymousContext, statementManager, NullLoggerFactory.Instance);

        anonymousContext.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }
}
