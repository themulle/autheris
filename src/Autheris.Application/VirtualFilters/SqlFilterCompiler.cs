namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Virtual filters, phase 7: stage 2 definitions, a correlated predicate in Trino syntax
/// (<c>from ... [join ...] where ...</c>) that addresses the protected object as <c>target</c> (design 3.7).
/// <see cref="Validate"/> runs before a definition is stored: one read-only statement fragment, <c>target</c> reserved
/// (never a table or alias), only catalogued tables of the filter's data source, functions from the filter list (Trino
/// names, checked before translation; <c>date_diff</c> is not allowed, decision 8), no parameters, and translatable into
/// the data source's dialect. <see cref="Compile"/> renders it per dialect with the Trino date functions translated,
/// correlated with the reserved alias <c>autheris_target</c>. Time functions stay functions: no value is ever baked in.
/// </summary>
public static partial class SqlFilterCompiler
{
    private const string TargetAlias = "target";

    /// <summary>Functions a filter may call (Trino names); everything else is rejected before translation.</summary>
    public static readonly FrozenSet<string> AllowedFunctions = new[]
    {
        "current_timestamp", "current_date", "date_add", "date_trunc", "now", "coalesce", "nullif", "lower", "upper", "trim", "length", "substring",
        "abs", "round", "floor", "ceil", "ceiling"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"'(?:[^']|'')*'", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"^\s*from\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartsWithFrom();

    /// <summary>Validates <paramref name="filter"/>'s SQL against <paramref name="catalog"/> and returns it with the derived target columns.</summary>
    /// <exception cref="ArgumentException">The definition is not acceptable (the message says why).</exception>
    public static VirtualFilter Validate(
        VirtualFilter filter,
        IReadOnlyList<TableMetadata> catalog,
        IReadOnlyList<string>? allowedReferenceTables = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(catalog);
        filter.Validate();
        var fragment = filter.Sql ?? throw new ArgumentException($"The virtual filter '{filter.Name}' has no sql definition.", nameof(filter));

        if (!StartsWithFrom().IsMatch(fragment))
        {
            throw Reject(filter, "the definition must start with FROM (the predicate form: from ... where ... target.<column> ...).");
        }

        var withoutLiterals = StringLiteral().Replace(fragment, "''");
        if (withoutLiterals.Contains('?') || withoutLiterals.Contains('@') || withoutLiterals.Contains(':'))
        {
            throw Reject(filter, "parameters (?, @name, :name) are not allowed.");
        }

        if (StringLiteral().Matches(fragment).Any(m => m.Value.Contains(TargetAlias, StringComparison.OrdinalIgnoreCase)))
        {
            throw Reject(filter, "string literals must not contain the reserved name 'target'.");
        }

        TrinoSqlEngine.Analysis.SqlQueryMetadata metadata;
        try
        {
            metadata = new FastSqlEngine().Analyze(Statement(fragment).AsMemory());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Reject(filter, $"the definition must be one read-only statement fragment ({ex.Message}).");
        }

        if (metadata.StatementType != TrinoSqlEngine.Analysis.SqlStatementType.Select)
        {
            throw Reject(filter, "only a read-only statement fragment is allowed.");
        }

        var sourceTables = new List<TableMetadata>();
        foreach (var table in metadata.ReferencedTables)
        {
            if (string.Equals(table.Alias, TargetAlias, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(table.TableName, TargetAlias, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(table.Alias, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(table.TableName, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase))
            {
                throw Reject(filter, $"'target' and '{RowFilterAliases.Target}' are reserved for the protected object and cannot be a table or alias.");
            }

            if (!string.IsNullOrWhiteSpace(table.Catalog) && !string.Equals(table.Catalog, filter.Source, StringComparison.OrdinalIgnoreCase))
            {
                throw Reject(filter, $"the table '{table.FullName}' is not in the filter's data source '{filter.Source}'.");
            }

            if (string.IsNullOrWhiteSpace(table.Schema))
            {
                throw Reject(filter, $"the table '{table.FullName}' must be written as schema.table.");
            }

            if (allowedReferenceTables is { Count: > 0 } allowed)
            {
                bool isAllowed = allowed.Any(a =>
                    string.Equals(a, table.FullName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, $"{table.Schema}.{table.TableName}", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, table.TableName, StringComparison.OrdinalIgnoreCase));
                if (!isAllowed)
                {
                    throw Reject(filter, $"the table '{table.FullName}' is not in the allowed reference tables list.");
                }
            }

            var catalogued = catalog.FirstOrDefault(t =>
                string.Equals(t.Identifier.Domain, filter.Source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Identifier.Schema, table.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Identifier.TableName, table.TableName, StringComparison.OrdinalIgnoreCase));
            sourceTables.Add(catalogued ?? throw Reject(filter, $"the table '{table.FullName}' is unknown in the catalog of '{filter.Source}'."));

            // SR15-11: If reference table has a tenant isolation column, verify the filter constrains it
            var tenantCol = catalogued.Columns.FirstOrDefault(c =>
                string.Equals(c.ColumnName, "tenant_id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.ColumnName, "tenantid", StringComparison.OrdinalIgnoreCase))?.ColumnName;
            if (tenantCol != null)
            {
                bool hasTenantFilter = (metadata.FilterColumnReferences ?? []).Any(r =>
                    (string.Equals(r.TableOrAlias, table.Alias ?? table.TableName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(r.TableOrAlias, table.TableName, StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(r.ColumnName, tenantCol, StringComparison.OrdinalIgnoreCase));
                if (!hasTenantFilter)
                {
                    throw Reject(filter, $"the subquery table '{table.FullName}' has tenant column '{tenantCol}' which must be constrained in the WHERE clause.");
                }
            }
        }

        foreach (var function in metadata.FunctionCalls ?? [])
        {
            var name = function.Split('.')[^1];
            if (string.Equals(name, "date_diff", StringComparison.OrdinalIgnoreCase))
            {
                throw Reject(filter, "date_diff is not allowed (its meaning differs between Trino and SQL Server, decision 8).");
            }

            if (!AllowedFunctions.Contains(name))
            {
                throw Reject(filter, $"the function '{name}' is not allowed in virtual filters.");
            }
        }

        var targetColumns = (metadata.FilterColumnReferences ?? [])
            .Select(r => (r.TableOrAlias, r.ColumnName))
            .Concat((metadata.JoinColumnReferences ?? []).Select(r => (r.TableOrAlias, r.ColumnName)))
            .Where(r => string.Equals(r.TableOrAlias, TargetAlias, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ColumnName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targetColumns.Count == 0)
        {
            throw Reject(filter, "the predicate must reference the protected object as target.<column>.");
        }

        foreach (var column in targetColumns)
        {
            VirtualFilterNames.ValidateIdentifier(column, nameof(filter));
        }

        var (tree, _) = new FastSqlEngine().Parse(Statement(fragment).AsMemory(), null, CancellationToken.None);
        var astBuilder = new TrinoSqlEngine.Ast.Builder.SqlAstBuilder();
        var ast = astBuilder.BuildStatement(tree);
        if (ast is not SelectStatement select || select.Body is not QuerySpecification spec)
        {
            throw Reject(filter, $"AST did not produce a query specification (got {ast?.GetType().Name} / {(ast as SelectStatement)?.Body?.GetType().Name}).");
        }
        if (spec.Where == null)
        {
            throw Reject(filter, "virtual filter definition must contain a WHERE predicate.");
        }
        ValidateAstPredicates(spec.Where, filter);

        var validated = filter with { SqlTargetColumns = targetColumns };
        var dialect = sourceTables.Count > 0 ? sourceTables[0].Dialect : DatabaseDialect.SqlServer;
        try
        {
            Compile(validated, dialect);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ArgumentException)
        {
            throw Reject(filter, $"it cannot be translated for {dialect}: {ex.Message}");
        }

        return validated;
    }

    private static void ValidateAstPredicates(Expression expr, VirtualFilter filter)
    {
        switch (expr)
        {
            case BinaryExpression orExpr when orExpr.Operator == BinaryOperator.Or:
                if (!ReferencesTarget(orExpr.Left) || !ReferencesTarget(orExpr.Right))
                {
                    throw Reject(filter, "disjunction (OR) branches must all constrain the protected 'target' entity. Unbound conditions like 'OR 1=1' are prohibited.");
                }
                ValidateAstPredicates(orExpr.Left, filter);
                ValidateAstPredicates(orExpr.Right, filter);
                break;

            case BinaryExpression bin:
                if (bin.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual or
                    BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or
                    BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual)
                {
                    if (ExpressionEquals(bin.Left, bin.Right))
                    {
                        throw Reject(filter, "tautological comparison (e.g. col = col or 1 = 1) is prohibited.");
                    }
                    if (bin.Left is LiteralExpression && bin.Right is LiteralExpression)
                    {
                        throw Reject(filter, "comparison between literals is prohibited.");
                    }
                }
                ValidateAstPredicates(bin.Left, filter);
                ValidateAstPredicates(bin.Right, filter);
                break;

            case LiteralExpression lit when lit.Type == LiteralType.Boolean && lit.Value is true:
                throw Reject(filter, "literal TRUE in filter predicate is prohibited.");

            case UnaryExpression un:
                ValidateAstPredicates(un.Operand, filter);
                break;

            case InListExpression inList:
                ValidateAstPredicates(inList.Operand, filter);
                foreach (var item in inList.Items)
                {
                    ValidateAstPredicates(item, filter);
                }
                break;

            case BetweenExpression bet:
                ValidateAstPredicates(bet.Operand, filter);
                ValidateAstPredicates(bet.Lower, filter);
                ValidateAstPredicates(bet.Upper, filter);
                break;

            case LikeExpression like:
                ValidateAstPredicates(like.Operand, filter);
                ValidateAstPredicates(like.Pattern, filter);
                break;

            case FunctionCallExpression fn:
                foreach (var arg in fn.Arguments)
                {
                    ValidateAstPredicates(arg, filter);
                }
                break;

            case CastExpression cast:
                ValidateAstPredicates(cast.Operand, filter);
                break;

            case CaseExpression cs:
                if (cs.Operand != null) ValidateAstPredicates(cs.Operand, filter);
                foreach (var whenClause in cs.WhenClauses)
                {
                    ValidateAstPredicates(whenClause.Condition, filter);
                    ValidateAstPredicates(whenClause.Result, filter);
                }
                if (cs.ElseResult != null) ValidateAstPredicates(cs.ElseResult, filter);
                break;
        }
    }

    private static bool ReferencesTarget(Expression? expr)
    {
        if (expr == null) return false;
        switch (expr)
        {
            case ColumnReference col:
                return col.Name.Parts.Count >= 2 &&
                       col.Name.Parts.Take(col.Name.Parts.Count - 1).Any(p => string.Equals(p.Value, TargetAlias, StringComparison.OrdinalIgnoreCase));

            case BinaryExpression bin:
                return ReferencesTarget(bin.Left) || ReferencesTarget(bin.Right);

            case UnaryExpression un:
                return ReferencesTarget(un.Operand);

            case InListExpression inList:
                return ReferencesTarget(inList.Operand) || inList.Items.Any(ReferencesTarget);

            case BetweenExpression bet:
                return ReferencesTarget(bet.Operand) || ReferencesTarget(bet.Lower) || ReferencesTarget(bet.Upper);

            case LikeExpression like:
                return ReferencesTarget(like.Operand) || ReferencesTarget(like.Pattern);

            case FunctionCallExpression fn:
                return fn.Arguments.Any(ReferencesTarget);

            case CastExpression cast:
                return ReferencesTarget(cast.Operand);

            case CaseExpression cs:
                return (cs.Operand != null && ReferencesTarget(cs.Operand)) ||
                       cs.WhenClauses.Any(w => ReferencesTarget(w.Condition) || ReferencesTarget(w.Result)) ||
                       (cs.ElseResult != null && ReferencesTarget(cs.ElseResult));

            case SubstringExpression sub:
                return ReferencesTarget(sub.Source) || ReferencesTarget(sub.Start) || (sub.Length != null && ReferencesTarget(sub.Length));

            case TrimExpression trim:
                return ReferencesTarget(trim.Source) || (trim.Characters != null && ReferencesTarget(trim.Characters));

            case PositionExpression pos:
                return ReferencesTarget(pos.Needle) || ReferencesTarget(pos.Haystack);

            case SubscriptExpression s:
                return ReferencesTarget(s.Target) || ReferencesTarget(s.Index);

            case ExtractExpression ext:
                return ReferencesTarget(ext.Source);

            default:
                return false;
        }
    }

    private static bool ExpressionEquals(Expression a, Expression b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;

        if (a is ColumnReference colA && b is ColumnReference colB)
        {
            return string.Equals(colA.Name.NormalizedName, colB.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
        }

        if (a is LiteralExpression litA && b is LiteralExpression litB)
        {
            return litA.Type == litB.Type && Equals(litA.Value, litB.Value);
        }

        return false;
    }

    /// <summary>EXISTS predicate of the SQL definition in <paramref name="dialect"/>, correlated with <c>autheris_target</c>.</summary>
    public static string Compile(VirtualFilter filter, DatabaseDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var fragment = filter.Sql ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no sql definition.");
        var options = new RlsOptions
        {
            TargetDialect = Sql.SqlDialectMapper.ToTargetDialect(dialect),
            RewriterEngine = "AstCompiler",
            TranslateTrinoDateFunctions = true,
            PolicyProvider = new DefaultRlsPolicyProvider(predicate: _ => false),
            EnforceReadOnlyQueries = true,
            EnforceFunctionPolicy = true,
            AllowedFunctions = AllowedFunctions,
            EnforcedMaxRows = 0
        };

        var generated = new FastSqlEngine().GenerateGovernedSql(Statement(fragment), options);
        if (!generated.StartsWith("SELECT 1 FROM ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The virtual filter '{filter.Name}' did not compile to a correlated subquery.");
        }

        var quotedTarget = dialect.QuoteIdentifier(TargetAlias) + ".";
        var correlated = generated.Replace(quotedTarget, dialect.QuoteIdentifier(RowFilterAliases.Target) + ".", StringComparison.Ordinal);
        return $"EXISTS ({correlated})";
    }

    private static string Statement(string fragment) => "SELECT 1 " + fragment.Trim();

    private static ArgumentException Reject(VirtualFilter filter, string reason) =>
        new($"The sql definition of the virtual filter '{filter.Name}' is rejected: {reason}", nameof(filter));
}
