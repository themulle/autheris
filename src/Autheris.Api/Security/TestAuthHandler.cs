using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

namespace Autheris.Api.Security;

#if AUTHERIS_TEST_AUTH
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestAuth";

    private readonly IOptions<GatewayOptions>? _gatewayOptions;
    private readonly Microsoft.Extensions.Hosting.IHostEnvironment? _environment;

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<GatewayOptions>? gatewayOptions = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null)
        : base(options, logger, encoder)
    {
        _gatewayOptions = gatewayOptions;
        _environment = environment;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (_environment == null || !_environment.IsDevelopment())
        {
            return Task.FromResult(AuthenticateResult.Fail("TestAuthHandler is strictly prohibited outside the Development environment."));
        }

        var headers = Request.Headers;

        if (!headers.TryGetValue("X-Test-User-Sid", out var userSidVal) || string.IsNullOrWhiteSpace(userSidVal))
        {
            if (headers.TryGetValue("X-Test-AppId", out var appIdOnly) && !string.IsNullOrWhiteSpace(appIdOnly))
            {
                userSidVal = appIdOnly;
            }
            else if (_gatewayOptions?.Value.IsAnonymousAccessAllowed == true)
            {
                var anonTenant = headers.TryGetValue("X-Test-Tenant", out var aTenantVal) && !string.IsNullOrWhiteSpace(aTenantVal)
                    ? aTenantVal.ToString().Trim()
                    : (headers.TryGetValue("X-Tenant-ID", out var aTidVal) && !string.IsNullOrWhiteSpace(aTidVal)
                        ? aTidVal.ToString().Trim()
                        : (headers.TryGetValue("X-Tenant-Id", out var aTidAltVal) && !string.IsNullOrWhiteSpace(aTidAltVal)
                            ? aTidAltVal.ToString().Trim()
                            : TenantId.LegacySingleTenant.Value));

                // Insecure Getting-Started: Allow anonymous access with guest identity (no admin privilege escalation)
                List<Claim> anonClaims =
                [
                    new(ClaimTypes.PrimarySid, "S-1-5-21-DEV-ANONYMOUS"),
                    new(ClaimTypes.Name, "DEV_ANONYMOUS"),
                    new(ClaimTypes.NameIdentifier, "S-1-5-21-DEV-ANONYMOUS"),
                    new("objectSid", "S-1-5-21-DEV-ANONYMOUS"),
                    new(ClaimTypes.Role, "AnonymousUser"),
                    new("tenant_id", anonTenant),
                    new("tenant", anonTenant),
                    new("tid", anonTenant)
                ];
                var anonIdentity = new ClaimsIdentity(anonClaims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
                var anonPrincipal = new ClaimsPrincipal(anonIdentity);
                var anonTicket = new AuthenticationTicket(anonPrincipal, SchemeName);
                return Task.FromResult(AuthenticateResult.Success(anonTicket));
            }
            else
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
        }

        var userSid = userSidVal.ToString();

        var userName = headers.TryGetValue("X-Test-User-Name", out var userNameVal) && !string.IsNullOrWhiteSpace(userNameVal)
            ? userNameVal.ToString()
            : $"CORP\\{userSid}";

        List<Claim> claims =
        [
            new(ClaimTypes.PrimarySid, userSid),
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.NameIdentifier, userSid),
            new("objectSid", userSid)
        ];

        if (headers.TryGetValue("X-Test-AppId", out var appIdHeader) && !string.IsNullOrWhiteSpace(appIdHeader))
        {
            claims.Add(new Claim("appid", appIdHeader.ToString().Trim()));
        }

        if (headers.TryGetValue("X-Test-IdTyp", out var idTypHeader) && !string.IsNullOrWhiteSpace(idTypHeader))
        {
            claims.Add(new Claim("idtyp", idTypHeader.ToString().Trim()));
        }

        if (headers.TryGetValue("X-Test-Tier", out var tierHeader) && !string.IsNullOrWhiteSpace(tierHeader))
        {
            claims.Add(new Claim("tier", tierHeader.ToString().Trim()));
        }

        if (headers.TryGetValue("X-Test-Group-Sids", out var groupSidsVal) && !string.IsNullOrWhiteSpace(groupSidsVal))
        {
            var groups = groupSidsVal.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var g in groups)
            {
                claims.Add(new Claim(ClaimTypes.GroupSid, g));
            }
        }

        if (headers.TryGetValue("X-Test-Roles", out var rolesVal) && !string.IsNullOrWhiteSpace(rolesVal))
        {
            var roles = rolesVal.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var r in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }
        }
        else if (userSid.Contains("ADMIN", StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new Claim(ClaimTypes.Role, "ClusterAdmin"));
            claims.Add(new Claim(ClaimTypes.Role, "GovernanceAdmin"));
            claims.Add(new Claim(ClaimTypes.Role, "DataOwner"));
            claims.Add(new Claim(ClaimTypes.Role, "Developer"));
        }

        var testTenant = headers.TryGetValue("X-Test-Tenant", out var tenantVal) && !string.IsNullOrWhiteSpace(tenantVal)
            ? tenantVal.ToString().Trim()
            : (headers.TryGetValue("X-Tenant-ID", out var tidVal) && !string.IsNullOrWhiteSpace(tidVal)
                ? tidVal.ToString().Trim()
                : (headers.TryGetValue("X-Tenant-Id", out var tidAltVal) && !string.IsNullOrWhiteSpace(tidAltVal)
                    ? tidAltVal.ToString().Trim()
                    : TenantId.LegacySingleTenant.Value));

        claims.Add(new Claim("tenant_id", testTenant));
        claims.Add(new Claim("tenant", testTenant));
        claims.Add(new Claim("tid", testTenant));

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
#endif
