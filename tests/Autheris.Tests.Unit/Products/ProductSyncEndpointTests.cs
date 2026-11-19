using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Application.Products;
using Autheris.Application.Products.Sync;
using Autheris.Application.Security;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

/// <summary>The sync routes of the data products, at the endpoint handler: the sync role is required and a read-only token never applies.</summary>
[Trait("Category", "Security")]
public sealed class ProductSyncEndpointTests
{
    private const string Body = """{"commit":"0123456789abcdef","files":[]}""";

    private readonly ProductHarness _h = new();

    private static DefaultHttpContext ContextOf(bool readOnly)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, ProductHarness.SyncIdentity.Value)], "test");
        if (readOnly)
        {
            TokenAccessScope.MarkReadOnly(identity);
        }

        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = ProductHarness.SyncIdentity,
            TenantId = ProductHarness.Tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(["DataProductSync"]),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Test"
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Body));
        context.Request.ContentType = "application/json";
        return context;
    }

    private Task<IResult> SyncAsync(HttpContext context, bool apply) =>
        ProductEndpoints.SyncAsync(context, new ProductSyncService(_h.Service, _h.Store, _h.Audit), apply);

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    [Fact]
    public async Task SyncApply_ReadOnlyToken_IsRefused()
    {
        var refused = await SyncAsync(ContextOf(readOnly: true), apply: true);

        StatusOf(refused).ShouldBe(StatusCodes.Status403Forbidden);
        _h.Audit.Entries.ShouldNotContain(e => e.EventType == Autheris.Domain.Audit.AuditEventTypes.ProductSyncApplied);

        // a read-only token may still plan (nothing is written), and a normal token applies
        StatusOf(await SyncAsync(ContextOf(readOnly: true), apply: false)).ShouldBe(StatusCodes.Status200OK);
        StatusOf(await SyncAsync(ContextOf(readOnly: false), apply: true)).ShouldBe(StatusCodes.Status200OK);
    }
}
