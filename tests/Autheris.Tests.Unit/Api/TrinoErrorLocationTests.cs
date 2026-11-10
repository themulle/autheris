namespace Autheris.Tests.Unit.Api;

using System;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.DataSources;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

public sealed class TrinoErrorLocationTests
{
    private static readonly TenantId TestTenant = new("tenant-trino");
    private static readonly ClaimsPrincipal TestUser = new(new ClaimsIdentity([
        new Claim(ClaimTypes.PrimarySid, "user-trino"),
        new Claim("tenant_id", "tenant-trino"),
        new Claim("tid", "tenant-trino"),
        new Claim(ClaimTypes.Name, "TrinoUser")
    ], "TestAuth"));

    private static IIngressTransportContext TrinoIngress()
    {
        var ctx = new IngressTransportContext();
        ctx.Set(DataSourceTransportFlags.Trino);
        return ctx;
    }

    private static (DefaultHttpContext Context, IWebSqlStatementManager StatementManager, IGovernedSqlExecutionService SqlService) CreateContext()
    {
        var statementManager = Substitute.For<IWebSqlStatementManager>();
        var sqlService = Substitute.For<IGovernedSqlExecutionService>();

        var services = new ServiceCollection();
        services.AddSingleton(statementManager);
        services.AddSingleton(sqlService);
        services.AddSingleton(TrinoIngress());
        var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = sp,
            User = TestUser
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("gateway.example.com");
        context.Request.Path = "/v1/statement";
        context.Request.ContentType = "text/plain";
        context.Request.Headers["X-Trino-User"] = "TrinoUser";
        context.Request.Headers["X-Tenant-Id"] = TestTenant.Value;
        context.Response.Body = new MemoryStream();

        return (context, statementManager, sqlService);
    }

    // Dummy exception classes to test reflection-based position extraction
    public class DummySqlParseException : Exception
    {
        public int LineNumber { get; }
        public int ColumnNumber { get; }
        public DummySqlParseException(int line, int column, string message) : base(message)
        {
            LineNumber = line;
            ColumnNumber = column;
        }
    }

    public class DummyCharPosAstBuildException : AstBuildException
    {
        public int Line { get; }
        public int CharPositionInLine { get; }
        public DummyCharPosAstBuildException(int line, int charPos, string message) : base(message)
        {
            Line = line;
            CharPositionInLine = charPos;
        }
    }

    public class DummyNestedLocationSqlCompileException : Exception
    {
        public object ErrorLocation { get; }
        public DummyNestedLocationSqlCompileException(int line, int column, string message) : base(message)
        {
            ErrorLocation = new { LineNumber = line, ColumnNumber = column };
        }
    }

    [Fact]
    public async Task Submit_SyntaxError_ReturnsErrorLocationWithAccurateLineAndColumn()
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT\n  FROM foo"));

        var syntaxError = new SqlSyntaxErrorException(2, 3, "line 2:2: mismatched input 'FROM'");
        statementManager.SubmitOrWaitAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(syntaxError);

        var gatewayOptions = Options.Create(new GatewayOptions());
        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FAILED");
        var error = root.GetProperty("error");
        error.GetProperty("errorType").GetString().ShouldBe("USER_ERROR");
        error.GetProperty("errorCode").GetInt32().ShouldBe(1);
        error.GetProperty("errorName").GetString().ShouldBe("SYNTAX_ERROR");

        error.TryGetProperty("errorLocation", out var location).ShouldBeTrue();
        location.GetProperty("lineNumber").GetInt32().ShouldBe(2);
        location.GetProperty("columnNumber").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Submit_SqlParseException_ExtractsLineAndColumnNumber()
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT invalid syntax"));

        var parseEx = new DummySqlParseException(1, 12, "Syntax error at column 12");
        statementManager.SubmitOrWaitAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(parseEx);

        var gatewayOptions = Options.Create(new GatewayOptions());
        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var error = doc.RootElement.GetProperty("error");

        error.TryGetProperty("errorLocation", out var location).ShouldBeTrue();
        location.GetProperty("lineNumber").GetInt32().ShouldBe(1);
        location.GetProperty("columnNumber").GetInt32().ShouldBe(12);
    }

    [Fact]
    public async Task Submit_AstBuildException_WithCharPositionInLine_ConvertsTo1BasedColumn()
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT construct()"));

        // charPositionInLine is 0-based (11 -> column 12)
        var astEx = new DummyCharPosAstBuildException(3, 11, "Unsupported AST construct");
        statementManager.SubmitOrWaitAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(astEx);

        var gatewayOptions = Options.Create(new GatewayOptions());
        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var error = doc.RootElement.GetProperty("error");

        error.TryGetProperty("errorLocation", out var location).ShouldBeTrue();
        location.GetProperty("lineNumber").GetInt32().ShouldBe(3);
        location.GetProperty("columnNumber").GetInt32().ShouldBe(12);
    }

    [Fact]
    public async Task Submit_SqlCompileException_WithNestedErrorLocation_ExtractsLocation()
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT bad_expr"));

        var compileEx = new DummyNestedLocationSqlCompileException(4, 18, "Compile error");
        statementManager.SubmitOrWaitAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(compileEx);

        var gatewayOptions = Options.Create(new GatewayOptions());
        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var error = doc.RootElement.GetProperty("error");

        error.TryGetProperty("errorLocation", out var location).ShouldBeTrue();
        location.GetProperty("lineNumber").GetInt32().ShouldBe(4);
        location.GetProperty("columnNumber").GetInt32().ShouldBe(18);
    }

    [Fact]
    public async Task Submit_NonSyntaxError_OmitsErrorLocation()
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT * FROM secret_table"));

        statementManager.SubmitOrWaitAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new SecurityException("Cross-tenant policy denied"));

        var gatewayOptions = Options.Create(new GatewayOptions());
        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FAILED");
        var error = root.GetProperty("error");
        error.GetProperty("errorType").GetString().ShouldBe("USER_ERROR");
        error.GetProperty("errorCode").GetInt32().ShouldBe(4);
        error.GetProperty("errorName").GetString().ShouldBe("PERMISSION_DENIED");
        error.TryGetProperty("errorLocation", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Poll_WithSyntaxErrorLocation_ReturnsErrorLocation()
    {
        var (context, statementManager, _) = CreateContext();

        var failure = new SqlFailure(SqlFailureKind.InvalidQuery, "Syntax error", "SYNTAX_ERROR", Line: 5, Column: 20);
        var status = new StatementExecutionStatus("stmt-poll-err", "FAILED", null, null, 10, Failure: failure);

        statementManager.GetStatusOrWaitAsync(
            "stmt-poll-err",
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(status));

        await TrinoProtocolEndpoints.HandleTrinoPoll("stmt-poll-err", 0, context, statementManager, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var error = doc.RootElement.GetProperty("error");

        error.TryGetProperty("errorLocation", out var location).ShouldBeTrue();
        location.GetProperty("lineNumber").GetInt32().ShouldBe(5);
        location.GetProperty("columnNumber").GetInt32().ShouldBe(20);
    }

    [Fact]
    public async Task QueryResultsWriter_DirectWrite_WithLineAndColumn_WritesErrorLocation()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var failure = new SqlFailure(SqlFailureKind.InvalidQuery, "Invalid syntax near SELECT", "SYNTAX_ERROR", Line: 1, Column: 12);
        var status = new StatementExecutionStatus("stmt-err", "FAILED", null, null, 10, Failure: failure);

        await TrinoQueryResultsWriter.WriteAsync(context, status, token: 0);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var error = doc.RootElement.GetProperty("error");

        error.TryGetProperty("errorLocation", out var location).ShouldBeTrue();
        location.GetProperty("lineNumber").GetInt32().ShouldBe(1);
        location.GetProperty("columnNumber").GetInt32().ShouldBe(12);
    }

    [Fact]
    public async Task QueryResultsWriter_DirectWrite_WithoutPosition_OmitsErrorLocation()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var failure = new SqlFailure(SqlFailureKind.Internal, "Generic server error", "GENERIC_INTERNAL_ERROR");
        var status = new StatementExecutionStatus("stmt-err-internal", "FAILED", null, null, 10, Failure: failure);

        await TrinoQueryResultsWriter.WriteAsync(context, status, token: 0);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var error = doc.RootElement.GetProperty("error");

        error.TryGetProperty("errorLocation", out _).ShouldBeFalse();
    }
}
