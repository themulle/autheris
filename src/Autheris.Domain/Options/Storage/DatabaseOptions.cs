using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class SqlDataSourceOptions
{
    /// <summary>
    /// Review E-5: when true, a table without a tenant column is refused (fail-closed) unless it is listed in
    /// <see cref="TenantColumnExemptTables"/>. Default false keeps tables that are shared across tenants readable.
    /// </summary>
    public bool RequireTenantColumn { get; init; }

    /// <summary>Tables (<c>schema.table</c> or <c>table</c>, case-insensitive) that are deliberately shared across tenants.</summary>
    public List<string> TenantColumnExemptTables { get; init; } = new();

    /// <summary>
    /// O10: Maximum number of concurrent table reads (OData, GraphQL, MCP, kernel) per tenant, user and table. Further
    /// requests get 429 with Retry-After instead of occupying another database worker and pool connection.
    /// 0 disables the limit.
    /// </summary>
    [Range(0, 1000)] public int MaxConcurrentReadsPerUserAndTable { get; init; } = 4;

    public Dictionary<string, DataSourceConnectionOptions> Connections { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WP-X0c (SEC-X1-09): how long the column types and collations read from the database metadata are cached, in seconds.
    /// The hard maximum is 300; configuration can only lower it. A larger value fails the startup.
    /// </summary>
    [Range(1, 300)] public int CatalogCacheTtlSeconds { get; init; } = 300;

    /// <summary>Global settings of the declarative HTTP data sources (web-api tables).</summary>
    public HttpDataSourceOptions Http { get; init; } = new();
}

public sealed class DataSourceConnectionOptions
{
    public string Provider { get; init; } = "Sqlite"; // "Sqlite", "SqlServer", "PostgreSql"
    public string ConnectionString { get; init; } = string.Empty;
    [Range(1, 300)] public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Issue #238 (PLAN-DATA-238, WP-SC-15): Policy enforcement mode (AutherisEnforced, Delegated, DelegatedDefenceInDepth).
    /// </summary>
    public EnforcementMode EnforcementMode { get; init; } = EnforcementMode.AutherisEnforced;

    /// <summary>
    /// Issue #132 (PLAN-DATA-132, SEC-DATA132-06): Administrative co-location group identifier for SQL Server data sources
    /// hosted on the same physical database instance with symmetric credentials.
    /// </summary>
    public string? CoLocationGroup { get; init; }

    /// <summary>
    /// Issue #132: Optional explicit physical server name or host for identity verification (e.g. @@SERVERNAME).
    /// </summary>
    public string? ServerName { get; init; }

    /// <summary>
    /// SQL Server only: every session of this data source runs <c>SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED</c>
    /// ("dirty read"). Reads then neither wait for nor block writers (no shared locks), at the price of seeing uncommitted,
    /// possibly rolled back rows and occasionally missing or duplicating rows during page splits. Meant for analytical reads
    /// on busy operational databases; default false. Ignored by other providers (PostgreSQL has no dirty reads).
    /// </summary>
    public bool ReadUncommitted { get; init; }

    /// <summary>
    /// SQL Server only: circuit breaker against Active Directory / SQL login lockout. After a login failure (error 18456, 18452, 18451, a rejected
    /// login or a credential-type NTLM failure) the factory refuses further connections of this data source, without contacting the server, for
    /// this many seconds (fail closed). The block is per credential identity (connection string + WindowsAuth settings): a changed configuration
    /// lifts it. Network and timeout failures never open it. Each replica has its own breaker. <c>0</c> disables the breaker. Default 300.
    /// </summary>
    [Range(0, 86400)] public int LoginFailureCooldownSeconds { get; init; } = 300;

    /// <summary>
    /// Issue #107 (SEC-RF107-01): operator attestation that the dependent tables of correlated row filters are not filtered per user
    /// at this source (no source-native per-user RLS). The key-set cache key query runs with a tenant-only session; without this flag
    /// every correlated filter of the data source falls back to <c>EXISTS</c> (reason <c>SourceNotEligible</c>). Default false.
    /// </summary>
    public bool KeySetCacheAllowed { get; init; }

    /// <summary>
    /// Issue #124 (SEC-DATA124-02): operator/data-owner consent allowing dynamic key-set pushdown filters
    /// to be imported and executed against this target data source during cross-source WebSQL joins. Default false (fail closed).
    /// </summary>
    public bool AllowKeySetImport { get; init; } = false;

    /// <summary>
    /// Issue #129 (BC-129-03): the connection may serve the privileged caller attribute lookup (a fixed read of the registered attribute
    /// table under the gateway service identity). It must use a dedicated read-only login. Default false: attribute-table providers fail
    /// closed (<c>SOURCE_NOT_ALLOWED</c>).
    /// </summary>
    public bool CallerAttributeLookupAllowed { get; init; }

    /// <summary>
    /// Issue #129 (BC-129-03, F-8): the owner records that the lookup login is NOT dedicated (shares the governed login). Honoured only
    /// in Development; elsewhere it is ignored and the provider fails closed unless <see cref="CallerAttributeDedicatedLogin"/> is set.
    /// </summary>
    public bool CallerAttributeSharedLoginAcknowledged { get; init; }

    /// <summary>Issue #129 (BC-129-03): operator attestation that the connection's login is dedicated, read-only, SELECT on the attribute tables only.</summary>
    public bool CallerAttributeDedicatedLogin { get; init; }

    /// <summary>
    /// Oracle only (WP-F1, CR-ADG-08): Key Vault reference of the database password. Outside Development the connection string
    /// must not contain a plaintext <c>Password</c>; the factory injects the resolved secret when it opens the connection.
    /// </summary>
    public string? PasswordKeyVaultRef { get; init; }

    /// <summary>
    /// WP-X0b: default schema pinned on every connection rental. PostgreSQL: <c>search_path = pg_catalog, &lt;schema&gt;, pg_temp</c>;
    /// Oracle: <c>CURRENT_SCHEMA</c>. SQL Server and SQLite ignore it. Validated against <see cref="DefaultSchemaPolicy"/>; the
    /// value is only ever passed as a bound parameter and quoted by the database (<c>format('%I')</c>,
    /// <c>DBMS_ASSERT.ENQUOTE_NAME</c>), never concatenated on the client.
    /// </summary>
    public string? DefaultSchema { get; init; }

    /// <summary>
    /// WP-X0b: the connection factory refuses to hand out a PostgreSQL or Oracle connection of this data source unless
    /// <see cref="DefaultSchema"/> is set and valid (SQL_SOURCE_UNAVAILABLE, fail closed), and startup validation refuses the start
    /// for such a source. Default <c>true</c> since X1: the schema pin (<c>search_path</c>, <c>CURRENT_SCHEMA</c>) is what makes an
    /// unqualified table name resolve to the governed table. A source that no WebSQL path reaches may opt out with <c>false</c>; a
    /// WebSQL-reachable PostgreSQL or Oracle source always needs the schema. Other providers ignore the flag.
    /// </summary>
    public bool RequireDefaultSchema { get; init; } = true;

    /// <summary>Databricks only (WP-X0i, SEC-ADG-09): Statement Execution API settings. Required when <c>Provider</c> is databricks.</summary>
    public DatabricksDataSourceOptions? Databricks { get; init; }

    /// <summary>
    /// SQL Server only (Issue #102): Windows Authentication (NTLM via <c>gss-ntlmssp</c> or Kerberos via <c>libgssapi-krb5-2</c>)
    /// settings for Linux containers and non-domain hosts. Passwords are resolved exclusively via secret references or
    /// <c>NTLM_USER_FILE</c> and never placed into the connection string or logs.
    /// </summary>
    public SqlServerWindowsAuthOptions? WindowsAuth { get; init; }
}

/// <summary>Windows authentication mode for SQL Server data sources on Linux/Windows (Issue #102).</summary>
public enum SqlServerWindowsAuthMode
{
    /// <summary>Windows authentication helper disabled (standard SQL login or host-level ambient SSPI).</summary>
    None = 0,

    /// <summary>Automatic detection based on configured credentials (NTLM user file / Domain+Username+PasswordRef vs. Kerberos keytab/cache).</summary>
    Auto = 1,

    /// <summary>NTLM authentication via <c>gss-ntlmssp</c> (<c>NTLM_USER_FILE</c> in <c>DOMAIN:USER:PASSWORD</c> format).</summary>
    Ntlm = 2,

    /// <summary>Kerberos authentication via <c>libgssapi-krb5-2</c> (<c>krb5.conf</c> and keytab or credential cache).</summary>
    Kerberos = 3
}

/// <summary>
/// Issue #102: Windows authentication (NTLM / Kerberos) configuration for a SQL Server data source running in a container.
/// Ensures <c>Integrated Security=true</c> is used with GSSAPI (<c>gss-ntlmssp</c> / <c>libgssapi-krb5-2</c>) without storing
/// plaintext passwords in connection strings or configuration files.
/// </summary>
public sealed class SqlServerWindowsAuthOptions
{
    /// <summary>Authentication mechanism mode (<see cref="SqlServerWindowsAuthMode.Auto"/>, <see cref="SqlServerWindowsAuthMode.Ntlm"/>, or <see cref="SqlServerWindowsAuthMode.Kerberos"/>).</summary>
    public SqlServerWindowsAuthMode Mode { get; init; } = SqlServerWindowsAuthMode.Auto;

    /// <summary>Active Directory NetBIOS domain or realm name (for example <c>LWEW2K</c>).</summary>
    public string? Domain { get; init; }

    /// <summary>Active Directory service account user name without domain prefix (for example <c>lwesvcbidbreportread</c>).</summary>
    public string? Username { get; init; }

    /// <summary>
    /// Secret reference for the Active Directory password (for example <c>file:/run/secrets/lwe_bi_password</c> or a Key Vault secret name).
    /// Outside Development, plaintext passwords in configuration are rejected (fail closed).
    /// </summary>
    public string? PasswordRef { get; init; }

    /// <summary>
    /// Optional explicit path to a pre-mounted <c>NTLM_USER_FILE</c> in <c>DOMAIN:USER:PASSWORD</c> format (permissions <c>0400</c> or <c>0600</c>).
    /// When omitted and <see cref="Domain"/>, <see cref="Username"/>, and <see cref="PasswordRef"/> are set, Autheris materializes a restricted
    /// (<c>0600</c>) NTLM user file in its private GSS state directory (<c>/app/data/gss</c>).
    /// </summary>
    public string? NtlmUserFilePath { get; init; }

    /// <summary>Optional path to <c>krb5.conf</c> for Kerberos authentication (sets <c>KRB5_CONFIG</c> when specified).</summary>
    public string? KerberosConfigPath { get; init; }

    /// <summary>Optional path to a Kerberos client keytab file (sets <c>KRB5_CLIENT_KTNAME</c> when specified).</summary>
    public string? KerberosKeytabPath { get; init; }

    /// <summary>Optional path to a Kerberos credential cache file (sets <c>KRB5CCNAME</c> when specified).</summary>
    public string? KerberosCachePath { get; init; }

    /// <summary>Optional explicit SQL Server Service Principal Name (for example <c>MSSQLSvc/sqlhost.corp.local:1433@CORP.LOCAL</c>).</summary>
    public string? ServicePrincipalName { get; init; }

    /// <summary>
    /// Issue #102: selects the NTLM path. <c>true</c> uses .NET's built-in managed NTLM (<c>System.Net.Security.UseManagedNtlm</c>) with a
    /// per-connection <c>Microsoft.Data.SqlClient.SspiContextProvider</c> backed by <see cref="System.Net.Security.NegotiateAuthentication"/>
    /// (no native library needed). <c>false</c> never sets the switch and never creates the provider, so SqlClient uses the native
    /// GSSAPI stack (<c>gss-ntlmssp</c> / <c>NTLM_USER_FILE</c>). When not configured (<c>null</c>) the process environment variable
    /// <c>DOTNET_System_Net_Security_UseManagedNtlm</c> decides (<c>true</c>/<c>1</c> or <c>false</c>/<c>0</c>); when that is unset or invalid, managed NTLM is used.
    /// See <see cref="ManagedNtlmPolicy"/>.
    /// </summary>
    public bool? UseManagedNtlm { get; init; }
}

/// <summary>
/// Issue #238 (PLAN-DATA-238, REQ-DATA-238): Governed enforcement mode for a data source connection.
/// </summary>
public enum EnforcementMode
{
    /// <summary>Autheris evaluates row filters, column masks, and table access controls directly (default).</summary>
    AutherisEnforced = 0,

    /// <summary>Autheris performs coarse admission; row-level security and column masking are delegated to the native source (e.g. Unity Catalog).</summary>
    Delegated = 1,

    /// <summary>Both Autheris policies and native data source policies are applied concurrently.</summary>
    DelegatedDefenceInDepth = 2
}

/// <summary>Authentication modes of the Databricks Statement Execution client.</summary>
public enum DatabricksAuthMode
{
    /// <summary>OAuth machine-to-machine (service principal client credentials). Default and the only mode outside Development.</summary>
    OAuthM2M = 0,

    /// <summary>Personal access token. Development only (SEC-ADG-09 item 4).</summary>
    Pat = 1,

    /// <summary>OAuth 2.0 On-Behalf-Of (OBO) token exchange for Azure Databricks (WP-SC-15 / Issue #238).</summary>
    OAuthObo = 2
}

/// <summary>
/// Issue #238 (PLAN-DATA-238, WP-SC-15): Configuration for Azure Databricks OAuth 2.0 On-Behalf-Of (OBO) token exchange.
/// </summary>
public sealed class DatabricksOboOptions
{
    /// <summary>Entra ID tenant ID (for authority URL resolution). When omitted, "common" or configured authority is used.</summary>
    public string? TenantId { get; init; }

    /// <summary>Application (client) ID of the Azure Databricks application registration.</summary>
    public string? ClientId { get; init; }

    /// <summary>Key Vault reference or secret value for the confidential client application.</summary>
    public string? ClientSecretRef { get; init; }

    /// <summary>Key Vault reference to a client certificate used for client assertion.</summary>
    public string? ClientCertificateRef { get; init; }

    /// <summary>Azure Databricks resource scope (default: 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default).</summary>
    public string Scopes { get; init; } = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default";

    /// <summary>Optional custom Entra ID authority URL (e.g. for national clouds).</summary>
    public string? Authority { get; init; }

    /// <summary>Controls refresh token retention for long-running statements (default false).</summary>
    public bool AllowLongRunning { get; init; } = false;
}

public sealed class DatabricksAuthOptions
{
    public DatabricksAuthMode Mode { get; init; } = DatabricksAuthMode.OAuthM2M;

    /// <summary>OAuth M2M: service principal application id.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth M2M: Key Vault reference of the service principal secret (plaintext only in Development).</summary>
    public string? ClientSecretRef { get; init; }

    /// <summary>PAT mode (Development only): Key Vault reference or plaintext token.</summary>
    public string? PatRef { get; init; }

    /// <summary>OAuth OBO settings (Issue #238 / WP-SC-15).</summary>
    public DatabricksOboOptions? Obo { get; init; }
}

/// <summary>
/// Settings of one Databricks SQL warehouse data source (design §6.3). The OAuth token endpoint is NOT configurable: it is
/// always <c>https://{Host}/oidc/v1/token</c> (SEC-ADG-09 item 3).
/// </summary>
public sealed class DatabricksDataSourceOptions
{
    /// <summary>Exact workspace host name (no scheme, port, path or wildcard), for example <c>adb-123.azuredatabricks.net</c>.</summary>
    public string Host { get; init; } = string.Empty;

    public string WarehouseId { get; init; } = string.Empty;
    public string Catalog { get; init; } = "main";
    public string Schema { get; init; } = "default";
    public DatabricksAuthOptions Auth { get; init; } = new();

    /// <summary>Server-side wait per call; clamped to 5-50 seconds (SEC-ADG-09 item 6).</summary>
    public int WaitTimeoutSeconds { get; init; } = 30;

    /// <summary>Maximum inline result size in bytes (also bounds the response buffer).</summary>
    [Range(1024, 104857600)] public long ByteLimit { get; init; } = 26214400;

    /// <summary>Delay between status polls in milliseconds.</summary>
    [Range(10, 10000)] public int PollIntervalMilliseconds { get; init; } = 500;
}

/// <summary>WP-X0b, SEC-X1-08: strict identifier rule for <see cref="DataSourceConnectionOptions.DefaultSchema"/>.</summary>
public static partial class DefaultSchemaPolicy
{
    // \z instead of $: in .NET "$" also matches before a trailing line feed.
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$#]{0,127}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    public static bool IsValid(string? schema) => schema is not null && Pattern().IsMatch(schema);
}

/// <summary>
/// Single rule that selects between managed NTLM and the native GSSAPI (<c>gss-ntlmssp</c>) path for SQL Server Windows authentication:
/// an explicit <see cref="SqlServerWindowsAuthOptions.UseManagedNtlm"/> wins; otherwise the environment variable
/// <see cref="EnvironmentVariableName"/> (<c>true</c>/<c>1</c>/<c>false</c>/<c>0</c>) decides; otherwise managed NTLM is used.
/// </summary>
public static class ManagedNtlmPolicy
{
    public const string EnvironmentVariableName = "DOTNET_System_Net_Security_UseManagedNtlm";

    public static bool Resolve(bool? configured, Func<string, string?>? getEnvVar = null)
    {
        if (configured is { } explicitValue)
        {
            return explicitValue;
        }

        string? raw = (getEnvVar ?? Environment.GetEnvironmentVariable)(EnvironmentVariableName)?.Trim();
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0")
        {
            return false;
        }

        return true;
    }
}
