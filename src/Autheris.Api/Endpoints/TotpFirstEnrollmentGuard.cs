namespace Autheris.Api.Endpoints;

using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// Result of <see cref="TotpFirstEnrollmentGuard.CheckAsync"/>: whether this is the user's first enrollment and, when
/// the request must not continue, the response to return.
/// </summary>
internal sealed record TotpFirstEnrollmentCheck(bool IsFirstEnrollment, IResult? Rejection);

/// <summary>How the caller's identity was authenticated, as far as the first-enrollment freshness rule is concerned.</summary>
internal enum TotpEnrollmentAuthKind
{
    /// <summary>Unrecognised authentication type: fail closed.</summary>
    Unknown = 0,

    /// <summary>Credential proven on this request/connection (Negotiate/Kerberos/NTLM, raw Basic header); nothing replayable.</summary>
    PerRequest = 1,

    /// <summary>Bearer-style credential that can be stolen and replayed (JWT, Basic session cookie, ForwardAuth proxy session).</summary>
    ReplayableToken = 2
}

/// <summary>
/// Security review (TOTP trust on first use): the first TOTP secret of a user gates HitL step-up approvals, but there
/// is no existing factor to prove possession of. For identities carrying a replayable token (JWT bearer, Basic session
/// cookie) the first enrollment therefore requires a fresh interactive authentication (<c>auth_time</c>, or <c>iat</c>
/// when the IdP does not issue <c>auth_time</c>) within <see cref="TotpOptions.FirstEnrollmentMaxAuthAgeMinutes"/>.
/// Identities authenticated per request (Negotiate/Kerberos, raw Basic header) carry no timestamp and nothing to replay;
/// they are allowed (audited). Unknown authentication types fail closed. Re-enrollment stays protected by the current
/// code (SR-M-01).
/// </summary>
internal static class TotpFirstEnrollmentGuard
{
    public const string AuthTimeClaimType = "auth_time";

    // Authentication type of a Windows identity authenticated via NTLM (no gateway constant exists for it).
    internal const string NtlmAuthenticationType = "NTLM";

    /// <summary>Classifies the identity by its <see cref="System.Security.Principal.IIdentity.AuthenticationType"/>.</summary>
    public static TotpEnrollmentAuthKind ClassifyAuthentication(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true } identity || string.IsNullOrWhiteSpace(identity.AuthenticationType))
        {
            return TotpEnrollmentAuthKind.Unknown;
        }

        var type = identity.AuthenticationType;
        if (IsType(type, Autheris.Api.Security.NegotiateHardening.KerberosAuthenticationType) ||
            IsType(type, Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme) ||
            IsType(type, NtlmAuthenticationType) ||
            IsType(type, Autheris.Api.Security.GatewayAuthSchemes.Basic))
        {
            return TotpEnrollmentAuthKind.PerRequest;
        }

        // JwtBearer identities carry the TokenValidationParameters authentication type (not set by ConfigureJwtBearer,
        // so the IdentityModel default); the scheme names are accepted as well.
        if (IsType(type, Microsoft.IdentityModel.Tokens.TokenValidationParameters.DefaultAuthenticationType) ||
            IsType(type, Autheris.Api.Security.GatewayAuthSchemes.JwtBearer) ||
            IsType(type, Autheris.Api.Security.GatewayAuthSchemes.JwtBearerAdfs) ||
            IsType(type, Autheris.Api.Security.BasicAuthSession.SchemeName) ||
            IsType(type, Autheris.Api.Security.GatewayAuthSchemes.ForwardAuth))
        {
            return TotpEnrollmentAuthKind.ReplayableToken;
        }

        return TotpEnrollmentAuthKind.Unknown;
    }

    private static bool IsType(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    // Tolerated clock skew between IdP and gateway for authentication timestamps in the future.
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>Time of the interactive authentication: <c>auth_time</c>, else <c>iat</c> (Unix seconds); null if neither is present.</summary>
    public static DateTimeOffset? GetAuthenticationTime(ClaimsPrincipal? principal)
    {
        var authTime = principal?.FindFirst(AuthTimeClaimType)?.Value;
        if (!string.IsNullOrWhiteSpace(authTime) &&
            long.TryParse(authTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
            seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return TokenRevocationKeys.GetIssuedAt(principal);
    }

    /// <summary>True when the authentication time is known and not older than <paramref name="maxAge"/> (fail closed otherwise).</summary>
    public static bool IsFreshAuthentication(ClaimsPrincipal? principal, TimeSpan maxAge, DateTimeOffset now)
    {
        if (GetAuthenticationTime(principal) is not { } authenticatedAt)
        {
            return false;
        }

        var age = now - authenticatedAt;
        return age <= maxAge && age >= -ClockSkew;
    }

    /// <summary>
    /// Rejects the first enrollment (no active secret) of a user whose authentication is not fresh with 403 and audits
    /// the denial. In the Development environment (persona login) the freshness check is skipped.
    /// </summary>
    public static async Task<TotpFirstEnrollmentCheck> CheckAsync(
        HttpContext context,
        ITotpEnrollmentService enrollmentService,
        string userSid,
        string phase,
        CancellationToken ct)
    {
        bool hasActiveSecret;
        try
        {
            hasActiveSecret = await enrollmentService.HasActiveSecretAsync(userSid, ct).ConfigureAwait(false);
        }
        catch
        {
            // Fail closed: without knowing whether a secret exists the freshness requirement cannot be decided.
            return new TotpFirstEnrollmentCheck(false, Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        if (hasActiveSecret)
        {
            return new TotpFirstEnrollmentCheck(false, null);
        }

        var env = context.RequestServices?.GetService<IHostEnvironment>();
        if (env?.IsDevelopment() == true)
        {
            return new TotpFirstEnrollmentCheck(true, null);
        }

        var authKind = ClassifyAuthentication(context.User);
        if (authKind == TotpEnrollmentAuthKind.PerRequest)
        {
            await RecordAsync(context, userSid, "ALLOW", phase, "per-request authentication (no freshness check)", ct).ConfigureAwait(false);
            return new TotpFirstEnrollmentCheck(true, null);
        }

        if (authKind == TotpEnrollmentAuthKind.Unknown)
        {
            await RecordAsync(context, userSid, "DENY", phase, "unknown authentication type", ct).ConfigureAwait(false);
            return new TotpFirstEnrollmentCheck(true, Results.Json(
                new { error = "First 2FA enrollment is not permitted for this authentication method." },
                statusCode: StatusCodes.Status403Forbidden));
        }

        var totpOptions = context.RequestServices?.GetService<IOptions<GatewayOptions>>()?.Value.Totp ?? new TotpOptions();
        var maxAge = TimeSpan.FromMinutes(Math.Max(1, totpOptions.FirstEnrollmentMaxAuthAgeMinutes));
        var now = context.RequestServices?.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow;

        if (IsFreshAuthentication(context.User, maxAge, now))
        {
            return new TotpFirstEnrollmentCheck(true, null);
        }

        await RecordAsync(context, userSid, "DENY", phase, "authentication not fresh", ct).ConfigureAwait(false);
        return new TotpFirstEnrollmentCheck(true, Results.Json(
            new { error = $"First 2FA enrollment requires a fresh sign-in (within {maxAge.TotalMinutes:0} minutes). Sign in again and retry." },
            statusCode: StatusCodes.Status403Forbidden));
    }

    /// <summary>Audits the completed first enrollment of a user (best effort; the endpoint audit covers the request itself).</summary>
    public static Task RecordFirstEnrollmentCompletedAsync(HttpContext context, string userSid, CancellationToken ct) =>
        RecordAsync(context, userSid, "ALLOW", "verify", "first enrollment completed", ct);

    private static async Task RecordAsync(HttpContext context, string userSid, string decision, string phase, string reason, CancellationToken ct)
    {
        var audit = context.RequestServices?.GetService<IAuditWriter>();
        if (audit == null)
        {
            return;
        }

        TenantId tenant;
        try
        {
            tenant = EndpointSecurity.GetRequestTenant(context);
        }
        catch
        {
            tenant = TenantId.LegacySingleTenant;
        }

        var authenticatedAt = GetAuthenticationTime(context.User);
        await audit.RecordBestEffortAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = phase == "verify" ? AuditEventTypes.TwoFactorEnrollmentVerified : AuditEventTypes.TwoFactorEnrollmentStarted,
            ActorSid = new Sid(userSid),
            TargetTable = string.Empty,
            Decision = decision,
            TraceId = context.TraceIdentifier,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                firstEnrollment = true,
                phase,
                reason,
                authenticatedAt = authenticatedAt?.ToString("O", CultureInfo.InvariantCulture)
            })
        }, ct).ConfigureAwait(false);
    }
}
