namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using Autheris.Api.Middleware;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#138 INC-1 P3: the <c>sample_rows</c> refusal of a non-Full shape (SEC-CAT138-15) is a client error (403), never a 500.</summary>
[Trait("Category", "Security")]
public sealed class CatalogSampleRowsErrorMappingTests
{
    [Fact]
    public async Task SampleRows_SummaryShape_ThrowsExceptionThatMapsTo403_NotToAServerError()
    {
        var table = Wp0.Table("sales", "full_t", "Full", ["id"]) with { CatalogView = CatalogView.Full };
        var service = Substitute.For<ICatalogVisibilityService>();
        service.FindVisibleAsync(Arg.Any<CatalogCaller>(), table.Identifier, Arg.Any<CancellationToken>())
            .Returns(new CatalogVisibleRow(table, CatalogOutputShape.Summary));
        var mcp = new McpDatasetCatalog(
            Wp0.Tables(table), Wp0.Consents(), Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(new GatewayOptions()), visibilityService: service);

        var thrown = await Should.ThrowAsync<Exception>(() => mcp.SampleRowsAsync(Wp0.User("S-USER", "tenant-a"), "sales.dbo.full_t", 3));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handled = await new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance).TryHandleAsync(context, thrown, CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldNotContain("full catalog access");
    }
}
