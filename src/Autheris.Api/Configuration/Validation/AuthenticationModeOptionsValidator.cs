using System.Text;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// Validates <c>Gateway:Authentication:Mode</c> (Direct, Upstream, Both) and the ForwardAuth settings it implies. Every violation
/// aborts the start with a message that names the configuration key and never a secret value or an identity value. The shape rules
/// (explicit mode, Direct versus ForwardAuth, header names, tenant allowlist, alias conflict, Basic SIDs) apply in every environment;
/// the trust rules (peer lists, shared secret length, scheme set) apply outside Development. The shared secret (plain or Key Vault)
/// is resolved here, once, so an unreachable or short secret stops the start instead of failing requests.
/// </summary>
public sealed class AuthenticationModeOptionsValidator(
    IHostEnvironment environment,
    IKeyVaultSecretProvider? secretProvider = null,
    ILogger<AuthenticationModeOptionsValidator>? logger = null) : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var auth = options.Authentication;
        var fwd = auth.ForwardAuth;
        var mode = auth.EffectiveMode;
        var failures = new List<string>();

        if (fwd.TrustUpstreamGroupSids is not null)
        {
            logger?.LogWarning(
                "Gateway:Authentication:ForwardAuth:TrustUpstreamGroupSids is a legacy alias and will be removed; use Gateway:Authentication:ForwardAuth:TrustUpstreamSids.");
            if (fwd.TrustUpstreamSids is not null && fwd.TrustUpstreamSids != fwd.TrustUpstreamGroupSids)
            {
                failures.Add("Security violation: Gateway:Authentication:ForwardAuth:TrustUpstreamGroupSids (legacy alias) and TrustUpstreamSids are both set with different values. Remove TrustUpstreamGroupSids.");
            }
        }

        if (fwd.TrustUpstreamTenant && fwd.AllowedTenantIds.Count == 0)
        {
            failures.Add("Security violation: Gateway:Authentication:ForwardAuth:TrustUpstreamTenant requires a non-empty Gateway:Authentication:ForwardAuth:AllowedTenantIds.");
        }

        for (var i = 0; i < auth.BasicAuth.Users.Count && auth.BasicAuth.Enabled; i++)
        {
            var user = auth.BasicAuth.Users[i];
            if ((user.Sid is not null && !SidFormat.IsCanonical(user.Sid)) || user.GroupSids.Any(g => !SidFormat.IsCanonical(g)))
            {
                failures.Add($"Security violation: Gateway:Authentication:BasicAuth:Users:{i} has a Sid or GroupSids entry that is not a canonical SID (S-1-...).");
            }
        }

        if (fwd.Enabled && auth.Mode == AuthenticationMode.Unset)
        {
            failures.Add("Security violation: Gateway:Authentication:Mode must be set explicitly (Upstream or Both) when Gateway:Authentication:ForwardAuth:Enabled is true.");
        }

        if (fwd.Enabled && mode == AuthenticationMode.Direct && auth.Mode == AuthenticationMode.Direct)
        {
            failures.Add("Security violation: Gateway:Authentication:Mode=Direct requires Gateway:Authentication:ForwardAuth:Enabled=false.");
        }

        if (!fwd.Enabled && mode is AuthenticationMode.Upstream or AuthenticationMode.Both)
        {
            failures.Add($"Security violation: Gateway:Authentication:Mode={mode} requires Gateway:Authentication:ForwardAuth:Enabled=true.");
        }

        if (fwd.Enabled)
        {
            ValidateHeaderNames(fwd, failures);
        }

        if (!environment.IsDevelopment())
        {
            ValidateOutsideDevelopment(auth, fwd, mode, failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateHeaderNames(ForwardAuthOptions fwd, List<string> failures)
    {
        (string Key, string? Value)[] names =
        [
            ("UserHeader", fwd.UserHeader), ("EmailHeader", fwd.EmailHeader), ("GroupsHeader", fwd.GroupsHeader),
            ("RolesHeader", fwd.RolesHeader), ("TenantHeader", fwd.TenantHeader), ("SharedSecretHeader", fwd.SharedSecretHeader)
        ];
        foreach (var (key, value) in names.Where(n => string.IsNullOrWhiteSpace(n.Value)))
        {
            failures.Add($"Security violation: Gateway:Authentication:ForwardAuth:{key} must not be empty or whitespace.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in names.Append((Key: "UserSidHeader", Value: fwd.UserSidHeader)).Where(n => !string.IsNullOrWhiteSpace(n.Value)))
        {
            if (!seen.Add(value!))
            {
                failures.Add($"Security violation: Gateway:Authentication:ForwardAuth:{key} duplicates another ForwardAuth header name; every header name must be distinct.");
            }
        }

        if (fwd.UserSidHeader is not null && string.IsNullOrWhiteSpace(fwd.UserSidHeader))
        {
            failures.Add("Security violation: Gateway:Authentication:ForwardAuth:UserSidHeader must not be whitespace.");
        }
    }

    private void ValidateOutsideDevelopment(AuthenticationOptions auth, ForwardAuthOptions fwd, AuthenticationMode mode, List<string> failures)
    {
        var directScheme = auth.EntraId.Enabled || auth.Adfs.Enabled || auth.BasicAuth.Enabled || auth.EnableKerberos;
        if (mode == AuthenticationMode.Direct && !directScheme)
        {
            failures.Add("Security violation: Gateway:Authentication:Mode=Direct requires at least one enabled scheme (EntraId, Adfs, BasicAuth or EnableKerberos).");
        }

        if (mode == AuthenticationMode.Upstream && directScheme)
        {
            failures.Add("Security violation: Gateway:Authentication:Mode=Upstream requires EntraId, Adfs, BasicAuth and EnableKerberos to be disabled (use Mode=Both to combine them).");
        }

        if (mode is not (AuthenticationMode.Upstream or AuthenticationMode.Both) || !fwd.Enabled)
        {
            return;
        }

        if (!fwd.RequireTrustedProxy)
        {
            failures.Add("Security violation: Gateway:Authentication:ForwardAuth:RequireTrustedProxy must be true outside Development.");
        }

        if (fwd.TrustedProxies.Count == 0 && fwd.TrustedNetworks.Count == 0)
        {
            failures.Add("Security violation: Gateway:Authentication:ForwardAuth:TrustedProxies or TrustedNetworks must list the proxy peers (ReverseProxy lists do not apply to identity headers).");
        }

        ValidateSecret(fwd, failures);
    }

    private void ValidateSecret(ForwardAuthOptions fwd, List<string> failures)
    {
        int? length = null;
        if (!string.IsNullOrWhiteSpace(fwd.SharedSecret))
        {
            length = Encoding.UTF8.GetByteCount(fwd.SharedSecret);
        }
        else if (!string.IsNullOrWhiteSpace(fwd.SharedSecretKeyVaultRef))
        {
            try
            {
                length = secretProvider?.GetSecretBytes(fwd.SharedSecretKeyVaultRef)?.Length;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                length = null; // the exception text could echo the reference or the value: report the key only
            }

            if (length is null or 0)
            {
                failures.Add("Security violation: Gateway:Authentication:ForwardAuth:SharedSecretKeyVaultRef could not be resolved to a non-empty secret at startup.");
                return;
            }
        }

        if (length is null or 0)
        {
            failures.Add("Security violation: Gateway:Authentication:ForwardAuth:SharedSecret or SharedSecretKeyVaultRef is required for Mode=Upstream/Both outside Development.");
        }
        else if (length < SecretKeyRequirements.MinimumKeyBytes)
        {
            failures.Add($"Security violation: the ForwardAuth shared secret must be at least {SecretKeyRequirements.MinimumKeyBytes} bytes (Gateway:Authentication:ForwardAuth:SharedSecret or SharedSecretKeyVaultRef).");
        }
    }
}
