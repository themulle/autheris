namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

/// <summary>What the validator needs to decide; every member is a function of the request (never of row data).</summary>
/// <param name="Supported">The capability flag of the compile dialect (<c>SupportsTimeZoneConversion</c>).</param>
/// <param name="Available">The application proved the conversion for the dialect (<c>CompileRequest.TimeZoneConversionAvailable</c>).</param>
/// <param name="Masks">Typed column masks: a masked column is never accepted as the timestamp (BC-130-1).</param>
/// <param name="TablesWithMaskedColumns">The mask guards' fallback for columns that do not resolve to a catalog table.</param>
/// <param name="ClientLists">Client list parameters of the statement; a list is never a zone (BC-130-5c).</param>
/// <param name="Federated">The federated DuckDB path: a bare column is not a timestamp and a parameter is not a zone (A-8).</param>
/// <param name="IsMaskedByName">Federated only: whether a (qualifier, column) pair names a masked column; no catalog exists there.</param>
internal sealed record TimeZoneValidationOptions(
    TargetSqlDialect Dialect,
    bool Supported,
    bool Available,
    IColumnMaskProvider Masks,
    IReadOnlySet<string> TablesWithMaskedColumns,
    ClientListShapes ClientLists,
    bool Federated,
    Func<string?, string, bool>? IsMaskedByName = null);

/// <summary>
/// REQ-SQL-130 (A-3, BC-130-1/3/5c/8/12): checks every call of <c>convert_timezone</c> of the user tree, in this fixed order and with fixed messages:
/// dialect, availability, call and nesting caps, shape, zone arguments, timestamp argument. It selects the call by the simple name, compared
/// case-insensitively and independent of quoting; a qualified name ending in that name is the function too and fails validation. It is built on the
/// common traversal base, so every call in every clause, window, subquery, CTE, set operation and DML statement is reached.
/// </summary>
internal sealed class TimeZoneCallValidator : ColumnScopeTracker
{
    private readonly ITableCatalog _catalog;
    private readonly TimeZoneValidationOptions options;

    public TimeZoneCallValidator(ITableCatalog catalog, TimeZoneValidationOptions options)
        : base(catalog)
    {
        _catalog = catalog;
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>BC-130-8, R-6: product limits as engine constants (no configuration).</summary>
    internal const int MaxCallsPerStatement = 8;

    internal const int MaxNestingDepth = 3;

    internal const string FunctionName = "convert_timezone";

    private int _calls;
    private int _depth;
    private readonly HashSet<OrderByClause> _selectOrderBys = new(ReferenceEqualityComparer.Instance);

    /// <summary>True when the validated statement contains at least one call (informational; the guard flag is set either way).</summary>
    public bool SawCall => _calls > 0;

    public void Validate(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        _calls = 0;
        _depth = 0;
        Visit(statement);
    }

    /// <summary>BC-130-3: the single predicate that decides whether a call is the function. Also used by the generator interception.</summary>
    internal static bool IsTimeZoneFunction(SqlQualifiedName name) =>
        name.Parts.Count > 0 && string.Equals(name.Parts[^1].Value, FunctionName, StringComparison.OrdinalIgnoreCase);

    public override SqlNode VisitSelectStatement(SelectStatement node)
    {
        if (node.OrderBy is not null && node.Body is QuerySpecification)
        {
            _selectOrderBys.Add(node.OrderBy);
        }

        return base.VisitSelectStatement(node);
    }

    public override SqlNode VisitOrderByClause(OrderByClause node)
    {
        // The ORDER BY of a select statement sees the columns of its query block.
        if (!_selectOrderBys.Remove(node) || !PushLastBlock())
        {
            return base.VisitOrderByClause(node);
        }

        try
        {
            return base.VisitOrderByClause(node);
        }
        finally
        {
            PopBlock();
        }
    }

    public override SqlNode VisitFunctionCallExpression(FunctionCallExpression node)
    {
        if (!IsTimeZoneFunction(node.Name))
        {
            return base.VisitFunctionCallExpression(node);
        }

        if (!options.Supported) throw new SqlTimeZoneException(GovernedSqlErrorCodes.FunctionUnsupportedDialect);
        if (!options.Available) throw new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneUnavailable);
        if (++_calls > MaxCallsPerStatement || _depth + 1 > MaxNestingDepth) throw Argument();

        // A qualified spelling (schema.convert_timezone) is never the function: it fails validation instead of passing through.
        if (node.Name.Parts.Count != 1) throw Argument();
        if (node.Arguments.Count != 3 || node.Distinct || node.Filter is not null || node.Window is not null || node.OrderWithin is not null || node.IsStar)
        {
            throw Argument();
        }

        CheckZone(node.Arguments[0]);
        CheckZone(node.Arguments[1]);
        CheckTimestamp(node.Arguments[2]);

        _depth++;
        try
        {
            return base.VisitFunctionCallExpression(node);
        }
        finally
        {
            _depth--;
        }
    }

    private static SqlTimeZoneException Argument() => new(GovernedSqlErrorCodes.TimeZoneArgument);

    private void CheckZone(Expression zone)
    {
        switch (zone)
        {
            case LiteralExpression { Type: LiteralType.String, Value: string text }:
                if (text.Length > TimeZoneRegistry.MaxLength || !TimeZoneRegistry.Contains(text))
                {
                    throw new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneInvalid);
                }

                return;
            case ParameterReference { IsSynthetic: true, ListIndex: null, ListCount: null } parameter:
                // A list parameter is never a zone (BC-130-5c); the federated path has no zone binder until #131 (A-8).
                if (options.Federated || options.ClientLists.TryGetCount(parameter.Name, out _)) throw Argument();
                return;
            default:
                throw Argument();
        }
    }

    private void CheckTimestamp(Expression ts)
    {
        ts = StripParentheses(ts);
        switch (ts)
        {
            case FunctionCallExpression nested when IsTimeZoneFunction(nested.Name):
                return;   // its own validation runs when the traversal reaches it (the result of a call is a plain timestamp)
            case TypedLiteralExpression { Kind: TypedLiteralKind.Timestamp }:
                return;
            case ColumnReference column:
                CheckColumn(column);
                return;
            case CastExpression cast when PlainTimestampClassifier.IsPlainCastTarget(cast.TargetType):
                CheckCastOperand(cast.Operand);
                return;
            default:
                throw Argument();
        }
    }

    private static Expression StripParentheses(Expression expression)
    {
        while (expression is ParenthesizedExpression parenthesized) expression = parenthesized.Expression;
        return expression;
    }

    private void CheckColumn(ColumnReference column)
    {
        // A bare column must resolve to a catalog column of a plain timestamp type that is not masked (BC-130-1). Federated statements have no
        // catalog types (they belong to the source dialects): the caller writes CAST(col AS timestamp) there.
        if (options.Federated || Resolve(column) is not { } resolved) throw Argument();
        if (IsMasked(resolved)) throw Argument();
        if (!PlainTimestampClassifier.IsPlain(options.Dialect, resolved.Column.DataType)) throw Argument();
    }

    private void CheckCastOperand(Expression operand)
    {
        var collector = new ColumnReferenceCollector();
        collector.Visit(operand);
        if (collector.HasSubquery) throw Argument();

        foreach (var column in collector.Columns)
        {
            if (Resolve(column) is { } resolved)
            {
                if (IsMasked(resolved)) throw Argument();
            }
            else if (IsMaskedUnresolved(column))
            {
                throw Argument();
            }
        }
    }

    /// <summary>The same HasMask semantics as the mask guards; a failing lookup is uncertainty and fails closed (MaskGuards.cs lookups).</summary>
    private bool IsMasked(ResolvedColumn resolved)
    {
        try
        {
            return options.Masks.HasMask(resolved.Table.Identity, resolved.Column.Name);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>A column that does not resolve to a catalog table: the guards' fallback over <c>TablesWithMaskedColumns</c> (and the federated name lookup).</summary>
    private bool IsMaskedUnresolved(ColumnReference column)
    {
        string name = column.Name.SimpleName;
        string? qualifier = column.Name.Parts.Count > 1 ? column.Name.Parts[^2].Value : null;
        try
        {
            if (options.IsMaskedByName?.Invoke(qualifier, name) == true) return true;
            foreach (string table in options.TablesWithMaskedColumns)
            {
                var entry = _catalog.Resolve(new SqlQualifiedName(table.Split('.')));
                if (entry is not null && options.Masks.HasMask(entry.Identity, name)) return true;
            }

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Collects the column references of a CAST operand and notes whether it contains a subquery (a nested scope the check does not resolve).</summary>
    private sealed class ColumnReferenceCollector : SqlAstRewriter
    {
        public List<ColumnReference> Columns { get; } = [];

        public bool HasSubquery { get; private set; }

        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            Columns.Add(node);
            return node;
        }

        public override SqlNode VisitSelectStatement(SelectStatement node)
        {
            HasSubquery = true;
            return node;
        }
    }
}
