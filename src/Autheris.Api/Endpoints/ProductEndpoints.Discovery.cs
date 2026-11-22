namespace Autheris.Api.Endpoints;

using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Products;
using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>Discovery, explain and usage of data products. Every answer is scoped to the caller; a hidden and an unknown product return the same 404.</summary>
public static partial class ProductEndpoints
{
    private static void MapDiscoveryRoutes(RouteGroupBuilder group)
    {
        group.MapGet("discovery", (HttpContext context, ProductDiscoveryService service) => DiscoverAsync(context, service))
            .RequireAuthorization(GatewayPolicies.ProductAccess)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapGet("{schema}/{name}/explain", (string schema, string name, HttpContext context, ProductDiscoveryService service) => ExplainProductAsync(schema, name, context, service))
            .RequireAuthorization(GatewayPolicies.ProductAccess)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapGet("{schema}/{name}/usage", (string schema, string name, HttpContext context, ProductDiscoveryService service) => UsageProductAsync(schema, name, context, service))
            .RequireAuthorization(GatewayPolicies.ProductAccess)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
    }

    private static Task<IResult> DiscoverAsync(HttpContext context, ProductDiscoveryService service) =>
        RunAsync(context, async caller =>
        {
            var limit = int.TryParse(context.Request.Query["limit"], out var parsed) ? parsed : 50;
            var page = await service.ListAsync(EndpointSecurity.GetSecurityContext(context), caller, context.Request.Query["after"], limit, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { items = page.Items, next_after = page.NextAfter });
        });

    private static Task<IResult> ExplainProductAsync(string schema, string name, HttpContext context, ProductDiscoveryService service) =>
        RunAsync(context, async caller =>
            Results.Ok(await service.ExplainAsync(EndpointSecurity.GetSecurityContext(context), caller, schema, name, context.RequestAborted).ConfigureAwait(false)));

    private static Task<IResult> UsageProductAsync(string schema, string name, HttpContext context, ProductDiscoveryService service) =>
        RunAsync(context, async caller =>
            Results.Ok(await service.UsageAsync(EndpointSecurity.GetSecurityContext(context), caller, schema, name, context.RequestAborted).ConfigureAwait(false)));
}
