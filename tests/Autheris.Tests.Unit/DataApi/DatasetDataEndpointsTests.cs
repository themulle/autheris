namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DatasetDataEndpointsTests
{
    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim("sub", "test-user"),
                new Claim("tenant", "default")
            }, "Bearer"));
        return context;
    }

    [Fact]
    public async Task HandleQueryThreePartAsync_Success_StreamsJsonEnvelope()
    {
        var httpContext = CreateHttpContext();
        var queryService = Substitute.For<IGovernedDataQueryService>();

        var envelope = new DatasetQueryEnvelope(
            Dataset: "sales.dbo.customers",
            Count: 1,
            Offset: 0,
            Limit: 10,
            HasMore: false,
            Columns: new List<DatasetColumnInfo>
            {
                new("id", "int", false),
                new("email", "varchar", true)
            },
            Data: new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["id"] = 1, ["email"] = "d***@example.com" }
            });

        queryService.ExecuteQueryAsync(Arg.Any<DatasetQueryRequest>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(envelope);

        await DatasetDataEndpoints.HandleQueryThreePartAsync(
            "sales", "dbo", "customers",
            httpContext,
            queryService,
            NullLoggerFactory.Instance,
            CancellationToken.None,
            select: "id,email",
            limit: 10);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        httpContext.Response.ContentType.ShouldNotBeNull();
        httpContext.Response.ContentType.ShouldContain("application/json");

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(httpContext.Response.Body);
        var json = await reader.ReadToEndAsync();

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("dataset").GetString().ShouldBe("sales.dbo.customers");
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("columns")[1].GetProperty("masked").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("data")[0].GetProperty("email").GetString().ShouldBe("d***@example.com");
    }

    [Fact]
    public async Task HandleQueryThreePartAsync_ForbiddenTable_Returns403()
    {
        var httpContext = CreateHttpContext();
        var queryService = Substitute.For<IGovernedDataQueryService>();

        queryService.ExecuteQueryAsync(Arg.Any<DatasetQueryRequest>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns<DatasetQueryEnvelope>(_ => throw new UnauthorizedAccessException("Forbidden"));

        await DatasetDataEndpoints.HandleQueryThreePartAsync(
            "sales", "dbo", "customers",
            httpContext,
            queryService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HandleQueryThreePartAsync_TableNotFound_Returns404()
    {
        var httpContext = CreateHttpContext();
        var queryService = Substitute.For<IGovernedDataQueryService>();

        queryService.ExecuteQueryAsync(Arg.Any<DatasetQueryRequest>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns<DatasetQueryEnvelope>(_ => throw new TableNotFoundException(new TableIdentifier("sales", "dbo", "unknown")));

        await DatasetDataEndpoints.HandleQueryThreePartAsync(
            "sales", "dbo", "unknown",
            httpContext,
            queryService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleQueryThreePartAsync_OracleInferenceRejected_Returns400()
    {
        var httpContext = CreateHttpContext();
        var queryService = Substitute.For<IGovernedDataQueryService>();

        queryService.ExecuteQueryAsync(Arg.Any<DatasetQueryRequest>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns<DatasetQueryEnvelope>(_ => throw new ArgumentException("Filtering on masked column rejected."));

        await DatasetDataEndpoints.HandleQueryThreePartAsync(
            "sales", "dbo", "customers",
            httpContext,
            queryService,
            NullLoggerFactory.Instance,
            CancellationToken.None,
            filter: "email eq 'test'");

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleQueryDatasetIdAsync_ValidIdentifier_StreamsEnvelope()
    {
        var httpContext = CreateHttpContext();
        var queryService = Substitute.For<IGovernedDataQueryService>();

        var envelope = new DatasetQueryEnvelope(
            Dataset: "sales.dbo.customers",
            Count: 0,
            Offset: 0,
            Limit: 50,
            HasMore: false,
            Columns: Array.Empty<DatasetColumnInfo>(),
            Data: Array.Empty<IReadOnlyDictionary<string, object?>>());

        queryService.ExecuteQueryAsync(Arg.Any<DatasetQueryRequest>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(envelope);

        await DatasetDataEndpoints.HandleQueryDatasetIdAsync(
            "sales.dbo.customers",
            httpContext,
            queryService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandleQueryDatasetIdAsync_InvalidFormat_Returns400()
    {
        var httpContext = CreateHttpContext();
        var queryService = Substitute.For<IGovernedDataQueryService>();

        await DatasetDataEndpoints.HandleQueryDatasetIdAsync(
            "invalid_single_token",
            httpContext,
            queryService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleGetByIdAsync_PassesFilterAndLimitOne()
    {
        var httpContext = CreateHttpContext();
        var queryService = Substitute.For<IGovernedDataQueryService>();

        DatasetQueryRequest? capturedRequest = null;
        queryService.ExecuteQueryAsync(
            Arg.Do<DatasetQueryRequest>(r => capturedRequest = r),
            Arg.Any<RequestContext>(),
            Arg.Any<CancellationToken>())
            .Returns(new DatasetQueryEnvelope("sales.dbo.customers", 1, 0, 1, false, Array.Empty<DatasetColumnInfo>(), Array.Empty<IReadOnlyDictionary<string, object?>>()));

        await DatasetDataEndpoints.HandleGetByIdAsync(
            "sales", "dbo", "customers", "1001",
            httpContext,
            queryService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Limit.ShouldBe(1);
        capturedRequest.FilterExpression.ShouldBe("id eq '1001'");
    }

    [Fact]
    public void AddAutherisDataApi_RegistersRequiredServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITableMetadataRepository>());
        services.AddSingleton(Substitute.For<Autheris.Application.Policy.IUnifiedPolicyDecisionPoint>());
        services.AddSingleton(Substitute.For<Autheris.Application.Sql.Interfaces.IGovernedSqlExecutionService>());
        services.AddSingleton(Substitute.For<Autheris.Application.Sql.Interfaces.IFederatedQueryExecutionService>());
        services.AddSingleton(Substitute.For<IColumnMaskingProvider>());

        services.AddAutherisDataApi();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var queryService = scope.ServiceProvider.GetService<IGovernedDataQueryService>();
        queryService.ShouldNotBeNull();

        var hostedServices = sp.GetServices<IHostedService>();
        hostedServices.Any(h => h is VirtualSystemTablesHostedService).ShouldBeTrue();
    }
}
