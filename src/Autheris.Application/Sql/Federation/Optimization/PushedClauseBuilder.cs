namespace Autheris.Application.Sql.Federation.Optimization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Services;
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
        var rendered = new List<(string Column, string Operator, string[] Markers, bool IsList)>();
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
                rendered.Add((c.ColumnName, c.Operator, paramMarkers.ToArray(), true));
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
                rendered.Add((c.ColumnName, c.Operator, [paramName], false));
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
            // SQLA-13: rendered from the conjuncts BuildClause actually bound, so markers and bound parameters can never diverge.
            TrinoSqlFactory = resolve => string.Join(" AND ", rendered.Select(r =>
                r.Markers.Length == 1 && !r.IsList
                    ? $"\"{resolve(r.Column)}\" {r.Operator} {r.Markers[0]}"
                    : $"\"{resolve(r.Column)}\" {r.Operator} ({string.Join(", ", r.Markers)})"))
        };
    }

    public static TableFilterClause? BuildPushdown(
        SqlStatement statement,
        TableAccessTarget target,
        TableMetadata metadata,
        TableAccessDecision decision,
        FederatedParameterContract contract,
        IReadOnlyList<ResolvedSourceTable> allTables,
        out PushedParameterAudit? parameterAudit)
    {
        ArgumentNullException.ThrowIfNull(allTables);
        parameterAudit = null;
        if (statement is not SelectStatement select || select.Body is not QuerySpecification querySpec || querySpec.Where == null)
        {
            return null;
        }

        // SQLA-12: only WHERE conjuncts that the splitter allows for this table (never the null-supplying side of an outer join; a full outer join
        // yields no conjuncts at all) are candidates.
        var leaves = new List<TableSource>();
        if (querySpec.From != null)
        {
            CollectLeaves(querySpec.From, leaves);
        }

        var targetSources = leaves.OfType<NamedTableSource>().Where(l => IsSourceOf(l, target)).ToList();
        if (targetSources.Count != 1)
        {
            return null;   // the table cannot be identified in the FROM clause unambiguously: nothing is pushed
        }

        var whereConjuncts = new List<Expression>();
        foreach (var conjunct in SqlConjunctSplitter.Split(statement))
        {
            if (conjunct.Origin == ConjunctOrigin.Where &&
                (conjunct.AllowedTableSources is null || conjunct.AllowedTableSources.Contains(targetSources[0])))
            {
                whereConjuncts.Add(conjunct.Expression);
            }
        }

        bool hasDerivedSources = leaves.Any(static l => l is not NamedTableSource);
        var candidates = ExtractCandidates(whereConjuncts, target, metadata, allTables, hasDerivedSources);
        if (candidates.Count == 0)
        {
            return null;
        }

        return BuildClause(metadata, decision, candidates, contract, DialectCapabilityTable.Default.Get(TargetSqlDialect.DuckDb), out parameterAudit);
    }

    private static void CollectLeaves(TableSource source, List<TableSource> leaves)
    {
        if (source is JoinedTableSource join)
        {
            CollectLeaves(join.Left, leaves);
            CollectLeaves(join.Right, leaves);
        }
        else
        {
            leaves.Add(source);
        }
    }

    private static bool IsSourceOf(NamedTableSource source, TableAccessTarget target) =>
        string.Equals(source.Name.Parts[^1].Value, target.TableName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(source.Alias?.Value ?? string.Empty, target.Alias ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static List<PushedConjunctCandidate> ExtractCandidates(
        IReadOnlyList<Expression> conjuncts,
        TableAccessTarget target,
        TableMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> allTables,
        bool hasDerivedSources)
    {
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
                        if (BelongsToTable(colRef.Name, target, metadata, allTables, hasDerivedSources, out var colName))
                        {
                            candidates.Add(new PushedConjunctCandidate(colName, opStr, paramRef.Name, IsList: false));
                        }
                    }
                    else if (binary.Right is ColumnReference colRef2 && binary.Left is ParameterReference paramRef2)
                    {
                        if (BelongsToTable(colRef2.Name, target, metadata, allTables, hasDerivedSources, out var colName))
                        {
                            candidates.Add(new PushedConjunctCandidate(colName, opStr, paramRef2.Name, IsList: false));
                        }
                    }
                }
            }
            else if (conjunct is InListExpression inList && inList.Items.Count == 1 && inList.Items[0] is ParameterReference paramRef)
            {
                if (inList.Operand is ColumnReference colRef && BelongsToTable(colRef.Name, target, metadata, allTables, hasDerivedSources, out var colName))
                {
                    candidates.Add(new PushedConjunctCandidate(colName, inList.IsNotIn ? "NOT IN" : "IN", paramRef.Name, IsList: true));
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// SQLA-12: attributes a column reference to <paramref name="target"/>. A qualifier must name the table's alias (or, without an alias, its table
    /// name or full name); a schema alone is no table identity. An unqualified column is accepted only when no other table of the statement (and no derived
    /// source) can expose a column of that name. Anything else is "not this table" and nothing is pushed.
    /// </summary>
    private static bool BelongsToTable(
        SqlQualifiedName name,
        TableAccessTarget target,
        TableMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> allTables,
        bool hasDerivedSources,
        out string columnName)
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
            if (hasDerivedSources) return false;
            foreach (var other in allTables)
            {
                if (other.Target.Equals(target)) continue;
                if (other.Metadata.GetColumn(colName) != null) return false;
            }

            columnName = colName;
            return true;
        }

        string qualifier = string.Join(".", name.Parts.Take(name.Parts.Count - 1).Select(p => p.Value));
        bool matches = !string.IsNullOrEmpty(target.Alias)
            ? string.Equals(qualifier, target.Alias, StringComparison.OrdinalIgnoreCase)
            : string.Equals(qualifier, target.TableName, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(qualifier, target.FullName, StringComparison.OrdinalIgnoreCase);
        if (!matches) return false;

        columnName = colName;
        return true;
    }
}
