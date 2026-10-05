using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Security;

public sealed class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Basic";
    private readonly GatewayOptions _gatewayOptions;

    // Constant dummy values for timing attack mitigation when username is not found
    private static readonly byte[] DummySalt = "AutherisTimingDefenseSalt2026!"u8.ToArray();
    private static readonly byte[] DummyTargetHash = new byte[32];

    private readonly bool _isDevelopment;
    private readonly int _dummyIterations;

    public BasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<GatewayOptions> gatewayOptions,
        Microsoft.AspNetCore.Hosting.IWebHostEnvironment? environment = null)
        : base(options, logger, encoder)
    {
        _gatewayOptions = gatewayOptions?.Value ?? new GatewayOptions();
        _isDevelopment = string.Equals(environment?.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);

        _dummyIterations = DetermineDummyIterations(_gatewayOptions.Authentication.BasicAuth.Users);
    }

    private static int DetermineDummyIterations(IEnumerable<BasicAuthUserConfig> users)
    {
        // RR-L2-03: mirror the most expensive configured hash so unknown users are never faster than any known user.
        var max = 0;
        foreach (var user in users)
        {
            if (user.Password != null && user.Password.StartsWith("$pbkdf2$", StringComparison.OrdinalIgnoreCase))
            {
                var parts = user.Password.Split('$');
                if (parts.Length == 5 && int.TryParse(parts[2], out var iters) && iters > 0)
                {
                    max = Math.Max(max, Math.Min(iters, MaxPbkdf2Iterations));
                }
            }
        }
        return max > 0 ? max : 10_000;
    }

    // RR-L2-03: hard bounds applied at verification time (startup validation enforces the configured minimum).
    internal const int MinPbkdf2Iterations = 10_000;
    internal const int MaxPbkdf2Iterations = 10_000_000;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_gatewayOptions.Authentication.BasicAuth.Enabled)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // E-13: anonymous probes never pay for PBKDF2 (and cannot be used as a password oracle).
        if (Request.Path.StartsWithSegments("/health"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) ||
            !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var encoded = authHeader["Basic ".Length..].Trim();
        byte[] decodedBytes;
        try
        {
            decodedBytes = Convert.FromBase64String(encoded);
        }
        catch
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid Base64 encoding in Basic authorization header."));
        }

        var credentialString = Encoding.UTF8.GetString(decodedBytes);
        var colonIndex = credentialString.IndexOf(':');
        if (colonIndex <= 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid Basic authorization format. Expected 'username:password'."));
        }

        var username = credentialString[..colonIndex];
        var password = credentialString[(colonIndex + 1)..];

        var guard = BasicAuthAttemptGuard.For(_gatewayOptions.Authentication.BasicAuth);
        var attemptKey = BasicAuthAttemptGuard.BuildAttemptKey(username, Context.Connection.RemoteIpAddress?.ToString());

        // RR-L2-03: locked-out (user, IP) pairs are rejected before any PBKDF2 work (no CPU amplification).
        if (guard.IsLockedOut(attemptKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid username or password."));
        }

        var configuredUser = _gatewayOptions.Authentication.BasicAuth.Users
            .FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

        if (configuredUser == null)
        {
            // SEC-03: Mitigate user enumeration timing attacks by running equivalent cryptographic hash calculation with mirrored iterations
            var dummyDerived = Rfc2898DeriveBytes.Pbkdf2(
                password,
                DummySalt,
                iterations: _dummyIterations,
                HashAlgorithmName.SHA256,
                outputLength: 32);
            CryptographicOperations.FixedTimeEquals(dummyDerived, DummyTargetHash);

            guard.RecordFailure(attemptKey);
            return Task.FromResult(AuthenticateResult.Fail("Invalid username or password."));
        }

        // RR-L2-03: recently verified identical credentials skip the PBKDF2 computation.
        bool passwordMatches =
            (guard.TryGetCachedSuccess(authHeader, out var cachedUser) &&
             string.Equals(cachedUser, configuredUser.Username, StringComparison.Ordinal)) ||
            VerifyPassword(password, configuredUser.Password, username);

        if (!passwordMatches)
        {
            guard.RecordFailure(attemptKey);
            return Task.FromResult(AuthenticateResult.Fail("Invalid username or password."));
        }

        guard.RecordSuccess(attemptKey);
        guard.CacheSuccess(authHeader, configuredUser.Username);

        // F-AUTH-DX: claims come from the shared factory so header login and cookie session are identical.
        var claims = BasicAuthPrincipalFactory.BuildClaims(configuredUser);
        claims.Add(new Claim(BasicAuthSession.AuthMethodClaimType, BasicAuthSession.AuthMethodBasic));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private bool VerifyPassword(string inputPassword, string storedPassword, string username)
    {
        // 1. Support modern Salted PBKDF2: $pbkdf2$iterations$salt$hash
        if (storedPassword.StartsWith("$pbkdf2$", StringComparison.OrdinalIgnoreCase))
        {
            var parts = storedPassword.Split('$');
            // Format: empty, "pbkdf2", iterations, salt_b64, hash_b64
            if (parts.Length == 5 && int.TryParse(parts[2], out var iterations))
            {
                if (iterations < MinPbkdf2Iterations || iterations > MaxPbkdf2Iterations)
                {
                    var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username)))[..12];
                    Logger.LogError("Basic authentication rejected user (hash: {UserHash}): PBKDF2 iteration count outside the permitted range.", userHash);
                    return false;
                }
                try
                {
                    var salt = Convert.FromBase64String(parts[3]);
                    var expectedHash = Convert.FromBase64String(parts[4]);

                    var computedHash = Rfc2898DeriveBytes.Pbkdf2(
                        inputPassword,
                        salt,
                        iterations,
                        HashAlgorithmName.SHA256,
                        expectedHash.Length);

                    return CryptographicOperations.FixedTimeEquals(expectedHash, computedHash);
                }
                catch
                {
                    return false;
                }
            }
        }

        // 2. Plaintext or unsalted SHA-256 passwords are strictly prohibited outside of Development
        if (!_isDevelopment)
        {
            var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username)))[..12];
            Logger.LogError("Basic authentication rejected user (hash: {UserHash}): Plaintext or unsalted SHA-256 passwords are strictly prohibited outside of Development.", userHash);
            return false;
        }

        var userPasswordBytes = Encoding.UTF8.GetBytes(storedPassword);
        var inputPasswordBytes = Encoding.UTF8.GetBytes(inputPassword);

        // 2a. Exact match (Development / test plaintext only)
        if (CryptographicOperations.FixedTimeEquals(userPasswordBytes, inputPasswordBytes))
        {
            return true;
        }

        // 2b. SHA-256 Hex Hash match (Development only)
        var inputHash = Convert.ToHexString(SHA256.HashData(inputPasswordBytes));
        var inputHashBytes = Encoding.UTF8.GetBytes(inputHash);
        if (CryptographicOperations.FixedTimeEquals(userPasswordBytes, inputHashBytes))
        {
            return true;
        }

        return false;
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Headers and status code cannot be changed once the response has started.
        if (Response.HasStarted)
        {
            return;
        }

        var realm = string.IsNullOrWhiteSpace(_gatewayOptions.Authentication.BasicAuth.Realm)
            ? "Autheris"
            : _gatewayOptions.Authentication.BasicAuth.Realm;

        // F-AUTH-DX: script requests (fetch/XHR from the DevPortal, Nitro, Swagger) get a plain 401 without the
        // Basic challenge so the browser does not pop up its native login dialog in the middle of a page.
        if (!Request.Headers.ContainsKey("X-Requested-With"))
        {
            Response.Headers["WWW-Authenticate"] = $"Basic realm=\"{realm}\"";
        }

        Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized;

        if (Response.HasStarted)
        {
            return;
        }

        var acceptsHtml = Request.Headers.Accept.Any(a => a != null && a.Contains("text/html", StringComparison.OrdinalIgnoreCase));
        if (acceptsHtml)
        {
            Response.ContentType = "text/html; charset=utf-8";
            var encodedRealm = System.Net.WebUtility.HtmlEncode(realm);
            var devTip = _isDevelopment
                ? "<p><strong>Development Tip:</strong> Provide HTTP Basic credentials (e.g. <code>Authorization: Basic ...</code>). With <code>BasicAuth.Session</code> enabled, one login is enough: the gateway then issues a session cookie.</p>"
                : string.Empty;

            var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <title>401 Unauthorized - Autheris Gateway</title>
                <style>
                    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; margin: 40px; line-height: 1.6; color: #333; }
                    .box { max-width: 600px; margin: 0 auto; border: 1px solid #e1e4e8; border-radius: 6px; padding: 24px; box-shadow: 0 2px 8px rgba(0,0,0,0.05); }
                    h1 { color: #d73a49; font-size: 20px; margin-top: 0; }
                    code { background-color: #f6f8fa; padding: 2px 6px; border-radius: 3px; font-size: 85%; }
                </style>
            </head>
            <body>
                <div class="box">
                    <h1>401 Unauthorized</h1>
                    <p>Authentication is required to access this endpoint.</p>
                    <p>Realm: <code>{{encodedRealm}}</code></p>
                    {{devTip}}
                </div>
            </body>
            </html>
            """;
            await Response.WriteAsync(html);
        }
        else if (_isDevelopment)
        {
            Response.ContentType = "application/problem+json; charset=utf-8";
            var activeSchemes = new List<string>();
            if (_gatewayOptions.Authentication.BasicAuth.Enabled) activeSchemes.Add("Basic");
            if (_gatewayOptions.Authentication.EntraId.Enabled) activeSchemes.Add("Bearer (EntraId)");
            if (_gatewayOptions.Authentication.Adfs.Enabled) activeSchemes.Add("Bearer (ADFS)");
            if (_gatewayOptions.Authentication.ForwardAuth.Enabled) activeSchemes.Add("ForwardAuth");
            if (!_gatewayOptions.Authentication.RequireKerberosOnly) activeSchemes.Add("Negotiate");

            var diagnosticObj = new
            {
                type = "https://tools.ietf.org/html/rfc7235#section-3.1",
                title = "Unauthorized",
                status = 401,
                detail = "Authentication required. No valid credentials provided in Authorization header.",
                realm,
                active_schemes = activeSchemes,
                hint = "Include an 'Authorization: Basic <base64(user:pass)>' or 'Authorization: Bearer <token>' header."
            };
            await Response.WriteAsJsonAsync(diagnosticObj, options: null, contentType: "application/problem+json; charset=utf-8");
        }
    }
}
