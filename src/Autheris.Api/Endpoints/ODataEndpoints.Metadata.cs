namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

public static partial class ODataEndpoints
{
    /// <summary>API-15: Swagger UI outside Development needs an authenticated caller unless OpenSchema is enabled.</summary>
    internal static bool RequiresSwaggerChallenge(GatewayOptions gatewayOptions, IWebHostEnvironment env, HttpContext context) =>
        !gatewayOptions.IsOpenSchemaAllowed && !env.IsDevelopment() && context.User?.Identity?.IsAuthenticated != true;

    /// <summary>API-15: Evaluates Swagger UI authorization: challenge if unauthenticated outside Dev, forbid if authenticated non-admin.</summary>
    internal static IResult? CheckSwaggerAuth(GatewayOptions gatewayOptions, IWebHostEnvironment env, HttpContext context)
    {
        if (gatewayOptions.IsOpenSchemaAllowed || env.IsDevelopment())
        {
            return null;
        }
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return Results.Challenge();
        }
        if (!IsOpenApiAdmin(context.User))
        {
            return Results.Forbid();
        }
        return null;
    }

    /// <summary>API-15: Metadata and service document outside Development require an authenticated caller (challenge).</summary>
    internal static bool RequiresMetadataChallenge(GatewayOptions gatewayOptions, IWebHostEnvironment env, HttpContext context) =>
        !gatewayOptions.IsOpenSchemaAllowed && !env.IsDevelopment() && context.User?.Identity?.IsAuthenticated != true;

    /// <summary>
    /// OData entity set query. F-DATA-01: with <c>Accept: application/vnd.apache.parquet</c> the governed rows of a
    /// successful result are returned as Apache Parquet; error results stay OData JSON.
    /// </summary>
    internal static bool IsOpenApiAdmin(ClaimsPrincipal? user)
        => EndpointSecurity.IsCanonicalClusterAdmin(user) || EndpointSecurity.IsGlobalGovernanceAdmin(user);

    private const string SwaggerUiVersion = "5.33.2";

    private static readonly Dictionary<string, string> SwaggerUiAssets = new(StringComparer.Ordinal)
    {
        ["swagger-ui.css"] = "text/css; charset=utf-8",
        ["swagger-ui-bundle.js"] = "text/javascript; charset=utf-8",
        ["swagger-ui-standalone-preset.js"] = "text/javascript; charset=utf-8",
        ["favicon-32x32.png"] = "image/png",
    };

    private static string GetSwaggerUiHtml(string nonce) => $$"""
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>Autheris - OpenAPI 3.1 & OData Explorer</title>
      <link rel="icon" type="image/png" href="/ui/swagger/assets/favicon-32x32.png" />
      <link rel="stylesheet" href="/ui/swagger/assets/swagger-ui.css?v={{SwaggerUiVersion}}" />
      <style>
        .swagger-ui .topbar { background-color: #1e293b; padding: 10px 0; }
        .swagger-ui .topbar .download-url-wrapper { display: flex; align-items: center; gap: 8px; }
        .swagger-ui .topbar .download-url-wrapper input[type=text] { border-radius: 4px; padding: 6px 10px; }
      </style>
    </head>
    <body>
    <div id="swagger-ui"></div>
    <script src="/ui/swagger/assets/swagger-ui-bundle.js?v={{SwaggerUiVersion}}"></script>
    <script src="/ui/swagger/assets/swagger-ui-standalone-preset.js?v={{SwaggerUiVersion}}"></script>
    <script nonce="{{nonce}}">
      window.onload = async () => {
        const params = new URLSearchParams(window.location.search);
        const targetDomain = params.get('domain');
        const customUrl = params.get('url');

        let specUrls = [
          { url: '/odata/v4/$openapi', name: 'All Domains (Monolithic)' },
          { url: '/odata/v4/$openapi?mode=modular', name: 'All Domains (Modular $ref)' },
          { url: '/api/v1/queries/openapi.json', name: 'Declarative SQL Queries' }
        ];
        let primaryName = 'All Domains (Monolithic)';

        try {
          const res = await fetch('/odata/v4/$openapi/index', { credentials: 'same-origin' });
          if (res.ok) {
            const data = await res.json();
            if (data && Array.isArray(data.apis)) {
              specUrls = data.apis.map(a => ({ url: a.url, name: a.name }));
            }
          }
        } catch (e) {
          // Graceful fallback to default URLs if unauthenticated or network error
        }

        if (targetDomain) {
          const match = specUrls.find(s => s.name.toLowerCase().includes(targetDomain.toLowerCase()) || s.url.toLowerCase().includes(`/${targetDomain.toLowerCase()}/`));
          if (match) {
            primaryName = match.name;
          }
        } else if (customUrl) {
          const match = specUrls.find(s => s.url === customUrl);
          if (match) {
            primaryName = match.name;
          } else {
            specUrls.unshift({ url: customUrl, name: 'Custom Specification' });
            primaryName = 'Custom Specification';
          }
        }

        window.ui = SwaggerUIBundle({
          urls: specUrls,
          "urls.primaryName": primaryName,
          dom_id: '#swagger-ui',
          presets: [
            SwaggerUIBundle.presets.apis,
            SwaggerUIStandalonePreset
          ],
          layout: "StandaloneLayout",
          deepLinking: true,
          displayRequestDuration: true
        });
      };
    </script>
    </body>
    </html>
    """;
}
