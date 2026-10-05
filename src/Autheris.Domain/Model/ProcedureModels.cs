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
    IReadOnlyDictionary<string, string> ParameterSqlTypes)
{
    public static ProcedureValidationResult Failed(params string[] errors) =>
        new(false, errors, [], [], new Dictionary<string, string>());
}

/// <summary>A procedure together with its runtime validation state.</summary>
public sealed record RegisteredProcedure(
    ProcedureDefinition Definition,
    ProcedureState State,
    ProcedureValidationResult? Validation,
    DateTimeOffset? ValidatedAt,
    string? DisabledReason);
