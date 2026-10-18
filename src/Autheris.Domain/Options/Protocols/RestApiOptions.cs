using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>PLAN-SEC-115: REST read-path switches (startup only; a restart is required).</summary>
public sealed class RestApiOptions
{
    public RestDataEndpointOptions DataEndpoint { get; init; } = new();
}

/// <summary>
/// Switch for the REST data family: <c>/api/v1/data/*</c> (GET, change-token, POST changes) and the sample-rows route.
/// Off means the routes are not mapped (router 404).
/// </summary>
public sealed class RestDataEndpointOptions
{
    public bool Enabled { get; init; } = true;
}

public sealed class WebSqlOptions
{
    // SEC C-01/C-03: WebSQL is opt-in (secure default).
    public bool Enabled { get; init; } = false;
    public bool AllowDml { get; init; } = false;
    public long DefaultMaxRows { get; init; } = 1000;
    public long MaxAllowedRows { get; init; } = 10000;
    public int MaxQueryLength { get; init; } = 64_000;
    public int ExecutionTimeoutSeconds { get; init; } = 30;
    public string DefaultDataSourceName { get; init; } = "default";
    public int MaxConcurrentSessionsPerUser { get; init; } = 10;
    public int MaxTotalStatementSessions { get; init; } = 1000;
    public int StatementRetentionMinutes { get; init; } = 5;

    /// <summary>REQ-SQL-128: maximum number of distinct values of one list parameter (1..10,000). Clamped per dialect at runtime.</summary>
    [Range(1, 10_000)]
    public int MaxListParameterValues { get; init; } = 1000;

    /// <summary>REQ-SQL-128: maximum number of list-valued parameters per request (1..64).</summary>
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
    /// SEC M-20: Roles that are authorized to execute DML via WebSQL (in addition to AllowDml).
    /// Consent and Casbin only evaluate read access, so DML is rejected unless the caller holds one of these roles.
    /// </summary>
    public List<string> DmlWriterRoles { get; init; } = [];

    /// <summary>
    /// DML guardrail: maximum number of rows a single WebSQL DML statement may affect. The statement runs in a
    /// transaction; if more rows are affected it is rolled back and rejected. 0 = unlimited.
    /// </summary>
    public long MaxAffectedRows { get; init; } = 1000;

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
}

public sealed class CrossSourceOptions
{
    public bool Enabled { get; init; } = false;
    /// <summary>
    /// Transport opt-in for federated (cross-source) queries. Valid names are the constants of <c>SqlTransports</c>:
    /// WebSql, Trino, Rest, Mcp, ArrowExport, FlightSql, SqlEndpoints, AsyncJob and Olap; an unknown name aborts the start.
    /// When not configured, only WebSql and Trino are allowed; every other channel (including AsyncJob and Olap) is rejected
    /// on federated queries unless listed here. A configured list replaces the default entirely. An explicitly empty list
    /// allows no transport (fail-closed).
    /// Nullable on purpose: the configuration binder appends to pre-filled lists, so a non-empty default could not be narrowed.
    /// </summary>
    public List<string>? AllowedTransports { get; init; }

    private static readonly string[] DefaultAllowedTransports = ["WebSql", "Trino"];

    /// <summary>Configured <see cref="AllowedTransports"/> or the default list (WebSql, Trino).</summary>
    public IReadOnlyList<string> GetEffectiveAllowedTransports() => AllowedTransports ?? (IReadOnlyList<string>)DefaultAllowedTransports;
    public int MaxTableCount { get; init; } = 5;
    public int MaxStagedRowsPerTable { get; init; } = 50000;
    public int MaxTotalStagedRows { get; init; } = 200000;
    public long MaxStagedBytesPerTable { get; init; } = 32 * 1024 * 1024;
    public long MaxTotalStagedBytes { get; init; } = 128 * 1024 * 1024;
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Upper bound (1..16) on parallel source reads. Currently staging reads sources sequentially, which trivially satisfies the bound.
    /// DuckDB memory and spill limits are configured with <c>Gateway:DuckDbOlap:MaxMemory</c> and <c>MaxTempDirectorySize</c>.
    /// </summary>
    public int MaxParallelSourceReads { get; init; } = 4;
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
}

public sealed class DynamicKeySetOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxKeySetKeys { get; init; } = 2000;
    public int MaxKeySetBatches { get; init; } = 10;
    public int MaxKeySetBatchSize { get; init; } = 500;
}

public sealed class PredicatePushdownOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxInListCount { get; init; } = 1000;
}

public sealed class SqlEndpointsOptions
{
    public bool Enabled { get; init; } = true;
    public string Directory { get; init; } = "queries";
    public bool EnableHotReload { get; init; } = true;

    /// <summary>
    /// Writes dbt models as SQL endpoint files. Note: SQL endpoints (also dbt-generated ones) are executed via
    /// GovernedSqlExecutionService and therefore require <c>WebSql.Enabled = true</c> (default: false); a model's
    /// <c>-- @datasource</c> (dbt database) must be <c>WebSql.DefaultDataSourceName</c> or listed in <c>WebSql.AllowedDataSources</c>.
    /// </summary>
    public bool AutoSyncFromDbt { get; init; } = true;
    public int MaxQueryTimeoutSeconds { get; init; } = 60;

    /// <summary>F-SQL-02: Governed stored procedure endpoints (disabled by default).</summary>
    public ProcedureEndpointsOptions Procedures { get; init; } = new();
}

/// <summary>
/// F-SQL-02: Options for governed stored procedure endpoints (SQL Server). The procedure connection must use a dedicated
/// technical login with EXECUTE on the allowed schemas only (ADR-018).
/// </summary>
public sealed class ProcedureEndpointsOptions
{
    public bool Enabled { get; init; } = false;

    /// <summary>Directory containing <c>*.proc.sql</c> declaration files.</summary>
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
    public bool EnableHotReload { get; init; } = true;

    /// <summary>
    /// Review P-1: Declarations with <c>validation: declared</c> skip the catalog validation (no check of SECURITY POLICY,
    /// permissions, dynamic SQL or write access). They are accepted outside the Development environment only when this
    /// switch is set explicitly. Even then they need <c>result_table</c> and non-empty <c>outputs</c>, and the declared
    /// outputs are governed with the consent column rules of the result table.
    /// </summary>
    public bool AllowDeclaredValidation { get; init; } = false;

    /// <summary>
    /// INC-2 D-8 / SEC-PROC152I2-13: whether <c>validation: declared</c> declarations must carry <c>integrity.ddl_hash</c>.
    /// Null resolves to required in every environment except exactly <c>Development</c> (Staging, Production, unknown and
    /// missing environment names: required). A missing hash is then rejected; otherwise it only logs a warning on every (re)load.
    /// </summary>
    public bool? RequireDdlHashForDeclared { get; init; }
}
