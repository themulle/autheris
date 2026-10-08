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
    private readonly HashSet<string> _joinConditionColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<JoinColumnReference> _joinColumnReferences = new();
    private readonly Stack<HashSet<string>> _cteScopeStack = new();
    private readonly List<string> _functionCalls = new();
    private readonly HashSet<string> _seenFunctionCalls = new(StringComparer.Ordinal);
    private readonly List<string> _tableFunctionCalls = new();
    private bool _hasSessionProperties;
    private bool _hasInlineFunctionDefinitions;

    private int _joinCount;
    private int _currentSubqueryDepth;
    private int _maxSubqueryDepth;
    private bool _hasExplicitLimit;
    private long? _explicitLimitValue;
    private bool _isRootQuerySpecification = true;

    public SqlQueryAnalyzer()
    {
        _cteScopeStack.Push(new HashSet<string>(StringComparer.Ordinal));
    }

    public SqlQueryMetadata Analyze(SqlBaseParser.SingleStatementContext statementContext)
    {
        ArgumentNullException.ThrowIfNull(statementContext);

        Reset();
        ParseTreeWalker.Default.Walk(this, statementContext);

        return new SqlQueryMetadata(
            StatementType: _statementType,
            ReferencedTables: _referencedTables.AsReadOnly(),
            ProjectedColumns: _projectedColumns.AsReadOnly(),
            JoinCount: _joinCount,
            MaxSubqueryDepth: _maxSubqueryDepth,
            HasExplicitLimit: _hasExplicitLimit,
            ExplicitLimitValue: _explicitLimitValue,
            JoinConditionColumns: new HashSet<string>(_joinConditionColumns, StringComparer.OrdinalIgnoreCase),
            FunctionCalls: _functionCalls.ToArray(),
            TableFunctionCalls: _tableFunctionCalls.ToArray(),
            HasSessionProperties: _hasSessionProperties,
            HasInlineFunctionDefinitions: _hasInlineFunctionDefinitions,
            JoinColumnReferences: _joinColumnReferences.ToArray());
    }

    private void Reset()
    {
        _statementType = SqlStatementType.Other;
        _referencedTables.Clear();
        _projectedColumns.Clear();
        _seenTableKeys.Clear();
        _joinConditionColumns.Clear();
        _joinColumnReferences.Clear();
        _cteScopeStack.Clear();
        _cteScopeStack.Push(new HashSet<string>(StringComparer.Ordinal));
        _functionCalls.Clear();
        _seenFunctionCalls.Clear();
        _tableFunctionCalls.Clear();
        _hasSessionProperties = false;
        _hasInlineFunctionDefinitions = false;
        _joinCount = 0;
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

    public override void ExitNamedQuery(SqlBaseParser.NamedQueryContext context)
    {
        // SEC C-02: CTE names are single-part identifiers (folded scope key, never a dotted string).
        string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(context.name.GetText());
        _cteScopeStack.Peek().Add(cteKey);
    }

    public override void EnterSubquery(SqlBaseParser.SubqueryContext context)
    {
        _currentSubqueryDepth++;
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
            _joinCount += (context.relation().Length - 1);
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
        _joinCount++;
    }

    public override void EnterJoinCriteria(SqlBaseParser.JoinCriteriaContext context)
    {
        ExtractJoinColumnReferences(context, _joinColumnReferences, _joinConditionColumns);
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
                    foreach (var id in leftIds) _joinConditionColumns.Add(id);
                    foreach (var id in rightIds) _joinConditionColumns.Add(id);
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

    public override void EnterQueryNoWith(SqlBaseParser.QueryNoWithContext context)
    {
        // Check top-level limit only on the root query (SEC M-22: CTE bodies and subqueries do not count)
        bool isRoot = context.Parent is SqlBaseParser.QueryContext query && query.Parent is SqlBaseParser.RootQueryContext;
        if (isRoot && _currentSubqueryDepth == 0 && context.limit != null)
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
    }

    public override void EnterUpdate(SqlBaseParser.UpdateContext context)
    {
        RecordTableAccess(context.qualifiedName(), null);
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
        if (ids != null && ids.Length == 1 &&
            _cteScopeStack.Peek().Contains(SqlIdentifierHelper.FoldIdentifierForScope(ids[0].GetText())))
        {
            return;
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

    private static bool IsQuotedIdentifier(string rawIdentifier)
    {
        var id = rawIdentifier.Trim();
        return id.Length >= 2 &&
               ((id[0] == '"' && id[^1] == '"') ||
                (id[0] == '`' && id[^1] == '`') ||
                (id[0] == '[' && id[^1] == ']'));
    }
}
