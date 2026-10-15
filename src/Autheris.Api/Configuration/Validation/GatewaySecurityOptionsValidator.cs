using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// WP-ARC-C-02: Validates security, authentication, audit retention, CORS, masking secret, and ReBAC seed options.
/// </summary>
public sealed class GatewaySecurityOptionsValidator : IValidateOptions<GatewayOptions>
{
    private readonly IHostEnvironment _environment;

    public GatewaySecurityOptionsValidator(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var failures = new List<string>();
        var isDev = _environment.IsDevelopment();

        if (opts.Audit.Retention.SecurityDays < 365)
        {
            failures.Add("Security violation: Audit:Retention:SecurityDays must be at least 365 days.");
        }

        if (opts.Audit.AuditLogRetentionDays < 365)
        {
            failures.Add("Security violation: Audit:AuditLogRetentionDays must be at least 365 days.");
        }

        if (opts.Authentication.RequireKerberosOnly && opts.Authentication.BasicAuth.Enabled)
        {
            failures.Add("Security conflict: BasicAuth must not be enabled when RequireKerberosOnly is set to true.");
        }

        if (!isDev &&
            opts.Authentication.ForwardAuth.Enabled &&
            string.IsNullOrWhiteSpace(opts.Authentication.ForwardAuth.SharedSecret) &&
            string.IsNullOrWhiteSpace(opts.Authentication.ForwardAuth.SharedSecretKeyVaultRef))
        {
            failures.Add("Security violation: Outside Development, ForwardAuth requires a configured SharedSecret or SharedSecretKeyVaultRef.");
        }

        if (!isDev &&
            opts.Authentication.ForwardAuth.Enabled &&
            !opts.Authentication.ForwardAuth.RequireTrustedProxy)
        {
            failures.Add("Security violation: RequireTrustedProxy must not be set to false when ForwardAuth is enabled outside Development.");
        }

#if AUTHERIS_TEST_AUTH
        if (!isDev && opts.Authentication.EnableTestAuthHandler)
        {
            failures.Add("Security violation: EnableTestAuthHandler may be true ONLY in the Development environment.");
        }
#else
        if (opts.Authentication.EnableTestAuthHandler)
        {
            failures.Add("Security violation: EnableTestAuthHandler may be true ONLY in the Development environment.");
        }
#endif

        if (!isDev && opts.IsAnonymousAccessAllowed)
        {
            failures.Add("Security violation: danger_allow_anonymous_access may be true ONLY in the Development environment.");
        }

        if (!isDev && opts.AreUntrustedCertificatesAllowed)
        {
            failures.Add("Security violation: danger_allow_untrusted_certificates may be true ONLY in the Development environment.");
        }

        if (!isDev && opts.GraphQL.TrustedOrigins.Contains("*"))
        {
            failures.Add("Security violation: TrustedOrigins '*' (wildcard CORS) is prohibited outside the Development environment for security reasons (CSRF protection).");
        }

        if (!isDev &&
            !opts.GraphQL.TrustedOrigins.All(o =>
                o == "*" ||
                (Uri.TryCreate(o, UriKind.Absolute, out var u) &&
                 string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))))
        {
            failures.Add("Security violation: Outside Development, TrustedOrigins must contain only HTTPS URLs.");
        }

        if (!isDev &&
            !(
                !string.IsNullOrWhiteSpace(opts.DataMasking.HmacSecretKeyVaultRef) &&
                opts.DataMasking.HmacSecretKeyVaultRef != "DEV_INSECURE_TEST_KEY_ONLY" &&
                opts.DataMasking.HmacSecretKeyVaultRef != "dev-only-hmac-salt-secure-fallback"
            ) &&
            !opts.IsInsecureTransportAllowed &&
            !opts.IsColumnMaskingDisabled)
        {
            failures.Add("NF-SEC-03 violation: Outside Development, HmacSecretKeyVaultRef must be a valid Key Vault secret reference.");
        }

        if (!isDev &&
            opts.Rebac.SeedTuples.Count > 0 &&
            opts.Rebac.SeedTuples.Any(t =>
                t.User.Contains("david", StringComparison.OrdinalIgnoreCase) ||
                t.Object.Contains("prod", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("Security critical: Demo or production-targeted ReBAC seed tuples are not permitted outside Development.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
