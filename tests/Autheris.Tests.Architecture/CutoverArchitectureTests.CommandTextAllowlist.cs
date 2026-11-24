namespace Autheris.Tests.Architecture;

public sealed partial class CutoverArchitectureTests
{
    /// <summary>
    /// The only production types that may assign <c>CommandText</c> (§28.4). Everything else, in particular every type of the governed path,
    /// reaches a database through <c>CompiledSql</c> and the binder. A type is allowed with a reason or not at all; the key is the top-level
    /// type, and a trailing <c>*</c> allows a namespace prefix.
    /// </summary>
    private static readonly (string Type, string Reason)[] CommandTextAllowlist =
    [
        // -- governed SQL: compiled text only --
        ("TrinoSqlEngine.Ast.Emit.DbCommandCompiledSqlBinder", "the binder: the only place that turns CompiledSql into command text"),
        ("Autheris.Infrastructure.Persistence.BindByNameOracleCommand", "DbCommand decorator: forwards the text the binder gave it to the Oracle command (BindByName)"),
        ("Autheris.Infrastructure.Persistence.GoldenQueries.GoldenQueryStoreOperations", "governance-store access with fixed, parameterised SQL constants (golden queries); no caller-supplied text"),
        ("Autheris.Infrastructure.Persistence.MetadataGuardedCommand", "DbCommand decorator: forwards the text to the inner command; the text is checked before every execution"),
        // -- the allowed exceptions of §28.9 (the text comes from the typed IR / the compiler, never from a client string) --
        ("Autheris.Application.Sql.Tree.GovernedTreeQueryService", "§28.9 OQ-GQL: executes compiled.Sql of the GraphQL tree compiler"),
        ("Autheris.Application.Olap.DuckDbOlapEngine", "§28.9 DuckDB staging: runs on the in-process staged copy, after the governed fetch"),
        ("Autheris.Application.Sql.TimeZones.DuckDbIcuProbe", "REQ-SQL-130 (BC-130-7): probe of the time zone state on throw-away in-memory DuckDB databases; constants only (sandbox setup, one conversion, two metadata reads), no client text"),
        ("Autheris.Application.Olap.OlapSqlInspector", "DuckDB parse-only (json_serialize_sql): the client text is passed as a quoted literal to the parser of a throw-away in-memory database; nothing is executed or staged"),
        ("Autheris.Infrastructure.Caching.TableCache.Storage.DuckDbConnectionPool", "DuckDB table cache connection pool: executes validated ATTACH statements on reader initialization; no client text"),
        ("Autheris.Application.VirtualFilters.Federation.SqlVirtualFilterKeySetProvider", "virtual-filter key set: the query is rendered from the typed key-source definition"),
        // -- statements of the gateway itself (no client text) --
        ("Autheris.Application.Services.DbSessionContextInitializer", "fixed session-initialisation statements (tenant and user session context)"),
        ("Autheris.Infrastructure.Persistence.SqlConnectionFactory", "fixed session pinning and hardening statements of a new connection"),
        ("Autheris.Infrastructure.Streaming.Producers.CdcSourceQueries", "CDC producer: fixed change-tracking statements over validated declared table and column names, bound values, no client text"),
        ("Autheris.Application.Procedures.Services.ProcedureInvoker", "stored-procedure call by a validated catalog name with bound parameters (not a WebSQL statement)"),
        ("Autheris.Application.Procedures.Services.SqlProcedureRowScopeResolver", "procedure row-scope key query over validated catalog identifiers, bound values, read-only; splices validated policy row-filter text (§7.3 consumer)"),
        ("Autheris.Infrastructure.Persistence.Catalog.*", "column-metadata readers: fixed information_schema/PRAGMA queries of the governed catalog provider"),
        // -- persistence of the gateway's own governance store (its schema bootstrap and repositories) --
        ("Autheris.Infrastructure.Persistence.GovernanceRepository", "unified governance store repository (WP-SIMPL-20)"),
        ("Autheris.Infrastructure.Persistence.ConsentWriteRulesSql", "governance store: consent write rules SQL helpers (WP-SIMPL-45 PR 4A), fixed statements with bound values, no client text"),
        ("Autheris.Infrastructure.Persistence.Dialects.*", "governance SQL dialect implementations (WP-SIMPL-20)"),
        ("Autheris.Infrastructure.Persistence.CatalogKeyDefinitionStore", "governance store: declared key/index tables of the three engines (issue #94), fixed statements with bound values, no client text"),
        ("Autheris.Infrastructure.Persistence.DatasetCatalogStore", "governance store: dataset catalog tags and visibility tier (issue #111), fixed statements with bound values over the three engines, no client text"),
        ("Autheris.Infrastructure.Persistence.DiscoveryArtifactStore", "governance store: catalog discovery artifacts, fixed statements with bound values, no client text"),
        ("Autheris.Infrastructure.Persistence.Migrations.*", "governance schema migrations (WP-ARC-B-12): fixed DDL and version-table statements, no client text"),
        ("Autheris.Infrastructure.Persistence.TenantRegistryStore", "tenant registry table of the governance store"),
        ("Autheris.Infrastructure.Persistence.VirtualFilterStore", "virtual filter table of the governance store"),
        ("Autheris.Infrastructure.Persistence.CallerAttributeStore", "caller attribute registry tables of the governance store (REQ-GOV-129): fixed statements with bound values, no client text"),
        ("Autheris.Infrastructure.Persistence.TrustedDocumentStore", "app-scoped trusted document tables of the governance store (Issue #281): fixed statements with bound values, no client text"),
        ("Autheris.Application.Identity.Attributes.AttributeTableProvider", "caller attribute lookup (REQ-GOV-129 A-5.2): the statement is built by AttributeTableQuery from validated identifiers (^[A-Za-z_][A-Za-z0-9_]{0,127}$, quoted per dialect) with the subject, tenant and issuer bound; no client text"),
        ("Autheris.Infrastructure.Persistence.AccessProfileStore", "access profile tables of the governance store (WP-SC-03): fixed statements with bound values, only integer-indexed parameter names and enum literals are spliced, no client text"),
        ("Autheris.Infrastructure.Persistence.ConsumerAggregationStore", "consumer aggregation store (Issue #114, SEC-AUD114-05): fixed rollup statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Usage.UsageStore", "governance usage store (WP-SIMPL-42 PR 2): fixed rollup, tariff and retention statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Persistence.ConsentGrantSqlCore", "consent grant statements (ARCH-MNT-22, SC-04): tenant-scoped statements plus the row-locked revoke by id; fixed SQL, bound values, no client text"),
        ("Autheris.Infrastructure.Persistence.AuditSqlCore", "audit statement execution (ARCH-MNT-22 Step 2): the dialect repositories hand in fixed statements, the core binds values and maps rows only; no client text"),
        ("Autheris.Infrastructure.Persistence.ConsentSqlCore", "consent statement execution (ARCH-MNT-22 Step 4): the dialect repositories hand in dialect statements, the core binds values and maps rows only; no client text"),
        ("Autheris.Infrastructure.Streaming.Producers.CustomQuery.CustomQueryPollerService", "CDC custom query poller service (Issue #101, Issue #210): reads watermark batches using configured poller queries, bound parameters, no client text"),
        ("Autheris.Application.DataSources.Relations.GovernedCrossSourceRowReader", "Cross-source GraphQL relation row reader (Issue #75, Issue #223): executes batched key lookups over child data sources (VF-02): quoted catalog identifiers, explicit projection, bound key values and bound tenant predicate, session initialized per read, no client text"),
        ("Autheris.Application.State.Caching.TableCache.Governance.GovernedTableCacheExecutionService", "Governed table cache execution service (BL-04, REQ-DATA-BL04): executes governed SELECT queries with AST tenant pushdown and bound parameters over embedded cache store; no client text"),
        ("Autheris.Infrastructure.Caching.TableCache.DuckDbPersistentTableCacheStore", "DuckDB persistent table cache store (BL-04, REQ-DATA-BL04): executes internal staging, swapping, insertion and drop statements on embedded cache database with bound parameters; no client text"),
        ("Autheris.Application.Products.ProductConnector", "data product stage 2 reader: one fixed SELECT over the active view of the caller tenant's cached product result (the quoted view name comes from the cache catalog, never from client text); the governed staging then applies the caller's decision to every row"),
        ("Autheris.Infrastructure.Caching.TableCache.InMemoryTableCacheStore", "In-memory table cache store (BL-04, REQ-DATA-BL04): executes internal memory-table DDL and mutation statements with bound parameters; no client text"),
        ("Autheris.Api.Hosting.Warmup.DataSourceConnectionWarmup", "Startup pre-warming probe (Issue #254, SEC-PERF254-02): executes read-only constant expression SELECT 1 to verify database connectivity on startup; constants only, no client text"),
        ("Autheris.Infrastructure.Persistence.GovernanceSchemaValidator", "governance store startup validator (SEC-SC-20): validates presence of Wave 2 tables at startup, fixed information_schema queries, no client text"),
        ("Autheris.Infrastructure.Persistence.IdentityStore", "governance store: SCIM and local identity provider storage (WP-SC-07, REQ-SCALE-5000-WAVE2), fixed statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Persistence.RightsAndPoliciesStore", "governance store: functional rights mappings and scoped access policies (WP-SC-06, WP-SC-14), fixed statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Persistence.GovernanceDomainStore", "governance store: governance domains, table assignments, and object protection (Issue #1009), fixed constant SQL statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Persistence.AccessPolicySettingsStore", "governance store: tenant access policy settings (Issue #1009), fixed constant SQL statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Persistence.MigrationPurgeStore", "governance store: D13 migration purge report, archived head reader, and schema version check (Issue #1009), fixed constant SQL statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Persistence.CatalogVisibilityGrantStore", "governance store: container-scoped catalog visibility grants (Issue #1009), fixed constant SQL statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Persistence.PermissionRequestStore", "governance store: single-transaction permission decision CAS and query store (Issue #1009), fixed constant SQL statements with bound parameters, no client text"),
    ];
}
