namespace Autheris.Api.Hosting;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Host implementation of <see cref="IModuleRequestContext"/>: a stateless forwarder to <c>EndpointSecurity</c> and
/// <c>ApiProblems</c> with no logic of its own.
/// </summary>
public sealed class ModuleRequestContext : IModuleRequestContext
{
    public TenantId GetRequestTenant(HttpContext context) => EndpointSecurity.GetRequestTenant(context);

    public SecurityPrincipalContext GetSecurityContext(HttpContext context) => EndpointSecurity.GetSecurityContext(context);

    public string? GetCallerIdentity(HttpContext context) => EndpointSecurity.GetCallerIdentity(context);

    public bool IsCanonicalClusterAdmin(ClaimsPrincipal? principal) => EndpointSecurity.IsCanonicalClusterAdmin(principal);

    public bool IsGlobalGovernanceAdmin(ClaimsPrincipal? principal) => EndpointSecurity.IsGlobalGovernanceAdmin(principal);

    public IResult? TryApplyBodyLimit(HttpRequest request, long maxBytes, string tooLargeMessage) =>
        EndpointSecurity.TryApplyBodyLimit(request, maxBytes, tooLargeMessage);

    public Task<(string? Body, IResult? Error)> TryReadBodyAsync(HttpRequest request, long maxBytes, string tooLargeMessage, CancellationToken ct) =>
        EndpointSecurity.TryReadBodyAsync(request, maxBytes, tooLargeMessage, ct);

    public Task<IResult> WithBodyLimitAsync(Func<Task<IResult>> action, string tooLargeMessage) =>
        EndpointSecurity.WithBodyLimitAsync(action, tooLargeMessage);

    public IResult NotFound(string detail, HttpContext? ctx = null) => ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, detail, ctx);

    public IResult BadRequest(string code, string detail, HttpContext? ctx = null) => ApiProblems.BadRequest(code, detail, ctx);

    public IResult Forbidden(HttpContext? ctx = null) => ApiProblems.Forbidden(httpContext: ctx);

    public IResult ServiceUnavailable(string detail, HttpContext? ctx = null) =>
        ApiProblems.ServiceUnavailable(ApiErrorCodes.ServiceUnavailable, detail, ctx);

    public IResult Problem(int status, string code, string detail, HttpContext? ctx = null, IDictionary<string, object?>? extraExtensions = null) =>
        ApiProblems.Problem(status, code, detail, ctx, extraExtensions: extraExtensions);
}
