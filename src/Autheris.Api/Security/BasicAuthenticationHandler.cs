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
    private readonly bool _hasArgon2Users;
    private readonly int _dummyArgon2MemoryKb;
    private readonly int _dummyArgon2Iterations;
    private readonly int _dummyArgon2Parallelism;
    private readonly int _dummyPbkdf2Iterations;
    private readonly Autheris.Application.Interfaces.IClientIpResolver? _clientIpResolver;

    public BasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<GatewayOptions> gatewayOptions,
        Microsoft.AspNetCore.Hosting.IWebHostEnvironment? environment = null,
        Autheris.Application.Interfaces.IClientIpResolver? clientIpResolver = null)
        : base(options, logger, encoder)
    {
        _gatewayOptions = gatewayOptions?.Value ?? new GatewayOptions();
        _isDevelopment = string.Equals(environment?.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
        _clientIpResolver = clientIpResolver;

        ConfigureDummyCost(
            _gatewayOptions.Authentication.BasicAuth.Users,
            out _hasArgon2Users,
            out _dummyArgon2MemoryKb,
            out _dummyArgon2Iterations,
            out _dummyArgon2Parallelism,
            out _dummyPbkdf2Iterations);
    }

    private static void ConfigureDummyCost(
        IEnumerable<BasicAuthUserConfig> users,
        out bool hasArgon2,
        out int argon2Mem,
        out int argon2Iters,
        out int argon2Par,
        out int pbkdf2Iters)
    {
        hasArgon2 = false;
        argon2Mem = PasswordHasher.DefaultArgon2MemorySizeKb;
        argon2Iters = PasswordHasher.DefaultArgon2Iterations;
        argon2Par = PasswordHasher.DefaultArgon2Parallelism;
        pbkdf2Iters = 10_000;

        foreach (var user in users)
        {
            if (string.IsNullOrWhiteSpace(user.Password))
            {
                continue;
            }

            if (user.Password.StartsWith("$argon2id$", StringComparison.OrdinalIgnoreCase))
            {
                hasArgon2 = true;
                var parts = user.Password.Split('$');
                if (parts.Length == 6)
                {
                    foreach (var pair in parts[3].Split(','))
                    {
                        var kv = pair.Split('=');
                        if (kv.Length == 2)
                        {
                            if (kv[0] == "m" && int.TryParse(kv[1], out int m)) argon2Mem = Math.Max(argon2Mem, m);
                            else if (kv[0] == "t" && int.TryParse(kv[1], out int t)) argon2Iters = Math.Max(argon2Iters, t);
                            else if (kv[0] == "p" && int.TryParse(kv[1], out int p)) argon2Par = Math.Max(argon2Par, p);
                        }
                    }
                }
            }
            else if (user.Password.StartsWith("$pbkdf2$", StringComparison.OrdinalIgnoreCase))
            {
                var parts = user.Password.Split('$');
                if (parts.Length == 5 && int.TryParse(parts[2], out var iters) && iters > 0)
                {
                    pbkdf2Iters = Math.Max(pbkdf2Iters, Math.Min(iters, PasswordHasher.MaxPbkdf2Iterations));
                }
            }
        }
    }

    private string ResolveClientIp()
    {
        var ipResolver = _clientIpResolver ?? Context.RequestServices?.GetService<Autheris.Application.Interfaces.IClientIpResolver>();
        var ip = ipResolver?.ResolveClientIp();
        if (ip != null && !ip.Equals(System.Net.IPAddress.None))
        {
            return ip.ToString();
        }

        return Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_gatewayOptions.Authentication.BasicAuth.Enabled)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // E-13: anonymous probes never pay for cryptographic hashing (and cannot be used as a password oracle).
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

        // Zero-allocation Base64 decoding directly from header span
        var encodedSpan = authHeader.AsSpan("Basic ".Length).Trim();
        if (encodedSpan.IsEmpty || encodedSpan.Length > 2048)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid Basic authorization header length."));
        }

        Span<byte> decodedBytes = stackalloc byte[2048];
        if (!Convert.TryFromBase64Chars(encodedSpan, decodedBytes, out int bytesWritten))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid Base64 encoding in Basic authorization header."));
        }

        var credentials = decodedBytes[..bytesWritten];
        int colonIndex = credentials.IndexOf((byte)':');
        if (colonIndex <= 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid Basic authorization format. Expected 'username:password'."));
        }

        var username = Encoding.UTF8.GetString(credentials[..colonIndex]);
        var password = Encoding.UTF8.GetString(credentials[(colonIndex + 1)..]);

        var distCache = Context.RequestServices?.GetService(typeof(Microsoft.Extensions.Caching.Distributed.IDistributedCache)) as Microsoft.Extensions.Caching.Distributed.IDistributedCache;
        var guard = BasicAuthAttemptGuard.For(_gatewayOptions.Authentication.BasicAuth, distCache);
        var attemptKey = BasicAuthAttemptGuard.BuildAttemptKey(username, ResolveClientIp());

        // RR-L2-03: locked-out (user, IP) pairs are rejected before any cryptographic work (no CPU amplification).
        if (guard.IsLockedOut(attemptKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid username or password."));
        }

        var configuredUser = _gatewayOptions.Authentication.BasicAuth.Users
            .FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

        if (configuredUser == null)
        {
            // SEC-03: Mitigate user enumeration timing attacks by running equivalent cryptographic hash calculation with mirrored cost
            if (_hasArgon2Users)
            {
                var dummyDerived = PasswordHasher.ComputeArgon2idHash(
                    password,
                    DummySalt,
                    _dummyArgon2MemoryKb,
                    _dummyArgon2Iterations,
                    _dummyArgon2Parallelism,
                    32);
                CryptographicOperations.FixedTimeEquals(dummyDerived, DummyTargetHash);
            }
            else
            {
                var dummyDerived = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    DummySalt,
                    iterations: _dummyPbkdf2Iterations,
                    HashAlgorithmName.SHA256,
                    outputLength: 32);
                CryptographicOperations.FixedTimeEquals(dummyDerived, DummyTargetHash);
            }

            guard.RecordFailure(attemptKey);
            return Task.FromResult(AuthenticateResult.Fail("Invalid username or password."));
        }

        // RR-L2-03: recently verified identical credentials skip the cryptographic computation.
        bool passwordMatches =
            (guard.TryGetCachedSuccess(authHeader, out var cachedUser) &&
             string.Equals(cachedUser, configuredUser.Username, StringComparison.Ordinal)) ||
            PasswordHasher.VerifyPassword(password, configuredUser.Password, username, _isDevelopment, msg => Logger.LogError("{Message}", msg));

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
