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
internal static class GraphQlAndSwitchStartupChecks
{
    internal static void DevelopmentInContainer(StartupCheckContext context)
    {
        var options = context.Options;
        var getEnvironmentVariable = context.GetEnvironmentVariable;

        // SEC C-04: Development disables most protections. Inside a container this is almost always an
        // accidentally shipped image default, so it requires an explicit opt-in.
        if (context.IsDevelopment &&
            GatewayStartupValidator.IsTruthy(getEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")) &&
            !options.AllowDevelopmentInContainer &&
            !GatewayStartupValidator.IsTruthy(getEnvironmentVariable("AUTHERIS_ALLOW_DEV_IN_CONTAINER")))
        {
            throw new ValidationException(
                "Security violation: ASPNETCORE_ENVIRONMENT=Development in a container (DOTNET_RUNNING_IN_CONTAINER=true) " +
                "is allowed only with an explicit opt-in (Gateway:AllowDevelopmentInContainer=true or AUTHERIS_ALLOW_DEV_IN_CONTAINER=true). " +
                "Use ASPNETCORE_ENVIRONMENT=Production for production; use docker-compose.dev.yml for local testing.");
        }
    }

    internal static void GraphQlTrustedDocuments(StartupCheckContext context)
    {
        var options = context.Options;

        // SEC H-08: PersistedQueriesOnly needs a trusted document store; otherwise the switch would be ineffective.
        // Issue #281: with app-scoped trusted documents the global directory may be empty (header-less requests are then rejected).
        var appScoped = options.GraphQL.AppScopedTrustedDocuments;
        if (appScoped.Enabled && !options.GraphQL.PersistedQueriesOnly)
        {
            throw new ValidationException(
                "Security violation: GraphQL.AppScopedTrustedDocuments.Enabled=true requires GraphQL.PersistedQueriesOnly=true; " +
                "otherwise ad-hoc documents would be accepted next to the app-scoped allowlist.");
        }

        if (options.GraphQL.PersistedQueriesOnly)
        {
            var dir = options.GraphQL.TrustedDocumentsDirectory;
            var directoryOptional = appScoped.Enabled && string.IsNullOrWhiteSpace(dir);
            if (!directoryOptional && (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.GetFullPath(dir))))
            {
                throw new ValidationException(
                    "Security violation: GraphQL.PersistedQueriesOnly=true requires an existing GraphQL.TrustedDocumentsDirectory " +
                    "containing the approved operations (*.graphql / *.gql). Without a document store, the switch would have no effect.");
            }
        }
    }

    internal static void FederationSigning(StartupCheckContext context)
    {
        var options = context.Options;

        // SR-P2-02 / SEC-GQL-02: Federation context header signing key must be configured outside Development
        if (!context.IsDevelopment && options.Federation.Enabled && options.Federation.SignContextHeaders)
        {
            if (string.IsNullOrWhiteSpace(options.Federation.SigningKey) ||
                string.Equals(options.Federation.SigningKey, "autheris-federation-default-secret", StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException(
                    "Security violation: Federation context header signing is enabled (Federation:SignContextHeaders = true), " +
                    "but Federation:SigningKey is not configured or uses the insecure default secret. A secure key of at least 32 bytes is required outside Development.");
            }

            Autheris.Application.Security.SecretKeyRequirements.EnsureMinimumLength(
                System.Text.Encoding.UTF8.GetBytes(options.Federation.SigningKey),
                "Gateway:Federation:SigningKey",
                isDevelopment: false);
        }
    }

    internal static void SecuritySwitchReport(StartupCheckContext context)
    {
        var options = context.Options;
        var logger = context.Logger;

        // Security switch semantics: DANGER = blocked outside Development (see below), WARN = permitted everywhere
        // but reported loudly at startup, regular options = no message (see GatewayOptions.GetAllActiveBypasses).
        var dangerBypasses = context.DangerBypasses;
        var warnings = context.Warnings;
        if (options.IsWebSqlGovernanceBypassed)
        {
            // X1: there is one WebSQL code path and the former bypass no longer exists. The flag is inert but still a DANGER switch, so it
            // fails the start outside Development (below); in Development it is reported once here, never per statement.
            const string message = "[DANGER] danger_bypass_websql_governance has no effect since the AST compiler cutover; every WebSQL statement is governed. Remove the setting.";
            if (logger is not null)
            {
                logger.LogWarning(message);
            }
            else
            {
                Console.WriteLine($"[Autheris] WARNING: {message}");
            }
        }

        if (dangerBypasses.Count > 0)
        {
            var bypasses = string.Join("\n  - ", dangerBypasses);
            Console.WriteLine(
                $"\n================================================================================\n" +
                $"⚠️⚠️⚠️  INSECURE GETTING-STARTED CONFIGURATION DETECTED  ⚠️⚠️⚠️\n" +
                $"The following security bypasses are currently ACTIVE:\n  - {bypasses}\n" +
                $"NEVER USE THESE INSECURE SETTINGS IN PRODUCTION ENVIRONMENTS!\n" +
                $"================================================================================\n");
        }

        if (warnings.Count > 0)
        {
            // WARN entries are permitted in Production; they are reported but never abort startup.
            Console.WriteLine(
                "[Autheris] WARNING: security-relevant settings are active (permitted, review regularly):\n  - " +
                string.Join("\n  - ", warnings));
        }

        GatewayStartupValidator.ReportTenancyAndCatalogSettings(options, logger);

        if (!context.IsDevelopment && !options.VirtualFilters.RequireApproval)
        {
            Console.WriteLine("[Autheris] WARNING: VirtualFilters.RequireApproval is false outside Development. Four-eyes principle is recommended for production.");
        }
    }

    internal static void OperationalInvariants(StartupCheckContext context)
    {
        var options = context.Options;
        var isDevEnvironment = context.IsDevelopment;

        if (options.WebSql.AllowDml && options.WebSql.DmlWriterRoles.Count == 0)
        {
            throw new ValidationException(
                "Configuration error: WebSql.AllowDml=true requires at least one role in WebSql.DmlWriterRoles " +
                "(SEC M-20: DML is allowed only for explicitly authorized roles).");
        }

        if (!context.IsDevelopment && options.IsQuickstartProfile)
        {
            throw new ValidationException("Security violation: The GettingStarted profile 'Quickstart' may be active ONLY in the Development environment.");
        }

        if (options.HighAvailability.ShutdownTimeoutSeconds < options.HighAvailability.QueryTimeoutSeconds + 10)
        {
            throw new ValidationException("NF-HA-01 violation: ShutdownTimeoutSeconds must be at least 10s greater than QueryTimeoutSeconds.");
        }

        if (options.HighAvailability.TerminationGracePeriodSeconds < options.HighAvailability.DrainDelaySeconds + options.HighAvailability.ShutdownTimeoutSeconds + 10)
        {
            throw new ValidationException("NF-HA-01 violation: TerminationGracePeriodSeconds must be greater than DrainDelay + ShutdownTimeout + 10s.");
        }

        var devErrors = DevOptionsValidator.Validate(options, isDevEnvironment);
        if (devErrors.Count > 0)
        {
            throw new ValidationException(string.Join("\n", devErrors));
        }
    }
}
