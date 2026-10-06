using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Security;

/// <summary>
/// F-AUTH-DX: Constants and helpers for the Basic-auth cookie session.
/// </summary>
public static class BasicAuthSession
{
    /// <summary>Authentication scheme (and identity authentication type) of the session cookie.</summary>
    public const string SchemeName = BasicAuthSessionOptions.SchemeName;

    /// <summary>Clients that want to stay stateless send this header with value "1" to suppress the cookie.</summary>
    public const string NoSessionHeader = "X-No-Session";

    public const string AuthMethodClaimType = "auth_method";
    public const string AuthMethodBasic = "basic";
    public const string AuthMethodSession = "basic-session";
    public const string StampClaimType = "autheris_session_stamp";
    public const string JtiClaimType = "jti";
    public const string IssuedAtClaimType = "iat";

    /// <summary>
    /// True when the session cookie may be used in this process: feature enabled, Basic auth enabled and the
    /// environment explicitly allowlisted (never Production). Startup validation rejects every other combination.
    /// </summary>
    public static bool IsAllowed(GatewayOptions options, IHostEnvironment environment)
    {
        var basic = options.Authentication.BasicAuth;
        if (!basic.Enabled || !basic.Session.Enabled || options.Authentication.RequireKerberosOnly)
        {
            return false;
        }

        if (environment.IsProduction())
        {
            return false;
        }

        // Cookies are ambient credentials: with wildcard CORS the Origin check is off, so the session stays disabled
        // (startup error outside Development, silently disabled with a warning in Development / Quickstart).
        if (options.IsWildcardCors)
        {
            return false;
        }

        return basic.Session.AllowedEnvironments.Any(e =>
            string.Equals(e, environment.EnvironmentName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the startup validation errors for the session configuration (empty when valid or disabled).
    /// </summary>
    public static IReadOnlyList<string> Validate(GatewayOptions options, IHostEnvironment environment)
    {
        var errors = new List<string>();
        var basic = options.Authentication.BasicAuth;
        var session = basic.Session;
        if (!session.Enabled)
        {
            return errors;
        }

        if (!basic.Enabled)
        {
            errors.Add("BasicAuth.Session.Enabled=true erfordert BasicAuth.Enabled=true.");
        }

        if (options.Authentication.RequireKerberosOnly)
        {
            errors.Add("BasicAuth.Session darf nicht aktiviert sein, wenn RequireKerberosOnly=true ist.");
        }

        if (environment.IsProduction() ||
            session.AllowedEnvironments.Any(e => string.Equals(e, Environments.Production, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("Sicherheitsverletzung: BasicAuth.Session ist in der Production-Umgebung grundsätzlich verboten.");
        }
        else if (!session.AllowedEnvironments.Any(e => string.Equals(e, environment.EnvironmentName, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Sicherheitsverletzung: BasicAuth.Session ist für die Umgebung '{environment.EnvironmentName}' nicht freigegeben " +
                       $"(BasicAuth.Session.AllowedEnvironments: {string.Join(", ", session.AllowedEnvironments)}).");
        }

        if (string.IsNullOrWhiteSpace(session.CookieName) ||
            !session.CookieName.StartsWith("__Host-", StringComparison.Ordinal) ||
            session.CookieName.Length <= "__Host-".Length)
        {
            errors.Add("BasicAuth.Session.CookieName muss das Präfix '__Host-' verwenden (Secure, Path=/, keine Domain).");
        }

        if (session.AbsoluteExpirationMinutes < session.SlidingExpirationMinutes)
        {
            errors.Add("BasicAuth.Session.AbsoluteExpirationMinutes muss >= SlidingExpirationMinutes sein.");
        }

        // Low-28: cookies are ambient credentials; wildcard CORS would disable the Origin check. In Development
        // (e.g. the Quickstart profile) the session is disabled instead of failing the start, see IsAllowed.
        if (options.IsWildcardCors && !environment.IsDevelopment())
        {
            errors.Add("Sicherheitsverletzung: BasicAuth.Session ist mit warn_allow_all_cors_origins / TrustedOrigins '*' nicht kombinierbar (CSRF).");
        }

        // Without a shared key ring every replica would reject the cookies issued by the others.
        if ((options.HighAvailability.MultiNodeClusterMode || options.HighAvailability.Replicas > 1) &&
            string.IsNullOrWhiteSpace(session.KeyDirectory))
        {
            errors.Add("BasicAuth.Session mit mehreren Replikas erfordert BasicAuth.Session.KeyDirectory auf einem gemeinsamen Volume.");
        }

        return errors;
    }

    /// <summary>Human-readable reason why an enabled session is not active (null when it is active or disabled).</summary>
    public static string? GetInactiveReason(GatewayOptions options, IHostEnvironment environment)
    {
        var basic = options.Authentication.BasicAuth;
        if (!basic.Session.Enabled || IsAllowed(options, environment))
        {
            return null;
        }

        if (options.IsWildcardCors)
        {
            return "wildcard CORS (warn_allow_all_cors_origins / TrustedOrigins '*' / Quickstart) is active";
        }

        return !basic.Enabled
            ? "BasicAuth.Enabled is false"
            : $"environment '{environment.EnvironmentName}' is not allowed";
    }

    /// <summary>
    /// Validates the configured users: non-empty username and password, unique usernames, and (R2-1 / N-1) SIDs
    /// without ':' and without the 'ITSM' prefix, because approver resolution historically split or prefix-matched SIDs.
    /// </summary>
    public static IReadOnlyList<string> ValidateUsers(BasicAuthOptions basic)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in basic.Users)
        {
            if (string.IsNullOrWhiteSpace(user.Username) || user.Username.Contains(':'))
            {
                errors.Add("BasicAuth-Benutzer benötigen einen nicht-leeren Benutzernamen ohne ':'.");
                continue;
            }

            if (!seen.Add(user.Username))
            {
                errors.Add($"BasicAuth-Benutzer '{user.Username}' ist mehrfach konfiguriert.");
            }

            if (string.IsNullOrEmpty(user.Password))
            {
                errors.Add($"Sicherheitsverletzung: BasicAuth-Benutzer '{user.Username}' hat kein Passwort.");
            }

            var sid = BasicAuthPrincipalFactory.ResolveSid(user);
            if (sid.Contains(':') ||
                sid.StartsWith("ITSM", StringComparison.OrdinalIgnoreCase) ||
                sid.StartsWith("S-1-5-21-ITSM-", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Sicherheitsverletzung: BasicAuth-Benutzer '{user.Username}' hat eine unzulässige SID (kein ':' und kein Präfix 'ITSM' erlaubt).");
            }

            if (!string.IsNullOrWhiteSpace(user.TenantId) && !TenantId.TryParse(user.TenantId, out _))
            {
                errors.Add($"Sicherheitsverletzung: BasicAuth-Benutzer '{user.Username}' hat ein ungültiges TenantId-Format.");
            }
        }

        return errors;
    }

    /// <summary>Applies the hardened cookie settings.</summary>
    public static void ConfigureCookie(CookieAuthenticationOptions cookie, BasicAuthSessionOptions session)
    {
        cookie.Cookie.Name = session.CookieName;
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        cookie.Cookie.Path = "/";
        cookie.Cookie.Domain = null;
        cookie.Cookie.IsEssential = true;
        cookie.ExpireTimeSpan = TimeSpan.FromMinutes(session.SlidingExpirationMinutes);
        cookie.SlidingExpiration = true;
        cookie.EventsType = typeof(BasicAuthSessionCookieEvents);
    }

    /// <summary>
    /// Builds the principal stored in the session cookie (fresh claims from configuration plus session metadata).
    /// </summary>
    public static ClaimsPrincipal CreateSessionPrincipal(BasicAuthUserConfig user, string jti, DateTimeOffset issuedAt)
    {
        var claims = BasicAuthPrincipalFactory.BuildClaims(user);
        claims.Add(new Claim(AuthMethodClaimType, AuthMethodSession));
        claims.Add(new Claim(JtiClaimType, jti));
        claims.Add(new Claim(IssuedAtClaimType, issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        claims.Add(new Claim(StampClaimType, BasicAuthPrincipalFactory.ComputeStamp(user)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role));
    }

    /// <summary>Signs the user into the session cookie (fresh session id) and returns the session id.</summary>
    public static async Task<string> IssueAsync(HttpContext context, BasicAuthUserConfig user)
    {
        var now = DateTimeOffset.UtcNow;
        var jti = Guid.NewGuid().ToString("N");
        await context.SignInAsync(SchemeName, CreateSessionPrincipal(user, jti, now), new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
            IssuedUtc = now
        }).ConfigureAwait(false);
        return jti;
    }

    public static bool IsSessionPrincipal(ClaimsPrincipal? principal) =>
        principal?.Identity is { IsAuthenticated: true, AuthenticationType: SchemeName };

    public static DateTimeOffset? GetIssuedAt(ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirst(IssuedAtClaimType)?.Value;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }
}

/// <summary>
/// F-AUTH-DX: Single source for the claims of a configured Basic-auth user (header login and cookie session).
/// </summary>
public static class BasicAuthPrincipalFactory
{
    public static string ResolveSid(BasicAuthUserConfig user) =>
        !string.IsNullOrWhiteSpace(user.Sid)
            ? user.Sid
            : $"S-1-5-21-BASIC-{user.Username.ToUpperInvariant()}";

    public static string ResolveTenant(BasicAuthUserConfig user) =>
        !string.IsNullOrWhiteSpace(user.TenantId)
            ? user.TenantId
            : TenantId.LegacySingleTenant.Value;

    public static List<Claim> BuildClaims(BasicAuthUserConfig user)
    {
        var sid = ResolveSid(user);
        var tenant = ResolveTenant(user);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Username),
            new(ClaimTypes.PrimarySid, sid),
            new("objectSid", sid),
            new("tenant_id", tenant),
            new("tenant", tenant),
            new("tid", tenant)
        };

        foreach (var role in user.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (var group in user.GroupSids)
        {
            claims.Add(new Claim(ClaimTypes.GroupSid, group));
        }

        return claims;
    }

    /// <summary>
    /// Stamp over the security-relevant user configuration. A change of password, SID or tenant invalidates
    /// existing sessions; role and group changes are applied on the next request (claims are rebuilt).
    /// </summary>
    public static string ComputeStamp(BasicAuthUserConfig user)
    {
        var material = string.Join('\n', user.Username.ToUpperInvariant(), user.Password, ResolveSid(user), ResolveTenant(user));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    public static BasicAuthUserConfig? FindUser(BasicAuthOptions basic, string? username) =>
        string.IsNullOrEmpty(username)
            ? null
            : basic.Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// F-AUTH-DX: Validates the session cookie on every request against the current configuration and turns the
/// cookie's login redirect into a Basic challenge (the gateway has no login page).
/// </summary>
public sealed class BasicAuthSessionCookieEvents(
    IOptions<GatewayOptions> gatewayOptions,
    ILogger<BasicAuthSessionCookieEvents> logger) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var basic = gatewayOptions.Value.Authentication.BasicAuth;
        var user = BasicAuthPrincipalFactory.FindUser(basic, principal?.Identity?.Name);
        var stamp = principal?.FindFirst(BasicAuthSession.StampClaimType)?.Value;
        var jti = principal?.FindFirst(BasicAuthSession.JtiClaimType)?.Value;
        var issuedAt = BasicAuthSession.GetIssuedAt(principal);
        var now = context.HttpContext.RequestServices.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow;

        string? rejectReason = null;
        if (!basic.Enabled || !basic.Session.Enabled)
        {
            rejectReason = "session feature disabled";
        }
        else if (user == null)
        {
            rejectReason = "user no longer configured";
        }
        else if (string.IsNullOrEmpty(stamp) ||
                 !CryptographicOperations.FixedTimeEquals(
                     Encoding.ASCII.GetBytes(stamp),
                     Encoding.ASCII.GetBytes(BasicAuthPrincipalFactory.ComputeStamp(user))))
        {
            rejectReason = "user configuration changed";
        }
        else if (issuedAt is not { } iat || string.IsNullOrEmpty(jti))
        {
            rejectReason = "session metadata missing";
        }
        else if (now - iat > TimeSpan.FromMinutes(basic.Session.AbsoluteExpirationMinutes))
        {
            rejectReason = "absolute lifetime exceeded";
        }

        if (rejectReason != null)
        {
            logger.LogInformation("Basic session cookie rejected: {Reason}.", rejectReason);
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(BasicAuthSession.SchemeName).ConfigureAwait(false);
            return;
        }

        // Rebuild the claims from the current configuration (role/group changes apply immediately) while keeping
        // the session identity (jti/iat) so token revocation keeps working.
        context.ReplacePrincipal(BasicAuthSession.CreateSessionPrincipal(user!, jti!, issuedAt!.Value));
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        context.HttpContext.ChallengeAsync(GatewayAuthSchemes.Basic);

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
