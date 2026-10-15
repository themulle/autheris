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
    /// SQL Server only: every session of this data source runs <c>SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED</c>
    /// ("dirty read"). Reads then neither wait for nor block writers (no shared locks), at the price of seeing uncommitted,
    /// possibly rolled back rows and occasionally missing or duplicating rows during page splits. Meant for analytical reads
    /// on busy operational databases; default false. Ignored by other providers (PostgreSQL has no dirty reads).
    /// </summary>
    public bool ReadUncommitted { get; init; }

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
}

/// <summary>Authentication modes of the Databricks Statement Execution client.</summary>
public enum DatabricksAuthMode
{
    /// <summary>OAuth machine-to-machine (service principal client credentials). Default and the only mode outside Development.</summary>
    OAuthM2M = 0,

    /// <summary>Personal access token. Development only (SEC-ADG-09 item 4).</summary>
    Pat = 1
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
