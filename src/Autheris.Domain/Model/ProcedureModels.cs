namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>F-SQL-02: Execution mode of a governed stored procedure.</summary>
public enum ProcedureMode
{
    Read = 0,
    Write = 1
}

/// <summary>F-SQL-02: How row-level security is enforced for the procedure.</summary>
public enum ProcedureRlsMode
{
    /// <summary>Database-side RLS (SQL Server SECURITY POLICY) reading the read-only SESSION_CONTEXT. Default.</summary>
    SessionContext = 0,

    /// <summary>No database-side RLS. Only allowed in Development.</summary>
    None = 1
}

/// <summary>Security context values the gateway may bind to a procedure parameter.</summary>
public enum ProcedureContextKey
{
    TenantId = 0,
    UserSid = 1,
    Purpose = 2
}

/// <summary>Declared client-supplied input parameter of a stored procedure.</summary>
public sealed record ProcedureParameter(
    string Name,
    string SqlType,
    Type ClrType,
    bool IsRequired,
    int? MaxLength = null,
    string? Description = null);

/// <summary>Binds a gateway security context value to a procedure parameter (never client-controlled).</summary>
public sealed record ProcedureContextBinding(ProcedureContextKey Key, string ParameterName);

/// <summary>Maps a result-set column to a catalog column so column governance can be applied.</summary>
public sealed record ProcedureResultColumn(string Name, bool IsCleared);

/// <summary>F-SQL-02: How a stored procedure endpoint is validated.</summary>
public enum ProcedureValidationMode
{
    /// <summary>Validated against the database catalog metadata (MSSQL default, Whitebox).</summary>
    Catalog = 0,

    /// <summary>Contract-first: input and output columns are explicitly declared in YAML (Blackbox).</summary>
    Declared = 1
}

/// <summary>The invocation style of the procedure endpoint.</summary>
public enum ProcedureKind
{
    /// <summary>Stored Procedure invoked via CommandType.StoredProcedure or CALL.</summary>
    Procedure = 0,

    /// <summary>Table-Valued Function invoked via SELECT * FROM func(...).</summary>
    TableValuedFunction = 1
}

/// <summary>
/// Immutable declaration of a stored procedure exposed as governed REST endpoint (F-SQL-02).
/// </summary>
public sealed record ProcedureDefinition(
    string Name,
    string Summary,
    string ProcedureName,
    ProcedureMode Mode,
    string? DataSource,
    IReadOnlyList<ProcedureParameter> Parameters,
    IReadOnlyList<ProcedureContextBinding> ContextBindings,
    ProcedureRlsMode RlsMode,
    string? ResultTable,
    IReadOnlyList<string> ClearedResultColumns,
    IReadOnlyList<string> RequiredRoles,
    bool AllowDynamicSql,
    int TimeoutSeconds,
    ProcedureValidationMode ValidationMode = ProcedureValidationMode.Catalog,
    IReadOnlyList<string>? DeclaredOutputs = null,
    ProcedureKind Kind = ProcedureKind.Procedure)
{
    public IReadOnlyList<string> DeclaredOutputs { get; init; } = DeclaredOutputs ?? [];

    /// <summary>
    /// Optional (composite) key that identifies a row of <see cref="ResultTable"/> in the result set. When set, a consent
    /// row filter on the result table is not a denial: the result rows are matched against the rows the filter allows
    /// (semi-join on these columns, evaluated by the database) and all other rows are removed. Without it a row filter
    /// denies the call (fail-closed).
    /// </summary>
    public IReadOnlyList<string> RowScopeKey { get; init; } = [];

    /// <summary>
    /// Column of <see cref="ResultTable"/> for each <see cref="RowScopeKey"/> column (same index). Equal to the result
    /// column unless declared as <c>result_column=table_column</c>. Empty means "same name".
    /// </summary>
    public IReadOnlyList<string> RowScopeKeyTable { get; init; } = [];

    /// <summary>
    /// Catalog domain of the tables the procedure reads. The catalog keys tables by their data source
    /// (TABLES.source_name), so it is the procedure's data source; "default" only when none is declared.
    /// </summary>
    public string CatalogDomain => string.IsNullOrWhiteSpace(DataSource) ? "default" : DataSource;

    /// <summary>
    /// Optional SQL types of the declared outputs (column name -> normalized type such as "float" or "varchar(255)").
    /// Documentation/contract only (OpenAPI schema); values are never converted at runtime.
    /// </summary>
    public IReadOnlyDictionary<string, string> DeclaredOutputTypes { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Contract-First / Pure-YAML: Result column source mappings (output column -> ResultColumnSource) declared in YAML.
    /// Enables table- and column-level consent and masking governance without requiring database catalog inspect permissions.
    /// </summary>
    public IReadOnlyDictionary<string, ResultColumnSource> DeclaredOutputSources { get; init; } = new Dictionary<string, ResultColumnSource>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicit list of physical tables referenced by the procedure, declared in YAML.
    /// </summary>
    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    /// <summary>
    /// Optional SHA-256 hash of the procedure DDL/header for schema drift prevention.
    /// </summary>
    public string? DdlHash { get; init; }

    /// <summary>
    /// Review P-2: Order of all arguments (client parameters and context bindings) as declared. Table-valued functions
    /// (and every other positional call) bind their arguments in exactly this order. When not set, client parameters
    /// come first, followed by the context bindings.
    /// </summary>
    public IReadOnlyList<string> ArgumentOrder
    {
        get => _argumentOrder ?? [.. Parameters.Select(p => p.Name), .. ContextBindings.Select(c => c.ParameterName)];
        init => _argumentOrder = value;
    }

    private readonly IReadOnlyList<string>? _argumentOrder;
}

/// <summary>Lifecycle state of a registered procedure.</summary>
public enum ProcedureState
{
    /// <summary>Declared but not (yet) validated against the database.</summary>
    Pending = 0,
    Active = 1,
    /// <summary>Validation failed or drift detected; the endpoint answers 503.</summary>
    Disabled = 2
}

/// <summary>Result of validating a declaration against the database catalog.</summary>
public sealed record ProcedureValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ResultColumns,
    IReadOnlyList<string> ReferencedTables,
    IReadOnlyDictionary<string, string> ParameterSqlTypes,
    IReadOnlyDictionary<string, ResultColumnSource>? ResultColumnSources = null)
{
    public static ProcedureValidationResult Failed(params string[] errors) =>
        new(false, errors, [], [], new Dictionary<string, string>());
}

/// <summary>
/// SEC D-2: source of a result column as reported by <c>sys.dm_exec_describe_first_result_set_for_object(@id, 1)</c> (browse mode).
/// Table/Column are null for computed or ambiguous columns.
/// </summary>
public sealed record ResultColumnSource(string? Schema, string? Table, string? Column);

/// <summary>A procedure together with its runtime validation state.</summary>
public sealed record RegisteredProcedure(
    ProcedureDefinition Definition,
    ProcedureState State,
    ProcedureValidationResult? Validation,
    DateTimeOffset? ValidatedAt,
    string? DisabledReason);
