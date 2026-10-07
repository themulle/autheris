namespace TrinoSqlEngine.Ast.Visitors;

using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Generic visitor interface for typed AST nodes.
/// </summary>
public interface ISqlAstVisitor<out TResult>
{
    TResult Visit(SqlNode node);
    TResult VisitSelectStatement(SelectStatement node);
    TResult VisitInsertStatement(InsertStatement node);
    TResult VisitUpdateStatement(UpdateStatement node);
    TResult VisitDeleteStatement(DeleteStatement node);
    TResult VisitQuerySpecification(QuerySpecification node);
    TResult VisitSetOperationQuery(SetOperationQuery node);
    TResult VisitValuesQueryBody(ValuesQueryBody node);
    TResult VisitTableQueryBody(TableQueryBody node);
    TResult VisitColumnSelectItem(ColumnSelectItem node);
    TResult VisitWildcardSelectItem(WildcardSelectItem node);
    TResult VisitWithClause(WithClause node);
    TResult VisitCommonTableExpression(CommonTableExpression node);
    TResult VisitNamedTableSource(NamedTableSource node);
    TResult VisitSubqueryTableSource(SubqueryTableSource node);
    TResult VisitJoinedTableSource(JoinedTableSource node);
    TResult VisitLateralTableSource(LateralTableSource node);
    TResult VisitOnJoinCondition(OnJoinCondition node);
    TResult VisitUsingJoinCondition(UsingJoinCondition node);
    TResult VisitColumnReference(ColumnReference node);
    TResult VisitParameterReference(ParameterReference node);
    TResult VisitLiteralExpression(LiteralExpression node);
    TResult VisitBinaryExpression(BinaryExpression node);
    TResult VisitUnaryExpression(UnaryExpression node);
    TResult VisitLikeExpression(LikeExpression node);
    TResult VisitInListExpression(InListExpression node);
    TResult VisitInSubqueryExpression(InSubqueryExpression node);
    TResult VisitExistsExpression(ExistsExpression node);
    TResult VisitScalarSubqueryExpression(ScalarSubqueryExpression node);
    TResult VisitQuantifiedComparisonExpression(QuantifiedComparisonExpression node);
    TResult VisitIsDistinctFromExpression(IsDistinctFromExpression node);
    TResult VisitBetweenExpression(BetweenExpression node);
    TResult VisitCaseExpression(CaseExpression node);
    TResult VisitWhenClause(WhenClause node);
    TResult VisitFunctionCallExpression(FunctionCallExpression node);
    TResult VisitWindowSpecification(WindowSpecification node);
    TResult VisitCastExpression(CastExpression node);
    TResult VisitRowValueExpression(RowValueExpression node);
    TResult VisitArrayConstructorExpression(ArrayConstructorExpression node);
    TResult VisitSubscriptExpression(SubscriptExpression node);
    TResult VisitExtractExpression(ExtractExpression node);
    TResult VisitOrderByClause(OrderByClause node);
    TResult VisitOrderByElement(OrderByElement node);
    TResult VisitPaginationClause(PaginationClause node);
    TResult VisitGroupByClause(GroupByClause node);
    TResult VisitUpdateAssignment(UpdateAssignment node);
    TResult VisitSqlIdentifier(SqlIdentifier node);
    TResult VisitSqlQualifiedName(SqlQualifiedName node);
}
