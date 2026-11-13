namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Common.Serialization;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestSupport;
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
/// WP-SIMPL-46 step 5 (SEC-AZ-01/10): the Arrow export decides a read through the decision point for the table named in the query string
/// or the JSON body and for every table the SQL references. A denied table, an unknown table or SQL that cannot be analyzed denies.
/// </summary>
[Trait("Category", "Security")]
public sealed class ArrowExportDecisionPointTests
{
    private const string Tenant = "tenant-1";
    private const string User = "alice";

    private sealed record Setup(IServiceProvider Services, IAccessDecisionPoint Pdp, List<AccessRequest> Requests);

    private static Setup CreateServices(bool withPdp = true, params string[] permittedTables)
    {
        var permitted = permittedTables.Select(t => TableIdentifierNormalizer.Normalize(t)).ToHashSet();
        var requests = new List<AccessRequest>();
        var pdp = PdpTestDoubles.From(r =>
        {
            requests.Add(r);
            return permitted.Contains(r.Object.Identifier)
                ? TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
                : TableAccessDecision.Denied(r.Object.Identifier, "no grant");
        });

        var services = new ServiceCollection();
        if (withPdp)
        {
            services.AddSingleton(pdp);
        }

        services.AddSingleton(PdpTestDoubles.AnyTable());
        services.AddSingleton<ISqlEngine>(new FastSqlEngine());
        services.AddLogging();
        services.AddSingleton(Options.Create(new GatewayOptions { Arrow = new ArrowExportOptions { Enabled = true, MaxExportRows = 1000, BatchSize = 1000 } }));
        return new Setup(services.BuildServiceProvider(), pdp, requests);
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

    private static ArrowExportService ArrowService(IServiceProvider services) =>
        new(services.GetRequiredService<IOptions<GatewayOptions>>(), NullLogger<ArrowExportService>.Instance);

    private static Task<IResult> RunAsync(HttpContext context, IServiceProvider services, IGovernedSqlExecutionService sql) =>
        ArrowExportEndpoints.HandleArrowExportAsync(
            context, ArrowService(services), sql, new Autheris.Application.DataSources.Services.GovernedTableReadPlanner(), CancellationToken.None);

    [Fact]
    public async Task Table_in_body_without_grant_returns_403_without_executing()
    {
        var setup = CreateServices();
        var context = CreateContext(setup.Services, "{\"table\":\"sales.public.orders\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();

        var result = await RunAsync(context, setup.Services, sql);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await sql.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
        setup.Requests.ShouldHaveSingleItem().Transport.ShouldBe(AccessTransport.ArrowExport);
    }

    [Fact]
    public async Task Table_with_grant_executes_and_decides_a_read()
    {
        var setup = CreateServices(true, "sales.public.orders");
        var context = CreateContext(setup.Services, "{\"table\":\"sales.public.orders\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", ["id"], [new Dictionary<string, object?> { ["id"] = 1 }], 1, 1));

        var result = await RunAsync(context, setup.Services, sql);

        result.ShouldNotBeOfType<ProblemHttpResult>();
        var request = setup.Requests.ShouldHaveSingleItem();
        request.Operation.ShouldBe(AccessOperation.Read);
        request.Subject.UserSid.Value.ShouldBe(User);
    }

    [Fact]
    public async Task Table_in_query_is_decided_not_the_body_table()
    {
        var setup = CreateServices(true, "sales.public.orders");
        var context = CreateContext(setup.Services, "{\"table\":\"hr.public.salaries\"}");
        context.Request.QueryString = new QueryString("?table=sales.public.orders");
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", ["id"], [], 0, 1));

        var result = await RunAsync(context, setup.Services, sql);

        result.ShouldNotBeOfType<ProblemHttpResult>();
        setup.Requests.Select(r => r.Object.Identifier.TableName).ShouldBe(["orders"]);
    }

    [Fact]
    public async Task Sql_referencing_a_denied_table_returns_403_without_executing()
    {
        var setup = CreateServices(true, "sales.public.orders");
        var context = CreateContext(setup.Services, "{\"sql\":\"SELECT o.id, s.amount FROM sales.public.orders o JOIN hr.public.salaries s ON s.id = o.id\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();

        var result = await RunAsync(context, setup.Services, sql);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await sql.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Sql_referencing_only_permitted_tables_executes()
    {
        var setup = CreateServices(true, "sales.public.orders");
        var context = CreateContext(setup.Services, "{\"sql\":\"SELECT id FROM sales.public.orders\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", ["id"], [new Dictionary<string, object?> { ["id"] = 1 }], 1, 1));

        var result = await RunAsync(context, setup.Services, sql);

        result.ShouldNotBeOfType<ProblemHttpResult>();
        await sql.Received(1).ExecuteQueryBufferedAsync(
            Arg.Is<GovernedSqlQueryRequest>(r => r.Sql == "SELECT id FROM sales.public.orders"), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{\"sql\":\"SELEC FROM WHERE (((\"}")]
    [InlineData("{\"sql\":\"SELECT 1\"}")]
    public async Task Unparseable_or_table_free_sql_is_denied(string body)
    {
        var setup = CreateServices(true, "sales.public.orders");
        var context = CreateContext(setup.Services, body);
        var sql = Substitute.For<IGovernedSqlExecutionService>();

        var result = await RunAsync(context, setup.Services, sql);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await sql.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
        setup.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Decision_point_timeout_with_a_live_token_denies()
    {
        var setup = CreateServices(true, "sales.public.orders");
        setup.Pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<TableAccessDecision>>(_ => throw new OperationCanceledException("timeout"));
        var context = CreateContext(setup.Services, "{\"table\":\"sales.public.orders\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();

        var result = await RunAsync(context, setup.Services, sql);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await sql.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Without_a_decision_point_the_export_is_denied()
    {
        var setup = CreateServices(withPdp: false);
        var context = CreateContext(setup.Services, "{\"table\":\"sales.public.orders\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();

        var result = await RunAsync(context, setup.Services, sql);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await sql.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Duplicate_keys_in_body_return_400()
    {
        var setup = CreateServices(true, "sales.public.orders");
        var context = CreateContext(setup.Services, "{\"table\":\"sales.public.orders\",\"Table\":\"hr.dbo.salaries\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();

        var result = await RunAsync(context, setup.Services, sql);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain("Duplicate");
        await sql.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task In_production_a_websql_policy_exception_is_sanitized()
    {
        var prodEnv = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        prodEnv.EnvironmentName.Returns("Production");
        var services = new ServiceCollection();
        services.AddSingleton(prodEnv);
        services.AddLogging();
        services.AddSingleton(Options.Create(new GatewayOptions()));
        services.AddSingleton(PdpTestDoubles.AllowAll());
        services.AddSingleton(PdpTestDoubles.AnyTable());
        var sp = services.BuildServiceProvider();

        var context = CreateContext(sp, "{\"table\":\"sales.public.orders\"}");
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GovernedSqlResult>(new Autheris.Application.Sql.WebSqlPolicyException("Sensitive internal table structure revealed")));

        var result = await ArrowExportEndpoints.HandleArrowExportAsync(
            context, new ArrowExportService(Options.Create(new GatewayOptions()), NullLogger<ArrowExportService>.Instance), sql, new Autheris.Application.DataSources.Services.GovernedTableReadPlanner(), CancellationToken.None);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        problem.ProblemDetails.Detail.ShouldBe(WebSqlEndpoints.GenericForbiddenMessage);
        problem.ProblemDetails.Detail.ShouldNotBeNull().ShouldNotContain("Sensitive internal table structure revealed");
        problem.ProblemDetails.Extensions.ShouldContainKey("traceId");
    }
}
