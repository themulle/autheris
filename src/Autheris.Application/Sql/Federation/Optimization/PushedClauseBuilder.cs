namespace Autheris.Application.Sql.Federation.Optimization;

using System.Collections.Generic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql.Federation.Parameters;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Nodes;

public sealed record PushedConjunctCandidate(
    string ColumnName,
    string Operator,
    string ParameterName,
    bool IsList = false);

public static class PushedClauseBuilder
{
    public static ColumnTypeClass InferColumnTypeClass(string? dataType) =>
        Pushdown.PushedClauseBuilder.InferColumnTypeClass(dataType);

    public static TableFilterClause? BuildClause(
        TableMetadata metadata,
        TableAccessDecision decision,
        IReadOnlyList<PushedConjunctCandidate> candidates,
        FederatedParameterContract contract,
        DialectCapabilities? capabilities,
        out PushedParameterAudit? parameterAudit) =>
        Pushdown.PushedClauseBuilder.BuildClause(metadata, decision, candidates, contract, capabilities, out parameterAudit);

    public static TableFilterClause? BuildPushdown(
        SqlStatement statement,
        TableAccessTarget target,
        TableMetadata metadata,
        TableAccessDecision decision,
        FederatedParameterContract contract,
        IReadOnlyList<ResolvedSourceTable> allTables,
        out PushedParameterAudit? parameterAudit) =>
        Pushdown.PushedClauseBuilder.BuildPushdown(statement, target, metadata, decision, contract, allTables, out parameterAudit);
}
