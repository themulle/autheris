namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #94 (M3): a table whose stored key definition is invalid is "unavailable". Every transport answers exactly as it does for
/// an unknown table (same status, same body, no extra detail), failing closed.
/// </summary>
[Trait("Category", "Security")]
public sealed class TableMetadataUnavailableMappingTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private static Exception Unknown() => new TableNotFoundException(Orders);

    private static Exception Unavailable() => new TableMetadataUnavailableException(Orders);

    [Fact]
    public async Task ExceptionHandler_UnavailableAnswersLikeUnknown()
    {
        var (s1, b1) = await Handle(Unknown());
        var (s2, b2) = await Handle(Unavailable());

        s2.ShouldBe(s1);
        s2.ShouldBeLessThan(500);
        b2.ShouldBe(b1);
        b2.ShouldNotContain("unavailable", Case.Insensitive);
    }

    private static async Task<(int Status, string Body)> Handle(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-1";
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);
        (await handler.TryHandleAsync(context, exception, CancellationToken.None)).ShouldBeTrue();
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task WebSql_UnavailableAnswersLikeUnknown()
    {
        var (s1, b1) = await WebSql(Unknown());
        var (s2, b2) = await WebSql(Unavailable());

        s2.ShouldBe(s1);
        s2.ShouldBeLessThan(500);
        b2.ShouldBe(b1);
    }

    private static async Task<(int Status, string Body)> WebSql(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-1";
        await WebSqlEndpoints.WriteWebSqlErrorAsync(context, exception, NullLogger.Instance, CancellationToken.None);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public void GraphQl_UnavailableAnswersLikeUnknown()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var filter = new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance);

        IError Sanitize(Exception ex) => filter.OnError(ErrorBuilder.New().SetMessage(ex.Message).SetException(ex).SetCode(((GatewaySecurityException)ex).ErrorCode).Build());

        var unknown = Sanitize(Unknown());
        var unavailable = Sanitize(Unavailable());

        unavailable.Code.ShouldBe(unknown.Code);
        unavailable.Message.ShouldBe(unknown.Message);
    }

    [Fact]
    public async Task Mcp_Executor_UnavailableAnswersLikeUnknown()
    {
        var registry = new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData());
        var tool = registry.FindTool("describe_dataset")!;

        async Task<string> Describe(Exception ex)
        {
            var catalog = Substitute.For<IMcpDatasetCatalog>();
            catalog.DescribeDatasetAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<McpDatasetDescription>(_ => throw ex);
            var executor = new GatewayMcpQueryExecutor(
                Substitute.For<IRequestExecutorProvider>(), Substitute.For<IGatewayExecutionService>(),
                NullLogger<GatewayMcpQueryExecutor>.Instance, datasetCatalog: catalog);
            return await executor.ExecuteOperationAsync(tool, """{"dataset":"sales.public.orders"}""",
                new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["Analyst"]));
        }

        var unknown = await Describe(Unknown());
        var unavailable = await Describe(Unavailable());

        unavailable.ShouldBe(unknown);
        unavailable.ShouldContain("NOT_FOUND");
    }

    [Fact]
    public async Task Mcp_Guardrail_UnavailableTable_IsDeniedWithoutThrowing()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>())
            .Returns<TableMetadata?>(_ => throw new TableMetadataUnavailableException(Orders));
        var executor = Substitute.For<IMcpQueryExecutor>();
        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData()),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, queryExecutor: executor,
            tableMetadataRepository: metadataRepo);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("sample_rows", """{"dataset":"sales.public.orders"}"""),
            new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER"));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldNotContain("unavailable", Case.Insensitive);
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    private sealed class FixedDemoData : IDemoDataSwitch
    {
        public bool Enabled => false;
    }
}
