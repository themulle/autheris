namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Application.Serialization;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// WebSQL findings 4.3: the Arrow export takes the table from the query string or the JSON body. The ReBAC check reads
/// the same place, and SQL in the body is checked for every table it references.
/// </summary>
public sealed class ArrowExportRebacBodyTests
{
    private const string Tenant = "tenant-1";
    private const string User = "user:alice";

    private static async Task<IServiceProvider> CreateServicesAsync(params string[] viewerTables)
    {
        var options = Options.Create(new GatewayOptions
        {
            Arrow = new ArrowExportOptions { Enabled = true, MaxExportRows = 1000, BatchSize = 1000 },
            Rebac = new RebacOptions { Enabled = true }
        });
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        foreach (var table in viewerTables)
        {
            await store.AddTupleAsync(new RebacTuple(Tenant, User, "viewer", "table:" + TableIdentifierNormalizer.Normalize(table).ToQualifiedName()));
        }

        var services = new ServiceCollection();
        services.AddSingleton<IRebacStore>(store);
        services.AddSingleton<IRebacEvaluator>(new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance));
        services.AddScoped<IRebacBatchDataLoader, RebacBatchDataLoader>();
        services.AddSingleton<ISqlEngine>(new FastSqlEngine());
        services.AddLogging();
        services.AddSingleton(options);
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider services, string body)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, User), new Claim("tenant_id", Tenant)], "TestAuth"));
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    private sealed class InvocationContext(HttpContext httpContext) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;
        public override IList<object?> Arguments { get; } = new List<object?>();
        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    private static async Task<(object? Result, bool NextCalled)> RunFilterAsync(HttpContext context)
    {
        var filter = new RebacEndpointFilter("viewer", "table", "table", RebacParameterSource.QueryOrJsonBody);
        bool nextCalled = false;
        var result = await filter.InvokeAsync(new InvocationContext(context), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        });
        return (result, nextCalled);
    }

    [Fact]
    public async Task Filter_TableInBody_WithViewerRelation_ProceedsAndKeepsBodyReadable()
    {
        var services = await CreateServicesAsync("sales.public.orders");
        var context = CreateContext(services, "{\"table\":\"sales.public.orders\"}");

        var (_, nextCalled) = await RunFilterAsync(context);

        nextCalled.ShouldBeTrue();
        context.Request.Body.Position.ShouldBe(0);
        (await new StreamReader(context.Request.Body).ReadToEndAsync()).ShouldContain("sales.public.orders");
    }

    [Fact]
    public async Task Filter_TableInBody_WithoutRelation_Returns403()
    {
        var services = await CreateServicesAsync();
        var context = CreateContext(services, "{\"table\":\"sales.public.orders\"}");

        var (result, nextCalled) = await RunFilterAsync(context);

        nextCalled.ShouldBeFalse();
        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Filter_TableInQuery_StillWins()
    {
        var services = await CreateServicesAsync("sales.public.orders");
        var context = CreateContext(services, "{\"table\":\"hr.public.salaries\"}");
        context.Request.QueryString = new QueryString("?table=sales.public.orders");

        var (_, nextCalled) = await RunFilterAsync(context);

        nextCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task Handler_SqlInBody_ReferencingTableWithoutRelation_Returns403WithoutExecuting()
    {
        var services = await CreateServicesAsync("sales.public.orders");
        var context = CreateContext(services, "{\"table\":\"sales.public.orders\",\"sql\":\"SELECT o.id, s.amount FROM sales.public.orders o JOIN hr.public.salaries s ON s.id = o.id\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();

        var result = await ArrowExportEndpoints.HandleArrowExportAsync(
            context, new ArrowExportService(services.GetRequiredService<IOptions<GatewayOptions>>(), NullLogger<ArrowExportService>.Instance), sql, CancellationToken.None);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await sql.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Handler_SqlInBody_ReferencingOnlyPermittedTables_Executes()
    {
        var services = await CreateServicesAsync("sales.public.orders");
        var context = CreateContext(services, "{\"table\":\"sales.public.orders\",\"sql\":\"SELECT id FROM sales.public.orders\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", "q", ["id"], [new Dictionary<string, object?> { ["id"] = 1 }], 1, 1));

        var result = await ArrowExportEndpoints.HandleArrowExportAsync(
            context, new ArrowExportService(services.GetRequiredService<IOptions<GatewayOptions>>(), NullLogger<ArrowExportService>.Instance), sql, CancellationToken.None);

        result.ShouldNotBeOfType<ProblemHttpResult>();
        await sql.Received(1).ExecuteQueryBufferedAsync(
            Arg.Is<GovernedSqlQueryRequest>(r => r.Sql == "SELECT id FROM sales.public.orders"), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Filter_DuplicateKeysInBody_Returns400BadRequest()
    {
        var services = await CreateServicesAsync("sales.public.orders");
        // Attacker payload: first key is allowed, second key is sensitive hr.dbo.salaries
        var context = CreateContext(services, "{\"table\":\"sales.public.orders\",\"Table\":\"hr.dbo.salaries\"}");

        var (result, nextCalled) = await RunFilterAsync(context);

        nextCalled.ShouldBeFalse();
        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain("Duplicate");
    }

    [Fact]
    public async Task Handler_DuplicateKeysInBody_Returns400BadRequest()
    {
        var services = await CreateServicesAsync("sales.public.orders");
        var context = CreateContext(services, "{\"table\":\"sales.public.orders\",\"Table\":\"hr.dbo.salaries\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();

        var result = await ArrowExportEndpoints.HandleArrowExportAsync(
            context, new ArrowExportService(services.GetRequiredService<IOptions<GatewayOptions>>(), NullLogger<ArrowExportService>.Instance), sql, CancellationToken.None);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain("Duplicate");
        await sql.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Handler_InProduction_SanitizesWebSqlPolicyException()
    {
        var prodEnv = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        prodEnv.EnvironmentName.Returns("Production");

        var services = new ServiceCollection();
        services.AddSingleton(prodEnv);
        services.AddLogging();
        services.AddSingleton(Options.Create(new GatewayOptions()));
        var sp = services.BuildServiceProvider();

        var context = CreateContext(sp, "{\"table\":\"sales.public.orders\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GovernedSqlResult>(new Autheris.Application.Sql.WebSqlPolicyException("Sensitive internal table structure revealed")));

        var result = await ArrowExportEndpoints.HandleArrowExportAsync(
            context, new ArrowExportService(Options.Create(new GatewayOptions()), NullLogger<ArrowExportService>.Instance), sql, CancellationToken.None);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        problem.ProblemDetails.Detail.ShouldBe(WebSqlEndpoints.GenericForbiddenMessage);
        problem.ProblemDetails.Detail.ShouldNotBeNull().ShouldNotContain("Sensitive internal table structure revealed");
        problem.ProblemDetails.Extensions.ShouldContainKey("traceId");
    }
}

