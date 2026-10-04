using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
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

        var sid = !string.IsNullOrWhiteSpace(configuredUser.Sid)
            ? configuredUser.Sid
            : $"S-1-5-21-BASIC-{username.ToUpperInvariant()}";

        var tenant = !string.IsNullOrWhiteSpace(configuredUser.TenantId)
            ? configuredUser.TenantId
            : TenantId.LegacySingleTenant.Value;

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.PrimarySid, sid),
            new("objectSid", sid),
            new("tenant_id", tenant),
            new("tenant", tenant),
            new("tid", tenant)
        };

        foreach (var role in configuredUser.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (var group in configuredUser.GroupSids)
        {
            claims.Add(new Claim(ClaimTypes.GroupSid, group));
        }

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

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var realm = string.IsNullOrWhiteSpace(_gatewayOptions.Authentication.BasicAuth.Realm)
            ? "Autheris"
            : _gatewayOptions.Authentication.BasicAuth.Realm;
        Response.Headers.Append("WWW-Authenticate", $"Basic realm=\"{realm}\"");
        Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
