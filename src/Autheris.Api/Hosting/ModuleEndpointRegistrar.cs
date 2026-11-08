namespace Autheris.Api.Hosting;

using System;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Endpoint metadata stamping the owning module identifier on every route mapped through <see cref="ModuleEndpointRegistrar"/>.
/// </summary>
public sealed record ModuleEndpointMetadata(string ModuleId);

/// <summary>
/// Guarded implementation of <see cref="IModuleEndpointRegistrar"/> that enforces authentication,
/// authorization, rate limiting, audit logging, tenant context validation, and ingress transport metadata on module routes.
/// </summary>
public sealed class ModuleEndpointRegistrar : IModuleEndpointRegistrar
{
    private static readonly Func<IEndpointRouteBuilder, string, RouteGroupBuilder> CreateRouteGroup =
        typeof(RouteGroupBuilder).Assembly
            .GetType("Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions")!
            .GetMethod("MapGroup", [typeof(IEndpointRouteBuilder), typeof(string)])!
            .CreateDelegate<Func<IEndpointRouteBuilder, string, RouteGroupBuilder>>();

    private readonly IEndpointRouteBuilder _endpoints;
    private readonly string _moduleId;

    public ModuleEndpointRegistrar(IEndpointRouteBuilder endpoints, string moduleId)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        _endpoints = endpoints;
        _moduleId = moduleId;
    }

    public string ModuleId => _moduleId;

    public RouteGroupBuilder MapGroup(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !prefix.StartsWith('/'))
        {
            throw new ModuleLoadException(_moduleId, $"Module endpoint group prefix '{prefix}' must start with '/'.");
        }

        RouteGroupBuilder group = CreateRouteGroup(_endpoints, prefix);

        group
            .RequireAuthorization()
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithAudit(AuditLevel.Full, AuditEventTypes.CatalogRead)
            .WithMetadata(new ModuleEndpointMetadata(_moduleId))
            .WithTags($"module:{_moduleId}")
            .AddEndpointFilter((invocationContext, next) => ExecuteModuleSecurityFilterAsync(_moduleId, invocationContext, next));

        return group;
    }

    /// <summary>
    /// Verifies after <see cref="IAutherisModule.MapEndpoints"/> that every module endpoint carries required
    /// authorization, audit, and ingress metadata and does not carry <see cref="IAllowAnonymous"/>.
    /// </summary>
    public static void VerifyModuleEndpoints(IEndpointRouteBuilder endpoints, string? expectedModuleId = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        foreach (var dataSource in endpoints.DataSources)
        {
            foreach (var endpoint in dataSource.Endpoints)
            {
                var moduleMeta = endpoint.Metadata.GetMetadata<ModuleEndpointMetadata>();
                var tagsMeta = endpoint.Metadata.GetMetadata<ITagsMetadata>();
                string? taggedModuleId = moduleMeta?.ModuleId;

                if (taggedModuleId is null && tagsMeta is not null)
                {
                    foreach (var tag in tagsMeta.Tags)
                    {
                        if (tag.StartsWith("module:", StringComparison.Ordinal) && tag.Length > "module:".Length)
                        {
                            taggedModuleId = tag["module:".Length..];
                            break;
                        }
                    }
                }

                if (taggedModuleId is null)
                {
                    continue;
                }

                if (expectedModuleId is not null && !string.Equals(taggedModuleId, expectedModuleId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                {
                    throw new ModuleLoadException(
                        taggedModuleId,
                        $"Endpoint '{endpoint.DisplayName}' in module '{taggedModuleId}' carries [AllowAnonymous], which is forbidden for module endpoints.");
                }

                var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
                if (authorizeData is null || authorizeData.Count == 0)
                {
                    throw new ModuleLoadException(
                        taggedModuleId,
                        $"Endpoint '{endpoint.DisplayName}' in module '{taggedModuleId}' is missing required authorization metadata.");
                }

                if (endpoint.Metadata.GetMetadata<AuditPolicy>() is null)
                {
                    throw new ModuleLoadException(
                        taggedModuleId,
                        $"Endpoint '{endpoint.DisplayName}' in module '{taggedModuleId}' is missing required audit policy metadata.");
                }

                if (endpoint.Metadata.GetMetadata<IngressTransportMetadata>() is null)
                {
                    throw new ModuleLoadException(
                        taggedModuleId,
                        $"Endpoint '{endpoint.DisplayName}' in module '{taggedModuleId}' is missing required ingress transport metadata.");
                }
            }
        }
    }

    private static async ValueTask<object?> ExecuteModuleSecurityFilterAsync(
        string moduleId,
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        var httpContext = invocationContext.HttpContext;
        var user = httpContext.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        // Enforce role requirements declared via RequireAuthorization(new AuthorizeAttribute { Roles = ... }) if not already enforced
        var endpoint = httpContext.GetEndpoint();
        if (endpoint is not null)
        {
            var authDataList = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            foreach (var authData in authDataList)
            {
                if (!string.IsNullOrWhiteSpace(authData.Roles))
                {
                    var roles = authData.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (roles.Length > 0 && !roles.Any(r => user.IsInRole(r)))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }
            }
        }

        // Enforce tenant context via EndpointSecurity.GetRequestTenant (fail-closed 403 when missing or invalid)
        TenantId resolvedTenant;
        try
        {
            resolvedTenant = EndpointSecurity.GetRequestTenant(httpContext);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or Autheris.Domain.Exceptions.TenantUnresolvedException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Enforce post-auth rate limiting when IRateLimiterService is registered
        var rateLimiter = httpContext.RequestServices.GetService<IRateLimiterService>();
        if (rateLimiter is not null)
        {
            var gatewayOpts = httpContext.RequestServices.GetService<IOptions<GatewayOptions>>()?.Value;
            var postAuthOpts = gatewayOpts?.RateLimiting?.PostAuthSidRateLimit ?? new PostAuthSidRateLimitOptions();
            var sid = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.Identity?.Name
                ?? "anonymous";
            var rateResult = await rateLimiter.CheckPostAuthSidAsync(sid, postAuthOpts, httpContext.RequestAborted).ConfigureAwait(false);
            if (!rateResult.Allowed)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
        }

        var response = await next(invocationContext).ConfigureAwait(false);

        // Emit audit entry when IAuditWriter is registered in RequestServices
        var writer = httpContext.RequestServices.GetService<IAuditWriter>();
        if (writer is not null)
        {
            var sidValue = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.Identity?.Name
                ?? "authenticated-user";
            var auditPolicy = endpoint?.Metadata.GetMetadata<AuditPolicy>();
            var requestPath = httpContext.Request.Path.Value ?? string.Empty;
            var entry = new AuditLogEntry
            {
                EventType = auditPolicy?.EventType ?? AuditEventTypes.CatalogRead,
                ActorSid = new Sid(sidValue),
                TenantId = resolvedTenant,
                TargetTable = CapAuditString(httpContext.Request.Path.Value ?? $"module:{moduleId}"),
                Decision = "ALLOW",
                DetailsJson = BuildModuleAuditDetails(moduleId, requestPath),
            };
            await writer.RecordBestEffortAsync(entry, httpContext.RequestAborted).ConfigureAwait(false);
        }

        return response;
    }

    internal static string BuildModuleAuditDetails(string moduleId, string? path)
    {
        var rawPath = path ?? string.Empty;
        var cappedPath = rawPath.Length > 512 ? rawPath[..512] : rawPath;

        using var memoryStream = new MemoryStream();
        using (var jsonWriter = new Utf8JsonWriter(memoryStream))
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WriteString("module", moduleId);
            jsonWriter.WriteString("path", cappedPath);
            jsonWriter.WriteEndObject();
        }

        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }

    internal static string CapAuditString(string? value, int maxLen = 512)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length > maxLen ? value[..maxLen] : value;
    }
}
