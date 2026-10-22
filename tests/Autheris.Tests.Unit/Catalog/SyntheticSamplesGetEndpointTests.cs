namespace Autheris.Tests.Unit.Catalog;

using System;
using Autheris.Application.Services;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Profiling;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>MCP-01: GET synthetic-samples is a governance-admin curation route, tenant/visibility confined and column filtered.</summary>
[Trait("Category", "Security")]
public sealed class SyntheticSamplesGetEndpointTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim("tenant_id", "tenant-a")], "TestAuth"));
        return context;
    }

    private static TableMetadata OrdersTable() => new()
    {
        Identifier = Orders,
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "name", DataType = "varchar" }],
        PredefinedSyntheticSamples =
        [
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "n", ["internal_secret"] = "leak" }
        ]
    };

    private static ITableMetadataRepository Repository(TableIdentifier? active)
    {
        var repository = Substitute.For<ITableMetadataRepository>();
        repository.GetActiveTableIdentifierAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(active);
        return repository;
    }

    private static ICatalogVisibilityService Visibility(TableMetadata? visible)
    {
        var service = Substitute.For<ICatalogVisibilityService>();
        service.FindVisibleAsync(Arg.Any<CatalogCaller>(), Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(visible is null ? default(CatalogVisibleRow?) : new CatalogVisibleRow(visible, CatalogOutputShape.Full));
        return service;
    }

    private static async Task<(int Status, string Body)> InvokeAsync(string id, ITableMetadataRepository repository, ICatalogVisibilityService visibility)
    {
        var result = await CatalogApiEndpoints.GetDatasetSyntheticSamplesHandler(
            id, repository, new SyntheticSampleRowGenerator(), visibility, CreateContext(), CancellationToken.None);
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;
        return (status, JsonSerializer.Serialize(((IValueHttpResult)result).Value));
    }

    [Fact]
    public void SyntheticSamplesGet_NonAdmin_Returns403_RouteRequiresGovernanceAdmin()
    {
        // MCP-01: both GET routes carry the GovernanceAdmin policy, so a non-admin is rejected before the handler runs.
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.MapCatalogEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText != null &&
                        e.RoutePattern.RawText.EndsWith("/datasets/{id}/synthetic-samples", StringComparison.Ordinal) &&
                        e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"))
            .ToList();

        endpoints.ShouldNotBeEmpty();
        foreach (var endpoint in endpoints)
        {
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy)
                .ShouldContain(GatewayPolicies.GovernanceAdmin);
        }
    }

    [Fact]
    public async Task SyntheticSamplesGet_OtherTenantDataset_Returns404()
    {
        // The visibility service answers null for a table outside the caller's tenant.
        var (status, _) = await InvokeAsync("sales.public.orders", Repository(Orders), Visibility(null));

        status.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task SyntheticSamplesGet_HiddenDataset_SameBodyAsUnknown()
    {
        var hidden = await InvokeAsync("sales.public.orders", Repository(Orders), Visibility(null));
        var unknown = await InvokeAsync("sales.public.missing", Repository(null), Visibility(null));

        hidden.Status.ShouldBe(unknown.Status);
        hidden.Body.ShouldBe(unknown.Body);
    }

    [Fact]
    public async Task SyntheticSamplesGet_DropsKeysNotInTableColumns()
    {
        var (status, body) = await InvokeAsync("sales.public.orders", Repository(Orders), Visibility(OrdersTable()));

        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldNotContain("internal_secret");
        using var doc = JsonDocument.Parse(body);
        var row = doc.RootElement.GetProperty("rows")[0];
        row.GetProperty("id").GetInt32().ShouldBe(1);
        row.GetProperty("name").GetString().ShouldBe("n");
    }
}
