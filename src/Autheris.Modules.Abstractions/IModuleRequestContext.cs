using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

namespace Autheris.Modules.Abstractions;

/// <summary>
/// Host-provided request helpers for module endpoints. The host implementation forwards every member 1:1 to the
/// helper the core endpoints use, so a module can never drift from the host's tenant, admin and problem handling.
/// Modules receive it by minimal-API parameter injection; it is a protected host service and cannot be replaced.
/// </summary>
public interface IModuleRequestContext
{
    /// <summary>Resolves the request tenant. Throws on a malformed tenant claim (fail closed).</summary>
    TenantId GetRequestTenant(HttpContext context);

    SecurityPrincipalContext GetSecurityContext(HttpContext context);

    string? GetCallerIdentity(HttpContext context);

    bool IsCanonicalClusterAdmin(ClaimsPrincipal? principal);

    bool IsGlobalGovernanceAdmin(ClaimsPrincipal? principal);

    /// <summary>Tightens the server-side body limit; returns a 413 problem when the declared length is already too large.</summary>
    IResult? TryApplyBodyLimit(HttpRequest request, long maxBytes, string tooLargeMessage);

    Task<(string? Body, IResult? Error)> TryReadBodyAsync(HttpRequest request, long maxBytes, string tooLargeMessage, CancellationToken ct);

    Task<IResult> WithBodyLimitAsync(Func<Task<IResult>> action, string tooLargeMessage);

    IResult NotFound(string detail, HttpContext? ctx = null);

    IResult BadRequest(string code, string detail, HttpContext? ctx = null);

    IResult Forbidden(HttpContext? ctx = null);

    IResult ServiceUnavailable(string detail, HttpContext? ctx = null);

    IResult Problem(int status, string code, string detail, HttpContext? ctx = null, IDictionary<string, object?>? extraExtensions = null);
}
