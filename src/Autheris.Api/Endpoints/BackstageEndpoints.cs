namespace Autheris.Api.Endpoints;

using System;
using System.Text;
using System.Threading;
using Autheris.Application.Integrations.Backstage;
using Autheris.Extensions.Backstage;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
        }).RequireAuthorization();

        app.MapGet("/api/integrations/backstage/catalog-entities/{name}", async (
            string name,
            string? format,
            IBackstageCatalogExportService backstageService,
            HttpContext context,
            CancellationToken ct) =>
        {
            var entity = await backstageService.ExportEntityByNameAsync(name, ct);
            if (entity == null)
            {
                return Results.NotFound(new { error = $"Backstage entity '{name}' not found." });
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
        }).RequireAuthorization();

        app.MapGet("/api/integrations/backstage/catalog-info.yaml", async (
            IBackstageCatalogExportService backstageService,
            CancellationToken ct) =>
        {
            var yaml = await backstageService.ExportCatalogEntitiesYamlAsync(cancellationToken: ct);
            return Results.Content(yaml, "text/yaml; charset=utf-8", Encoding.UTF8);
        }).RequireAuthorization();

        return app;
    }
}
