namespace TrinoSqlEngine.Ast.Emit;

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>Origin of a bound value (plan 4.2). It decides how the binder and the plan cache treat the value.</summary>
public enum ParameterOrigin { ClientNamed, ClientPositional, QueryLiteral, Tenant, Policy, Mask, PageOffset, KeysetSeek }

/// <summary>The value family a client bind must conform to because the statement compares it with a typed column under a row policy (checked by the binder).</summary>
public enum BindFamily { Numeric, Temporal, Boolean, Text }

public enum SqlStatementClass { Select, Insert, Update, Delete, Merge }

/// <summary>
/// A bound parameter. <paramref name="Marker"/> is the exact marker text in the SQL; <paramref name="Name"/> is the provider
/// binding name. <paramref name="SourceName"/> is the client parameter name (<see cref="ParameterOrigin.ClientNamed"/>) or the
/// policy parameter name (Tenant, Policy, Mask); it is null for query literals. For client parameters
/// <paramref name="Value"/> is null (the binder resolves it per request) and <paramref name="Type"/> is a placeholder.
/// </summary>
public readonly record struct BoundParameter(
    string Marker,
    string Name,
    int Ordinal,
    object? Value,
    SqlParameterType Type,
    ParameterOrigin Origin,
    string? SourceName = null,
    string? ColumnType = null,
    int? ListIndex = null,
    int? ListCount = null,
    bool ZoneArgument = false,
    bool EscapeLikePattern = false,
    BindFamily? RequiredFamily = null)
{
    // INV-16: bound values never appear in logs, spans or exception text.
    public override string ToString() => $"BoundParameter {{ Marker = {Marker}, Type = {Type}, Origin = {Origin} }}";
}

/// <summary>Compiled, parameterized SQL (plan 4.2).</summary>
public sealed record CompiledSql(
    string Sql,
    ImmutableArray<BoundParameter> Parameters,
    TargetSqlDialect Dialect,
    SqlStatementClass StatementClass,
    ImmutableArray<SecurityPredicateId> AppliedPredicates,
    string CompilerVersion)
{
    /// <summary>
    /// CR-ADG-45: the expected affected row count of a check-option INSERT. The setter is internal, so a <c>with</c> copy outside
    /// the assembly cannot drop the requirement; the binders additionally derive it from the reserved check alias in the SQL.
    /// </summary>
    public int? ExpectedAffectedRows { get; internal init; }

    /// <summary>
    /// Output column mask flags for each root output column of a SELECT query.
    /// </summary>
    public ImmutableArray<bool> OutputColumnMasks { get; internal init; } = ImmutableArray<bool>.Empty;

    /// <summary>The keyset shape of an eligible paged read (<see cref="CompileRequest.Keyset"/> was set); null otherwise.</summary>
    public KeysetKeyInfo? KeysetKey { get; internal init; }

    /// <summary>Why a statement with a <see cref="CompileRequest.Keyset"/> candidate is not keyset-eligible (a <see cref="KeysetUnavailableReasons"/> value), else null.</summary>
    public string? KeysetUnavailable { get; internal init; }

    /// <summary>
    /// Issue #277: true when the emitted statement contains a recursive WITH definition. The cache keeps it, so a template hit
    /// audits like the first compile.
    /// </summary>
    public bool HasRecursiveCte { get; init; }

    /// <summary>
    /// True when the compiler proved a static affected-row upper bound (<see cref="PreBoundedMaxRows"/>) on a bounded single-table
    /// DML statement (INSERT VALUES or PK-constrained UPDATE/DELETE). Internal-init so callers outside the engine cannot forge it.
    /// </summary>
    public bool IsPreBoundedDml { get; internal init; }

    /// <summary>Static upper bound on rows affected by a pre-bounded DML statement, when <see cref="IsPreBoundedDml"/> is true.</summary>
    public long? PreBoundedMaxRows { get; internal init; }

    /// <summary>
    /// Value-free pre-execution SELECT-RLS check compiled for bounded single-table INSERT on dialects without INSERT row counts
    /// (such as Databricks Delta). Evaluated by the checked DML executor before any database command is executed.
    /// </summary>
    public Ast.Security.PreExecutionRowCheck? PreExecutionRowCheck { get; internal init; }

    /// <summary>
    /// Current request's rebound policy values for <see cref="PreExecutionRowCheck"/>. Never stored in <see cref="CompiledSqlTemplate"/>.
    /// </summary>
    public ImmutableDictionary<string, PolicyValue> PreExecutionPolicyValues { get; internal init; } = ImmutableDictionary<string, PolicyValue>.Empty;

    /// <summary>
    /// CR-ADG-35: true for an INSERT with check-option semantics. The executor must run the statement inside a transaction and call
    /// <see cref="DmlCheckOption.Enforce"/> with the affected row count before it commits; a difference rolls the transaction back.
    /// </summary>
    public bool RequiresRowCountCheck => ExpectedAffectedRows.HasValue;

    /// <summary>
    /// 130 (BC-130-5d): a time zone slot is consumable only through the production binder, which maps its value through the closed zone
    /// set before the command exists. Every other consumer of <see cref="Parameters"/> (generated-query paths, key-set providers, tree queries) calls
    /// this first, so a zone slot never reaches a provider as an unmapped value or as <c>NULL</c>. The message carries no value and no name.
    /// </summary>
    public void EnsureNoZoneArguments()
    {
        foreach (var parameter in Parameters)
        {
            if (parameter.ZoneArgument)
            {
                throw new InvalidOperationException("The compiled SQL contains a parameter that only the governed binder may bind.");
            }
        }
    }
}

/// <summary>The row-count contract of an INSERT with check-option semantics (CR-ADG-35).</summary>
public static class DmlCheckOption
{
    /// <summary>
    /// Throws <see cref="DmlCheckOptionViolationException"/> when <paramref name="affectedRows"/> differs from the expected count. The
    /// caller rolls the transaction back on that exception. A statement without the check passes unchanged.
    /// </summary>
    public static void Enforce(CompiledSql compiled, int affectedRows)
    {
        System.ArgumentNullException.ThrowIfNull(compiled);
        if (compiled.ExpectedAffectedRows is { } expected && affectedRows != expected)
        {
            throw new DmlCheckOptionViolationException(compiled.Dialect);
        }
    }
}

/// <summary>Supplies gateway-bound values for <see cref="PolicyParameterExpression"/> nodes; query literals come from the AST.</summary>
public sealed record ParameterSource(
    IReadOnlyDictionary<string, PolicyValue> PolicyValues,
    IReadOnlyDictionary<string, object?> ClientNamedValues)
{
    public static ParameterSource Empty { get; } = new(
        FrozenDictionary<string, PolicyValue>.Empty,
        new Dictionary<string, object?>());
}

public static class CompilerInfo
{
    /// <summary>Compiler version recorded in audit records, spans and the plan cache key (INV-8').</summary>
    public const string Version = "ast-compiler/1.0";
}
