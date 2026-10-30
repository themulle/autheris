namespace Autheris.Api.Endpoints;

using System;
using System.Text;
using System.Threading;
using Autheris.Api.Extensions;
using Autheris.Application.Integrations.Backstage;
using Autheris.Domain.Audit;
using Autheris.Extensions.Backstage;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;

public static class BackstageEndpoints
{
    public static IEndpointRouteBuilder MapBackstageEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        if (!gatewayOptions.Backstage.Enabled)
        {
            return app;
        }

        app.MapGet("/api/integrations/backstage/catalog-entities", async (
            string? kind,
            string? type,
            string? format,
            IBackstageCatalogExportService backstageService,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!IsAuthorizedBackstageReader(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var accept = context.Request.Headers.Accept.ToString();
            var wantsYaml = string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase) ||
                            accept.Contains("application/yaml", StringComparison.OrdinalIgnoreCase) ||
                            accept.Contains("text/yaml", StringComparison.OrdinalIgnoreCase);

            if (wantsYaml)
            {
                var yaml = await backstageService.ExportCatalogEntitiesYamlAsync(kind, type, ct);
                return Results.Content(yaml, "application/yaml", Encoding.UTF8);
            }

            var entities = await backstageService.ExportCatalogEntitiesAsync(kind, type, ct);
            return Results.Ok(entities);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/api/integrations/backstage/catalog-entities/{name}", async (
            string name,
            string? format,
            IBackstageCatalogExportService backstageService,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!IsAuthorizedBackstageReader(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var entity = await backstageService.ExportEntityByNameAsync(name, ct);
            if (entity == null)
            {
                // Backstage ErrorResponseBody (protocol allowlist, NL-SC-29): fixed message, no entity-name echo.
                return Results.Json(
                    new { error = new { name = "NotFoundError", message = "The requested Backstage entity was not found." }, response = new { statusCode = StatusCodes.Status404NotFound } },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var accept = context.Request.Headers.Accept.ToString();
            var wantsYaml = string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase) ||
                            accept.Contains("application/yaml", StringComparison.OrdinalIgnoreCase) ||
                            accept.Contains("text/yaml", StringComparison.OrdinalIgnoreCase);

            if (wantsYaml)
            {
                var singleYaml = BackstageYamlSerializer.Serialize(entity);
                return Results.Content(singleYaml, "application/yaml", Encoding.UTF8);
            }

            return Results.Ok(entity);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/api/integrations/backstage/catalog-info.yaml", async (
            IBackstageCatalogExportService backstageService,
            Autheris.Application.Catalog.ICatalogVersionProvider versionProvider,
            Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!IsAuthorizedBackstageReader(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var version = await versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
            var etagValue = $"\"{version.StructureVersion}-{version.ContentVersion}\"";
            context.Response.Headers.ETag = etagValue;

            var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString().Trim();
            if (!string.IsNullOrEmpty(ifNoneMatch))
            {
                var candidates = ifNoneMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (var candidate in candidates)
                {
                    if (candidate == "*" || candidate == etagValue || candidate == $"W/{etagValue}" || candidate == etagValue.Trim('"'))
                    {
                        return Results.StatusCode(StatusCodes.Status304NotModified);
                    }
                }
            }

            var cacheKey = $"backstage:yaml:{etagValue}";
            if (!memoryCache.TryGetValue(cacheKey, out var raw) || raw is not string yaml || string.IsNullOrEmpty(yaml))
            {
                yaml = await backstageService.ExportCatalogEntitiesYamlAsync(cancellationToken: ct).ConfigureAwait(false);
                memoryCache.Set(cacheKey, yaml, new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                    Size = Math.Max(256, yaml.Length * 2L)
                });
            }

            return Results.Content(yaml, "text/yaml; charset=utf-8", Encoding.UTF8);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        return app;
    }

    private static bool IsAuthorizedBackstageReader(System.Security.Claims.ClaimsPrincipal user) =>
        EndpointSecurity.IsGlobalGovernanceAdmin(user) ||
        user.IsInRole("CatalogSync") ||
        user.IsInRole("ClusterAdmin");
}
