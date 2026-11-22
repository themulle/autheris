using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Tests.Unit.Catalog;
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
        status.Failure.ShouldBeNull();
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
        status.Failure.ShouldNotBeNull();
        status.Failure.Kind.ShouldBe(SqlFailureKind.InvalidQuery);
        status.Failure.FixedMessage.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.SyntaxError));
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
        status.Failure.ShouldNotBeNull();
        status.Failure.FixedMessage.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.LimitExceeded));
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
        status.Failure.ShouldNotBeNull();
        status.Failure.FixedMessage.ShouldNotContain("SECRET_IBAN");
        status.Failure.FixedMessage.ShouldBe("The SQL statement could not be executed. Contact support with the trace id.");
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

        await Autheris.Api.Endpoints.TrinoProtocolEndpoints.HandleTrinoPoll("stmt-1", 0, anonymousContext, statementManager, NullLoggerFactory.Instance);

        anonymousContext.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task MetadataEmulation_OnlyOnTrinoTransport()
    {
        var visibility = TrinoMetadataVisibilityFakes.AllowAll();
        _serviceProvider.GetService(typeof(Autheris.Application.Common.Interfaces.ITableMetadataRepository))
            .Returns(new FakeTableMetadataRepository());
        _serviceProvider.GetService(typeof(Autheris.Application.Catalog.Discovery.IDiscoveryService))
            .Returns(visibility.Discovery);
        _serviceProvider.GetService(typeof(Autheris.Application.Policy.Pdp.IAccessDecisionPoint))
            .Returns(visibility.DecisionPoint);

        var trinoRequest = new GovernedSqlQueryRequest("SHOW CATALOGS", Transport: SqlTransports.Trino);
        var statusTrino = await _manager.SubmitOrWaitAsync(trinoRequest, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));

        statusTrino.State.ShouldBe("FINISHED");
        statusTrino.Columns.ShouldBe(new[] { "Catalog" });

        var webSqlRequest = new GovernedSqlQueryRequest("SHOW CATALOGS", Transport: SqlTransports.WebSql);
        _sqlExecutionService.ExecuteQueryBufferedAsync(webSqlRequest, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GovernedSqlResult("SHOW CATALOGS", ["col"], [], 0, 5)));

        var statusWebSql = await _manager.SubmitOrWaitAsync(webSqlRequest, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));
        statusWebSql.State.ShouldBe("FINISHED");
        statusWebSql.Columns.ShouldBe(new[] { "col" });
    }

    [Fact]
    public async Task Truncated_IsCarriedIntoStatus()
    {
        var request = new GovernedSqlQueryRequest("SELECT 1");
        var truncatedResult = new GovernedSqlResult("SELECT 1", ["id"], [], 0, 5, Truncated: true);
        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(truncatedResult));

        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));
        status.Truncated.ShouldBeTrue();

        var nonTruncatedResult = new GovernedSqlResult("SELECT 1", ["id"], [], 0, 5, Truncated: false);
        _sqlExecutionService.ExecuteQueryBufferedAsync(request, TestUser, TestTenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(nonTruncatedResult));

        var statusNonTruncated = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));
        statusNonTruncated.Truncated.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MetadataQuery_WithoutDiscoveryInScope_FailsClosed()
    {
        _serviceProvider.GetService(typeof(Autheris.Application.Common.Interfaces.ITableMetadataRepository))
            .Returns(new FakeTableMetadataRepository());

        var trinoRequest = new GovernedSqlQueryRequest("SHOW CATALOGS", Transport: SqlTransports.Trino);
        var status = await _manager.SubmitOrWaitAsync(trinoRequest, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));

        status.State.ShouldBe("FAILED");
        status.Data.ShouldBeNull();
    }

    [Fact]
    public async Task MetadataQuery_RespectsRequestRowLimit_AndMarksTruncated()
    {
        var tables = new[]
        {
            Wp0.Table("sales", "t1", columns: ["id"]),
            Wp0.Table("sales", "t2", columns: ["id"]),
            Wp0.Table("sales", "t3", columns: ["id"])
        };
        var visibility = TrinoMetadataVisibilityFakes.AllowAll(tables);
        var repo = Wp0.Tables(tables);
        _serviceProvider.GetService(typeof(Autheris.Application.Common.Interfaces.ITableMetadataRepository))
            .Returns(repo);
        _serviceProvider.GetService(typeof(Autheris.Application.Catalog.Discovery.IDiscoveryService))
            .Returns(visibility.Discovery);
        _serviceProvider.GetService(typeof(Autheris.Application.Policy.Pdp.IAccessDecisionPoint))
            .Returns(visibility.DecisionPoint);

        var request = new GovernedSqlQueryRequest(
            "SELECT TABLE_NAME FROM system.jdbc.tables",
            RowLimit: new SqlRowLimit(2, 2),
            Transport: SqlTransports.Trino);
        var status = await _manager.SubmitOrWaitAsync(request, TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));

        status.State.ShouldBe("FINISHED");
        status.Data.ShouldNotBeNull();
        status.Data.Count.ShouldBe(2);
        status.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task SubmitOrWaitAsync_ConcurrentSubmits_EnforcesMaxSessionsPerUserAtomicallyAcrossAdmission()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredAdmission = 0;
        var allSlotsEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quota = new StubQueryCostQuota(async (_, ct) =>
        {
            if (Interlocked.Increment(ref enteredAdmission) == 2)
            {
                allSlotsEntered.TrySetResult(true);
            }

            await gate.Task.WaitAsync(ct);
            return new Autheris.Application.Common.Quota.QueryCostAdmission(
                Autheris.Application.Common.Quota.QueryCostOutcome.Admitted,
                "user",
                Autheris.Domain.Model.ClientQuotaPolicy.ForTier(Autheris.Domain.Model.ClientTier.Standard),
                1, 100, 99, 0, null);
        });

        var scopeFactory = new StubScopeFactory(quota, TrinoSqlEngine.FastSqlEngine.Default, new StubSqlExecutor());
        using var smallManager = new WebSqlStatementManager(
            scopeFactory,
            NullLogger<WebSqlStatementManager>.Instance,
            options: null,
            retentionPeriod: TimeSpan.FromMinutes(1),
            maxSessionsPerUser: 2,
            maxTotalSessions: 10);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<StatementExecutionStatus>? t1 = null;
        Task<StatementExecutionStatus>? t2 = null;
        try
        {
            t1 = Task.Run(() => smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(5), ct: testCts.Token));
            t2 = Task.Run(() => smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 2"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(5), ct: testCts.Token));

            await allSlotsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var concurrentTasks = Enumerable.Range(3, 6)
                .Select(i => Task.Run(async () =>
                {
                    try
                    {
                        await smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest($"SELECT {i}"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(5), ct: testCts.Token);
                        return true;
                    }
                    catch (Autheris.Domain.Exceptions.GatewayThrottledException)
                    {
                        return false;
                    }
                }))
                .ToArray();

            var overflowResults = await Task.WhenAll(concurrentTasks).WaitAsync(TimeSpan.FromSeconds(5));
            overflowResults.ShouldAllBe(admitted => !admitted);
            Volatile.Read(ref enteredAdmission).ShouldBe(2);
        }
        finally
        {
            gate.TrySetResult(true);
        }

        var completed = await Task.WhenAll(t1!, t2!).WaitAsync(TimeSpan.FromSeconds(5));
        completed.ShouldAllBe(s => s.State == "FINISHED");
    }

    [Fact]
    public async Task SubmitOrWaitAsync_ConcurrentSubmitsAcrossUsers_EnforcesMaxTotalSessionsAtomicallyAcrossAdmission()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredAdmission = 0;
        var allSlotsEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quota = new StubQueryCostQuota(async (_, ct) =>
        {
            if (Interlocked.Increment(ref enteredAdmission) == 3)
            {
                allSlotsEntered.TrySetResult(true);
            }

            await gate.Task.WaitAsync(ct);
            return new Autheris.Application.Common.Quota.QueryCostAdmission(
                Autheris.Application.Common.Quota.QueryCostOutcome.Admitted,
                "user",
                Autheris.Domain.Model.ClientQuotaPolicy.ForTier(Autheris.Domain.Model.ClientTier.Standard),
                1, 100, 99, 0, null);
        });

        var scopeFactory = new StubScopeFactory(quota, TrinoSqlEngine.FastSqlEngine.Default, new StubSqlExecutor());
        using var smallManager = new WebSqlStatementManager(
            scopeFactory,
            NullLogger<WebSqlStatementManager>.Instance,
            options: null,
            retentionPeriod: TimeSpan.FromMinutes(1),
            maxSessionsPerUser: 2,
            maxTotalSessions: 3);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<StatementExecutionStatus>[] initial = [];
        try
        {
            initial = Enumerable.Range(1, 3)
                .Select(i =>
                {
                    var u = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, $"user-{i}")], "TestAuth"));
                    var sid = new Sid($"user-{i}");
                    return Task.Run(() => smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest($"SELECT {i}"), u, sid, TestTenant, TimeSpan.FromSeconds(5), ct: testCts.Token));
                })
                .ToArray();

            await allSlotsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var extraUser = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "user-extra")], "TestAuth"));
            await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewayThrottledException>(() =>
                smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 99"), extraUser, new Sid("user-extra"), TestTenant, TimeSpan.FromSeconds(5), ct: testCts.Token)
                    .WaitAsync(TimeSpan.FromSeconds(5)));

            Volatile.Read(ref enteredAdmission).ShouldBe(3);
        }
        finally
        {
            gate.TrySetResult(true);
        }

        var completed = await Task.WhenAll(initial).WaitAsync(TimeSpan.FromSeconds(5));
        completed.ShouldAllBe(s => s.State == "FINISHED");
    }

    [Fact]
    public async Task SubmitOrWaitAsync_AdmissionDenialExceptionAndCancellation_ReleaseReservedSlots()
    {
        int call = 0;
        var std = Autheris.Domain.Model.ClientQuotaPolicy.ForTier(Autheris.Domain.Model.ClientTier.Standard);
        var quota = new StubQueryCostQuota((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            int current = Interlocked.Increment(ref call);
            return current switch
            {
                1 => Task.FromResult(new Autheris.Application.Common.Quota.QueryCostAdmission(
                    Autheris.Application.Common.Quota.QueryCostOutcome.OverCost, "user", std, 0, 10, 0, 0, null)),
                2 => Task.FromResult(new Autheris.Application.Common.Quota.QueryCostAdmission(
                    Autheris.Application.Common.Quota.QueryCostOutcome.QuotaExhausted, "user", std, 0, 100, 0, 7, null)),
                3 => Task.FromException<Autheris.Application.Common.Quota.QueryCostAdmission>(new InvalidOperationException("quota backend down")),
                _ => Task.FromResult(new Autheris.Application.Common.Quota.QueryCostAdmission(
                    Autheris.Application.Common.Quota.QueryCostOutcome.Admitted, "user", std, 1, 100, 99, 0, null))
            };
        });

        var scopeFactory = new StubScopeFactory(quota, TrinoSqlEngine.FastSqlEngine.Default, new StubSqlExecutor());
        using var smallManager = new WebSqlStatementManager(
            scopeFactory,
            NullLogger<WebSqlStatementManager>.Instance,
            options: null,
            retentionPeriod: TimeSpan.FromMinutes(1),
            maxSessionsPerUser: 1,
            maxTotalSessions: 1);

        await Should.ThrowAsync<TrinoSqlEngine.Ast.Emit.SqlCostExceededException>(() =>
            smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(1)));

        await Should.ThrowAsync<TrinoSqlEngine.Ast.Emit.SqlQuotaExhaustedException>(() =>
            smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(1)));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(1)));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(1), ct: cts.Token));

        var ok = await smallManager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), TestUser, TestOwner, TestTenant, TimeSpan.FromSeconds(2));
        ok.State.ShouldBe("FINISHED");
    }

    private sealed class StubQueryCostQuota(
        Func<Autheris.Application.Common.Quota.QueryCostRequest, CancellationToken, Task<Autheris.Application.Common.Quota.QueryCostAdmission>> admit)
        : Autheris.Application.Common.Quota.IQueryCostQuota
    {
        public Task<Autheris.Application.Common.Quota.QueryCostAdmission> AdmitAsync(Autheris.Application.Common.Quota.QueryCostRequest request, CancellationToken ct) =>
            admit(request, ct);

        public Task<Autheris.Domain.Model.CostQuotaResult?> SettleAsync(Autheris.Application.Common.Quota.QueryCostAdmission admission, int actualCost, CancellationToken ct) =>
            Task.FromResult<Autheris.Domain.Model.CostQuotaResult?>(null);

        public Task ChargeFailureFeeAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubSqlExecutor : IGovernedSqlExecutionService
    {
        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult(new GovernedSqlResult(request.Sql, ["col"], [new Dictionary<string, object?> { ["col"] = 1 }], 1, 1));

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubScopeFactory(
        Autheris.Application.Common.Quota.IQueryCostQuota quota,
        TrinoSqlEngine.ISqlEngine engine,
        IGovernedSqlExecutionService executor) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(quota, engine, executor));

        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider => provider;
            public void Dispose() { }
        }

        private sealed class Provider(
            Autheris.Application.Common.Quota.IQueryCostQuota quota,
            TrinoSqlEngine.ISqlEngine engine,
            IGovernedSqlExecutionService executor) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(Autheris.Application.Common.Quota.IQueryCostQuota) ? quota :
                serviceType == typeof(TrinoSqlEngine.ISqlEngine) ? engine :
                serviceType == typeof(IGovernedSqlExecutionService) ? executor : null;
        }
    }

    private sealed class FakeTableMetadataRepository : Autheris.Application.Common.Interfaces.ITableMetadataRepository
    {
        public Task<Autheris.Domain.Model.TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<Autheris.Domain.Model.TableMetadata?>(null);
        public Task<IReadOnlyList<Autheris.Domain.Model.TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Autheris.Domain.Model.TableMetadata>>([]);
        public Task<Autheris.Domain.Model.TableMetadata> UpsertTableMetadataAsync(Autheris.Domain.Model.TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<Autheris.Domain.Model.TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<Autheris.Domain.Model.TableIndexDefinition> indexes,
            IReadOnlyList<Autheris.Domain.Model.KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) => Task.FromResult<Autheris.Domain.Model.TableKeyDefinitionChange?>(null);
    }
}
