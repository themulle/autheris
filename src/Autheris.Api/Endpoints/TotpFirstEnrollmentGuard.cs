namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
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

    /// <summary>
    /// JWT bearer (Entra ID / ADFS): replayable. Only <c>auth_time</c> proves the interactive sign-in; <c>iat</c> is reset
    /// by every silent token refresh and is therefore not accepted.
    /// </summary>
    ReplayableToken = 2,

    /// <summary>Basic session cookie: replayable; its <c>iat</c> is the login time and is kept across sliding refreshes.</summary>
    SessionCookie = 3,

    /// <summary>ForwardAuth proxy assertion: carries no authentication timestamp, so a first enrollment is always denied.</summary>
    ProxyAsserted = 4
}

/// <summary>
/// Security review (TOTP trust on first use): the first TOTP secret of a user gates HitL step-up approvals, but there
/// is no existing factor to prove possession of. For identities carrying a replayable credential the first enrollment
/// therefore requires a fresh interactive authentication within <see cref="TotpOptions.FirstEnrollmentMaxAuthAgeMinutes"/>:
/// JWT bearer identities must carry <c>auth_time</c> (<c>iat</c> is not accepted, a silent refresh resets it), Basic
/// session cookies are judged by their <c>iat</c> (= login time). ForwardAuth identities carry no timestamp and are
/// denied. Identities authenticated per request (Negotiate/Kerberos, raw Basic header) carry no timestamp and nothing to
/// replay; they are allowed (audited). Unknown authentication types fail closed. Re-enrollment stays protected by the
/// current code (SR-M-01).
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
            IsType(type, Autheris.Api.Security.GatewayAuthSchemes.JwtBearerAdfs))
        {
            return TotpEnrollmentAuthKind.ReplayableToken;
        }

        if (IsType(type, Autheris.Api.Security.BasicAuthSession.SchemeName))
        {
            return TotpEnrollmentAuthKind.SessionCookie;
        }

        if (IsType(type, Autheris.Api.Security.GatewayAuthSchemes.ForwardAuth))
        {
            return TotpEnrollmentAuthKind.ProxyAsserted;
        }

        return TotpEnrollmentAuthKind.Unknown;
    }

    private static bool IsType(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    // Tolerated clock skew between IdP and gateway for authentication timestamps in the future.
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Time of the interactive authentication (Unix seconds), depending on the authentication kind: <c>auth_time</c> for
    /// JWT bearer identities, the session <c>iat</c> (login time) for Basic session cookies; null otherwise (missing
    /// claim, per-request, ForwardAuth or unknown authentication).
    /// </summary>
    public static DateTimeOffset? GetAuthenticationTime(ClaimsPrincipal? principal) =>
        ClassifyAuthentication(principal) switch
        {
            TotpEnrollmentAuthKind.ReplayableToken => ParseUnixSeconds(principal?.FindFirst(AuthTimeClaimType)?.Value),
            TotpEnrollmentAuthKind.SessionCookie => ParseUnixSeconds(principal?.FindFirst(Autheris.Api.Security.BasicAuthSession.IssuedAtClaimType)?.Value),
            _ => null
        };

    private static DateTimeOffset? ParseUnixSeconds(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
            seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return null;
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
    /// Maximum authentication age for the given phase. "begin" uses <see cref="TotpOptions.FirstEnrollmentMaxAuthAgeMinutes"/>.
    /// "verify" can only complete a pending seed created by a "begin" that passed this check, and the pending seed lives
    /// <see cref="TotpOptions.PendingEnrollmentMinutes"/>; the verify window is therefore the begin window plus the pending
    /// lifetime, so an enrollment admitted at "begin" does not fail again at "verify" for the same pending seed.
    /// </summary>
    internal static TimeSpan GetMaxAuthAge(TotpOptions options, string phase)
    {
        var beginMinutes = Math.Max(1, options.FirstEnrollmentMaxAuthAgeMinutes);
        if (!string.Equals(phase, "verify", StringComparison.Ordinal))
        {
            return TimeSpan.FromMinutes(beginMinutes);
        }

        return TimeSpan.FromMinutes(beginMinutes + Math.Max(1, options.PendingEnrollmentMinutes));
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
            // ALLOW is audited on the required path (AuditPolicyTable: TwoFactorEnrollment* = FailClosed): no audit, no enrollment.
            try
            {
                await RecordAsync(context, userSid, "ALLOW", phase, "per-request authentication (no freshness check)", ct).ConfigureAwait(false);
            }
            catch (AuditUnavailableException)
            {
                return new TotpFirstEnrollmentCheck(true, Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            }

            return new TotpFirstEnrollmentCheck(true, null);
        }

        if (authKind == TotpEnrollmentAuthKind.Unknown)
        {
            await RecordAsync(context, userSid, "DENY", phase, "unknown authentication type", ct).ConfigureAwait(false);
            return new TotpFirstEnrollmentCheck(true, ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "First 2FA enrollment is not permitted for this authentication method."));
        }

        if (authKind == TotpEnrollmentAuthKind.ProxyAsserted)
        {
            // ForwardAuth carries no authentication timestamp; the proxy session cannot prove a fresh sign-in.
            await RecordAsync(context, userSid, "DENY", phase, "ForwardAuth identity (no authentication time)", ct).ConfigureAwait(false);
            return new TotpFirstEnrollmentCheck(true, ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "First 2FA enrollment is not permitted for ForwardAuth (proxy) sign-ins, which carry no sign-in time. Sign in with Entra ID/ADFS (fresh sign-in), Kerberos or Basic and retry."));
        }

        var totpOptions = context.RequestServices?.GetService<IOptions<GatewayOptions>>()?.Value.Totp ?? new TotpOptions();
        var maxAge = GetMaxAuthAge(totpOptions, phase);
        var now = context.RequestServices?.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow;

        if (IsFreshAuthentication(context.User, maxAge, now))
        {
            return new TotpFirstEnrollmentCheck(true, null);
        }

        var reason = GetAuthenticationTime(context.User) == null
            ? (authKind == TotpEnrollmentAuthKind.ReplayableToken ? "auth_time claim missing" : "authentication time missing")
            : "authentication not fresh";
        await RecordAsync(context, userSid, "DENY", phase, reason, ct).ConfigureAwait(false);
        return new TotpFirstEnrollmentCheck(true, ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "First 2FA enrollment requires a fresh sign-in. Sign in again and retry."));
    }

    /// <summary>
    /// Audits the completed first enrollment of a user on the required path (fail closed): an unavailable audit store
    /// surfaces as <see cref="AuditUnavailableException"/>, which the gateway exception handler maps to 503.
    /// </summary>
    public static Task RecordFirstEnrollmentCompletedAsync(HttpContext context, string userSid, CancellationToken ct) =>
        RecordAsync(context, userSid, "ALLOW", "verify", "first enrollment completed", ct);

    // ALLOW records use the required path (throws AuditUnavailableException), DENY records stay best effort (a denial still denies).
    private static async Task RecordAsync(HttpContext context, string userSid, string decision, string phase, string reason, CancellationToken ct)
    {
        var isAllow = string.Equals(decision, "ALLOW", StringComparison.Ordinal);
        var audit = context.RequestServices?.GetService<IAuditWriter>();
        if (audit == null)
        {
            if (isAllow)
            {
                throw new AuditUnavailableException("No audit writer is registered (fail-closed).");
            }

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
        var entry = new AuditLogEntry
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
        };

        if (isAllow)
        {
            await audit.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
        else
        {
            await audit.RecordBestEffortAsync(entry, ct).ConfigureAwait(false);
        }
    }
}
