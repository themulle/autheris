namespace Autheris.Application.Sql.Federation.Optimization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql.Federation.Parameters;
using Autheris.Application.Sql.ListParameters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine;
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
    public static ColumnTypeClass InferColumnTypeClass(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return ColumnTypeClass.Unknown;
        }

        var normalized = dataType.Trim().ToLowerInvariant();
        if (normalized.Contains('('))
        {
            normalized = normalized[..normalized.IndexOf('(')].Trim();
        }

        return normalized switch
        {
            "int" or "integer" or "bigint" or "smallint" or "tinyint" or "int4" or "int8" or "int2" => ColumnTypeClass.Integer,
            "numeric" or "decimal" or "money" or "smallmoney" => ColumnTypeClass.Decimal,
            "bool" or "boolean" or "bit" => ColumnTypeClass.Boolean,
            "varchar" or "text" or "char" or "nvarchar" or "string" or "nchar" => ColumnTypeClass.String,
            "date" or "time" or "timestamp" or "timestamptz" or "datetime" or "datetime2" => ColumnTypeClass.Temporal,
            "guid" or "uuid" or "uniqueidentifier" => ColumnTypeClass.Guid,
            "float" or "double" or "real" or "float4" or "float8" => ColumnTypeClass.Float,
            "bytea" or "binary" or "varbinary" or "blob" => ColumnTypeClass.Binary,
            "json" or "jsonb" => ColumnTypeClass.Json,
            _ => ColumnTypeClass.Unknown
        };
    }

    public static TableFilterClause? BuildClause(
        TableMetadata metadata,
        TableAccessDecision decision,
        IReadOnlyList<PushedConjunctCandidate> candidates,
        FederatedParameterContract contract,
        DialectCapabilities? capabilities,
        out PushedParameterAudit? parameterAudit)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(contract);

        capabilities ??= DialectCapabilityTable.Default.Get(TargetSqlDialect.DuckDb);
        parameterAudit = null;

        if (candidates.Count == 0 || !contract.HasValues)
        {
            return null;
        }

        var pushedClauses = new List<string>();
        var pushedParameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var pushedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pushedParamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int conjunctIdx = 0;
        foreach (var c in candidates)
        {
            // SEC-FED-19: Zero-Trust Masked Column Gate
            var effectiveAccess = decision.GetEffectiveColumnAccess(c.ColumnName, metadata);
            if (effectiveAccess is ColumnAccessLevel.Mask or ColumnAccessLevel.Deny)
            {
                continue; // Strictly forbidden from pushdown
            }

            var col = metadata.GetColumn(c.ColumnName);
            if (col == null)
            {
                continue;
            }

            var colClass = InferColumnTypeClass(col.DataType);
            if (colClass is not (ColumnTypeClass.Integer or ColumnTypeClass.Decimal or ColumnTypeClass.Boolean or ColumnTypeClass.String))
            {
                continue; // Complex or temporal types remain exclusively in DuckDB
            }

            if (c.IsList)
            {
                int effectiveMax = ListParameterLimits.EffectiveMax(
                    capabilities.MaxInListItems ?? 1000,
                    capabilities);

                if (!contract.TryGetList(c.ParameterName, colClass, effectiveMax, out var listValues))
                {
                    continue;
                }

                if (listValues.Count == 0 || listValues.Count > effectiveMax)
                {
                    continue;
                }

                var paramMarkers = new List<string>(listValues.Count);
                for (int i = 0; i < listValues.Count; i++)
                {
                    var paramName = $"@p_pd_{conjunctIdx}_{i}";
                    // SEC-ADG-02: Fail-closed on parameter name collision
                    if (!pushedParameters.TryAdd(paramName, listValues[i]))
                    {
                        throw new SecurityException("A statement parameter name is bound twice (fail-closed).");
                    }
                    paramMarkers.Add(paramName);
                }

                pushedClauses.Add($"\"{c.ColumnName}\" {c.Operator} ({string.Join(", ", paramMarkers)})");
                pushedCols.Add(c.ColumnName);
                pushedParamNames.Add(c.ParameterName);
                listCounts[c.ParameterName] = listValues.Count;
                conjunctIdx++;
            }
            else
            {
                if (!contract.TryGetScalar(c.ParameterName, colClass, out var scalarVal) || scalarVal == null)
                {
                    continue;
                }

                var paramName = $"@p_pd_{conjunctIdx}_0";
                // SEC-ADG-02: Fail-closed on parameter name collision
                if (!pushedParameters.TryAdd(paramName, scalarVal))
                {
                    throw new SecurityException("A statement parameter name is bound twice (fail-closed).");
                }

                pushedClauses.Add($"\"{c.ColumnName}\" {c.Operator} {paramName}");
                pushedCols.Add(c.ColumnName);
                pushedParamNames.Add(c.ParameterName);
                conjunctIdx++;
            }
        }

        if (pushedClauses.Count == 0)
        {
            return null;
        }

        parameterAudit = new PushedParameterAudit(
            pushedParamNames.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            listCounts);

        string sqlPredicate = string.Join(" AND ", pushedClauses);
        var refCols = pushedCols.ToArray();

        return new TableFilterClause(
            sqlPredicate,
            pushedParameters,
            refCols)
        {
            TrinoSqlFactory = resolve =>
            {
                var trinoParts = new List<string>();
                int cIdx = 0;
                foreach (var c in candidates)
                {
                    var effectiveAccess = decision.GetEffectiveColumnAccess(c.ColumnName, metadata);
                    if (effectiveAccess is ColumnAccessLevel.Mask or ColumnAccessLevel.Deny)
                        continue;

                    var col = metadata.GetColumn(c.ColumnName);
                    if (col == null) continue;
                    var colClass = InferColumnTypeClass(col.DataType);
                    if (colClass is not (ColumnTypeClass.Integer or ColumnTypeClass.Decimal or ColumnTypeClass.Boolean or ColumnTypeClass.String))
                        continue;

                    string resolvedCol = $"\"{resolve(c.ColumnName)}\"";
                    if (c.IsList)
                    {
                        if (contract.TryGetList(c.ParameterName, colClass, 10000, out var items))
                        {
                            var markers = Enumerable.Range(0, items.Count).Select(i => $"@p_pd_{cIdx}_{i}");
                            trinoParts.Add($"{resolvedCol} {c.Operator} ({string.Join(", ", markers)})");
                            cIdx++;
                        }
                    }
                    else
                    {
                        if (contract.TryGetScalar(c.ParameterName, colClass, out _))
                        {
                            trinoParts.Add($"{resolvedCol} {c.Operator} @p_pd_{cIdx}_0");
                            cIdx++;
                        }
                    }
                }
                return string.Join(" AND ", trinoParts);
            }
        };
    }

    public static TableFilterClause? BuildPushdown(
        SqlStatement statement,
        TableAccessTarget target,
        TableMetadata metadata,
        TableAccessDecision decision,
        FederatedParameterContract contract,
        out PushedParameterAudit? parameterAudit)
    {
        parameterAudit = null;
        if (statement is not SelectStatement select || select.Body is not QuerySpecification querySpec || querySpec.Where == null)
        {
            return null;
        }

        var candidates = ExtractCandidates(querySpec.Where, target, metadata);
        if (candidates.Count == 0)
        {
            return null;
        }

        return BuildClause(metadata, decision, candidates, contract, DialectCapabilityTable.Default.Get(TargetSqlDialect.DuckDb), out parameterAudit);
    }

    private static List<PushedConjunctCandidate> ExtractCandidates(
        Expression expression,
        TableAccessTarget target,
        TableMetadata metadata)
    {
        var conjuncts = new List<Expression>();
        FlattenAnd(expression, conjuncts);

        var candidates = new List<PushedConjunctCandidate>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct is BinaryExpression binary)
            {
                string? opStr = binary.Operator switch
                {
                    BinaryOperator.Equal => "=",
                    BinaryOperator.NotEqual => "<>",
                    BinaryOperator.GreaterThan => ">",
                    BinaryOperator.GreaterThanOrEqual => ">=",
                    BinaryOperator.LessThan => "<",
                    BinaryOperator.LessThanOrEqual => "<=",
                    _ => null
                };

                if (opStr != null)
                {
                    if (binary.Left is ColumnReference colRef && binary.Right is ParameterReference paramRef)
                    {
                        if (BelongsToTable(colRef.Name, target, metadata, out var colName))
                        {
                            candidates.Add(new PushedConjunctCandidate(colName, opStr, paramRef.Name, IsList: false));
                        }
                    }
                    else if (binary.Right is ColumnReference colRef2 && binary.Left is ParameterReference paramRef2)
                    {
                        if (BelongsToTable(colRef2.Name, target, metadata, out var colName))
                        {
                            candidates.Add(new PushedConjunctCandidate(colName, opStr, paramRef2.Name, IsList: false));
                        }
                    }
                }
            }
            else if (conjunct is InListExpression inList && inList.Items.Count == 1 && inList.Items[0] is ParameterReference paramRef)
            {
                if (inList.Operand is ColumnReference colRef && BelongsToTable(colRef.Name, target, metadata, out var colName))
                {
                    candidates.Add(new PushedConjunctCandidate(colName, inList.IsNotIn ? "NOT IN" : "IN", paramRef.Name, IsList: true));
                }
            }
        }

        return candidates;
    }

    private static void FlattenAnd(Expression expr, List<Expression> result)
    {
        if (expr is BinaryExpression { Operator: BinaryOperator.And } andExpr)
        {
            FlattenAnd(andExpr.Left, result);
            FlattenAnd(andExpr.Right, result);
        }
        else
        {
            result.Add(expr);
        }
    }

    private static bool BelongsToTable(SqlQualifiedName name, TableAccessTarget target, TableMetadata metadata, out string columnName)
    {
        columnName = string.Empty;
        if (name.Parts.Count == 0) return false;

        string colName = name.Parts[^1].Value;
        if (metadata.GetColumn(colName) == null)
        {
            return false;
        }

        if (name.Parts.Count == 1)
        {
            columnName = colName;
            return true;
        }

        string qualifier = string.Join(".", name.Parts.Take(name.Parts.Count - 1).Select(p => p.Value));
        if (string.Equals(qualifier, target.Alias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(qualifier, target.TableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(qualifier, target.FullName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(qualifier, target.Schema, StringComparison.OrdinalIgnoreCase))
        {
            columnName = colName;
            return true;
        }

        columnName = colName;
        return true;
    }
}
