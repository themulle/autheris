namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

public sealed class PhysicalKeyRefreshEndpointTests
{
    private readonly IPhysicalKeyRefreshService _refreshService = Substitute.For<IPhysicalKeyRefreshService>();

    private static HttpContext CreateContext(string? sid = "S-1-5-21-ADMIN")
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Response.Body = new MemoryStream();
        ctx.TraceIdentifier = "trace-cat427";

        var claims = new List<Claim>
        {
            new("tenant_id", "tenant-a"),
            new(ClaimTypes.Role, "GovernanceAdmin")
        };
        if (sid != null)
        {
            claims.Add(new Claim(ClaimTypes.PrimarySid, sid));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, sid));
        }

        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        return ctx;
    }

    private static async Task<(int Status, string Body)> Run(IResult result, HttpContext? ctx = null)
    {
        var targetCtx = ctx ?? CreateContext();
        await result.ExecuteAsync(targetCtx);
        targetCtx.Response.Body.Position = 0;
        return (targetCtx.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)targetCtx.Response.Body).ToArray()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CAT427_T08_RefreshEndpoint_WhenNoSid_ReturnsForbidden()
    {
        // Act
        var result = await CatalogApiEndpoints.RefreshDatasourcePhysicalKeysHandler(
            "default",
            new PhysicalKeyRefreshRequest(),
            _refreshService,
            CreateContext(sid: null),
            CancellationToken.None);

        // Assert
        var (status, _) = await Run(result, CreateContext(sid: null));
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../secrets")]
    [InlineData("data/source")]
    [InlineData("source\0bad")]
    public async Task CAT427_T09_RefreshEndpoint_WhenInvalidDatasourceId_ReturnsBadRequest(string invalidId)
    {
        // Act
        var result = await CatalogApiEndpoints.RefreshDatasourcePhysicalKeysHandler(
            invalidId,
            new PhysicalKeyRefreshRequest(),
            _refreshService,
            CreateContext(),
            CancellationToken.None);

        // Assert
        var (status, body) = await Run(result);
        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("invalid");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("../table")]
    [InlineData("table\0name")]
    [InlineData("table/path")]
    [InlineData("table\\path")]
    public async Task CAT427_T10_RefreshEndpoint_WhenInvalidTableFilter_ReturnsBadRequest(string invalidFilter)
    {
        // Act
        var result = await CatalogApiEndpoints.RefreshDatasourcePhysicalKeysHandler(
            "default",
            new PhysicalKeyRefreshRequest(TableFilter: invalidFilter),
            _refreshService,
            CreateContext(),
            CancellationToken.None);

        // Assert
        var (status, body) = await Run(result);
        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("invalid");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CAT427_T11_RefreshEndpoint_WhenDatasourceNotFound_ReturnsNotFound()
    {
        // Arrange
        _refreshService.RefreshDatasourceKeysAsync(
            "unknown-ds",
            Arg.Any<PhysicalKeyRefreshRequest>(),
            Arg.Any<Sid>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException("Datasource unknown-ds not found"));

        // Act
        var result = await CatalogApiEndpoints.RefreshDatasourcePhysicalKeysHandler(
            "unknown-ds",
            new PhysicalKeyRefreshRequest(),
            _refreshService,
            CreateContext(),
            CancellationToken.None);

        // Assert
        var (status, body) = await Run(result);
        status.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CAT427_T12_RefreshEndpoint_WhenValid_ReturnsOkWithRefreshResult()
    {
        // Arrange
        var expected = new PhysicalKeyRefreshResult(
            "default",
            DryRun: false,
            TablesScanned: 2,
            KeysUpdated: 1,
            IndexesUpdated: 0,
            Details:
            [
                new TableKeyRefreshItem("default.dbo.orders", "Updated", ["Applied physical direction: DESC"], ["DESC", "ASC"])
            ]);

        _refreshService.RefreshDatasourceKeysAsync(
            "default",
            Arg.Any<PhysicalKeyRefreshRequest>(),
            Arg.Any<Sid>(),
            Arg.Any<CancellationToken>())
            .Returns(expected);

        // Act
        var result = await CatalogApiEndpoints.RefreshDatasourcePhysicalKeysHandler(
            "default",
            new PhysicalKeyRefreshRequest(),
            _refreshService,
            CreateContext(),
            CancellationToken.None);

        // Assert
        var (status, body) = await Run(result);
        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldContain("default.dbo.orders");
        body.ShouldContain("Updated");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CAT427_T13_MapCatalogEndpoints_RegistersRefreshRouteWithoutException()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        // Act & Assert
        Should.NotThrow(() => app.MapCatalogEndpoints());
    }
}
