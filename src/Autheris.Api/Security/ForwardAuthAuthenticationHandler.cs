using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Autheris.Api.Security;

public sealed class ForwardAuthAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ForwardAuth";
    private readonly GatewayOptions _gatewayOptions;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly ITrustedProxyValidator _proxyValidator;
    private readonly bool _isDevelopment;
    private readonly byte[]? _precomputedSharedSecretBytes;

    public ForwardAuthAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<GatewayOptions> gatewayOptions,
        ITrustedProxyValidator proxyValidator,
        IKeyVaultSecretProvider? secretProvider = null,
        IWebHostEnvironment? environment = null)
        : base(options, logger, encoder)
    {
        _gatewayOptions = gatewayOptions?.Value ?? new GatewayOptions();
        _proxyValidator = proxyValidator;
        _secretProvider = secretProvider;
        _isDevelopment = environment?.IsDevelopment() ?? false;

        var configuredSecret = _gatewayOptions.Authentication.ForwardAuth.SharedSecret;
        if (!string.IsNullOrWhiteSpace(configuredSecret))
        {
            _precomputedSharedSecretBytes = Encoding.UTF8.GetBytes(configuredSecret);
        }
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var forwardAuthOptions = _gatewayOptions.Authentication.ForwardAuth;
        if (!forwardAuthOptions.Enabled)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // The mode is fixed by configuration; startup guarantees Enabled=false in Direct mode.
        if (string.IsNullOrWhiteSpace(forwardAuthOptions.UserHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var headers = Request.Headers;
        var userHeader = forwardAuthOptions.UserHeader;

        // Presence only: the value is parsed after the peer and the secret have been verified.
        if (!headers.TryGetValue(userHeader, out var userVal) || StringValues.IsNullOrEmpty(userVal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // 2. Zero-Trust Verification: the connection peer (never X-Forwarded-For) must be a ForwardAuth proxy
        if (forwardAuthOptions.RequireTrustedProxy)
        {
            var rawRemoteIp = TrustedProxyPeer.GetRawRemoteIp(Context);
            var remoteIp = rawRemoteIp ?? (_isDevelopment ? IPAddress.Loopback : null);

            if (remoteIp == null)
            {
                Logger.LogWarning("ForwardAuth rejected: Remote client IP address could not be determined.");
                return Task.FromResult(AuthenticateResult.Fail("Remote client IP address could not be determined."));
            }

            if (!_proxyValidator.IsForwardAuthPeerTrusted(remoteIp))
            {
                Logger.LogWarning(
                    "ForwardAuth rejected: identity headers from untrusted peer '{RemoteIp}'.",
                    remoteIp);

                return Task.FromResult(AuthenticateResult.Fail("Untrusted proxy IP for ForwardAuth."));
            }
        }
        else if (!_isDevelopment)
        {
            Logger.LogWarning("ForwardAuth rejected: Disabling RequireTrustedProxy is strictly prohibited outside the Development environment.");
            return Task.FromResult(AuthenticateResult.Fail("Outside of Development, RequireTrustedProxy must be enabled."));
        }

        // 3. Shared Secret Validation (defense-in-depth between Traefik and Gateway)
        bool hasSecretConfigured = _precomputedSharedSecretBytes != null ||
                                   !string.IsNullOrWhiteSpace(forwardAuthOptions.SharedSecretKeyVaultRef);

        if (!_isDevelopment && !hasSecretConfigured)
        {
            Logger.LogWarning("ForwardAuth rejected: Outside of Development environment, a shared secret is strictly required.");
            return Task.FromResult(AuthenticateResult.Fail("Outside of Development, ForwardAuth requires a configured shared secret."));
        }

        if (hasSecretConfigured)
        {
            byte[]? expectedBytes = _precomputedSharedSecretBytes;
            if (expectedBytes == null && !string.IsNullOrWhiteSpace(forwardAuthOptions.SharedSecretKeyVaultRef) && _secretProvider != null)
            {
                expectedBytes = _secretProvider.GetSecretBytes(forwardAuthOptions.SharedSecretKeyVaultRef);
            }

            if (expectedBytes == null || expectedBytes.Length == 0)
            {
                Logger.LogWarning("ForwardAuth rejected: Configured shared secret could not be resolved.");
                return Task.FromResult(AuthenticateResult.Fail("Configured ForwardAuth shared secret could not be resolved."));
            }

            // The secret length is verified once at startup (AuthenticationModeOptionsValidator), not per request.
            var secretHeader = string.IsNullOrWhiteSpace(forwardAuthOptions.SharedSecretHeader)
                ? "X-Forwarded-Secret"
                : forwardAuthOptions.SharedSecretHeader;

            if (!headers.TryGetValue(secretHeader, out var actualSecretVal) ||
                string.IsNullOrWhiteSpace(actualSecretVal))
            {
                Logger.LogWarning("ForwardAuth rejected: Missing required shared secret header '{SecretHeader}'.", secretHeader);
                return Task.FromResult(AuthenticateResult.Fail("Missing ForwardAuth shared secret header."));
            }

            var actualSecretStr = actualSecretVal.ToString();
            if (actualSecretStr.Length > 512)
            {
                Logger.LogWarning("ForwardAuth rejected: Shared secret header length exceeds maximum permitted limit.");
                return Task.FromResult(AuthenticateResult.Fail("Invalid ForwardAuth shared secret."));
            }

            var actualBytes = Encoding.UTF8.GetBytes(actualSecretStr);
            var hashExpected = forwardAuthOptions.SharedSecretDigest is { } digest && ReferenceEquals(expectedBytes, _precomputedSharedSecretBytes)
                ? digest
                : SHA256.HashData(expectedBytes);
            var hashActual = SHA256.HashData(actualBytes);

            if (!CryptographicOperations.FixedTimeEquals(hashExpected, hashActual))
            {
                Logger.LogWarning("ForwardAuth rejected: Shared secret mismatch.");
                return Task.FromResult(AuthenticateResult.Fail("Invalid ForwardAuth shared secret."));
            }
        }

        // 4. Username (strict ASCII charset, exactly one header value) and user SID
        if (userVal.Count != 1 || !IsValidUsername(userVal[0]))
        {
            Logger.LogWarning("ForwardAuth rejected: the user header value is not a single valid username.");
            return Task.FromResult(AuthenticateResult.Fail("Invalid ForwardAuth username."));
        }

        var username = userVal[0]!;
        var userSid = $"S-1-5-21-FORWARD-USR-{username.ToLowerInvariant()}";
        if (forwardAuthOptions.IsUpstreamSidTrusted &&
            !string.IsNullOrWhiteSpace(forwardAuthOptions.UserSidHeader) &&
            headers.TryGetValue(forwardAuthOptions.UserSidHeader, out var sidVal) && !StringValues.IsNullOrEmpty(sidVal))
        {
            if (sidVal.Count != 1 || !SidFormat.IsCanonical(sidVal[0]))
            {
                Logger.LogWarning("ForwardAuth rejected: the user SID header value is not a single canonical SID.");
                return Task.FromResult(AuthenticateResult.Fail("Invalid ForwardAuth user SID."));
            }

            userSid = sidVal[0]!;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.PrimarySid, userSid),
            new("objectSid", userSid)
        };

        // Extract Email if present (strictly configured or default)
        var emailHeader = !string.IsNullOrWhiteSpace(forwardAuthOptions.EmailHeader)
            ? forwardAuthOptions.EmailHeader
            : "X-Forwarded-Email";

        if (headers.TryGetValue(emailHeader, out var emailVal) && !string.IsNullOrWhiteSpace(emailVal))
        {
            claims.Add(new Claim(ClaimTypes.Email, emailVal.ToString().Trim()));
        }

        // Extract Groups (strictly configured or default, comma or semicolon separated)
        var groupsHeader = !string.IsNullOrWhiteSpace(forwardAuthOptions.GroupsHeader)
            ? forwardAuthOptions.GroupsHeader
            : "X-Forwarded-Groups";

        string? groupsRaw = null;
        if (headers.TryGetValue(groupsHeader, out var customGroupsVal) && !string.IsNullOrWhiteSpace(customGroupsVal))
        {
            groupsRaw = customGroupsVal.ToString();
        }

        // RR-L2-02: header groups are read only from an explicitly trusted upstream and must be canonical SIDs; one
        // non-canonical value fails the request (a silently dropped group could drop a group-scoped restriction).
        if (forwardAuthOptions.IsUpstreamSidTrusted && !string.IsNullOrWhiteSpace(groupsRaw))
        {
            var groups = groupsRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var g in groups)
            {
                if (!SidFormat.IsCanonical(g))
                {
                    Logger.LogWarning("ForwardAuth rejected: a group header value is not a canonical SID.");
                    return Task.FromResult(AuthenticateResult.Fail("Invalid ForwardAuth group SID."));
                }

                claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(g));
            }
        }

        // Extract Tenant (SEC H-1: Only accept tenant header if TrustUpstreamTenant is explicitly enabled and in non-empty AllowedTenantIds)
        var tenantHeader = !string.IsNullOrWhiteSpace(forwardAuthOptions.TenantHeader)
            ? forwardAuthOptions.TenantHeader
            : "X-Forwarded-Tenant";

        string? tenant = null;
        if (forwardAuthOptions.TrustUpstreamTenant)
        {
            if (headers.TryGetValue(tenantHeader, out var customTenantVal) && !string.IsNullOrWhiteSpace(customTenantVal))
            {
                var candidateTenant = customTenantVal.ToString().Trim();
                if (forwardAuthOptions.AllowedTenantIds is { Count: > 0 } allowedTenants &&
                    allowedTenants.Contains(candidateTenant, StringComparer.OrdinalIgnoreCase))
                {
                    tenant = candidateTenant;
                }
                else
                {
                    Logger.LogWarning("ForwardAuth rejected: the tenant header value is not in ForwardAuth.AllowedTenantIds.");
                    return Task.FromResult(AuthenticateResult.Fail("Tenant not permitted for ForwardAuth."));
                }
            }
            else
            {
                Logger.LogWarning("ForwardAuth rejected: TrustUpstreamTenant is enabled but the tenant header is missing.");
                return Task.FromResult(AuthenticateResult.Fail("Missing required tenant header for ForwardAuth."));
            }
        }

        // DT-A-5: without a trusted upstream tenant header the principal carries no tenant claim; the resolver assigns the default
        // tenant (or leaves it unresolved). The ForwardAuth-specific default tenant is no longer read.
        if (tenant is not null)
        {
            claims.Add(new Claim("tenant_id", tenant));
            claims.Add(new Claim("tenant", tenant));
            claims.Add(new Claim("tid", tenant));
        }

        var hasRole = false;
        // Extract Roles (comma or semicolon separated; count and total length are bounded)
        var rolesHeader = !string.IsNullOrWhiteSpace(forwardAuthOptions.RolesHeader)
            ? forwardAuthOptions.RolesHeader
            : "X-Forwarded-Roles";

        string? rolesRaw = null;
        if (headers.TryGetValue(rolesHeader, out var customRolesVal) && !string.IsNullOrWhiteSpace(customRolesVal))
        {
            rolesRaw = customRolesVal.ToString();
        }

        if (!string.IsNullOrWhiteSpace(rolesRaw))
        {
            if (rolesRaw.Length > MaxRolesHeaderLength)
            {
                Logger.LogWarning("ForwardAuth rejected: the roles header exceeds the permitted length.");
                return Task.FromResult(AuthenticateResult.Fail("ForwardAuth roles header too long."));
            }

            var allowedRoles = forwardAuthOptions.AllowedRoleSet;
            var roles = rolesRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (roles.Length > MaxRoles)
            {
                Logger.LogWarning("ForwardAuth rejected: the roles header carries more than {Max} roles.", MaxRoles);
                return Task.FromResult(AuthenticateResult.Fail("ForwardAuth roles header carries too many roles."));
            }

            foreach (var r in roles)
            {
                // A tenant prefix is honoured only for the resolved, trusted header tenant.
                var colonIdx = r.LastIndexOf(':');
                var baseRole = colonIdx >= 0 ? r[(colonIdx + 1)..].Trim() : r;
                if (colonIdx >= 0)
                {
                    var prefix = r[..colonIdx].Trim();
                    if (tenant is null)
                    {
                        DiscardRole("tenant_prefix_untrusted");
                        continue;
                    }

                    if (!string.Equals(prefix, tenant, StringComparison.OrdinalIgnoreCase))
                    {
                        DiscardRole("tenant_mismatch");
                        continue;
                    }
                }

                // RR-L2-02: allowlist instead of denylist; administrative roles are never accepted from headers.
                if (IsHeaderForbiddenRole(baseRole))
                {
                    DiscardRole("forbidden");
                    continue;
                }

                if (!allowedRoles.Contains(baseRole))
                {
                    DiscardRole("not_allowed");
                    continue;
                }

                claims.Add(new Claim(ClaimTypes.Role, r));
                hasRole = true;
            }
        }

        // Map default enterprise user role if none assigned
        if (!hasRole)
        {
            claims.Add(new Claim(ClaimTypes.Role, "GatewayUser"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private const int MaxRolesHeaderLength = 2048;
    private const int MaxRoles = 32;

    // The log line and the counter carry a fixed reason only, never the role value.
    private void DiscardRole(string reason)
    {
        AuthMetrics.ForwardAuthRoleDiscarded.Add(1, new KeyValuePair<string, object?>("reason", reason));
        Logger.LogWarning("ForwardAuth: discarded a header role ({Reason}).", reason);
    }

    /// <summary>1..256 ASCII characters of <c>[A-Za-z0-9._@\-\\]</c>, compared by code point, no normalization.</summary>
    internal static bool IsValidUsername(string? value)
    {
        if (value is null || value.Length is 0 or > 256)
        {
            return false;
        }

        foreach (var c in value)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '@' or '-' or '\\';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static readonly HashSet<string> HeaderForbiddenRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClusterAdmin", "PlatformAdmin", "GatewayAdmin", "GovernanceAdmin", "SecurityAdmin",
        "PrivacyAdmin", "BreakGlassOperator", "DataProtectionOfficer"
    };

    /// <summary>RR-L2-02: Administrative roles are never accepted from proxy headers (even if listed in AllowedRoles).</summary>
    internal static bool IsHeaderForbiddenRole(string role) =>
        HeaderForbiddenRoles.Contains(role) ||
        (Autheris.Domain.Security.GatewayRoleExtensions.TryParseRole(role, out var canonical) &&
         canonical is Autheris.Domain.Security.GatewayRole.ClusterAdmin or Autheris.Domain.Security.GatewayRole.GovernanceAdmin);
}
