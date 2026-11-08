namespace TrinoSqlEngine.Analysis;

using System;
using System.Collections.Generic;
using System.Security;
using Antlr4.Runtime.Tree;

public sealed class SqlQueryAnalyzer : SqlBaseBaseListener, ISqlQueryAnalyzer
{
    private SqlStatementType _statementType = SqlStatementType.Other;
    private readonly List<TableAccessTarget> _referencedTables = new();
    private readonly List<string> _projectedColumns = new();
    private readonly HashSet<string> _seenTableKeys = new(StringComparer.Ordinal);
    private readonly List<JoinColumnReference> _joinColumnReferences = new();
    private readonly List<FilterColumnReference> _filterColumnReferences = new();
    private readonly List<FilterColumnReference> _windowColumnReferences = new();
    private readonly Stack<HashSet<string>> _cteScopeStack = new();
    private readonly List<string> _functionCalls = new();
    private readonly HashSet<string> _seenFunctionCalls = new(StringComparer.Ordinal);
    private readonly List<string> _tableFunctionCalls = new();
    private readonly List<string> _definingNonRecursiveCtes = new();
    private bool _hasRecursiveCte;
    private bool _hasNonRecursiveSelfReference;
    private bool _hasSessionProperties;
    private bool _hasInlineFunctionDefinitions;

    private int _joinCount;
    private int _crossJoinCount;
    private int _setOperationCount;
    private bool _hasRootOrderBy;
    private bool _hasRootOffset;
    private readonly List<CrossJoinTarget> _crossJoins = new();
    private int _currentSubqueryDepth;
    private int _maxSubqueryDepth;
    private bool _hasExplicitLimit;
    private long? _explicitLimitValue;
    private bool _isRootQuerySpecification = true;

    private sealed class CteStats
    {
        public int JoinCount { get; set; }
        public int CrossJoinCount { get; set; }
        public List<CrossJoinTarget> CrossJoins { get; } = new();
        public int SetOperationCount { get; set; }
        public int MaxSubqueryDepth { get; set; }
        public List<string> References { get; } = new();
    }

    private readonly Stack<CteStats> _cteStack = new();
    private readonly Dictionary<string, CteStats> _completedCteStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _rootCteReferences = new();

    public SqlQueryAnalyzer()
    {
        _cteScopeStack.Push(new HashSet<string>(StringComparer.Ordinal));
    }

    public SqlQueryMetadata Analyze(SqlBaseParser.SingleStatementContext statementContext)
    {
        ArgumentNullException.ThrowIfNull(statementContext);

        Reset();
        ParseTreeWalker.Default.Walk(this, statementContext);

        void ResolveCte(string name, HashSet<string> visited)
        {
            if (!visited.Add(name)) return;
            if (_completedCteStats.TryGetValue(name, out var cte))
            {
                _joinCount += cte.JoinCount;
                _crossJoinCount += cte.CrossJoinCount;
                _crossJoins.AddRange(cte.CrossJoins);
                _setOperationCount += cte.SetOperationCount;
                if (cte.MaxSubqueryDepth > _maxSubqueryDepth)
                {
                    _maxSubqueryDepth = cte.MaxSubqueryDepth;
                }
                foreach (var subRef in cte.References)
                {
                    ResolveCte(subRef, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase));
                }
            }
        }

        foreach (var rootRef in _rootCteReferences)
        {
            ResolveCte(rootRef, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return new SqlQueryMetadata(
            StatementType: _statementType,
            ReferencedTables: _referencedTables.AsReadOnly(),
            ProjectedColumns: _projectedColumns.AsReadOnly(),
            JoinCount: _joinCount,
            MaxSubqueryDepth: _maxSubqueryDepth,
            HasExplicitLimit: _hasExplicitLimit,
            ExplicitLimitValue: _explicitLimitValue,
            FunctionCalls: _functionCalls.ToArray(),
            TableFunctionCalls: _tableFunctionCalls.ToArray(),
            HasSessionProperties: _hasSessionProperties,
            HasInlineFunctionDefinitions: _hasInlineFunctionDefinitions,
            JoinColumnReferences: _joinColumnReferences.ToArray(),
            FilterColumnReferences: _filterColumnReferences.ToArray())
        {
            WindowColumnReferences = _windowColumnReferences.ToArray(),
            HasRecursiveCte = _hasRecursiveCte,
            HasNonRecursiveSelfReference = _hasNonRecursiveSelfReference,
            CrossJoinCount = _crossJoinCount,
            SetOperationCount = _setOperationCount,
            HasRootOrderBy = _hasRootOrderBy,
            HasRootOffset = _hasRootOffset,
            CrossJoins = _crossJoins.ToArray()
        };
    }

    private void Reset()
    {
        _statementType = SqlStatementType.Other;
        _referencedTables.Clear();
        _projectedColumns.Clear();
        _seenTableKeys.Clear();
        _joinColumnReferences.Clear();
        _filterColumnReferences.Clear();
        _windowColumnReferences.Clear();
        _cteScopeStack.Clear();
        _cteScopeStack.Push(new HashSet<string>(StringComparer.Ordinal));
        _functionCalls.Clear();
        _seenFunctionCalls.Clear();
        _tableFunctionCalls.Clear();
        _definingNonRecursiveCtes.Clear();
        _hasRecursiveCte = false;
        _hasNonRecursiveSelfReference = false;
        _hasSessionProperties = false;
        _hasInlineFunctionDefinitions = false;
        _joinCount = 0;
        _crossJoinCount = 0;
        _setOperationCount = 0;
        _hasRootOrderBy = false;
        _hasRootOffset = false;
        _crossJoins.Clear();
        _cteStack.Clear();
        _completedCteStats.Clear();
        _rootCteReferences.Clear();
        _currentSubqueryDepth = 0;
        _maxSubqueryDepth = 0;
        _hasExplicitLimit = false;
        _explicitLimitValue = null;
        _isRootQuerySpecification = true;
    }

    public override void EnterSingleStatement(SqlBaseParser.SingleStatementContext context)
    {
        var stmt = context.statement();
        if (stmt == null) return;

        _statementType = stmt switch
        {
            SqlBaseParser.StatementDefaultContext => SqlStatementType.Select,
            SqlBaseParser.InsertIntoContext => SqlStatementType.Insert,
            SqlBaseParser.UpdateContext => SqlStatementType.Update,
            SqlBaseParser.DeleteContext => SqlStatementType.Delete,
            SqlBaseParser.CreateTableContext or
            SqlBaseParser.CreateTableAsSelectContext or
            SqlBaseParser.DropTableContext or
            SqlBaseParser.TruncateTableContext or
            SqlBaseParser.RenameTableContext or
            SqlBaseParser.AddColumnContext or
            SqlBaseParser.RenameColumnContext or
            SqlBaseParser.DropColumnContext or
            SqlBaseParser.SetDefaultValueContext or
            SqlBaseParser.DropDefaultValueContext or
            SqlBaseParser.SetColumnTypeContext or
            SqlBaseParser.DropNotNullConstraintContext or
            SqlBaseParser.SetTablePropertiesContext or
            SqlBaseParser.TableExecuteContext or
            SqlBaseParser.CreateViewContext or
            SqlBaseParser.DropViewContext or
            SqlBaseParser.CreateMaterializedViewContext or
            SqlBaseParser.DropMaterializedViewContext or
            SqlBaseParser.CreateSchemaContext or
            SqlBaseParser.DropSchemaContext or
            SqlBaseParser.RenameSchemaContext or
            SqlBaseParser.GrantRolesContext or
            SqlBaseParser.GrantPrivilegesContext or
            SqlBaseParser.RevokeRolesContext or
            SqlBaseParser.RevokePrivilegesContext or
            SqlBaseParser.DenyContext => SqlStatementType.Ddl,
            _ => SqlStatementType.Other
        };
    }

    public override void EnterQuery(SqlBaseParser.QueryContext context)
    {
        _cteScopeStack.Push(new HashSet<string>(_cteScopeStack.Peek(), StringComparer.Ordinal));
    }

    // SEC C-01: collect called function names (lower-case, qualified as written).
    public override void EnterFunctionCall(SqlBaseParser.FunctionCallContext context)
    {
        string name = SqlIdentifierHelper.NormalizeQualifiedName(context.qualifiedName()).ToLowerInvariant();
        if (name.Length > 0 && _seenFunctionCalls.Add(name))
        {
            _functionCalls.Add(name);
        }
    }

    // SEC P-01: Method call syntax (expression.method(...)) is unconditionally rejected.
    public override void EnterMethodCall(SqlBaseParser.MethodCallContext context)
    {
        throw new SecurityException("Method call syntax (expression.method(...)) is not permitted.");
    }

    public override void EnterStaticMethodCall(SqlBaseParser.StaticMethodCallContext context)
    {
        throw new SecurityException("Static method call syntax (Type::method(...)) is not permitted.");
    }

    // SEC H-14: table functions, WITH SESSION and WITH FUNCTION are surfaced for policy decisions.
    public override void EnterTableFunctionInvocation(SqlBaseParser.TableFunctionInvocationContext context)
    {
        _tableFunctionCalls.Add(SqlIdentifierHelper.NormalizeQualifiedName(context.tableFunctionCall().qualifiedName()).ToLowerInvariant());
        RecordJoin();
        RecordCrossJoin(new CrossJoinTarget(null, null, true));
    }

    public override void EnterUnnest(SqlBaseParser.UnnestContext context)
    {
        RecordJoin();
        RecordCrossJoin(new CrossJoinTarget(null, null, true));
    }

    public override void EnterLateral(SqlBaseParser.LateralContext context)
    {
        RecordJoin();
        RecordCrossJoin(new CrossJoinTarget(null, null, true));
    }

    public override void EnterSetOperation(SqlBaseParser.SetOperationContext context)
    {
        RecordSetOperation();
    }

    public override void EnterRootQueryWithSession(SqlBaseParser.RootQueryWithSessionContext context)
    {
        var properties = context.sessionProperty();
        if (properties != null && properties.Length > 0)
        {
            _hasSessionProperties = true;
        }
    }

    public override void EnterRootQuery(SqlBaseParser.RootQueryContext context)
    {
        var functions = context.functionSpecification();
        if (functions != null && functions.Length > 0)
        {
            _hasInlineFunctionDefinitions = true;
        }
    }

    public override void ExitQuery(SqlBaseParser.QueryContext context)
    {
        if (_cteScopeStack.Count > 1)
        {
            _cteScopeStack.Pop();
        }
    }

    public override void EnterNamedQuery(SqlBaseParser.NamedQueryContext context)
    {
        _currentSubqueryDepth++;
        if (_currentSubqueryDepth > _maxSubqueryDepth)
        {
            _maxSubqueryDepth = _currentSubqueryDepth;
        }
        _cteStack.Push(new CteStats());

        string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(context.name.GetText());
        if ((context.Parent as SqlBaseParser.WithContext)?.RECURSIVE() != null)
        {
            // Issue #277: in a WITH RECURSIVE the name is visible in its own body (and in later CTEs, never in earlier ones),
            // exactly like the compiler's scope. Exit-timing below stays (idempotent add).
            _hasRecursiveCte = true;
            _cteScopeStack.Peek().Add(cteKey);
        }
        else
        {
            // CR-ADG-32: names of non-recursive CTEs whose body is being walked; an unshadowed reference to one is a self-reference.
            _definingNonRecursiveCtes.Add(cteKey);
        }
    }

    public override void ExitNamedQuery(SqlBaseParser.NamedQueryContext context)
    {
        // SEC C-02: CTE names are single-part identifiers (folded scope key, never a dotted string).
        string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(context.name.GetText());
        if (_cteStack.Count > 0)
        {
            var stats = _cteStack.Pop();
            _completedCteStats[cteKey] = stats;
        }
        if (_currentSubqueryDepth > 0)
        {
            _currentSubqueryDepth--;
        }

        if ((context.Parent as SqlBaseParser.WithContext)?.RECURSIVE() == null && _definingNonRecursiveCtes.Count > 0)
        {
            _definingNonRecursiveCtes.RemoveAt(_definingNonRecursiveCtes.Count - 1);
        }

        _cteScopeStack.Peek().Add(cteKey);
    }

    public override void EnterSubquery(SqlBaseParser.SubqueryContext context)
    {
        _currentSubqueryDepth++;
        if (_cteStack.Count > 0 && _currentSubqueryDepth > _cteStack.Peek().MaxSubqueryDepth)
        {
            _cteStack.Peek().MaxSubqueryDepth = _currentSubqueryDepth;
        }
        if (_currentSubqueryDepth > _maxSubqueryDepth)
        {
            _maxSubqueryDepth = _currentSubqueryDepth;
        }
    }

    public override void ExitSubquery(SqlBaseParser.SubqueryContext context)
    {
        if (_currentSubqueryDepth > 0)
        {
            _currentSubqueryDepth--;
        }
    }

    public override void EnterSubqueryRelation(SqlBaseParser.SubqueryRelationContext context)
    {
        _currentSubqueryDepth++;
        if (_cteStack.Count > 0 && _currentSubqueryDepth > _cteStack.Peek().MaxSubqueryDepth)
        {
            _cteStack.Peek().MaxSubqueryDepth = _currentSubqueryDepth;
        }
        if (_currentSubqueryDepth > _maxSubqueryDepth)
        {
            _maxSubqueryDepth = _currentSubqueryDepth;
        }
    }

    public override void ExitSubqueryRelation(SqlBaseParser.SubqueryRelationContext context)
    {
        if (_currentSubqueryDepth > 0)
        {
            _currentSubqueryDepth--;
        }
    }

    public override void EnterQuerySpecification(SqlBaseParser.QuerySpecificationContext context)
    {
        if (context.relation() != null && context.relation().Length > 1)
        {
            int commaJoins = context.relation().Length - 1;
            for (int i = 0; i < commaJoins; i++)
            {
                RecordJoin();
                string? left = ExtractRelationName(context.relation(i));
                string? right = ExtractRelationName(context.relation(i + 1));
                RecordCrossJoin(new CrossJoinTarget(left, right, false));
            }
        }

        if (context.where != null)
        {
            ExtractFilterColumnReferences(context.where, _filterColumnReferences);
        }

        if (context.having != null)
        {
            ExtractFilterColumnReferences(context.having, _filterColumnReferences);
        }

        if (_isRootQuerySpecification && _currentSubqueryDepth == 0)
        {
            _isRootQuerySpecification = false;
            var selectItems = context.selectItem();
            if (selectItems != null)
            {
                foreach (var item in selectItems)
                {
                    if (item is SqlBaseParser.SelectSingleContext single)
                    {
                        if (single.identifier() != null)
                        {
                            _projectedColumns.Add(SqlIdentifierHelper.NormalizeIdentifier(single.identifier().GetText()));
                        }
                        else if (single.expression() != null)
                        {
                            _projectedColumns.Add(single.expression().GetText());
                        }
                    }
                    else if (item is SqlBaseParser.SelectAllContext all)
                    {
                        _projectedColumns.Add(all.GetText());
                    }
                }
            }
        }
    }

    public override void EnterJoinRelation(SqlBaseParser.JoinRelationContext context)
    {
        RecordJoin();
        if (context.CROSS() != null)
        {
            string? left = ExtractRelationName(context.left);
            string? right = ExtractRelationName(context.right);
            RecordCrossJoin(new CrossJoinTarget(left, right, false));
        }
        else if (context.joinCriteria() != null && context.joinCriteria().ON() != null)
        {
            if (IsConstantExpression(context.joinCriteria().booleanExpression()))
            {
                string? left = ExtractRelationName(context.left);
                string? right = ExtractRelationName((Antlr4.Runtime.RuleContext?)context.rightRelation ?? context.right);
                RecordCrossJoin(new CrossJoinTarget(left, right, false));
            }
        }
    }

    public override void EnterJoinCriteria(SqlBaseParser.JoinCriteriaContext context)
    {
        ExtractJoinColumnReferences(context, _joinColumnReferences, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public override void EnterComparison(SqlBaseParser.ComparisonContext context)
    {
        var op = context.comparisonOperator();
        if (op != null && (op.EQ() != null || string.Equals(op.GetText(), "=", StringComparison.Ordinal)))
        {
            var leftCtx = context.value ?? (context.Parent as SqlBaseParser.PredicatedContext)?.valueExpression();
            var rightCtx = context.right ?? context.valueExpression();

            if (leftCtx != null && rightCtx != null)
            {
                var leftRefs = new List<JoinColumnReference>();
                var rightRefs = new List<JoinColumnReference>();
                var leftIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rightIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                ExtractJoinColumnReferences(leftCtx, leftRefs, leftIds);
                ExtractJoinColumnReferences(rightCtx, rightRefs, rightIds);

                // If identifiers exist on BOTH sides of equality (e.g. a.col = b.col),
                // this is a relational equijoin predicate (e.g. ANSI-89 comma join in WHERE clause)
                if (leftIds.Count > 0 && rightIds.Count > 0)
                {
                    _joinColumnReferences.AddRange(leftRefs);
                    _joinColumnReferences.AddRange(rightRefs);
                }
            }
        }
    }

    private static void ExtractJoinColumnReferences(Antlr4.Runtime.RuleContext? ctx, List<JoinColumnReference> references, HashSet<string> flatColumnNames)
    {
        if (ctx == null) return;

        if (ctx is SqlBaseParser.JoinCriteriaContext joinCriteria && joinCriteria.USING() != null)
        {
            var ids = joinCriteria.identifier();
            if (ids != null)
            {
                foreach (var id in ids)
                {
                    string colName = SqlIdentifierHelper.NormalizeIdentifier(id.GetText());
                    if (!string.IsNullOrWhiteSpace(colName))
                    {
                        references.Add(new JoinColumnReference(null, colName));
                        flatColumnNames.Add(colName);
                    }
                }
            }
            return;
        }

        if (ctx is SqlBaseParser.DereferenceContext deref && deref.fieldName != null)
        {
            string colName = SqlIdentifierHelper.NormalizeIdentifier(deref.fieldName.GetText());
            string tableOrAlias = SqlIdentifierHelper.NormalizeIdentifier(deref.baseExpression.GetText());
            if (!string.IsNullOrWhiteSpace(colName))
            {
                references.Add(new JoinColumnReference(tableOrAlias, colName));
                flatColumnNames.Add(colName);
            }
            return;
        }

        if (ctx is SqlBaseParser.ColumnReferenceContext colRef && colRef.identifier() != null)
        {
            string colName = SqlIdentifierHelper.NormalizeIdentifier(colRef.identifier().GetText());
            if (!string.IsNullOrWhiteSpace(colName))
            {
                references.Add(new JoinColumnReference(null, colName));
                flatColumnNames.Add(colName);
            }
            return;
        }

        for (int i = 0; i < ctx.ChildCount; i++)
        {
            if (ctx.GetChild(i) is Antlr4.Runtime.RuleContext child)
            {
                ExtractJoinColumnReferences(child, references, flatColumnNames);
            }
        }
    }

    private static void ExtractFilterColumnReferences(Antlr4.Runtime.RuleContext? ctx, List<FilterColumnReference> references)
    {
        if (ctx == null) return;

        if (ctx is SqlBaseParser.DereferenceContext deref && deref.fieldName != null)
        {
            string colName = SqlIdentifierHelper.NormalizeIdentifier(deref.fieldName.GetText());
            string tableOrAlias = SqlIdentifierHelper.NormalizeIdentifier(deref.baseExpression.GetText());
            if (!string.IsNullOrWhiteSpace(colName))
            {
                references.Add(new FilterColumnReference(tableOrAlias, colName));
            }
            return;
        }

        if (ctx is SqlBaseParser.ColumnReferenceContext colRef && colRef.identifier() != null)
        {
            string colName = SqlIdentifierHelper.NormalizeIdentifier(colRef.identifier().GetText());
            if (!string.IsNullOrWhiteSpace(colName))
            {
                references.Add(new FilterColumnReference(null, colName));
            }
            return;
        }

        for (int i = 0; i < ctx.ChildCount; i++)
        {
            if (ctx.GetChild(i) is Antlr4.Runtime.RuleContext child)
            {
                ExtractFilterColumnReferences(child, references);
            }
        }
    }

    /// <summary>
    /// Befund 3.6: sorting on a redacted column orders by the replacement value; guarded like WHERE/HAVING. Covers the
    /// ORDER BY of queries, windows and aggregates.
    /// </summary>
    public override void EnterOrderBy(SqlBaseParser.OrderByContext context)
    {
        // #322: the ORDER BY of a window is a window reference (the window guard answers it), not a plain filter reference.
        ExtractFilterColumnReferences(context, context.Parent is SqlBaseParser.WindowSpecificationContext ? _windowColumnReferences : _filterColumnReferences);
    }

    /// <summary>#322: PARTITION BY expressions of a window (the ORDER BY is taken by<see cref="EnterOrderBy"/>).</summary>
    public override void EnterWindowSpecification(SqlBaseParser.WindowSpecificationContext context)
    {
        foreach (var partition in context._partition)
        {
            ExtractFilterColumnReferences(partition, _windowColumnReferences);
        }
    }

    /// <summary>Wunsch 4 / Befund 3.6: an aggregate <c>FILTER (WHERE …)</c> filters rows like WHERE.</summary>
    public override void EnterFilter(SqlBaseParser.FilterContext context)
    {
        ExtractFilterColumnReferences(context.booleanExpression(), _filterColumnReferences);
    }

    public override void EnterQueryNoWith(SqlBaseParser.QueryNoWithContext context)
    {
        // Check top-level limit only on the root query (SEC M-22: CTE bodies and subqueries do not count)
        bool isRoot = context.Parent is SqlBaseParser.QueryContext query && query.Parent is SqlBaseParser.RootQueryContext;
        if (isRoot && _currentSubqueryDepth == 0)
        {
            if (context.limit != null)
            {
                _hasExplicitLimit = true;
                if (context.limit.rowCount() != null)
                {
                    string text = context.limit.rowCount().GetText();
                    if (long.TryParse(text, out long limitVal))
                    {
                        _explicitLimitValue = limitVal;
                    }
                }
            }

            if (context.orderBy() != null)
            {
                _hasRootOrderBy = true;
            }

            if (context.OFFSET() != null || context.offset != null)
            {
                _hasRootOffset = true;
            }
        }
    }

    public override void EnterTableName(SqlBaseParser.TableNameContext context)
    {
        RecordTableAccess(context.qualifiedName(), context.Parent as SqlBaseParser.AliasedRelationContext);
    }

    public override void EnterTable(SqlBaseParser.TableContext context)
    {
        RecordTableAccess(context.qualifiedName(), null);
    }

    public override void EnterTableArgumentTable(SqlBaseParser.TableArgumentTableContext context)
    {
        RecordTableAccess(context.qualifiedName(), null);
    }

    public override void EnterDelete(SqlBaseParser.DeleteContext context)
    {
        RecordTableAccess(context.qualifiedName(), null);
        if (context.booleanExpression() != null)
        {
            ExtractFilterColumnReferences(context.booleanExpression(), _filterColumnReferences);
        }
    }

    public override void EnterUpdate(SqlBaseParser.UpdateContext context)
    {
        RecordTableAccess(context.qualifiedName(), null);
        if (context.where != null)
        {
            ExtractFilterColumnReferences(context.where, _filterColumnReferences);
        }
    }

    public override void EnterInsertInto(SqlBaseParser.InsertIntoContext context)
    {
        RecordTableAccess(context.qualifiedName(), null);
    }

    private void RecordTableAccess(SqlBaseParser.QualifiedNameContext qualifiedNameContext, SqlBaseParser.AliasedRelationContext? aliasedRelation)
    {
        if (qualifiedNameContext == null) return;

        string normalizedFullName = SqlIdentifierHelper.NormalizeQualifiedName(qualifiedNameContext);
        if (string.IsNullOrWhiteSpace(normalizedFullName)) return;

        var ids = qualifiedNameContext.identifier();

        // SEC C-02: Skip only single-part names that resolve to a CTE in the current scope.
        if (ids != null && ids.Length == 1)
        {
            string key = SqlIdentifierHelper.FoldIdentifierForScope(ids[0].GetText());
            if (_cteScopeStack.Peek().Contains(key))
            {
                if (_cteStack.Count > 0)
                {
                    _cteStack.Peek().References.Add(key);
                }
                else
                {
                    _rootCteReferences.Add(key);
                }
                return;
            }

            // Issue #277: keyword-less self-reference; the name stays reported as a table (see SqlQueryMetadata).
            if (_definingNonRecursiveCtes.Contains(key))
            {
                _hasNonRecursiveSelfReference = true;
            }
        }

        string? alias = null;
        if (aliasedRelation?.identifier() != null)
        {
            alias = SqlIdentifierHelper.NormalizeIdentifier(aliasedRelation.identifier().GetText());
        }

        string? catalog = null;
        string? schema = null;
        string tableName;
        bool schemaQuoted = false;
        bool tableNameQuoted = false;
        if (ids != null && ids.Length >= 4)
        {
            throw new Antlr4.Runtime.Misc.ParseCanceledException($"Four-part table names ('{normalizedFullName}') are not permitted.");
        }

        if (ids != null && ids.Length == 3)
        {
            catalog = SqlIdentifierHelper.NormalizeIdentifier(ids[0].GetText());
            schema = SqlIdentifierHelper.NormalizeIdentifier(ids[1].GetText());
            tableName = SqlIdentifierHelper.NormalizeIdentifier(ids[2].GetText());
            schemaQuoted = IsQuotedIdentifier(ids[1].GetText());
            tableNameQuoted = IsQuotedIdentifier(ids[2].GetText());
        }
        else if (ids != null && ids.Length == 2)
        {
            schema = SqlIdentifierHelper.NormalizeIdentifier(ids[0].GetText());
            tableName = SqlIdentifierHelper.NormalizeIdentifier(ids[1].GetText());
            schemaQuoted = IsQuotedIdentifier(ids[0].GetText());
            tableNameQuoted = IsQuotedIdentifier(ids[1].GetText());
        }
        else if (ids != null && ids.Length == 1)
        {
            tableName = SqlIdentifierHelper.NormalizeIdentifier(ids[0].GetText());
            tableNameQuoted = IsQuotedIdentifier(ids[0].GetText());
        }
        else
        {
            tableName = normalizedFullName;
        }

        // RR-L5-02: dedupe on folded identifiers (quoted = exact case, unquoted = lower case) with ordinal comparison,
        // so that "Orders" and orders (two different PostgreSQL tables) are reported as two distinct targets.
        string dedupeKey = ids != null && ids.Length > 0
            ? string.Join(".", Array.ConvertAll(ids, i => SqlIdentifierHelper.FoldIdentifierForScope(i.GetText()))) + "->" + (alias ?? "")
            : $"{normalizedFullName}->{alias ?? ""}";
        if (_seenTableKeys.Add(dedupeKey))
        {
            _referencedTables.Add(new TableAccessTarget(
                Catalog: catalog,
                Schema: schema,
                TableName: tableName,
                Alias: alias,
                FullName: normalizedFullName,
                SchemaQuoted: schemaQuoted,
                TableNameQuoted: tableNameQuoted));
        }
    }

    private void RecordJoin()
    {
        if (_cteStack.Count > 0)
        {
            _cteStack.Peek().JoinCount++;
        }
        else
        {
            _joinCount++;
        }
    }

    private void RecordCrossJoin(CrossJoinTarget target)
    {
        if (_cteStack.Count > 0)
        {
            _cteStack.Peek().CrossJoinCount++;
            _cteStack.Peek().CrossJoins.Add(target);
        }
        else
        {
            _crossJoinCount++;
            _crossJoins.Add(target);
        }
    }

    private void RecordSetOperation()
    {
        if (_cteStack.Count > 0)
        {
            _cteStack.Peek().SetOperationCount++;
        }
        else
        {
            _setOperationCount++;
        }
    }

    private static string? ExtractRelationName(Antlr4.Runtime.RuleContext? ctx)
    {
        if (ctx == null) return null;

        if (ctx is SqlBaseParser.TableNameContext tbl && tbl.qualifiedName() != null)
        {
            return SqlIdentifierHelper.NormalizeQualifiedName(tbl.qualifiedName());
        }

        if (ctx is SqlBaseParser.AliasedRelationContext aliased)
        {
            var primaryName = ExtractRelationName(aliased.relationPrimary());
            if (primaryName != null) return primaryName;
            if (aliased.identifier() != null)
            {
                return SqlIdentifierHelper.NormalizeIdentifier(aliased.identifier().GetText());
            }
        }

        for (int i = 0; i < ctx.ChildCount; i++)
        {
            if (ctx.GetChild(i) is Antlr4.Runtime.RuleContext child)
            {
                var name = ExtractRelationName(child);
                if (name != null) return name;
            }
        }

        return null;
    }

    private static bool IsConstantExpression(Antlr4.Runtime.RuleContext? ctx)
    {
        if (ctx == null) return false;
        var refs = new List<FilterColumnReference>();
        ExtractFilterColumnReferences(ctx, refs);
        return refs.Count == 0;
    }

    private static bool IsQuotedIdentifier(string rawIdentifier)
    {
        var id = rawIdentifier.Trim();
        return id.Length >= 2 &&
               ((id[0] == '"' && id[^1] == '"') ||
                (id[0] == '`' && id[^1] == '`') ||
                (id[0] == '[' && id[^1] == ']'));
    }
}
