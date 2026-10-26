namespace Autheris.Api.Middleware;

using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

/// <summary>
/// Rewrites domain-scoped GraphQL requests (<c>/graphql/{domain}</c>) to <c>/graphql</c> and populates
/// <c>HttpContext.Items["Autheris.DomainScope"]</c> and <c>X-Autheris-Domain</c>
/// BEFORE authentication, authorization, rate limiting, and audit middlewares execute (WP-ARC-C-04 / ARC-A-10).
/// </summary>
public sealed partial class DomainScopeRewriteMiddleware
{
    public const string DomainScopeItemKey = "Autheris.DomainScope";
    public const string DomainHeaderName = "X-Autheris-Domain";

    private readonly RequestDelegate _next;
    private readonly string _graphQlEndpoint;

    public DomainScopeRewriteMiddleware(RequestDelegate next, IOptions<GatewayOptions> options)
        : this(next, ResolveEndpoint(options?.Value))
    {
    }

    public DomainScopeRewriteMiddleware(RequestDelegate next, GatewayOptions options)
        : this(next, ResolveEndpoint(options))
    {
    }

    public DomainScopeRewriteMiddleware(RequestDelegate next, string graphQlEndpoint)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _graphQlEndpoint = string.IsNullOrWhiteSpace(graphQlEndpoint)
            ? "/graphql"
            : (graphQlEndpoint.StartsWith('/') ? graphQlEndpoint.TrimEnd('/') : "/" + graphQlEndpoint.TrimEnd('/'));
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidDomainSlugRegex();

    public static bool IsValidDomainSlug(string? slug) =>
        !string.IsNullOrEmpty(slug) && ValidDomainSlugRegex().IsMatch(slug);

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value;
        if (!string.IsNullOrEmpty(path) &&
            path.StartsWith(_graphQlEndpoint + "/", StringComparison.OrdinalIgnoreCase))
        {
            var subPath = path[(_graphQlEndpoint.Length + 1)..].Trim('/');
            if (!string.IsNullOrEmpty(subPath))
            {
                // Preserve HotChocolate built-in schema download path (/graphql/schema.graphql) so schema request policies apply.
                if (string.Equals(subPath, "schema.graphql", StringComparison.OrdinalIgnoreCase))
                {
                    await _next(context).ConfigureAwait(false);
                    return;
                }

                if (!IsValidDomainSlug(subPath))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    var problem = ApiProblems.CreateProblemDetails(
                        StatusCodes.Status400BadRequest,
                        ApiErrorCodes.InvalidDomainScope,
                        "The requested GraphQL domain scope is invalid. Domain slugs must match ^[a-zA-Z0-9_-]{1,64}$.",
                        context);
                    await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted)
                        .ConfigureAwait(false);
                    return;
                }

                context.Items[DomainScopeItemKey] = subPath;
                context.Request.Headers[DomainHeaderName] = subPath;
                context.Request.Path = _graphQlEndpoint;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private static string ResolveEndpoint(GatewayOptions? options)
    {
        var ep = options?.GraphQL?.EndpointPath;
        if (string.IsNullOrWhiteSpace(ep))
        {
            return "/graphql";
        }

        return ep.StartsWith('/') ? ep : "/" + ep;
    }
}
