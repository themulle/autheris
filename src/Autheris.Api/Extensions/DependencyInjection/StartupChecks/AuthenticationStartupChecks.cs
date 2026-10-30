namespace Autheris.Api.Extensions.DependencyInjection.StartupChecks;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Autheris.Api.Configuration;
using Autheris.Api.Security;
using Autheris.Application.Governance;
using Autheris.Application.Policy.Lint;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Dialects;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>Ordered startup checks of one area; each method is one contiguous section of the former <c>ValidateStartup</c> body (ARCH-MNT-12).</summary>
internal static class AuthenticationStartupChecks
{
    internal static void BasicAuthSession(StartupCheckContext context)
    {
        var options = context.Options;
        var environment = context.Environment;

        // F-AUTH-DX: session cookie guards (environment allowlist, never Production, no wildcard CORS, shared keys)
        var basicSessionErrors = Autheris.Api.Security.BasicAuthSession.Validate(options, environment);
        if (basicSessionErrors.Count > 0)
        {
            throw new ValidationException(string.Join("\n", basicSessionErrors));
        }
    }

    internal static void BasicAuthUsers(StartupCheckContext context)
    {
        var options = context.Options;

        // R2-1 / N-1: Basic-auth users need a password; SIDs must not contain ':' or start with 'ITSM'
        if (options.Authentication.BasicAuth.Enabled)
        {
            var sidErrors = Autheris.Api.Security.BasicAuthSession.ValidateUsers(options.Authentication.BasicAuth);
            if (sidErrors.Count > 0)
            {
                throw new ValidationException(string.Join("\n", sidErrors));
            }
        }
    }

    internal static void ForwardAuth(StartupCheckContext context)
    {
        var options = context.Options;

        if (options.Authentication.RequireKerberosOnly && options.Authentication.BasicAuth.Enabled)
        {
            throw new ValidationException("Security conflict: BasicAuth must not be enabled when RequireKerberosOnly is set to true.");
        }

        if (!context.IsDevelopment && options.Authentication.ForwardAuth.Enabled)
        {
            var hasSecret = !string.IsNullOrWhiteSpace(options.Authentication.ForwardAuth.SharedSecret) ||
                            !string.IsNullOrWhiteSpace(options.Authentication.ForwardAuth.SharedSecretKeyVaultRef);
            if (!hasSecret)
            {
                throw new ValidationException("Security violation: Outside Development, ForwardAuth requires a configured SharedSecret or SharedSecretKeyVaultRef.");
            }

            if (!string.IsNullOrWhiteSpace(options.Authentication.ForwardAuth.SharedSecret) &&
                System.Text.Encoding.UTF8.GetByteCount(options.Authentication.ForwardAuth.SharedSecret) < 32)
            {
                throw new ValidationException("Security violation: Outside the development environment, ForwardAuth.SharedSecret must be at least 32 bytes long.");
            }

            if (!options.Authentication.ForwardAuth.RequireTrustedProxy)
            {
                throw new ValidationException("Security violation: RequireTrustedProxy must not be set to false when ForwardAuth is enabled outside Development.");
            }

            if (options.Authentication.ForwardAuth.TrustedNetworks != null)
            {
                foreach (var netStr in options.Authentication.ForwardAuth.TrustedNetworks)
                {
                    if (!System.Net.IPNetwork.TryParse(netStr, out var network))
                    {
                        throw new ValidationException($"Configuration error ForwardAuth.TrustedNetworks: invalid IP network '{netStr}'.");
                    }

                    if (network.PrefixLength == 0)
                    {
                        throw new ValidationException($"Sicherheitsverletzung (API-8): ForwardAuth.TrustedNetworks '{netStr}' ist ein Wildcard-Netzwerk (/0). Wildcard-Netzwerke sind verboten!");
                    }
                }
            }

            if (options.Authentication.ForwardAuth.TrustedProxies != null)
            {
                foreach (var proxyStr in options.Authentication.ForwardAuth.TrustedProxies)
                {
                    if (!System.Net.IPAddress.TryParse(proxyStr, out _))
                    {
                        throw new ValidationException($"Configuration error ForwardAuth.TrustedProxies: invalid IP address '{proxyStr}'.");
                    }
                }
            }
        }
    }

    internal static void ProductionAuthentication(StartupCheckContext context)
    {
        var options = context.Options;

        if (context.IsDevelopment)
        {
            return;
        }

        if (options.Authentication.BasicAuth.Enabled)
        {
            if (options.Authentication.BasicAuth.Users.Any(u => string.IsNullOrWhiteSpace(u.Password) || (!u.Password.StartsWith("$pbkdf2$", StringComparison.OrdinalIgnoreCase) && !u.Password.StartsWith("$argon2id$", StringComparison.OrdinalIgnoreCase))))
            {
                throw new ValidationException("Security violation (DEP-14): Outside Development, BasicAuth passwords must be stored as a hash ($argon2id$... or $pbkdf2$...). Plaintext passwords are prohibited.");
            }

            // RR-L2-03: weak work factors (e.g. $pbkdf2$1$...) are rejected at boot.
            var minIterations = Math.Max(210_000, options.Authentication.BasicAuth.MinimumPbkdf2Iterations);
            if (options.Authentication.BasicAuth.Users.Any(u =>
                    u.Password.StartsWith("$pbkdf2$", StringComparison.OrdinalIgnoreCase) &&
                    (u.Password.Split('$') is not { Length: 5 } parts ||
                     !int.TryParse(parts[2], out var iterations) ||
                     iterations < minIterations)))
            {
                throw new ValidationException($"Security violation: Outside Development, BasicAuth PBKDF2 hashes must use at least {minIterations} iterations.");
            }

            // API-05: truncated hash / short salt (copy-paste, templating, line wrapping) must fail at boot, not authenticate weakly.
            if (options.Authentication.BasicAuth.Users.Any(u => !PasswordHasher.HasWellFormedSaltAndHash(u.Password)))
            {
                throw new ValidationException("Security violation (API-05): Outside Development, BasicAuth hashes must carry a salt of at least 16 bytes and a 32-byte hash (truncated value?).");
            }
        }

        // SEC M-02: Fail-closed when JWT is active without audience or issuer.
        if (options.Authentication.EntraId.Enabled &&
            ((string.IsNullOrWhiteSpace(options.Authentication.EntraId.Audience) && string.IsNullOrWhiteSpace(options.Authentication.EntraId.ClientId)) ||
             string.IsNullOrWhiteSpace(options.Authentication.EntraId.TenantId)))
        {
            throw new ValidationException("Security violation: Outside Development, EntraId requires Audience or ClientId, plus TenantId (issuer), to be configured.");
        }

        if (options.Authentication.Adfs.Enabled && (string.IsNullOrWhiteSpace(options.Authentication.Adfs.Audience) || string.IsNullOrWhiteSpace(options.Authentication.Adfs.Authority)))
        {
            throw new ValidationException("Security violation: Outside Development, Adfs.Audience and Authority must be configured.");
        }
    }
}
