namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions.DependencyInjection.StartupChecks;
using Autheris.Api.Security;
using Autheris.Application.Governance;
using Autheris.Application.Policy.Lint;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Dialects;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Observability;
using Autheris.Infrastructure.Databricks;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class GatewayStartupValidator
{
    /// <summary>
    /// Issue #277 (SEC-277-08): with <c>WebSql:AllowRecursiveCte</c> on, a recursive statement can fill provider temp space until the
    /// execution timeout fires; the gateway has no in-code bound for it. One warning names the values and the deployment prerequisite.
    /// </summary>
    internal static void WarnAboutRecursiveCte(GatewayOptions options, ILogger? logger)
    {
        if (options.WebSql is { AllowRecursiveCte: true } webSql)
        {
            logger?.LogWarning(
                "WebSQL WITH RECURSIVE is enabled (MaxRecursionDepth {MaxRecursionDepth}, ExecutionTimeoutSeconds {TimeoutSeconds}). The timeout is the work bound; " +
                "limit temp space on the gateway database roles (PostgreSQL temp_file_limit/work_mem, Oracle temp tablespace quota or profile, SQL Server tempdb limits).",
                webSql.MaxRecursionDepth, webSql.ExecutionTimeoutSeconds);
        }
    }

    public static void ValidateStartup(
        GatewayOptions options,
        IHostEnvironment environment,
        Func<string, string?>? getEnvVar = null,
        ILogger? logger = null,
        ISqlDialectRegistry? dialectRegistry = null,
        TelemetryOptions? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        ValidateStartup(options, environment, getEnvVar, logger, GatewayStartupChecks.All, telemetry);
    }

    /// <summary>Runs the checks in order; the first failure propagates (no catch, no aggregation). The list is a seam for the removal mutation test.</summary>
    internal static void ValidateStartup(
        GatewayOptions options,
        IHostEnvironment environment,
        Func<string, string?>? getEnvVar,
        ILogger? logger,
        IReadOnlyList<StartupCheck> checks,
        TelemetryOptions? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(checks);

        var context = new StartupCheckContext(options, environment, getEnvVar ?? Environment.GetEnvironmentVariable, logger, telemetry);
        foreach (var check in checks)
        {
            check.Run(context);
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

            // Issue #238 (SEC-OBO238-04): EnforcementMode cross-validation
            if (connection.EnforcementMode is EnforcementMode.Delegated or EnforcementMode.DelegatedDefenceInDepth)
            {
                if (connection.KeySetCacheAllowed)
                {
                    throw new ValidationException($"Security violation (SEC-RF107-01 / SEC-OBO238-04): Databricks data source '{name}' has EnforcementMode = '{connection.EnforcementMode}', but KeySetCacheAllowed is true. KeySetCacheAllowed must be false for delegated enforcement to prevent cross-user tenant cache pollution.");
                }

                if (db.Auth.Mode != DatabricksAuthMode.OAuthObo)
                {
                    throw new ValidationException($"Security violation (SEC-OBO238-04): Databricks data source '{name}' has EnforcementMode = '{connection.EnforcementMode}', but Auth.Mode is '{db.Auth.Mode}'. Delegated enforcement requires Auth.Mode = OAuthObo.");
                }
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
            else if (db.Auth.Mode == DatabricksAuthMode.OAuthObo)
            {
                var obo = db.Auth.Obo
                    ?? throw new ValidationException($"Databricks data source '{name}': Auth.Mode OAuthObo requires the Auth.Obo section.");

                if (string.IsNullOrWhiteSpace(obo.ClientId))
                {
                    throw new ValidationException($"Databricks data source '{name}': Auth.Obo.ClientId is required for OAuthObo mode.");
                }

                if (string.IsNullOrWhiteSpace(obo.ClientSecretRef) && string.IsNullOrWhiteSpace(obo.ClientCertificateRef))
                {
                    throw new ValidationException($"Databricks data source '{name}': Auth.Obo requires either ClientSecretRef or ClientCertificateRef.");
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

    internal static bool IsInMemorySqlite(string? connectionString)
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

    internal static bool IsTruthy(string? value)
    {
        var trimmed = value?.Trim();
        return string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "1", StringComparison.Ordinal);
    }

    internal static void ValidateObjectRecursively(object instance)
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

    internal static void ValidateQueryCostOptions(QueryCostOptions? queryCost, IHostEnvironment environment)
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
