namespace Autheris.Api.Security;

using System;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Options.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class ScimAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IOptionsMonitor<IdentityRealmsOptions> _realmOptions;

    public ScimAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<IdentityRealmsOptions> realmOptions)
        : base(options, logger, encoder)
    {
        _realmOptions = realmOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader) || string.IsNullOrWhiteSpace(authHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var headerValue = authHeader.ToString().Trim();
        if (!headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid Authorization header scheme. Expected 'Bearer'."));
        }

        var token = headerValue["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.Fail("Empty bearer token."));
        }

        // Extract realmId from path if available: /scim/v2/{realmId}/...
        var path = Request.Path.Value ?? string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? realmId = null;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "scim", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[i + 1], "v2", StringComparison.OrdinalIgnoreCase) &&
                i + 2 < segments.Length)
            {
                realmId = segments[i + 2];
                break;
            }
        }

        realmId ??= _realmOptions.CurrentValue.DefaultRealm ?? "workforce-entra";

        var scimOpts = _realmOptions.CurrentValue.Scim;
        var primary = scimOpts.PrimarySecret;
        var secondary = scimOpts.SecondarySecret;

        if (string.IsNullOrWhiteSpace(primary) && string.IsNullOrWhiteSpace(secondary))
        {
            return Task.FromResult(AuthenticateResult.Fail("SCIM authentication is not configured with secrets."));
        }

        // Constant-time validation
        bool isValid = ValidateSecretConstantTime(token, primary) || ValidateSecretConstantTime(token, secondary);

        if (!isValid)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid SCIM bearer token or standard user token supplied on SCIM endpoint."));
        }

        // Security review: the realm comes from the URL. When realms are configured, only realms provisioned via SCIM
        // can be targeted. NOTE: the SCIM secrets are global (not bound to a realm), so a secret holder can still
        // provision every SCIM realm; per-realm secrets would need a configuration extension.
        if (!IsScimRealm(_realmOptions.CurrentValue, realmId))
        {
            return Task.FromResult(AuthenticateResult.Fail("SCIM provisioning is not enabled for the requested realm."));
        }

        // Security review: the secret holder gets only the dedicated provisioner role, never ClusterAdmin.
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, $"scim-client:{realmId}"),
            new Claim(ClaimTypes.NameIdentifier, $"scim-client:{realmId}"),
            new Claim(ClaimTypes.PrimarySid, $"scim-client:{realmId}"),
            new Claim("sub", $"scim-client:{realmId}"),
            new Claim("autheris:realm", realmId),
            new Claim(ClaimTypes.Role, GatewayPolicies.ScimProvisionerRole)
        };

        var identity = new ClaimsIdentity(claims, GatewayAuthSchemes.Scim);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, GatewayAuthSchemes.Scim);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// True when no realms are configured (legacy single-realm setup) or the realm is configured with SCIM provisioning.
    /// </summary>
    internal static bool IsScimRealm(IdentityRealmsOptions options, string realmId)
    {
        if (options.Realms.Count == 0)
        {
            return true;
        }

        return options.Realms.TryGetValue(realmId, out var realm) &&
               realm.Provisioning == Autheris.Domain.Identity.RealmProvisioningMode.Scim;
    }

    private static bool ValidateSecretConstantTime(string input, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;

        var inputBytes = Encoding.UTF8.GetBytes(input);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        if (inputBytes.Length != expectedBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(inputBytes, expectedBytes);
    }
}
