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
using Microsoft.AspNetCore.Http.HttpResults;
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
        ValidateRoutePrefix(prefix);

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
    /// Captures the endpoints that exist before <see cref="IAutherisModule.MapEndpoints"/> so that
    /// <see cref="VerifyModuleEndpoints(IEndpointRouteBuilder, string?, EndpointSnapshot?)"/> can tell which ones the module added.
    /// </summary>
    internal static EndpointSnapshot CaptureEndpointSnapshot(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var dataSource in endpoints.DataSources)
        {
            foreach (var endpoint in dataSource.Endpoints)
            {
                var key = SnapshotKey(endpoint);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        return new EndpointSnapshot(counts);
    }

    internal sealed class EndpointSnapshot(Dictionary<string, int> counts)
    {
        internal Dictionary<string, int> Counts { get; } = counts;
    }

    private static string SnapshotKey(Endpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
        return string.Concat(methods is null ? "*" : string.Join(',', methods), " ", route, " ", endpoint.DisplayName);
    }

    /// <summary>
    /// Verifies after <see cref="IAutherisModule.MapEndpoints"/> that every module endpoint carries required
    /// authorization, audit, and ingress metadata and does not carry <see cref="IAllowAnonymous"/>.
    /// </summary>
    public static void VerifyModuleEndpoints(IEndpointRouteBuilder endpoints, string? expectedModuleId = null) =>
        VerifyModuleEndpoints(endpoints, expectedModuleId, before: null);

    /// <summary>
    /// Like <see cref="VerifyModuleEndpoints(IEndpointRouteBuilder, string?)"/>; with a <paramref name="before"/> snapshot every endpoint
    /// the module added must carry that module's <see cref="ModuleEndpointMetadata"/> and live under its route namespace.
    /// </summary>
    internal static void VerifyModuleEndpoints(IEndpointRouteBuilder endpoints, string? expectedModuleId, EndpointSnapshot? before)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var remaining = before is null ? null : new Dictionary<string, int>(before.Counts, StringComparer.Ordinal);

        foreach (var dataSource in endpoints.DataSources)
        {
            foreach (var endpoint in dataSource.Endpoints)
            {
                if (remaining is not null)
                {
                    var key = SnapshotKey(endpoint);
                    if (remaining.TryGetValue(key, out var left) && left > 0)
                    {
                        remaining[key] = left - 1;
                    }
                    else if (expectedModuleId is not null)
                    {
                        VerifyAddedEndpointOwnership(endpoint, expectedModuleId);
                    }
                }

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

    private static void VerifyAddedEndpointOwnership(Endpoint endpoint, string moduleId)
    {
        var owner = endpoint.Metadata.GetMetadata<ModuleEndpointMetadata>();
        if (owner is null || !string.Equals(owner.ModuleId, moduleId, StringComparison.Ordinal))
        {
            throw new ModuleLoadException(
                moduleId,
                $"Endpoint '{endpoint.DisplayName}' added while mapping module '{moduleId}' does not carry that module's endpoint ownership metadata.");
        }

        if (endpoint is RouteEndpoint routeEndpoint)
        {
            ValidateRoutePrefix(moduleId, routeEndpoint.RoutePattern.RawText);
        }
    }

    private void ValidateRoutePrefix(string? prefix) => ValidateRoutePrefix(_moduleId, prefix);

    /// <summary>
    /// A module route must live under <c>/modules/{moduleId}</c> (module id lowercase, ordinal comparison) and must not contain
    /// dot segments, empty segments or encoded dots and slashes. Checked on the raw pattern and on its decoded form.
    /// </summary>
    internal static void ValidateRoutePrefix(string moduleId, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !prefix.StartsWith('/'))
        {
            throw new ModuleLoadException(moduleId, $"Module endpoint group prefix '{prefix}' must start with '/'.");
        }

        var ownPrefix = "/modules/" + moduleId.ToLowerInvariant();
        foreach (var candidate in RouteCandidates(prefix))
        {
            if (candidate.Contains("//", StringComparison.Ordinal) ||
                candidate.Contains('\\') ||
                candidate.Split('/').Any(segment => segment is "." or ".."))
            {
                throw new ModuleLoadException(moduleId, $"Module route '{prefix}' contains an empty, dot or encoded segment.");
            }

            var underNamespace = candidate.StartsWith(ownPrefix, StringComparison.Ordinal) &&
                (candidate.Length == ownPrefix.Length || candidate[ownPrefix.Length] == '/');
            if (!underNamespace)
            {
                throw new ModuleLoadException(moduleId, $"Module route '{prefix}' must live under '{ownPrefix}/'.");
            }
        }

        if (prefix.Contains('%', StringComparison.Ordinal))
        {
            throw new ModuleLoadException(moduleId, $"Module route '{prefix}' must not contain percent-encoded characters.");
        }
    }

    private static IEnumerable<string> RouteCandidates(string raw)
    {
        yield return raw;
        string decoded = raw;
        for (var i = 0; i < 3; i++)
        {
            var next = Uri.UnescapeDataString(decoded);
            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                break;
            }

            decoded = next;
            yield return decoded;
        }
    }

    internal static async ValueTask<object?> ExecuteModuleSecurityFilterAsync(
        string moduleId,
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        var httpContext = invocationContext.HttpContext;
        var state = new ModuleAuditState();

        try
        {
            var denied = await CheckPreconditionsAsync(httpContext, state).ConfigureAwait(false);
            if (denied is not null)
            {
                state.Decision = DenyDecision;
                return denied;
            }

            var response = await next(invocationContext).ConfigureAwait(false);
            state.Decision = ClassifyResponse(response, httpContext);
            return response;
        }
        catch (Exception ex)
        {
            state.Decision = ErrorDecision;
            state.ErrorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await WriteAuditAsync(moduleId, httpContext, state).ConfigureAwait(false);
        }
    }

    private const string AllowDecision = "ALLOW";
    private const string DenyDecision = "DENY";
    private const string ErrorDecision = "ERROR";

    private sealed class ModuleAuditState
    {
        public string Decision { get; set; } = ErrorDecision;

        public string? ErrorType { get; set; }

        public TenantId? Tenant { get; set; }
    }

    private static async ValueTask<IResult?> CheckPreconditionsAsync(HttpContext httpContext, ModuleAuditState state)
    {
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
                        state.Tenant = TryResolveTenant(httpContext);
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }
            }
        }

        // Enforce tenant context via EndpointSecurity.GetRequestTenant (fail-closed 403 when missing or invalid)
        try
        {
            state.Tenant = EndpointSecurity.GetRequestTenant(httpContext);
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

        return null;
    }

    private static TenantId? TryResolveTenant(HttpContext httpContext)
    {
        try
        {
            return EndpointSecurity.GetRequestTenant(httpContext);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or Autheris.Domain.Exceptions.TenantUnresolvedException)
        {
            return null;
        }
    }

    private static string ClassifyResponse(object? response, HttpContext httpContext)
    {
        var status = response is IStatusCodeHttpResult { StatusCode: { } resultStatus }
            ? resultStatus
            : httpContext.Response.StatusCode;

        return status switch
        {
            >= 500 => ErrorDecision,
            >= 400 => DenyDecision,
            _ => AllowDecision,
        };
    }

    private static async Task WriteAuditAsync(string moduleId, HttpContext httpContext, ModuleAuditState state)
    {
        var writer = httpContext.RequestServices.GetService<IAuditWriter>();
        if (writer is null)
        {
            return;
        }

        var user = httpContext.User;
        var sidValue = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user?.Identity?.Name
            ?? (user?.Identity?.IsAuthenticated == true ? "authenticated-user" : "anonymous");
        var auditPolicy = httpContext.GetEndpoint()?.Metadata.GetMetadata<AuditPolicy>();
        var requestPath = httpContext.Request.Path.Value ?? string.Empty;
        var entry = new AuditLogEntry
        {
            EventType = auditPolicy?.EventType ?? AuditEventTypes.CatalogRead,
            ActorSid = new Sid(sidValue),
            TenantId = state.Tenant ?? TenantId.Platform,
            TargetTable = CapAuditString(httpContext.Request.Path.Value ?? $"module:{moduleId}"),
            Decision = state.Decision,
            DetailsJson = BuildModuleAuditDetails(moduleId, requestPath, state.ErrorType),
        };

        // The record is written in a finally block, so it must not depend on the (possibly aborted) request token.
        await writer.RecordBestEffortAsync(entry, CancellationToken.None).ConfigureAwait(false);
    }

    internal static string BuildModuleAuditDetails(string moduleId, string? path, string? errorType = null)
    {
        var rawPath = path ?? string.Empty;
        var cappedPath = rawPath.Length > 512 ? rawPath[..512] : rawPath;

        using var memoryStream = new MemoryStream();
        using (var jsonWriter = new Utf8JsonWriter(memoryStream))
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WriteString("module", moduleId);
            jsonWriter.WriteString("path", cappedPath);
            if (errorType is not null)
            {
                // Exception type only: the message can carry secrets and is never written to the audit trail.
                jsonWriter.WriteString("errorType", CapAuditString(errorType, 128));
            }

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
