namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

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

    /// <summary>
    /// SR-L-11: the reserved value a sql definition compares the tenant column of every tenanted source table with:
    /// <c>alias.tenant_id = '{{tenant}}'</c> in a conjunctive position. The gateway replaces it with the caller's tenant (a bound
    /// parameter in the typed form); the tenant never comes from the filter text.
    /// </summary>
    public const string TenantPlaceholder = "{{tenant}}";

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
        IReadOnlyList<string>? allowedReferenceTables = null,
        bool requireTenantColumn = false,
        IEnumerable<string>? tenantColumnExemptTables = null,
        Autheris.Application.Identity.Attributes.CallerAttributeSnapshot? callerAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(catalog);
        filter.Validate();
        var fragment = filter.Sql ?? throw new ArgumentException($"The virtual filter '{filter.Name}' has no sql definition.", nameof(filter));
        var original = fragment;

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

        // REQ-GOV-129 (A-4): a caller placeholder becomes the reserved sentinel before the fragment is parsed.
        IReadOnlyList<string> callerNames = [];
        if (CallerPlaceholders.Contains(fragment))
        {
            if (callerAttributes is null)
            {
                throw Reject(filter, "caller attributes are not available (Gateway:CallerAttributes:Enabled).");
            }

            try
            {
                fragment = CallerPlaceholders.Rewrite(fragment, out callerNames);
            }
            catch (ArgumentException ex)
            {
                throw Reject(filter, ex.Message);
            }
        }

        TrinoSqlEngine.Analysis.SqlQueryMetadata metadata;
        try
        {
            metadata = FastSqlEngine.Default.Analyze(Statement(fragment).AsMemory());
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
        var tenanted = new List<(TableAccessTarget Table, string Column)>();
        var sourceByAlias = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
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
            sourceByAlias[table.Alias ?? table.TableName] = catalogued;

            // SR-L-11: a tenanted source table must be compared with the reserved tenant value (checked on the AST below).
            string? tenantCol;
            try
            {
                tenantCol = VirtualFilterTenantColumns.Resolve(catalogued, requireTenantColumn, tenantColumnExemptTables);
            }
            catch (InvalidOperationException ex)
            {
                throw Reject(filter, ex.Message);
            }

            if (tenantCol != null)
            {
                tenanted.Add((table, tenantCol));
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

        var (tree, _) = FastSqlEngine.Default.Parse(Statement(fragment).AsMemory(), null, CancellationToken.None);
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
        RequireTenantBinding(filter, tenanted, spec.Where);
        if (callerNames.Count > 0)
        {
            ValidateCallerPlaceholders(filter, select, spec.Where, callerAttributes!, sourceByAlias);
        }

        var validated = filter with { SqlTargetColumns = targetColumns };
        var dialect = sourceTables.Count > 0 ? sourceTables[0].Dialect : DatabaseDialect.SqlServer;
        try
        {
            // The translation check runs on the definition with harmless values for the sentinels; the stored definition keeps the placeholders.
            Compile(validated with { Sql = Substitute(CallerPlaceholders.WithHarmlessValues(fragment), "validation") }, dialect);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ArgumentException)
        {
            throw Reject(filter, $"it cannot be translated for {dialect}: {ex.Message}");
        }

        return validated;
    }

    /// <summary>The position grammar and the registry rules of the placeholders of a definition (BC-129-06, A-9).</summary>
    private static void ValidateCallerPlaceholders(VirtualFilter filter, SelectStatement select, Expression where, Autheris.Application.Identity.Attributes.CallerAttributeSnapshot attributes, IReadOnlyDictionary<string, TableMetadata> sourceByAlias)
    {
        IReadOnlyList<(string Name, bool InList, ColumnReference? Column)> uses;
        try
        {
            uses = CallerPlaceholders.EnforcePositions(select, where);
        }
        catch (ArgumentException ex)
        {
            throw Reject(filter, ex.Message);
        }

        foreach (var (name, inList, column) in uses)
        {
            if (!attributes.TryGetDefinition(filter.TenantId, name, out var definition))
            {
                throw Reject(filter, "a caller attribute of the definition is not registered and active.");
            }

            if (!inList && definition.Cardinality != CallerAttributeCardinality.Scalar)
            {
                throw Reject(filter, "a list caller attribute is only allowed as the item of IN or NOT IN.");
            }

            // V-129-4: the type of a compared source column is known here; the protected object's columns are only known at binding.
            if (column is { Name.Parts.Count: >= 2 } &&
                sourceByAlias.TryGetValue(column.Name.Parts[^2].Value, out var source) &&
                source.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, column.Name.Parts[^1].Value, StringComparison.OrdinalIgnoreCase)) is { } sourceColumn &&
                !Autheris.Application.Identity.Attributes.CallerAttributeColumnTypeFit.Fits(definition.ValueType, sourceColumn.DataType))
            {
                throw Reject(filter, $"the type of a caller attribute does not fit the type of the column '{sourceColumn.ColumnName}'.");
            }
        }
    }

    /// <summary>
    /// SR-L-11: every tenanted source table needs a conjunct <c>alias.&lt;tenantcolumn&gt; = '{{tenant}}'</c> in the WHERE clause; a
    /// mere reference, a literal or another column does not bind the tenant.
    /// </summary>
    private static void RequireTenantBinding(VirtualFilter filter, IReadOnlyList<(TableAccessTarget Table, string Column)> tenanted, Expression where)
    {
        foreach (var (table, column) in tenanted)
        {
            if (!IsTenantBound(where, table.Alias ?? table.TableName, column))
            {
                throw Reject(filter, $"the source table '{table.FullName}' has the tenant column '{column}', which must be compared with the reserved tenant value as a WHERE conjunct: {table.Alias ?? table.TableName}.{column} = '{TenantPlaceholder}'.");
            }
        }
    }

    private static bool IsTenantBound(Expression expression, string qualifier, string column) => expression switch
    {
        ParenthesizedExpression p => IsTenantBound(p.Expression, qualifier, column),
        BinaryExpression { Operator: BinaryOperator.And } conjunction =>
            IsTenantBound(conjunction.Left, qualifier, column) || IsTenantBound(conjunction.Right, qualifier, column),
        BinaryExpression { Operator: BinaryOperator.Equal } eq =>
            IsTenantComparison(eq.Left, eq.Right, qualifier, column) || IsTenantComparison(eq.Right, eq.Left, qualifier, column),
        _ => false
    };

    private static bool IsTenantComparison(Expression columnSide, Expression valueSide, string qualifier, string column) =>
        columnSide is ColumnReference { Name.Parts.Count: >= 2 } reference &&
        string.Equals(reference.Name.Parts[^1].Value, column, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(reference.Name.Parts[^2].Value, qualifier, StringComparison.OrdinalIgnoreCase) &&
        valueSide is LiteralExpression { Type: LiteralType.String, Value: string text } &&
        string.Equals(text, TenantPlaceholder, StringComparison.Ordinal);

    /// <summary>The definition with the reserved tenant value replaced by the quoted <paramref name="tenant"/> (escaped; the typed form binds it).</summary>
    private static string Substitute(string fragment, string tenant) =>
        fragment.Replace($"'{TenantPlaceholder}'", "'" + tenant.Replace("'", "''", StringComparison.Ordinal) + "'", StringComparison.Ordinal);

    /// <summary>
    /// The definition ready to compile: with a <paramref name="tenant"/> context every tenanted source table must still compare its
    /// tenant column with the reserved value (a stored definition that does not is denied, never skipped), and the value becomes the
    /// caller's tenant. Without a context a definition that uses the reserved value is refused (fail closed).
    /// </summary>
    private static VirtualFilter BindTenant(VirtualFilter filter, VirtualFilterTenantContext? tenant)
    {
        var fragment = filter.Sql ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no sql definition.");
        if (tenant == null)
        {
            if (fragment.Contains(TenantPlaceholder, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The virtual filter '{filter.Name}' uses the reserved tenant value but no tenant is bound.");
            }

            return filter;
        }

        var metadata = FastSqlEngine.Default.Analyze(Statement(fragment).AsMemory());
        var tenanted = new List<(TableAccessTarget Table, string Column)>();
        foreach (var table in metadata.ReferencedTables)
        {
            var column = tenant.TenantColumnOf(new TableIdentifier(filter.Source, table.Schema ?? string.Empty, table.TableName));
            if (column != null)
            {
                tenanted.Add((table, column));
            }
        }

        if (tenanted.Count > 0)
        {
            var (tree, _) = FastSqlEngine.Default.Parse(Statement(fragment).AsMemory(), null, CancellationToken.None);
            var ast = new TrinoSqlEngine.Ast.Builder.SqlAstBuilder().BuildStatement(tree);
            if (ast is not SelectStatement { Body: QuerySpecification { Where: not null } spec })
            {
                throw new InvalidOperationException($"The virtual filter '{filter.Name}' did not compile to a correlated subquery.");
            }

            try
            {
                RequireTenantBinding(filter, tenanted, spec.Where);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }

        return filter with { Sql = Substitute(fragment, tenant.Tenant) };
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

    /// <summary>The catalog identifiers of the tables a sql definition reads (data source of the filter, schema, table).</summary>
    public static IReadOnlyList<TableIdentifier> ReferencedTables(VirtualFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var fragment = filter.Sql ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no sql definition.");
        return FastSqlEngine.Default.Analyze(Statement(fragment).AsMemory()).ReferencedTables
            .Select(t => new TableIdentifier(filter.Source, t.Schema ?? string.Empty, t.TableName))
            .ToList();
    }

    /// <summary>EXISTS predicate of the SQL definition in <paramref name="dialect"/>, correlated with <c>autheris_target</c>.</summary>
    public static string Compile(VirtualFilter filter, DatabaseDialect dialect, VirtualFilterTenantContext? tenant = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (CallerPlaceholders.Contains(filter.Sql))
        {
            // BC-129-05: the text form of a caller attribute filter never carries a value or a sentinel (NOT IN of a sentinel would match every
            // row): it is the constant that denies all rows of this filter. The typed form is authoritative where a row policy factory exists.
            return CallerAttributeFilterText;
        }

        filter = BindTenant(filter, tenant);
        var fragment = filter.Sql ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no sql definition.");
        var target = Sql.SqlDialectMapper.ToTargetDialect(dialect);
        var generated = GenerateInline(Statement(fragment), target);
        if (!generated.StartsWith("SELECT 1 FROM ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The virtual filter '{filter.Name}' did not compile to a correlated subquery.");
        }

        var quotedTarget = dialect.QuoteIdentifier(TargetAlias) + ".";
        var correlated = generated.Replace(quotedTarget, dialect.QuoteIdentifier(RowFilterAliases.Target) + ".", StringComparison.Ordinal);
        return $"EXISTS ({correlated})";
    }

    /// <summary>The text form of a filter with caller placeholders: no rows (BC-129-05).</summary>
    public const string CallerAttributeFilterText = "(1 = 0)";

    /// <summary>Default prefix of the policy parameters of a typed virtual filter predicate.</summary>
    public const string DefaultTypedParameterPrefix = "__gql_vf_";

    /// <summary>
    /// Typed variant of <see cref="Compile"/> (WP-X0g): the same <c>EXISTS (SELECT 1 FROM ... WHERE ...)</c> as AST, correlated with
    /// <c>autheris_target</c>. The reserved <c>target</c> qualifier is rebased on the tree (no text edit), time functions stay AST
    /// nodes, and every literal that is a direct operand of a comparison, IN list, BETWEEN or LIKE becomes a bound policy parameter
    /// named <paramref name="parameterPrefix"/> plus a counter. <paramref name="engine"/> only parses (it is injected, never created here).
    /// </summary>
    /// <exception cref="InvalidOperationException">The definition has no sql, or does not compile to a correlated subquery.</exception>
    public static PolicyPredicate CompileTyped(
        VirtualFilter filter, DatabaseDialect dialect, ISqlEngine engine, string parameterPrefix = DefaultTypedParameterPrefix,
        VirtualFilterTenantContext? tenant = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(engine);
        if (CallerPlaceholders.Contains(filter.Sql))
        {
            // Re-checked at every compile: a stored definition is never trusted to have been validated (BC-129-05, BC-129-06).
            filter = filter with { Sql = CallerPlaceholders.Rewrite(filter.Sql!, out _) };
        }

        filter = BindTenant(filter, tenant);
        var fragment = filter.Sql ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no sql definition.");
        var target = Sql.SqlDialectMapper.ToTargetDialect(dialect);
        var (tree, _) = engine.Parse(Statement(fragment).AsMemory(), SqlTokenSecurityOptions.ForDialect(target), CancellationToken.None);
        var secured = new SqlAstBuilder(BuilderOptions()).BuildStatement(tree);
        var simplified = (SqlStatement)new AstSimplificationVisitor().Visit(secured);
        new AstValidationVisitor().Validate(simplified);

        if (simplified is not SelectStatement { Body: QuerySpecification { Where: not null } } select)
        {
            throw new InvalidOperationException($"The virtual filter '{filter.Name}' did not compile to a correlated subquery.");
        }

        if (CallerPlaceholders.Contains(fragment))
        {
            // On the tree as built, before the simplifier: it rewrites NOT (a = b) to a <> b, which would hide a negation over a placeholder.
            if (secured is not SelectStatement { Body: QuerySpecification { Where: { } securedWhere } } securedSelect)
            {
                throw new InvalidOperationException($"The virtual filter '{filter.Name}' did not compile to a correlated subquery.");
            }

            CallerPlaceholders.EnforcePositions(securedSelect, securedWhere);
        }

        var shaper = new TypedShaper(new Sql.Policy.PolicyParameterNamer(parameterPrefix));
        var shaped = (SelectStatement)shaper.Visit(select);
        return PolicyPredicate.Create(new ExistsExpression(shaped), shaper.Namer.Values);
    }

    /// <summary>Rebases <c>target</c> onto the reserved alias and turns comparison operands into policy parameters.</summary>
    private sealed class TypedShaper(Sql.Policy.PolicyParameterNamer namer) : SqlAstRewriter
    {
        public Sql.Policy.PolicyParameterNamer Namer { get; } = namer;

        public override SqlNode Visit(SqlNode node)
        {
            var visited = base.Visit(node);
            switch (visited)
            {
                case ColumnReference { Name.Parts.Count: > 1 } column
                    when string.Equals(column.Name.Parts[0].Value, TargetAlias, StringComparison.OrdinalIgnoreCase):
                    var parts = new List<SqlIdentifier>(column.Name.Parts) { [0] = column.Name.Parts[0] with { Value = RowFilterAliases.Target } };
                    return column with { Name = new SqlQualifiedName(parts) };
                case BinaryExpression bin when bin.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan
                    or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual:
                    return bin with { Left = Bind(bin.Left), Right = Bind(bin.Right) };
                case InListExpression inList:
                    return inList with { Items = inList.Items.Select(i => Bind(i)).ToList() };
                case BetweenExpression between:
                    return between with { Lower = Bind(between.Lower), Upper = Bind(between.Upper) };
                case LikeExpression like:
                    return like with { Pattern = Bind(like.Pattern, isLikePattern: true) };
                default:
                    return visited;
            }
        }

        private Expression Bind(Expression expression, bool isLikePattern = false) => expression switch
        {
            LiteralExpression { Type: LiteralType.String, Value: string sentinel } when CallerPlaceholders.TryParseSentinel(sentinel, out var attribute) =>
                Autheris.Application.Identity.Attributes.CallerAttributeBinder.CreateSlot(Namer, attribute),
            LiteralExpression { Type: LiteralType.String, Value: string text } => Namer.Add(new PolicyValue(text, SqlParameterType.String), isLikePattern),
            LiteralExpression { Type: LiteralType.Integer, Value: long number } => Namer.Add(new PolicyValue(number, SqlParameterType.Int64)),
            LiteralExpression { Type: LiteralType.Decimal, Value: decimal number } => Namer.Add(new PolicyValue(number, SqlParameterType.Decimal)),
            LiteralExpression { Type: LiteralType.Boolean, Value: bool flag } => Namer.Add(new PolicyValue(flag, SqlParameterType.Boolean)),
            _ => expression
        };
    }

    private static AstBuilderOptions BuilderOptions() => new()
    {
        EnforceReadOnlyQueries = true,
        EnforceFunctionPolicy = true,
        AllowedFunctions = AllowedFunctions,
        TranslateTrinoDateFunctions = true
    };

    /// <summary>
    /// Inline text of a definition for the target dialect (the string form of the predicate, no policy applies to a definition): the
    /// same parse, build, simplify and validate steps as <see cref="CompileTyped"/>, emitted without bound parameters.
    /// </summary>
    private static string GenerateInline(string statement, TargetSqlDialect target)
    {
        var (tree, _) = FastSqlEngine.Default.Parse(statement.AsMemory(), SqlTokenSecurityOptions.ForDialect(target), CancellationToken.None);
        var ast = new SqlAstBuilder(BuilderOptions()).BuildStatement(tree);
        var simplified = (SqlStatement)new AstSimplificationVisitor().Visit(ast);
        new AstValidationVisitor().Validate(simplified);
        return TrinoSqlEngine.Ast.Generators.SqlDialectGeneratorFactory.GetGenerator(target).GenerateSql(simplified);
    }

    private static string Statement(string fragment) => "SELECT 1 " + fragment.Trim();

    private static ArgumentException Reject(VirtualFilter filter, string reason) =>
        new($"The sql definition of the virtual filter '{filter.Name}' is rejected: {reason}", nameof(filter));
}
