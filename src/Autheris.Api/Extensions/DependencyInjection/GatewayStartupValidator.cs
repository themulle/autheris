namespace Autheris.Api.Extensions.DependencyInjection;

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

public static class GatewayStartupValidator
{
    public static void ValidateStartup(
        GatewayOptions options,
        IHostEnvironment environment,
        Func<string, string?>? getEnvVar = null,
        ILogger? logger = null,
        ISqlDialectRegistry? dialectRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var registry = dialectRegistry ?? SqlDialectRegistry.Default;
        var getEnvironmentVariable = getEnvVar ?? Environment.GetEnvironmentVariable;
        bool isDevEnvironment = environment.IsDevelopment();

        ValidateObjectRecursively(options);

        _ = registry;
        ValidateOracleRuntime(options, environment, logger);
        ValidateSqlServerWindowsAuth(options, environment, getEnvironmentVariable, logger);
        ValidateDatabricks(options, environment);
        ValidateDefaultSchemas(options);
        ValidateTenantCollisions(options, environment, logger);
        ValidateWebhookSubscriptions(options);
        ValidateQueryCostOptions(options.QueryCost, environment);

        // SEC E-01: the egress allowlist is validated in every environment; invalid or too broad entries abort the start.
        var egressErrors = EgressAllowlist.Validate(options.Egress);
        if (egressErrors.Count > 0)
        {
            throw new ValidationException(
                "Egress allowlist configuration error (Gateway:Egress): IPv4 networks at least /8, IPv6 at least /32, no overlap with " +
                "loopback/link-local/metadata/CGNAT/multicast/IPv4-mapped ranges:\n  - " + string.Join("\n  - ", egressErrors));
        }

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

        // SEC C-04: Development disables most protections. Inside a container this is almost always an
        // accidentally shipped image default, so it requires an explicit opt-in.
        if (environment.IsDevelopment() &&
            IsTruthy(getEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")) &&
            !options.AllowDevelopmentInContainer &&
            !IsTruthy(getEnvironmentVariable("AUTHERIS_ALLOW_DEV_IN_CONTAINER")))
        {
            throw new ValidationException(
                "Security violation: ASPNETCORE_ENVIRONMENT=Development in a container (DOTNET_RUNNING_IN_CONTAINER=true) " +
                "is allowed only with an explicit opt-in (Gateway:AllowDevelopmentInContainer=true or AUTHERIS_ALLOW_DEV_IN_CONTAINER=true). " +
                "Use ASPNETCORE_ENVIRONMENT=Production for production; use docker-compose.dev.yml for local testing.");
        }

        // SEC H-08: PersistedQueriesOnly needs a trusted document store; otherwise the switch would be ineffective.
        if (options.GraphQL.PersistedQueriesOnly)
        {
            var dir = options.GraphQL.TrustedDocumentsDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.GetFullPath(dir)))
            {
                throw new ValidationException(
                    "Security violation: GraphQL.PersistedQueriesOnly=true requires an existing GraphQL.TrustedDocumentsDirectory " +
                    "containing the approved operations (*.graphql / *.gql). Without a document store, the switch would have no effect.");
            }
        }

        // SR-P2-02 / SEC-GQL-02: Federation context header signing key must be configured outside Development
        if (!environment.IsDevelopment() && options.Federation.Enabled && options.Federation.SignContextHeaders)
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

        // Security switch semantics: DANGER = blocked outside Development (see below), WARN = permitted everywhere
        // but reported loudly at startup, regular options = no message (see GatewayOptions.GetAllActiveBypasses).
        var dangerBypasses = options.GetActiveDangerBypasses();
        var warnings = options.GetActiveWarnings();
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

        ReportTenancyAndCatalogSettings(options, logger);

        if (!environment.IsDevelopment() && !options.VirtualFilters.RequireApproval)
        {
            Console.WriteLine("[Autheris] WARNING: VirtualFilters.RequireApproval is false outside Development. Four-eyes principle is recommended for production.");
        }

        if (options.WebSql.AllowDml && options.WebSql.DmlWriterRoles.Count == 0)
        {
            throw new ValidationException(
                "Configuration error: WebSql.AllowDml=true requires at least one role in WebSql.DmlWriterRoles " +
                "(SEC M-20: DML is allowed only for explicitly authorized roles).");
        }

        if (!environment.IsDevelopment() && options.IsQuickstartProfile)
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

        // F-AUTH-DX: session cookie guards (environment allowlist, never Production, no wildcard CORS, shared keys)
        var basicSessionErrors = BasicAuthSession.Validate(options, environment);
        if (basicSessionErrors.Count > 0)
        {
            throw new ValidationException(string.Join("\n", basicSessionErrors));
        }

        // R2-1 / N-1: Basic-auth users need a password; SIDs must not contain ':' or start with 'ITSM'
        if (options.Authentication.BasicAuth.Enabled)
        {
            var sidErrors = BasicAuthSession.ValidateUsers(options.Authentication.BasicAuth);
            if (sidErrors.Count > 0)
            {
                throw new ValidationException(string.Join("\n", sidErrors));
            }
        }

        // Review (Low): a malformed default tenant must abort startup instead of degrading to the legacy tenant.
        if (options.Authentication.ForwardAuth.Enabled &&
            !string.IsNullOrWhiteSpace(options.Authentication.ForwardAuth.DefaultTenantId) &&
            !TenantId.TryParse(options.Authentication.ForwardAuth.DefaultTenantId, out _))
        {
            throw new ValidationException("ForwardAuth.DefaultTenantId has an invalid tenant format.");
        }

        if (options.Authentication.RequireKerberosOnly && options.Authentication.BasicAuth.Enabled)
        {
            throw new ValidationException("Security conflict: BasicAuth must not be enabled when RequireKerberosOnly is set to true.");
        }

        if (!environment.IsDevelopment() && options.Authentication.ForwardAuth.Enabled)
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

        if (!environment.IsDevelopment())
        {
            if (options.GovernanceDb.SeedDemoData == true)
            {
                throw new ValidationException("Sicherheitsverletzung: GovernanceDb.SeedDemoData darf AUSSCHLIESSLICH in der Development-Umgebung true sein!");
            }

            // WP-ARC-B-12 (fail closed): the schema must never be started unchecked outside Development.
            if (options.GovernanceDb.Migrations.Mode == GovernanceMigrationMode.Skip)
            {
                throw new ValidationException("Security violation: GovernanceDb:Migrations:Mode = Skip is only allowed in the Development environment. Use Apply or Verify.");
            }

            if (options.Authentication.EnableTestAuthHandler)
            {
                throw new ValidationException("Security violation: EnableTestAuthHandler may be true ONLY in the Development environment.");
            }

            // SEC SG-37: RequireHttpsMetadata must not be false outside Development.
            if (options.Authentication.EntraId.RequireHttpsMetadata == false ||
                options.Authentication.Adfs.RequireHttpsMetadata == false)
            {
                throw new ValidationException("Security violation: RequireHttpsMetadata must not be false outside Development.");
            }

            if (options.IsAnonymousAccessAllowed)
            {
                throw new ValidationException("Security violation: danger_allow_anonymous_access may be true ONLY in the Development environment.");
            }

            if (options.Rebac.SeedTuples.Count > 0)
            {
                if (options.Rebac.SeedTuples.Any(t => t.User.Contains("david", StringComparison.OrdinalIgnoreCase) || t.Object.Contains("prod", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ValidationException("Security critical: Demo or production-targeted ReBAC seed tuples are not permitted outside Development.");
                }
            }

            // Only DANGER entries are blocked outside Development; WARN entries are permitted (reported above).
            if (dangerBypasses.Count > 0)
            {
                throw new ValidationException(
                    $"Critical security violation: The following security bypasses may be active ONLY in the Development environment:\n  - " +
                    string.Join("\n  - ", dangerBypasses));
            }
        }

        if (!environment.IsDevelopment())
        {
            // RR-L3-05: warn_enable_introspection and GraphQL:EnableIntrospection are prohibited outside Development without explicit opt-in
            if ((options.IsIntrospectionForced || options.GraphQL.EnableIntrospection) && !options.AllowInsecureWarnFlagsInProduction)
            {
                throw new ValidationException(
                    "Security violation: Outside Development, GraphQL introspection (GraphQL:EnableIntrospection or warn_enable_introspection) " +
                    "may be active only with an explicit opt-in (AllowInsecureWarnFlagsInProduction = true).");
            }

            // API-16: warn_allow_all_cors_origins is prohibited outside Development without explicit opt-in
            if (options.IsAllCorsAllowed && !options.AllowInsecureWarnFlagsInProduction)
            {
                throw new ValidationException(
                    "Security violation (API-16): Outside Development, warn_allow_all_cors_origins may be active only with an explicit " +
                    "opt-in (AllowInsecureWarnFlagsInProduction = true).");
            }

            // Sicherheits-Invariante 2: Mcp.EnableDeveloperCors is strictly prohibited outside Development and cannot be bypassed
            if (options.Mcp.EnableDeveloperCors)
            {
                throw new ValidationException(
                    "Security violation: Mcp.EnableDeveloperCors is strictly prohibited outside Development and cannot be bypassed.");
            }

            if (!options.IsInsecureTransportAllowed && !options.IsColumnMaskingDisabled &&
                (string.IsNullOrWhiteSpace(options.DataMasking.HmacSecretKeyVaultRef) ||
                options.DataMasking.HmacSecretKeyVaultRef == "DEV_INSECURE_TEST_KEY_ONLY" ||
                options.DataMasking.HmacSecretKeyVaultRef == "dev-only-hmac-salt-secure-fallback"))
            {
                throw new ValidationException("NF-SEC-03 violation: Outside Development, HmacSecretKeyVaultRef must be a valid Key Vault secret reference.");
            }

            if (!options.IsInsecureTransportAllowed && options.OpenMetadata.Enabled &&
                Uri.TryCreate(options.OpenMetadata.ServerUrl, UriKind.Absolute, out var omUri) &&
                !string.Equals(omUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException("Security violation: Outside Development, OpenMetadata.ServerUrl must use HTTPS.");
            }

            if (options.AreUntrustedCertificatesAllowed)
            {
                throw new ValidationException("Security violation: danger_allow_untrusted_certificates may be true ONLY in the Development environment.");
            }

            // DEP-7 / INF-6: data source connections must encrypt and verify the server certificate outside Development.
            foreach (var (name, connection) in options.DataSources.Connections)
            {
                if (ConnectionTlsPolicy.Validate(connection.Provider, connection.ConnectionString) is { } tlsError)
                {
                    throw new ValidationException($"Sicherheitsverletzung: DataSources:Connections:{name}: {tlsError}");
                }
            }

            if (options.GraphQL.TrustedOrigins.Contains("*"))
            {
                throw new ValidationException("Security violation: TrustedOrigins '*' (wildcard CORS) is prohibited outside the Development environment for security reasons (CSRF protection).");
            }

            if (options.GraphQL.TrustedOrigins.Any(o => o != "*" && (!Uri.TryCreate(o, UriKind.Absolute, out var u) || !string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))))
            {
                throw new ValidationException("Security violation: Outside Development, TrustedOrigins must contain only HTTPS URLs.");
            }

            if (options.HighAvailability.MultiNodeClusterMode && options.Rebac.Enabled && !options.Caching.Redis.Enabled)
            {
                throw new ValidationException("Security violation: ReBAC in MultiNodeClusterMode requires Caching.Redis.Enabled = true for cluster-wide invalidation (RR-L4-04).");
            }

            if ((options.HighAvailability.MultiNodeClusterMode || options.HighAvailability.Replicas > 1) && !options.Caching.Redis.Enabled)
            {
                throw new ValidationException("NF-HA-02 violation: In MultiNodeClusterMode or with more than one replica (PG-6), cluster-wide cache and epoch invalidation requires Caching.Redis.Enabled = true.");
            }

            if (!string.IsNullOrWhiteSpace(options.Plugins.Directory) &&
                Directory.Exists(Path.GetFullPath(options.Plugins.Directory)) &&
                !options.Plugins.RequireIntegrityManifest)
            {
                throw new ValidationException("Security violation: Outside Development, a configured plugin directory requires Plugins.RequireIntegrityManifest = true.");
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

            if (options.Audit.Worm.Enabled && string.Equals(options.Audit.Worm.StorageType, "S3", StringComparison.OrdinalIgnoreCase) && options.Audit.Worm.EnforceObjectLock &&
                (string.IsNullOrWhiteSpace(options.Audit.Worm.S3AccessKey) || string.IsNullOrWhiteSpace(options.Audit.Worm.S3SecretKey)))
            {
                throw new ValidationException("Security violation: Outside Development, S3 WORM with EnforceObjectLock requires S3AccessKey and S3SecretKey to be configured.");
            }

            // WP-ARC-B-13: an unset connection string resolves to the state-directory file outside Development/Test, so only an
            // explicitly configured in-memory database can be in effect here (and is rejected).
            if (DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite) &&
                IsInMemorySqlite(options.GovernanceDb.ConnectionString))
            {
                throw new ValidationException("Security violation: In-memory SQLite databases (GovernanceDb.ConnectionString) are strictly prohibited outside Development.");
            }

            // DEP-4: In container environments, a relative SQLite database path in /app is unwritable for non-root APP_UID
            if (IsTruthy(getEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")) &&
                DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite))
            {
                try
                {
                    var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(options.GovernanceDb.ConnectionString);
                    if (!string.IsNullOrWhiteSpace(builder.DataSource) &&
                        !builder.DataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase) &&
                        builder.Mode != Microsoft.Data.Sqlite.SqliteOpenMode.Memory)
                    {
                        var isDirectRootFile = !builder.DataSource.Contains('/') && !builder.DataSource.Contains('\\');
                        var isAppRoot = builder.DataSource.Equals("/app/governance.db", StringComparison.OrdinalIgnoreCase);
                        if (isDirectRootFile || isAppRoot)
                        {
                            throw new ValidationException(
                                "Security and configuration error (DEP-4): In the container, the process runs as non-root user ($APP_UID). " +
                                $"The SQLite path '{builder.DataSource}' is located directly in the non-writable application directory (/app). " +
                                "Use the writable data directory '/app/data' (for example Gateway:State:StateDirectory=/app/data).");
                        }
                    }
                }
                catch (ValidationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to parse SQLite connection string during container path check.");
                }
            }

            // AU-01 & AU-03: Startup Fail-Closed & Umgebungsvalidierung für Audit
            var envName = getEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? environment.EnvironmentName;
            bool isDevOrTest = string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(envName, "Test", StringComparison.OrdinalIgnoreCase);

            if (!isDevOrTest)
            {
                if (options.Audit.HmacKeyIsFallback)
                {
                    throw new ValidationException(
                        "CRITICAL SECURITY VIOLATION: Hardcoded or fallback HMAC audit keys are strictly prohibited in production.");
                }

                // Produktion: Leerer Wert gilt zwingend als Produktion! Every provider, SQLite files included, needs an explicit anchor:
                // the anchor must not live only next to the database it protects (consistent with AuditChainAnchorStoreFactory).
                if (string.IsNullOrWhiteSpace(options.Audit.ChainAnchorPath) &&
                    string.IsNullOrWhiteSpace(options.Audit.ChainAnchorWormDirectory) &&
                    string.IsNullOrWhiteSpace(options.Audit.ChainAnchorSignerKeyVaultRef))
                {
                    throw new ValidationException(
                        "CRITICAL AUDIT MISCONFIGURATION (AU-01/AU-03): In production environments, " +
                        "a persistent audit anchor store (ChainAnchorPath, ChainAnchorWormDirectory, or KMS Signer) " +
                        "is mandatory. In-memory anchor stores are strictly prohibited outside Development/Test.");
                }
            }
        }

        if (!IsSupportedGovernanceDbProvider(options.GovernanceDb.Provider))
        {
            throw new ValidationException($"GovernanceDb provider '{options.GovernanceDb.Provider}' is not supported. Allowed values are 'Sqlite', 'PostgreSql' or 'SqlServer'.");
        }

        if ((options.HighAvailability.MultiNodeClusterMode || options.HighAvailability.Replicas > 1) &&
            DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite))
        {
            throw new ValidationException("Security violation (E-2): Multi-node cluster mode and more than 1 replica are not allowed with SQLite, because SQLite uses local database files per instance. Configure GovernanceDb.Provider = 'PostgreSql' or 'SqlServer' for cluster operation.");
        }

        // WP-ARC-A-09: an embedded Garnet server lives inside one process and is shared with no other replica, so it cannot
        // provide cluster-wide state. This also catches EnableEmbeddedServer = true combined with Redis.Enabled = true,
        // where the embedded server silently wins the registration.
        if ((options.HighAvailability.MultiNodeClusterMode || options.HighAvailability.Replicas > 1) &&
            options.Caching.Garnet.EnableEmbeddedServer)
        {
            throw new ValidationException("NF-HA-02 violation: Multi-node cluster mode or more than one replica requires an external Redis/Garnet. The embedded Garnet server (Caching.Garnet.EnableEmbeddedServer = true) is local to one process and cannot provide cluster-wide state; disable it and configure Caching.Redis.");
        }
    }

    /// <summary>
    /// Reports the effective tenancy mode (Warning when it is the default, Information when explicit) and the deprecation of the
    /// <c>Catalog:AllowAuthenticatedCatalogDiscovery</c> flag, which no longer widens any catalog view.
    /// </summary>
    internal static void ReportTenancyAndCatalogSettings(GatewayOptions options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tenancy = options.Tenancy;
        if (!tenancy.IsExplicit)
        {
            Report(logger, LogLevel.Warning,
                "Gateway:Tenancy:Mode is not configured; defaulting to Multi. Single-tenant deployments must set Gateway:Tenancy:Mode=Single; " +
                "in Multi mode a principal without tenant claim is not treated as an administrator and sees only consented tables.");
        }
        else if (tenancy.IsValid)
        {
            Report(logger, LogLevel.Information, $"Gateway:Tenancy:Mode is {tenancy.EffectiveMode}.");
        }

        if (options.Catalog.AllowAuthenticatedCatalogDiscovery)
        {
            Report(logger, LogLevel.Warning,
                "Gateway:Catalog:AllowAuthenticatedCatalogDiscovery is deprecated and has no effect: non-administrators see only consented tables in the GraphQL catalog.");
        }
    }

    private static void Report(ILogger? logger, LogLevel level, string message)
    {
        if (logger is not null)
        {
            logger.Log(level, "{Message}", message);
        }
        else
        {
            Console.WriteLine($"[Autheris] {level.ToString().ToUpperInvariant()}: {message}");
        }
    }

    /// <summary>
    /// SEC-ADG-28 (X1): <c>WebSql:SqlRewriterEngine</c> was the switch between the legacy string rewriter and the AST compiler. There is
    /// one path now and no runtime switch (rollback is a revert), so a configured value fails the start in every environment:
    /// warn-and-ignore would let an operator believe in a kill switch that does not exist. The configured value is never echoed.
    /// </summary>
    /// <summary>
    /// SR-L-02 / G-4: outside Development <c>Gateway:Hosting:AllowedHosts</c> must be set and must not be the bare wildcard.
    /// </summary>
    public static void ValidateAllowedHosts(GatewayOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment())
        {
            return;
        }

        var hosts = options.Hosting.AllowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToList();
        if (hosts.Count == 0)
        {
            throw new ValidationException(
                "Gateway:Hosting:AllowedHosts is required outside Development. List the host names clients use to reach the gateway " +
                "(for example [\"gateway.corp.example\"], \"host:port\" or \"*.corp.example\"), or set Gateway__Hosting__AllowedHosts__0 in the environment.");
        }

        if (hosts.Contains("*"))
        {
            throw new ValidationException(
                "Gateway:Hosting:AllowedHosts must not contain the bare wildcard '*' outside Development; list the concrete host names.");
        }
    }

    public static void ValidateSqlRewriterEngine(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.GetSection($"{GatewayOptions.SectionName}:WebSql:SqlRewriterEngine").Exists())
        {
            throw new ValidationException(
                "WebSql:SqlRewriterEngine is no longer supported: the AST compiler is the only SQL path and there is no runtime switch. Remove the key from the configuration.");
        }
    }

    /// <summary>
    /// X1: <c>WebSql:PlanCache</c> (incl. <c>MaxEntries</c>) configured the cache of the removed legacy path; the engine template cache has a
    /// fixed size of 512. A configured section fails the start in every environment (it would otherwise be silently ignored). Values are never echoed.
    /// </summary>
    public static void ValidateRemovedWebSqlSettings(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.GetSection($"{GatewayOptions.SectionName}:WebSql:PlanCache").Exists())
        {
            throw new ValidationException(
                "WebSql:PlanCache is no longer supported: the engine template cache has a fixed size of 512 and is not configurable. Remove the section from the configuration.");
        }
    }

    /// <summary>Code of a table that is denied because its dialect is not admitted to WebSQL (<see cref="SqlDialectMapper.IsExecutable(DatabaseDialect)"/>).</summary>
    public const string DeniedDialectNotAdmitted = "DIALECT_NOT_ADMITTED";

    /// <summary>One table that the governed pipeline denies: the table and policy ids and a stable code, never a predicate or a value.</summary>
    public sealed record DeniedTable(string Table, string PolicyId, string Code);

    /// <summary>
    /// SEC-ADG-26 (X1): every stored row predicate that the production parser rejects denies its table, and so does a dialect that WebSQL
    /// does not admit. A deny at scale is an outage, so the start lists all of them (a warning per table, ids and codes only). The same
    /// lint runs as the blocking pre-deploy step <c>tools/PolicyPredicateLint</c>.
    /// </summary>
    public static IReadOnlyList<DeniedTable> ListDeniedTables(
        PolicyLintInput input,
        ILogger? logger = null,
        SqlDataSourceOptions? dataSources = null,
        ISqlDialectRegistry? dialectRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var registry = dialectRegistry ?? SqlDialectRegistry.Default;
        var denied = new List<DeniedTable>();

        foreach (var finding in new PolicyPredicateLinter(dataSources: dataSources).Lint(input).Findings)
        {
            denied.Add(new DeniedTable(finding.Table, finding.PolicyId, finding.Code));
        }

        foreach (var table in input.Catalog)
        {
            if (table.DataSourceType != DataSourceType.Sql)
            {
                continue;   // not a WebSQL pushdown table
            }

            if (!registry.TryGetByName(table.Table.SourceType, out var provider) || !provider.Descriptor.IsExecutable)
            {
                denied.Add(new DeniedTable(table.Identifier.ToString(), "-", DeniedDialectNotAdmitted));
            }
        }

        foreach (var entry in denied)
        {
            logger?.LogWarning("Governed SQL: table {Table} is denied (policy {PolicyId}, code {Code}).", entry.Table, entry.PolicyId, entry.Code);
        }

        if (denied.Count > 0)
        {
            logger?.LogWarning("Governed SQL: {Count} table/policy combination(s) are denied at startup. Run tools/PolicyPredicateLint before the next deploy.", denied.Count);
        }

        return denied;
    }

    /// <summary>WP-X0i / SEC-ADG-09: validates every Databricks data source (exact host, auth mode, Pat only in Development).</summary>
    public static void ValidateDatabricks(GatewayOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var (name, connection) in options.DataSources.Connections)
        {
            if (!DataSourceProvider.Is(connection.Provider, DatabaseDialect.Databricks))
            {
                continue;
            }

            var db = connection.Databricks
                ?? throw new ValidationException($"Databricks data source '{name}' requires a DataSources:Connections:{name}:Databricks section.");
            if (!DatabricksHostPolicy.IsValidHost(db.Host))
            {
                throw new ValidationException($"Databricks data source '{name}': Host must be one exact DNS name (no scheme, port, path, wildcard or IP literal).");
            }

            if (string.IsNullOrWhiteSpace(db.WarehouseId))
            {
                throw new ValidationException($"Databricks data source '{name}': WarehouseId is required.");
            }

            if (db.Auth.Mode == DatabricksAuthMode.Pat)
            {
                if (!environment.IsDevelopment())
                {
                    throw new ValidationException($"Security violation: Databricks data source '{name}' uses Auth.Mode Pat, which is allowed in Development only.");
                }

                if (string.IsNullOrWhiteSpace(db.Auth.PatRef))
                {
                    throw new ValidationException($"Databricks data source '{name}': Auth.PatRef is required for Pat mode.");
                }
            }
            else if (string.IsNullOrWhiteSpace(db.Auth.ClientId) || string.IsNullOrWhiteSpace(db.Auth.ClientSecretRef))
            {
                throw new ValidationException($"Databricks data source '{name}': OAuthM2M requires Auth.ClientId and Auth.ClientSecretRef (Key Vault reference).");
            }
        }
    }

    /// <summary>
    /// WP-F1 / CR-ADG-08: outside Development an Oracle data source never carries a plaintext password (the connection string
    /// has no <c>Password</c>; <c>PasswordKeyVaultRef</c> names the Key Vault secret) and always uses the policy-compliant account.
    /// </summary>
    public static void ValidateOracleRuntime(GatewayOptions options, IHostEnvironment environment, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        bool development = environment.IsDevelopment();

        foreach (var (name, connection) in options.DataSources.Connections)
        {
            if (!DataSourceProvider.Is(connection.Provider, DatabaseDialect.Oracle) || string.IsNullOrWhiteSpace(connection.ConnectionString))
            {
                continue;
            }

            if (development)
            {
                if (OracleConnectionStringPolicy.HasPlaintextPassword(connection.ConnectionString))
                {
                    logger?.LogWarning("Oracle data source '{DataSource}' uses a plaintext password (allowed in Development only).", name);
                }

                continue;
            }

            OracleConnectionStringPolicy.Validate(connection.ConnectionString, requireTcps: true);
            if (OracleConnectionStringPolicy.HasPlaintextPassword(connection.ConnectionString))
            {
                throw new ValidationException(
                    $"Security violation: Oracle data source '{name}' has a plaintext password in its connection string. Outside Development the password must come from Key Vault (DataSources:Connections:{name}:PasswordKeyVaultRef).");
            }

            if (string.IsNullOrWhiteSpace(connection.PasswordKeyVaultRef))
            {
                throw new ValidationException(
                    $"Security violation: Oracle data source '{name}' needs DataSources:Connections:{name}:PasswordKeyVaultRef outside Development.");
            }
        }
    }

    /// <summary>
    /// Issue #102: Validates SQL Server Windows Authentication (NTLM / Kerberos) configuration at startup without opening a network
    /// connection (so Active Directory service accounts can never be locked out by startup probes).
    /// </summary>
    public static void ValidateSqlServerWindowsAuth(
        GatewayOptions options,
        IHostEnvironment environment,
        Func<string, string?>? getEnvVar = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var getEnvironmentVariable = getEnvVar ?? Environment.GetEnvironmentVariable;
        bool isContainer = IsTruthy(getEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"));

        foreach (var (name, connection) in options.DataSources.Connections)
        {
            if (!DataSourceProvider.Is(connection.Provider, DatabaseDialect.SqlServer) || string.IsNullOrWhiteSpace(connection.ConnectionString))
            {
                continue;
            }

            Microsoft.Data.SqlClient.SqlConnectionStringBuilder builder;
            try
            {
                builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection.ConnectionString);
            }
            catch (ArgumentException ex)
            {
                throw new ValidationException($"SQL Server data source '{name}' has an invalid connection string: {ex.Message}", ex);
            }

            // Reject domain-qualified User ID without Integrated Security=true (prevents sending AD user as SQL Login and locking out AD account)
            if (!builder.IntegratedSecurity &&
                !string.IsNullOrWhiteSpace(builder.UserID) &&
                builder.UserID.Contains('\\', StringComparison.Ordinal))
            {
                throw new ValidationException(
                    $"Security violation: SQL Server data source '{name}' specifies a domain-qualified 'User ID' ('{builder.UserID}') without 'Integrated Security=true'. " +
                    "Microsoft.Data.SqlClient sends 'User ID' as a SQL Server login, which fails for Active Directory accounts and risks locking out the AD account. " +
                    $"Remove 'User ID'/'Password' from the connection string and configure 'Integrated Security=true' with NTLM_USER_FILE or DataSources:Connections:{name}:WindowsAuth.");
            }

            var winAuth = connection.WindowsAuth;
            bool hasWinAuth = winAuth is not null && winAuth.Mode != SqlServerWindowsAuthMode.None;
            if (!hasWinAuth && !builder.IntegratedSecurity)
            {
                continue;
            }

            if (hasWinAuth)
            {
                var mode = SqlServerWindowsAuthConfigurator.ResolveEffectiveMode(winAuth!, getEnvironmentVariable);
                if (mode == SqlServerWindowsAuthMode.Kerberos)
                {
                    if (!string.IsNullOrWhiteSpace(winAuth!.KerberosConfigPath) && !File.Exists(Path.GetFullPath(winAuth.KerberosConfigPath)))
                    {
                        throw new ValidationException(
                            $"SQL Server data source '{name}': configured KerberosConfigPath '{winAuth.KerberosConfigPath}' does not exist.");
                    }

                    if (!string.IsNullOrWhiteSpace(winAuth.KerberosKeytabPath) && !File.Exists(Path.GetFullPath(winAuth.KerberosKeytabPath)))
                    {
                        throw new ValidationException(
                            $"SQL Server data source '{name}': configured KerberosKeytabPath '{winAuth.KerberosKeytabPath}' does not exist.");
                    }
                }
                else if (mode == SqlServerWindowsAuthMode.Ntlm)
                {
                    string? ntlmFile = !string.IsNullOrWhiteSpace(winAuth!.NtlmUserFilePath)
                        ? winAuth.NtlmUserFilePath
                        : getEnvironmentVariable("NTLM_USER_FILE");

                    if (!string.IsNullOrWhiteSpace(winAuth.PasswordRef))
                    {
                        var (_, username) = SqlServerWindowsAuthConfigurator.NormalizeDomainAndUser(winAuth.Domain, winAuth.Username);
                        if (string.IsNullOrWhiteSpace(username))
                        {
                            throw new ValidationException(
                                $"SQL Server data source '{name}': WindowsAuth.Username (and Domain) is required when WindowsAuth.PasswordRef is set.");
                        }

                        if (winAuth.PasswordRef.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                        {
                            string secretFilePath = winAuth.PasswordRef["file:".Length..].Trim();
                            if (string.IsNullOrWhiteSpace(secretFilePath) || !File.Exists(Path.GetFullPath(secretFilePath)))
                            {
                                throw new ValidationException(
                                    $"SQL Server data source '{name}': WindowsAuth.PasswordRef secret file '{secretFilePath}' does not exist.");
                            }
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(ntlmFile))
                    {
                        try
                        {
                            SqlServerWindowsAuthConfigurator.ValidateNtlmUserFile(ntlmFile, logger);
                        }
                        catch (Exception ex)
                        {
                            throw new ValidationException($"SQL Server data source '{name}': {ex.Message}", ex);
                        }
                    }
                    else
                    {
                        throw new ValidationException(
                            $"SQL Server data source '{name}': WindowsAuth (NTLM) requires either (Domain, Username, PasswordRef) or an existing NtlmUserFilePath / NTLM_USER_FILE.");
                    }
                }
            }
            else if (builder.IntegratedSecurity)
            {
                string? ambientNtlmFile = getEnvironmentVariable("NTLM_USER_FILE");
                if (!string.IsNullOrWhiteSpace(ambientNtlmFile))
                {
                    try
                    {
                        SqlServerWindowsAuthConfigurator.ValidateNtlmUserFile(ambientNtlmFile, logger);
                    }
                    catch (Exception ex)
                    {
                        throw new ValidationException($"SQL Server data source '{name}': {ex.Message}", ex);
                    }
                }
                else if (OperatingSystem.IsLinux() && isContainer &&
                         string.IsNullOrWhiteSpace(getEnvironmentVariable("KRB5_CLIENT_KTNAME")) &&
                         string.IsNullOrWhiteSpace(getEnvironmentVariable("KRB5CCNAME")))
                {
                    logger?.LogWarning(
                        "SQL Server data source '{DataSource}' uses 'Integrated Security=true' inside a Linux container, but neither NTLM_USER_FILE, " +
                        "KRB5_CLIENT_KTNAME, KRB5CCNAME, nor DataSources:Connections:{DataSource}:WindowsAuth is configured. " +
                        "Connections will fail unless ambient GSSAPI credentials are available.",
                        name,
                        name);
                }
            }

            if (OperatingSystem.IsLinux() && isContainer && !SqlServerWindowsAuthConfigurator.IsLinuxGssNtlmMechanismPresent())
            {
                logger?.LogWarning(
                    "SQL Server data source '{DataSource}' uses Windows Authentication on Linux, but '{GssMechConfig}' was not found. " +
                    "Ensure 'gss-ntlmssp' and 'libgssapi-krb5-2' are installed in the container image.",
                    name,
                    SqlServerWindowsAuthConfigurator.GssMechConfigPath);
            }
        }
    }

    /// <summary>
    /// WP-ARC-A-02: configured webhook subscriptions need a stable, unique id (<= 128 chars). Outbox rows reference the id, so a generated
    /// or colliding id would dead-letter queued deliveries after a restart or on another node. Messages never echo URLs or secrets.
    /// </summary>
    public static void ValidateWebhookSubscriptions(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Webhooks.Enabled)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.Webhooks.Subscriptions.Count; i++)
        {
            var id = options.Webhooks.Subscriptions[i].Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ValidationException($"Gateway:Webhooks:Subscriptions:{i}:Id is required (a stable, unique id of at most 128 characters).");
            }

            if (id.Length > 128)
            {
                throw new ValidationException($"Gateway:Webhooks:Subscriptions:{i}:Id exceeds 128 characters.");
            }

            if (!seen.Add(id))
            {
                throw new ValidationException($"Gateway:Webhooks:Subscriptions:{i}:Id '{id}' is not unique.");
            }
        }
    }

    /// <summary>
    /// WP-X0b, SEC-X1-08: <c>DefaultSchema</c> must match the strict identifier rule in every environment, and a WebSQL-reachable
    /// PostgreSQL or Oracle data source must have one (the session pin needs it).
    /// </summary>
    public static void ValidateDefaultSchemas(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var webSqlSources = options.WebSql.Enabled
            ? WebSqlReachableDataSources(options)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, connection) in options.DataSources.Connections)
        {
            if (connection.DefaultSchema is not null && !DefaultSchemaPolicy.IsValid(connection.DefaultSchema))
            {
                throw new ValidationException(
                    $"Security violation: DataSources:Connections:{name}:DefaultSchema must match ^[A-Za-z_][A-Za-z0-9_$#]{{0,127}}$.");
            }

            bool pinned = DataSourceProvider.Is(connection.Provider, DatabaseDialect.PostgreSql) || DataSourceProvider.Is(connection.Provider, DatabaseDialect.Oracle);
            if (pinned && connection.DefaultSchema is null && webSqlSources.Contains(name))
            {
                throw new ValidationException(
                    $"WebSQL data source '{name}' ({connection.Provider}) needs DataSources:Connections:{name}:DefaultSchema; the session pin cannot be applied without it.");
            }

            // X1: RequireDefaultSchema defaults to true, so the factory would refuse this source at request time. Refuse the start instead.
            if (pinned && connection.DefaultSchema is null && connection.RequireDefaultSchema)
            {
                throw new ValidationException(
                    $"Data source '{name}' ({connection.Provider}) needs DataSources:Connections:{name}:DefaultSchema (or an explicit RequireDefaultSchema = false for a source that no WebSQL path reaches).");
            }
        }
    }

    private static HashSet<string> WebSqlReachableDataSources(GatewayOptions options)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { options.WebSql.DefaultDataSourceName };
        names.UnionWith(options.WebSql.AllowedDataSources);
        names.UnionWith(options.WebSql.DataSourceMappings.Values);
        foreach (var list in options.WebSql.TenantDataSourceAllowlist.Values)
        {
            names.UnionWith(list);
        }

        return names;
    }

    /// <summary>
    /// Decision B-1: tenant ids that collide case-insensitively are reported. By default the start only logs a critical warning:
    /// the colliding tenants are denied per request (<c>SecurityContextResolutionMiddleware</c>, 403 <c>TENANT_ID_COLLISION</c>)
    /// and every other tenant keeps working. <c>Gateway:TenantIsolation:StrictCollisionStartup</c> refuses the start outside
    /// Development instead. Ids are never rewritten.
    /// </summary>
    public static void ValidateTenantCollisions(GatewayOptions options, IHostEnvironment environment, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var collisions = TenantCollisionCheck.FindCollisions(TenantCollisionGuard.ConfiguredTenantIds(options));
        if (collisions.Count == 0)
        {
            return;
        }

        string description = string.Join("; ", collisions.Select(c => string.Join(" / ", c.Spellings)));
        if (environment.IsDevelopment())
        {
            logger?.LogWarning("Tenant ids collide case-insensitively (decision B-1): {Collisions}. Resolve before production.", description);
            return;
        }

        if (options.TenantIsolation.StrictCollisionStartup)
        {
            throw new ValidationException(
                $"Security violation (B-1): tenant ids that differ only in case are configured ({description}). Strict mode refuses the start; rename one of them.");
        }

        logger?.LogCritical(
            "Security (B-1): tenant ids that differ only in case are configured ({Collisions}). Requests of these tenants are denied with 403 TENANT_ID_COLLISION until an operator renames one of them.",
            description);
    }

    internal static bool IsSupportedGovernanceDbProvider(string? provider) =>
        DataSourceProvider.Is(provider, DatabaseDialect.Sqlite) || DataSourceProvider.Is(provider, DatabaseDialect.PostgreSql) || DataSourceProvider.Is(provider, DatabaseDialect.SqlServer);

    private static bool IsInMemorySqlite(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
            return builder.DataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase) ||
                   builder.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory;
        }
        catch (ArgumentException)
        {
            return connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ||
                   connectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsTruthy(string? value)
    {
        var trimmed = value?.Trim();
        return string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "1", StringComparison.Ordinal);
    }

    private static void ValidateObjectRecursively(object instance)
    {
        var context = new ValidationContext(instance);
        Validator.ValidateObject(instance, context, validateAllProperties: true);

        foreach (var prop in instance.GetType().GetProperties())
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string) && !prop.PropertyType.IsArray && !typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType))
            {
                var val = prop.GetValue(instance);
                if (val != null)
                {
                    ValidateObjectRecursively(val);
                }
            }
        }
    }

    private static void ValidateQueryCostOptions(QueryCostOptions? queryCost, IHostEnvironment environment)
    {
        if (queryCost == null)
        {
            throw new ValidationException("QueryCostOptions must not be null.");
        }

        if (!environment.IsDevelopment())
        {
            if (queryCost.BaseCost < 1 || queryCost.RowCostFactor < 0 || queryCost.FailureFee < 1 ||
                queryCost.CostPerSingleRowLookup < 1 || queryCost.DefaultListMultiplier < 1 || queryCost.NestedCostFactor < 0)
            {
                throw new ValidationException("Invalid QueryCost configuration outside Development: BaseCost and FailureFee must be >= 1, RowCostFactor and NestedCostFactor must be >= 0.");
            }
        }
    }
}
