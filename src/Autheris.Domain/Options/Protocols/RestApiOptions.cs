using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>REST read-path switches (startup only; a restart is required).</summary>
public sealed class RestApiOptions
{
    /// <summary>REST data endpoint kill-switch settings (<c>Gateway:RestApi:DataEndpoint</c>).</summary>
    public RestDataEndpointOptions DataEndpoint { get; init; } = new();

    /// <summary>Bulk keyset streaming endpoint settings (<c>Gateway:RestApi:BulkStream</c>).</summary>
    public BulkStreamOptions BulkStream { get; init; } = new();
}

/// <summary>
/// Switch for the REST data family: <c>/api/v1/data/*</c> (GET, change-token, POST changes) and the sample-rows route.
/// Off means the routes are not mapped (router 404).
/// </summary>
public sealed class RestDataEndpointOptions
{
    /// <summary>Enables the REST data endpoint family.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Issue #222: Dedicated switch for bulk keyset streaming endpoints (/stream, /bulk).
/// </summary>
public sealed class BulkStreamOptions
{
    /// <summary>Enables bulk keyset streaming endpoints (null inherits <see cref="RestDataEndpointOptions.Enabled"/>).</summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// HMAC key of the keyset cursors shared by the bulk stream endpoints and GraphQL connections (the same value must be configured on every node).
    /// Outside Development a non-placeholder key of at least 32 UTF-8 bytes after trim is always required, otherwise startup fails
    /// (<c>BulkStreamSigningKeyOptionsValidator</c>). In Development an unset key logs a warning and uses a per-instance ephemeral key,
    /// so cursors are only valid on that instance until restart.
    /// </summary>
    public string? SigningKey { get; init; }
}

/// <summary>Governed WebSQL REST execution, DML, cost, and paging options (<c>Gateway:WebSql</c>).</summary>
public sealed class WebSqlOptions
{
    // SEC C-01/C-03: WebSQL is opt-in (secure default).
    /// <summary>Enables the governed WebSQL REST endpoints.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Enables governed DML write statements (INSERT/UPDATE/DELETE) through WebSQL.</summary>
    public bool AllowDml { get; init; } = false;

    /// <summary>Default row limit applied when a WebSQL query omits an explicit limit.</summary>
    public long DefaultMaxRows { get; init; } = 1000;

    /// <summary>Hard upper bound on rows returned by a single WebSQL query.</summary>
    public long MaxAllowedRows { get; init; } = 10000;

    /// <summary>Maximum permitted SQL statement length in characters.</summary>
    public int MaxQueryLength { get; init; } = 64_000;

    /// <summary>
    /// Hard upper bound in seconds of the TTL a data source can configure through
    /// <c>DataSources:Connections:*:ResultCacheTtlSeconds</c>.
    /// </summary>
    public const int ResultCacheHardMaxTtlSeconds = 3600;

    /// <summary>
    /// Global memory budget of the WebSQL result cache in megabytes (0..4096). 0 (default) switches the cache off for every data
    /// source; a source additionally needs <c>ResultCacheTtlSeconds</c> greater than 0.
    /// </summary>
    public int ResultCacheMaxMegabytes { get; init; } = 0;

    /// <summary>Maximum size of one cached WebSQL result in megabytes (at least 1; values above <c>ResultCacheMaxMegabytes</c> are clamped to it).</summary>
    public int ResultCacheMaxEntryMegabytes { get; init; } = 16;

    /// <summary>Maximum memory one tenant and user may occupy in the WebSQL result cache in megabytes (at least 1; values above <c>ResultCacheMaxMegabytes</c> are clamped to it).</summary>
    public int ResultCacheMaxUserMegabytes { get; init; } = 64;

    /// <summary>Maximum number of rows of one cached WebSQL result (1..100,000). Larger results are streamed and not stored.</summary>
    public int ResultCacheMaxRowsPerEntry { get; init; } = 10_000;

    /// <summary>Maximum number of cached WebSQL results per tenant and user (1..10,000).</summary>
    public int ResultCacheMaxEntriesPerUser { get; init; } = 100;

    /// <summary>Default execution timeout in seconds for WebSQL statements.</summary>
    public int ExecutionTimeoutSeconds { get; init; } = 30;

    /// <summary>Default data source connection key targeted when no explicit data source is specified.</summary>
    public string DefaultDataSourceName { get; init; } = "default";

    /// <summary>Maximum concurrent prepared statement sessions permitted per caller identity.</summary>
    public int MaxConcurrentSessionsPerUser { get; init; } = 10;

    /// <summary>Maximum total prepared statement sessions held across all callers.</summary>
    public int MaxTotalStatementSessions { get; init; } = 1000;

    /// <summary>Retention window in minutes for prepared WebSQL statement sessions.</summary>
    public int StatementRetentionMinutes { get; init; } = 5;

    /// <summary>
    /// Issue #277: opt-in switch for<c>WITH RECURSIVE</c> in governed read statements. Default <c>false</c>; never enabled by
    /// a profile. Requires <see cref="ExecutionTimeoutSeconds"/>in 1..300 (the timeout is the work bound) and the provider
    /// temp-space prerequisites of the feature doc.
    /// </summary>
    public bool AllowRecursiveCte { get; init; } = false;

    /// <summary>
    /// Issue #277: recursion depth bound (1..10,000) emitted as <c>OPTION (MAXRECURSION n)</c> on SQL Server when
    /// <see cref="AllowRecursiveCte"/> is on. Validated at start; never taken from SQL text.
    /// </summary>
    public int MaxRecursionDepth { get; init; } = 100;

    /// <summary>maximum number of distinct values of one list parameter (1..10,000). Clamped per dialect at runtime.</summary>
    [Range(1, 10_000)]
    public int MaxListParameterValues { get; init; } = 1000;

    /// <summary>maximum number of list-valued parameters per request (1..64).</summary>
    [Range(1, 64)]
    public int MaxListParameters { get; init; } = 8;

    /// <summary>
    /// SEC C-03: Additional data sources (keys of DataSources.Connections) a WebSQL request may target.
    /// DefaultDataSourceName is always allowed; empty list = only DefaultDataSourceName.
    /// </summary>
    public List<string> AllowedDataSources { get; init; } = [];

    /// <summary>
    /// Logical-to-physical data source connection mappings (e.g. catalog/domain name -&gt; connection key in DataSources.Connections).
    /// </summary>
    public Dictionary<string, string> DataSourceMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SEC C-03: Optional per-tenant data source allowlist (tenant id -&gt; data sources). If the tenant has an
    /// entry, only these data sources are allowed, intersected with the global allowlist
    /// (DefaultDataSourceName + AllowedDataSources). Tenants without an entry keep the global allowlist.
    /// Applies to WebSQL and SQL endpoints (both run through GovernedSqlExecutionService).
    /// </summary>
    public Dictionary<string, List<string>> TenantDataSourceAllowlist { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// DML guardrail: maximum number of rows a single WebSQL DML statement may affect. The statement runs in a
    /// transaction; if more rows are affected it is rolled back and rejected. 0 = unlimited.
    /// </summary>
    public long MaxAffectedRows { get; init; } = 1000;

    /// <summary>
    /// Maximum number of DML write statements per minute allowed for a single tenant and caller identity when DML is enabled.
    /// </summary>
    public int WriteStatementsPerMinute { get; init; } = 60;

    /// <summary>
    /// Maximum cumulative number of rows affected per hour across DML write statements for a single tenant and caller identity when DML is enabled.
    /// </summary>
    public long WriteRowsPerHour { get; init; } = 10_000;

    /// <summary>
    /// Retention window in seconds for DML idempotency replay and conflict records.
    /// </summary>
    public int WriteIdempotencyTtlSeconds { get; init; } = 86_400;

    /// <summary>
    /// SQ-06 / SEC P-02: Additional SQL function names (unqualified, case-insensitive) permitted in WebSQL on top of the
    /// curated allowlist of the target dialect (TrinoSqlEngine.SqlFunctionAllowlists). Names on the built-in function
    /// denylist are never permitted, even if listed here. Since X1 the list can only narrow the dialect's function map
    /// (the compiler needs an emission rule for every function): a name without a rule is still rejected.
    /// </summary>
    public List<string> AdditionalAllowedFunctions { get; init; } = [];

    /// <summary>
    /// Heterogeneous Cross-Source / Federation Join options (DuckDB in-process engine).
    /// </summary>
    public CrossSourceOptions CrossSource { get; init; } = new();

    /// <summary>Cost estimation, quota admission and settlement options for WebSQL statements.</summary>
    public WebSqlCostOptions Cost { get; init; } = new();

    /// <summary>HMAC-bound statement paging options for WebSQL statements.</summary>
    public WebSqlPagingOptions Paging { get; init; } = new();
}

/// <summary>HMAC-bound statement paging options for WebSQL statements (<c>Gateway:WebSql:Paging</c>).</summary>
public sealed class WebSqlPagingOptions
{
    public const string SectionName = "Paging";

    /// <summary>Maximum permitted OFFSET value for offset-based WebSQL paging.</summary>
    [Range(0, 1_000_000)]
    public long MaxOffset { get; init; } = 10_000;

    /// <summary>Lifetime in minutes for signed WebSQL page tokens.</summary>
    [Range(1, 1440)]
    public int TokenLifetimeMinutes { get; init; } = 15;
}

/// <summary>Cost estimation, quota admission and settlement options for WebSQL statements (<c>Gateway:WebSql:Cost</c>).</summary>
public sealed class WebSqlCostOptions
{
    public const string SectionName = "Cost";

    /// <summary>Maximum estimated cost permitted for a single WebSQL statement.</summary>
    [Range(1, 1_000_000)]
    public int MaxStatementCost { get; init; } = 2000;

    /// <summary>Cost weight added per cross join in a WebSQL statement.</summary>
    [Range(0, 100_000)]
    public int CrossJoinCost { get; init; } = 250;

    /// <summary>Cost weight added per set operation (UNION/INTERSECT/EXCEPT) in a WebSQL statement.</summary>
    [Range(0, 10_000)]
    public int SetOperationCost { get; init; } = 25;

    /// <summary>Number of OFFSET rows corresponding to one cost unit.</summary>
    [Range(1, 1_000_000)]
    public int OffsetRowsPerCostUnit { get; init; } = 100;

    /// <summary>Cost weight charged per second of actual statement execution.</summary>
    [Range(0, 10_000)]
    public int ExecutionSecondCost { get; init; } = 10;

    /// <summary>Base cost weight charged for a WebSQL DML statement.</summary>
    [Range(0, 100_000)]
    public int DmlStatementCost { get; init; } = 50;
}

/// <summary>Heterogeneous cross-source federation options (<c>Gateway:WebSql:CrossSource</c>).</summary>
public sealed class CrossSourceOptions
{
    /// <summary>Enables cross-source federated query execution.</summary>
    public bool Enabled { get; init; } = false;
    /// <summary>
    /// Transport opt-in for federated (cross-source) queries. Valid names are the constants of <c>SqlTransports</c>:
    /// WebSql, Trino, Rest, Mcp, ArrowExport, SqlEndpoints, AsyncJob and Olap; an unknown name aborts the start.
    /// When not configured, only WebSql and Trino are allowed; every other channel (including AsyncJob and Olap) is rejected
    /// on federated queries unless listed here. A configured list replaces the default entirely. An explicitly empty list
    /// allows no transport (fail-closed).
    /// Nullable on purpose: the configuration binder appends to pre-filled lists, so a non-empty default could not be narrowed.
    /// </summary>
    public List<string>? AllowedTransports { get; init; }

    private static readonly string[] DefaultAllowedTransports = ["WebSql", "Trino"];

    /// <summary>Configured <see cref="AllowedTransports"/> or the default list (WebSql, Trino).</summary>
    public IReadOnlyList<string> GetEffectiveAllowedTransports() => AllowedTransports ?? (IReadOnlyList<string>)DefaultAllowedTransports;
    /// <summary>Maximum number of tables allowed in a single cross-source federated query.</summary>
    public int MaxTableCount { get; init; } = 5;

    /// <summary>Maximum number of rows staged per source table in a cross-source query.</summary>
    public int MaxStagedRowsPerTable { get; init; } = 50000;

    /// <summary>Maximum total rows staged across all tables in a cross-source query.</summary>
    public int MaxTotalStagedRows { get; init; } = 200000;

    /// <summary>Maximum bytes staged per source table in a cross-source query.</summary>
    public long MaxStagedBytesPerTable { get; init; } = 32 * 1024 * 1024;

    /// <summary>Maximum total bytes staged across all tables in a cross-source query.</summary>
    public long MaxTotalStagedBytes { get; init; } = 128 * 1024 * 1024;

    /// <summary>Execution timeout in seconds for cross-source federated queries.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Issue #123: Maximum number of tables permitted in a single same-source AST subtree pushdown (default 12).
    /// </summary>
    public int MaxTablesPerSubtree { get; init; } = 12;

    /// <summary>
    /// Upper bound (1..16) on parallel source reads. Currently staging reads sources sequentially, which trivially satisfies the bound.
    /// DuckDB memory and spill limits are configured with <c>Gateway:DuckDbOlap:MaxMemory</c> and <c>MaxTempDirectorySize</c>.
    /// </summary>
    public int MaxParallelSourceReads { get; init; } = 4;
    /// <summary>Allows non-equi joins in cross-source federated queries (default false).</summary>
    public bool AllowNonEquiJoins { get; init; } = false;

    /// <summary>
    /// HTTP data source authentication modes allowed in federated queries. When not configured: None, StaticApiKey and
    /// ClientCredentials. A configured list replaces that default entirely. Nullable on purpose: the configuration
    /// binder appends to pre-filled lists, so a non-empty default could not be narrowed. Enforced fail-closed by the
    /// federated staging step before any source is read: an HTTP source whose auth mode is not listed is denied with
    /// error code HTTP_AUTH_MODE_DENIED (HTTP 403). ForwardBearerToken is never permitted, even if listed. An empty JSON
    /// array aborts the start (it would silently apply the default); to permit no federated HTTP source, set
    /// <see cref="Enabled"/> to false.
    /// </summary>
    public List<string>? AllowedHttpAuthModes { get; init; }

    private static readonly string[] DefaultAllowedHttpAuthModes = ["None", "StaticApiKey", "ClientCredentials"];

    /// <summary>Configured <see cref="AllowedHttpAuthModes"/> or the default list.</summary>
    public IReadOnlyList<string> GetEffectiveAllowedHttpAuthModes() => AllowedHttpAuthModes ?? (IReadOnlyList<string>)DefaultAllowedHttpAuthModes;

    /// <summary>Issue #122: Governed WebSQL filter pushdown to remote relational SQL data sources.</summary>
    public PredicatePushdownOptions PredicatePushdown { get; init; } = new();

    /// <summary>Issue #124: Governed dynamic key-set filters in cross-source WebSQL joins.</summary>
    public DynamicKeySetOptions DynamicKeySet { get; init; } = new();

    /// <summary>Issue #132: Co-located SQL Server data source execution options.</summary>
    public CoLocationOptions CoLocation { get; init; } = new();
}

/// <summary>Dynamic key-set filter options for cross-source WebSQL joins.</summary>
public sealed class DynamicKeySetOptions
{
    /// <summary>Maximum number of distinct keys extracted for a dynamic key-set join filter.</summary>
    public int MaxKeySetKeys { get; init; } = 2000;

    /// <summary>Maximum number of key-set batches issued against a driven source table.</summary>
    public int MaxKeySetBatches { get; init; } = 10;

    /// <summary>Maximum number of keys included in a single key-set batch.</summary>
    public int MaxKeySetBatchSize { get; init; } = 500;
}

/// <summary>Co-located SQL Server data source execution options.</summary>
public sealed class CoLocationOptions
{
    /// <summary>Enables co-located execution for eligible same-server SQL data sources.</summary>
    public bool Enabled { get; init; } = false;
}

/// <summary>Remote relational SQL filter pushdown options.</summary>
public sealed class PredicatePushdownOptions
{
    /// <summary>Maximum number of literals permitted in a pushed-down IN list predicate.</summary>
    public int MaxInListCount { get; init; } = 1000;
}

/// <summary>File-backed governed SQL endpoint options (<c>Gateway:SqlEndpoints</c>).</summary>
public sealed class SqlEndpointsOptions
{
    /// <summary>Enables file-backed governed SQL endpoints.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Directory scanned for governed SQL endpoint definition files.</summary>
    public string Directory { get; init; } = "queries";

    /// <summary>Enables automatic hot-reload when SQL endpoint files change on disk.</summary>
    public bool EnableHotReload { get; init; } = true;

    /// <summary>
    /// Writes dbt models as SQL endpoint files. Note: SQL endpoints (also dbt-generated ones) are executed via
    /// GovernedSqlExecutionService and therefore require <c>WebSql.Enabled = true</c> (default: false); a model's
    /// <c>-- @datasource</c> (dbt database) must be <c>WebSql.DefaultDataSourceName</c> or listed in <c>WebSql.AllowedDataSources</c>.
    /// </summary>
    public bool AutoSyncFromDbt { get; init; } = true;

    /// <summary>F-SQL-02: Governed stored procedure endpoints (disabled by default).</summary>
    public ProcedureEndpointsOptions Procedures { get; init; } = new();
}

/// <summary>
/// F-SQL-02: Options for governed stored procedure endpoints (SQL Server). The procedure connection must use a dedicated
/// technical login with EXECUTE on the allowed schemas only (ADR-018).
/// </summary>
public sealed class ProcedureEndpointsOptions
{
    /// <summary>Enables governed stored procedure endpoints.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Directory containing <c>*.proc.yaml</c> and <c>*.proc.yml</c> declaration files.</summary>
    public string Directory { get; init; } = "procedures";

    /// <summary>Key in <c>DataSources.Connections</c> of the EXECUTE-only technical login.</summary>
    public string ConnectionName { get; init; } = "procedures";

    /// <summary>Additional data source names a declaration may use (the connection name is always allowed).</summary>
    public List<string> AllowedDataSources { get; init; } = [];

    /// <summary>Only procedures in these schemas can be exposed. Empty = nothing can be exposed (fail-closed).</summary>
    public List<string> AllowedSchemas { get; init; } = [];

    [Range(1, 1440)] public int RevalidationIntervalMinutes { get; init; } = 15;
    [Range(0, 60000)] public int LockTimeoutMs { get; init; } = 5000;
    [Range(1, 100000)] public int MaxRows { get; init; } = 5000;

    /// <summary>
    /// Upper bound of raw rows one call with an active row filter (consent row scope) may read before the scope is applied.
    /// Null resolves to <c>Math.Min(10 * MaxRows, 100000)</c> (see <see cref="EffectiveMaxScanRows"/>). It must not be below
    /// <see cref="MaxRows"/> and not above 100000 (startup validation). Cost: the row scope resolver checks the candidates in
    /// batches of at most 1000 keys, so a scoped call can cost up to <c>MaxScanRows / 1000</c> resolver round trips (100 at the
    /// cap) on the procedure connection while the concurrency lease is held; <see cref="MaxResponseBytes"/> keeps bounding the
    /// raw scan, and exceeding it is a fixed RESPONSE_TOO_LARGE error without a partial answer.
    /// </summary>
    [Range(1, 100000)] public int? MaxScanRows { get; init; }

    /// <summary>Effective raw scan cap: <c>MaxScanRows ?? Math.Min(10 * MaxRows, 100000)</c> (overflow-safe).</summary>
    public int EffectiveMaxScanRows => MaxScanRows ?? (int)Math.Min(10L * MaxRows, 100000L);

    /// <summary>R-SQL-8: response budget of one procedure call (estimated JSON size; binary values count as Base64).</summary>
    [Range(1024, 104857600)] public long MaxResponseBytes { get; init; } = 10485760;
    [Range(1, 1000000)] public int MaxStringParameterLength { get; init; } = 4000;
    [Range(1, 300)] public int MaxTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Controls whether declared procedure contracts must carry <c>integrity.ddl_hash</c>.
    /// Null resolves to required in every environment except exactly <c>Development</c> (Staging, Production, unknown and
    /// missing environment names: required). A missing hash is then rejected; otherwise it only logs a warning on every (re)load.
    /// </summary>
    public bool? RequireDdlHashForDeclared { get; init; }
}
