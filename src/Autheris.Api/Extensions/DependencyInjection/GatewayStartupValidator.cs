namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Autheris.Api.Configuration;
using Autheris.Api.Security;
using Autheris.Application.Governance;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class GatewayStartupValidator
{
    public static void ValidateStartup(
        GatewayOptions options,
        IHostEnvironment environment,
        Func<string, string?>? getEnvVar = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var getEnvironmentVariable = getEnvVar ?? Environment.GetEnvironmentVariable;
        bool isDevEnvironment = environment.IsDevelopment();

        ValidateObjectRecursively(options);

        ValidateOracleRuntime(options, environment, logger);
        ValidateTenantCollisions(options, environment, logger);

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
            foreach (var netStr in options.ReverseProxy.KnownNetworks)
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

            if (DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite) &&
                !string.IsNullOrWhiteSpace(options.GovernanceDb.ConnectionString) &&
                (options.GovernanceDb.ConnectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ||
                 options.GovernanceDb.ConnectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase)))
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
                                "Use the writable data directory '/app/data' (e.g. 'Data Source=/app/data/governance.db;Cache=Shared').");
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

            bool isSqliteFile = false;
            if (DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite) &&
                !string.IsNullOrWhiteSpace(options.GovernanceDb.ConnectionString))
            {
                try
                {
                    var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(options.GovernanceDb.ConnectionString);
                    isSqliteFile = !string.IsNullOrWhiteSpace(csb.DataSource) &&
                                   !csb.DataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase) &&
                                   csb.Mode != Microsoft.Data.Sqlite.SqliteOpenMode.Memory;
                }
                catch (ArgumentException ex)
                {
                    // AR-06: Replace empty catch with logged warning, isSqliteFile remains false (fail-closed)
                    logger?.LogWarning(ex, "Failed to parse SQLite connection string; falling back to strict production audit anchor requirements.");
                }
            }

            if (!isDevOrTest)
            {
                if (options.Audit.HmacKeyIsFallback)
                {
                    throw new ValidationException(
                        "CRITICAL SECURITY VIOLATION: Hardcoded or fallback HMAC audit keys are strictly prohibited in production.");
                }

                if (!isSqliteFile)
                {
                    // Produktion: Leerer Wert gilt zwingend als Produktion!
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
    /// Decision B-1: tenant ids that collide case-insensitively are reported; outside Development the start is refused (all
    /// requests of the colliding tenants are denied until an operator resolves the collision). Ids are never rewritten.
    /// </summary>
    public static void ValidateTenantCollisions(GatewayOptions options, IHostEnvironment environment, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var collisions = TenantCollisionCheck.FindCollisions(ConfiguredTenantIds(options));
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

        throw new ValidationException(
            $"Security violation (B-1): tenant ids that differ only in case are configured ({description}). The colliding tenants are denied; rename one of them.");
    }

    private static IEnumerable<string?> ConfiguredTenantIds(GatewayOptions options)
    {
        var forwardAuth = options.Authentication.ForwardAuth;
        yield return forwardAuth.DefaultTenantId;
        foreach (var id in forwardAuth.AllowedTenantIds) yield return id;
        foreach (var id in options.WebSql.TenantDataSourceAllowlist.Keys) yield return id;
        yield return options.OpenMetadata.DefaultTenantId;
        foreach (var id in options.OpenMetadata.ServiceDatabaseToTenantMap.Values) yield return id;
        foreach (var id in options.Itsm.InstanceToTenantMap.Values) yield return id;
    }

    internal static bool IsSupportedGovernanceDbProvider(string? provider) =>
        DataSourceProvider.Is(provider, DatabaseDialect.Sqlite) || DataSourceProvider.Is(provider, DatabaseDialect.PostgreSql) || DataSourceProvider.Is(provider, DatabaseDialect.SqlServer);

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
}
