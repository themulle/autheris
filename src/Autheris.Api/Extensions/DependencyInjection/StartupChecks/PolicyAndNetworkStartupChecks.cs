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
internal static class PolicyAndNetworkStartupChecks
{
    internal static void Egress(StartupCheckContext context)
    {
        var options = context.Options;

        // SEC E-01: the egress allowlist is validated in every environment; invalid or too broad entries abort the start.
        var egressErrors = EgressAllowlist.Validate(options.Egress);
        if (egressErrors.Count > 0)
        {
            throw new ValidationException(
                "Egress allowlist configuration error (Gateway:Egress): IPv4 networks at least /8, IPv6 at least /32, no overlap with " +
                "loopback/link-local/metadata/CGNAT/multicast/IPv4-mapped ranges:\n  - " + string.Join("\n  - ", egressErrors));
        }
    }

    internal static void CasbinModel(StartupCheckContext context)
    {
        var options = context.Options;

        // POL-1 / F-7: Validate Casbin ModelPath whenever configured, or require it when Enabled
        if (!string.IsNullOrWhiteSpace(options.Casbin.ModelPath))
        {
            if (!File.Exists(options.Casbin.ModelPath))
            {
                throw new ValidationException($"Casbin model file '{options.Casbin.ModelPath}' was not found.");
            }
            if (new FileInfo(options.Casbin.ModelPath).Length == 0)
            {
                throw new ValidationException($"Casbin Model-Datei '{options.Casbin.ModelPath}' ist leer.");
            }

            try
            {
                CasbinModelContract.Verify(File.ReadAllText(options.Casbin.ModelPath));
            }
            catch (CasbinModelValidationException ex)
            {
                throw new ValidationException($"Casbin-Modell '{options.Casbin.ModelPath}' does not satisfy the gateway contract: {string.Join("; ", ex.Violations)}", ex);
            }
        }
        else if (options.Casbin.Enabled)
        {
            // SR15-50: Embedded default model is verified and allowed when ModelPath is not configured
            try
            {
                CasbinModelContract.Verify(CasbinEnforcementService.DefaultModelText);
            }
            catch (CasbinModelValidationException ex)
            {
                throw new ValidationException($"Embedded default Casbin model does not satisfy the gateway contract: {string.Join("; ", ex.Violations)}", ex);
            }
        }
    }

    internal static void CasbinPolicy(StartupCheckContext context)
    {
        var options = context.Options;

        // POL-1 / R-POL-5: If Casbin is enabled, PolicyPath must exist, not be empty, and contain valid 'p' rules
        if (options.Casbin.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.Casbin.PolicyPath))
            {
                throw new ValidationException("Casbin is enabled (Gateway:Casbin:Enabled = true), but Casbin:PolicyPath is not configured.");
            }
            if (!File.Exists(options.Casbin.PolicyPath))
            {
                throw new ValidationException($"Casbin is enabled, but policy file '{options.Casbin.PolicyPath}' was not found.");
            }
            if (new FileInfo(options.Casbin.PolicyPath).Length == 0)
            {
                throw new ValidationException($"Casbin is enabled, but policy file '{options.Casbin.PolicyPath}' is empty.");
            }

            // R-POL-5: Test-parse policy file at startup and verify that at least one 'p' rule exists (fail-closed)
            try
            {
                var policyText = File.ReadAllText(options.Casbin.PolicyPath);
                int totalPRules = CasbinEnforcementService.ValidatePolicyFile(policyText, modelSupportsWildcardTenant: true);
                if (totalPRules == 0)
                {
                    throw new ValidationException($"Casbin is enabled, but policy file '{options.Casbin.PolicyPath}' contains no valid 'p' rules.");
                }
            }
            catch (ValidationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ValidationException($"Casbin is enabled, but policy file '{options.Casbin.PolicyPath}' is invalid: {ex.Message}", ex);
            }
        }
    }

    internal static void ReverseProxy(StartupCheckContext context)
    {
        var options = context.Options;

        // RR-L1-01: Validate ReverseProxy.KnownNetworks and KnownProxies against invalid formats and wildcard spoofing
        if (options.ReverseProxy.Enabled)
        {
            foreach (var netStr in options.ReverseProxy.GetEffectiveKnownNetworks())
            {
                if (!System.Net.IPNetwork.TryParse(netStr, out var network))
                {
                    throw new ValidationException($"Configuration error ReverseProxy.KnownNetworks: invalid IP network '{netStr}'.");
                }

                if (network.PrefixLength == 0)
                {
                    throw new ValidationException($"Sicherheitsverletzung: ReverseProxy.KnownNetworks '{netStr}' ist ein Wildcard-Netzwerk (/0). Wildcard-Proxies sind verboten!");
                }
            }

            foreach (var proxyStr in options.ReverseProxy.KnownProxies)
            {
                if (!System.Net.IPAddress.TryParse(proxyStr, out _))
                {
                    throw new ValidationException($"Configuration error ReverseProxy.KnownProxies: invalid IP address '{proxyStr}'.");
                }
            }
        }
    }
}
