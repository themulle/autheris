namespace Autheris.Application.Sql.Federation.Pushdown;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Olap;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.Optimization;
using Autheris.Application.Sql.Federation.Parameters;
using Autheris.Application.Sql.ListParameters;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Consolidated parameterized TableFilterClause builder for federated predicate pushdown.
/// Resolves column names against catalog metadata, quotes identifiers consistently, and enforces fail-closed parameter binding.
/// </summary>
public static class PushedClauseBuilder
{
    public static TableFilterClause Build(
        IReadOnlyList<SingleTableConjunct> conjuncts,
        TableMetadata metadata,
        IReadOnlyDictionary<string, object?>? clientParameters = null)
    {
        ArgumentNullException.ThrowIfNull(conjuncts);
        ArgumentNullException.ThrowIfNull(metadata);
        if (conjuncts.Count == 0) throw new ArgumentException("Cannot build a filter clause with zero conjuncts.", nameof(conjuncts));

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var referencedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var structuralPredicates = new List<PushdownPredicateInfo>();
        var renderers = new List<Func<Func<string, string>, string>>(conjuncts.Count);
        int paramIndex = 0;

        foreach (var conjunct in conjuncts)
        {
            foreach (var col in conjunct.Columns) referencedColumns.Add(ResolveColumnName(metadata, col));
            renderers.Add(BuildRenderer(conjunct.Predicate, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters));
        }

        string BuildSql(Func<string, string> resolve) =>
            renderers.Count == 1 ? renderers[0](resolve) : string.Join(" AND ", renderers.Select(r => "(" + r(resolve) + ")"));

        return new TableFilterClause(BuildSql(static col => col), parameters, referencedColumns.ToList())
        {
            TrinoSqlFactory = columnResolver => BuildSql(columnResolver ?? (static col => col)),
            PushdownPredicates = structuralPredicates
        };
    }

    public static ColumnTypeClass InferColumnTypeClass(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return ColumnTypeClass.Unknown;
        var normalized = dataType.Trim().ToLowerInvariant();
        if (normalized.Contains('(', StringComparison.Ordinal)) normalized = normalized[..normalized.IndexOf('(', StringComparison.Ordinal)].Trim();

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
        if (candidates.Count == 0 || !contract.HasValues) return null;

        var pushedClauses = new List<string>();
        var rendered = new List<(string Column, string Operator, string[] Markers, bool IsList)>();
        var pushedParameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var pushedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pushedParamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int conjunctIdx = 0;
        foreach (var c in candidates)
        {
            var col = metadata.GetColumn(c.ColumnName);
            if (col == null) continue;

            var canonicalColumn = col.ColumnName;
            if (decision.GetEffectiveColumnAccess(canonicalColumn, metadata) is ColumnAccessLevel.Mask or ColumnAccessLevel.Deny) continue;

            var colClass = InferColumnTypeClass(col.DataType);
            if (colClass is not (ColumnTypeClass.Integer or ColumnTypeClass.Decimal or ColumnTypeClass.Boolean or ColumnTypeClass.String)) continue;

            if (c.IsList)
            {
                int effectiveMax = ListParameterLimits.EffectiveMax(capabilities.MaxInListItems ?? 1000, capabilities);
                if (!contract.TryGetList(c.ParameterName, colClass, effectiveMax, out var listValues) || listValues.Count == 0 || listValues.Count > effectiveMax) continue;

                var paramMarkers = new string[listValues.Count];
                for (int i = 0; i < listValues.Count; i++)
                {
                    paramMarkers[i] = $"@p_pd_{conjunctIdx}_{i}";
                    BindParameterStrict(pushedParameters, paramMarkers[i], listValues[i], "A statement parameter name is bound twice (fail-closed).");
                }

                pushedClauses.Add($"{DataApiFilterTranslator.QuoteIdentifier(canonicalColumn)} {c.Operator} ({string.Join(", ", paramMarkers)})");
                rendered.Add((canonicalColumn, c.Operator, paramMarkers, true));
                listCounts[c.ParameterName] = listValues.Count;
            }
            else
            {
                if (!contract.TryGetScalar(c.ParameterName, colClass, out var scalarVal) || scalarVal == null) continue;
                var paramName = $"@p_pd_{conjunctIdx}_0";
                BindParameterStrict(pushedParameters, paramName, scalarVal, "A statement parameter name is bound twice (fail-closed).");

                pushedClauses.Add($"{DataApiFilterTranslator.QuoteIdentifier(canonicalColumn)} {c.Operator} {paramName}");
                rendered.Add((canonicalColumn, c.Operator, [paramName], false));
            }

            pushedCols.Add(canonicalColumn);
            pushedParamNames.Add(c.ParameterName);
            conjunctIdx++;
        }

        if (pushedClauses.Count == 0) return null;

        parameterAudit = new PushedParameterAudit(pushedParamNames.OrderBy(p => p, StringComparer.Ordinal).ToArray(), listCounts);

        return new TableFilterClause(string.Join(" AND ", pushedClauses), pushedParameters, pushedCols.ToArray())
        {
            ConstrainedColumns = rendered.Where(r => r.Operator is "=" or "<" or "<=" or ">" or ">=" or "IN")
                .Select(r => r.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            TrinoSqlFactory = resolve =>
            {
                var resolver = resolve ?? (static col => col);
                return string.Join(" AND ", rendered.Select(r =>
                    r.Markers.Length == 1 && !r.IsList
                        ? $"{DataApiFilterTranslator.QuoteIdentifier(resolver(r.Column))} {r.Operator} {r.Markers[0]}"
                        : $"{DataApiFilterTranslator.QuoteIdentifier(resolver(r.Column))} {r.Operator} ({string.Join(", ", r.Markers)})"));
            }
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
        if (statement is not SelectStatement { Body: QuerySpecification { Where: not null } querySpec }) return null;

        var leaves = new List<TableSource>();
        if (querySpec.From != null) CollectLeaves(querySpec.From, leaves);
        var targetSources = leaves.OfType<NamedTableSource>().Where(l => IsSourceOf(l, target)).ToList();
        if (targetSources.Count != 1) return null;

        var whereConjuncts = SqlConjunctSplitter.Split(statement)
            .Where(c => c.Origin == ConjunctOrigin.Where && (c.AllowedTableSources is null || c.AllowedTableSources.Contains(targetSources[0])))
            .Select(c => c.Expression)
            .ToList();

        var candidates = ExtractCandidates(whereConjuncts, target, metadata, allTables, leaves.Any(static l => l is not NamedTableSource));
        return candidates.Count == 0
            ? null
            : BuildClause(metadata, decision, candidates, contract, DialectCapabilityTable.Default.Get(TargetSqlDialect.DuckDb), out parameterAudit);
    }

    private static Func<Func<string, string>, string> BuildRenderer(
        SqlNode node, TableMetadata metadata, Dictionary<string, object?> parameters, ref int paramIndex,
        List<PushdownPredicateInfo> structuralPredicates, IReadOnlyDictionary<string, object?>? clientParameters)
    {
        switch (node)
        {
            case ColumnReference col:
                var colName = ResolveColumnName(metadata, col.Name.Parts[^1].Value);
                return resolve => DataApiFilterTranslator.QuoteIdentifier(resolve(colName));

            case LiteralExpression lit:
                var litMarker = AllocateParameter(parameters, ref paramIndex, lit.Value);
                return _ => litMarker;

            case ParameterReference or NamedBindMarkerExpression:
                if (!PushdownParameterResolver.TryResolve(node, clientParameters, out var clientValue)) throw new PushdownRejectedException();
                var pMarker = AllocateParameter(parameters, ref paramIndex, clientValue);
                return _ => pMarker;

            case ParenthesizedExpression paren:
                var inner = BuildRenderer(paren.Expression, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                return resolve => $"({inner(resolve)})";

            case BinaryExpression binary when binary.Operator is BinaryOperator.And or BinaryOperator.Or:
                var logOp = binary.Operator == BinaryOperator.And ? "AND" : "OR";
                var lLog = BuildRenderer(binary.Left, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var rLog = BuildRenderer(binary.Right, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                return resolve => $"({lLog(resolve)} {logOp} {rLog(resolve)})";

            case BinaryExpression binary:
                var opString = FormatBinaryOperator(binary.Operator)
                    ?? throw new NotSupportedException($"Binary operator '{binary.Operator}' is not supported for pushdown.");
                if ((ExtractColumnName(binary.Left, metadata) ?? ExtractColumnName(binary.Right, metadata)) is { } bCol)
                    structuralPredicates.Add(new PushdownPredicateInfo(bCol, binary.Operator.ToString()));
                var lBin = BuildRenderer(binary.Left, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var rBin = BuildRenderer(binary.Right, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                return resolve => $"{lBin(resolve)} {opString} {rBin(resolve)}";

            case UnaryExpression unary:
                var uCol = ExtractColumnName(unary.Operand, metadata);
                var uOp = BuildRenderer(unary.Operand, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                switch (unary.Operator)
                {
                    case UnaryOperator.IsNull:
                        if (uCol != null) structuralPredicates.Add(new PushdownPredicateInfo(uCol, "IsNull"));
                        return resolve => $"{uOp(resolve)} IS NULL";
                    case UnaryOperator.IsNotNull:
                        if (uCol != null) structuralPredicates.Add(new PushdownPredicateInfo(uCol, "IsNotNull"));
                        return resolve => $"{uOp(resolve)} IS NOT NULL";
                    case UnaryOperator.Not:
                        return resolve => $"NOT ({uOp(resolve)})";
                    default:
                        throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported for pushdown.");
                }

            case InListExpression inList:
                if (ExtractColumnName(inList.Operand, metadata) is { } iCol)
                    structuralPredicates.Add(new PushdownPredicateInfo(iCol, inList.IsNotIn ? "NotIn" : "In"));
                var opR = BuildRenderer(inList.Operand, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var itemRenderers = new List<Func<Func<string, string>, string>>(inList.Items.Count);
                foreach (var item in inList.Items) itemRenderers.Add(BuildRenderer(item, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters));
                var inOp = inList.IsNotIn ? "NOT IN" : "IN";
                return resolve => $"{opR(resolve)} {inOp} ({string.Join(", ", itemRenderers.Select(i => i(resolve)))})";

            case BetweenExpression between:
                if (ExtractColumnName(between.Operand, metadata) is { } btCol)
                    structuralPredicates.Add(new PushdownPredicateInfo(btCol, between.IsNotBetween ? "NotBetween" : "Between"));
                var btOpR = BuildRenderer(between.Operand, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var lowR = BuildRenderer(between.Lower, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var upR = BuildRenderer(between.Upper, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var btOp = between.IsNotBetween ? "NOT BETWEEN" : "BETWEEN";
                return resolve => $"{btOpR(resolve)} {btOp} {lowR(resolve)} AND {upR(resolve)}";

            case LikeExpression like:
                if (ExtractColumnName(like.Operand, metadata) is { } lkCol)
                    structuralPredicates.Add(new PushdownPredicateInfo(lkCol, like.IsNotLike ? "NotLike" : "Like"));
                var lkOpR = BuildRenderer(like.Operand, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var patR = BuildRenderer(like.Pattern, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var escR = like.Escape != null ? BuildRenderer(like.Escape, metadata, parameters, ref paramIndex, structuralPredicates, clientParameters) : null;
                var lkOp = like.IsNotLike ? "NOT LIKE" : "LIKE";
                return resolve => $"{lkOpR(resolve)} {lkOp} {patR(resolve)}{(escR != null ? $" ESCAPE {escR(resolve)}" : string.Empty)}";

            default:
                throw new NotSupportedException($"Expression type '{node.GetType().Name}' is not supported for pushdown.");
        }
    }

    private static string? FormatBinaryOperator(BinaryOperator op) => op switch
    {
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.LessThan => "<",
        BinaryOperator.LessThanOrEqual => "<=",
        BinaryOperator.GreaterThan => ">",
        BinaryOperator.GreaterThanOrEqual => ">=",
        _ => null
    };

    private static string ResolveColumnName(TableMetadata metadata, string rawColumnName) =>
        metadata.GetColumn(rawColumnName)?.ColumnName ?? rawColumnName;

    private static string AllocateParameter(Dictionary<string, object?> parameters, ref int index, object? value)
    {
        var marker = "@p_pd_" + (index++).ToString(CultureInfo.InvariantCulture);
        BindParameterStrict(parameters, marker, value, $"Duplicate pushdown parameter name '{marker}' encountered (fail-closed).");
        return marker;
    }

    private static void BindParameterStrict(Dictionary<string, object?> parameters, string marker, object? value, string conflictMessage)
    {
        if (!parameters.TryAdd(marker, value)) throw new SecurityException(conflictMessage);
    }

    private static string? ExtractColumnName(SqlNode node, TableMetadata metadata)
    {
        while (node is ParenthesizedExpression p) node = p.Expression;
        return node is ColumnReference col ? ResolveColumnName(metadata, col.Name.Parts[^1].Value) : null;
    }

    private static void CollectLeaves(TableSource source, List<TableSource> leaves)
    {
        if (source is JoinedTableSource join) { CollectLeaves(join.Left, leaves); CollectLeaves(join.Right, leaves); }
        else leaves.Add(source);
    }

    private static bool IsSourceOf(NamedTableSource source, TableAccessTarget target) =>
        string.Equals(source.Name.Parts[^1].Value, target.TableName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(source.Alias?.Value ?? string.Empty, target.Alias ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static List<PushedConjunctCandidate> ExtractCandidates(
        IReadOnlyList<Expression> conjuncts, TableAccessTarget target, TableMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> allTables, bool hasDerivedSources)
    {
        var candidates = new List<PushedConjunctCandidate>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct is BinaryExpression binary && FormatBinaryOperator(binary.Operator) is { } opStr)
            {
                if (binary.Left is ColumnReference colRef && binary.Right is ParameterReference paramRef &&
                    BelongsToTable(colRef.Name, target, metadata, allTables, hasDerivedSources, out var colName))
                {
                    candidates.Add(new PushedConjunctCandidate(colName, opStr, paramRef.Name, IsList: false));
                }
                else if (binary.Right is ColumnReference colRef2 && binary.Left is ParameterReference paramRef2 &&
                         BelongsToTable(colRef2.Name, target, metadata, allTables, hasDerivedSources, out var colName2))
                {
                    candidates.Add(new PushedConjunctCandidate(colName2, opStr, paramRef2.Name, IsList: false));
                }
            }
            else if (conjunct is InListExpression { Items: [ParameterReference paramRef] } inList &&
                     inList.Operand is ColumnReference colRef &&
                     BelongsToTable(colRef.Name, target, metadata, allTables, hasDerivedSources, out var colName))
            {
                candidates.Add(new PushedConjunctCandidate(colName, inList.IsNotIn ? "NOT IN" : "IN", paramRef.Name, IsList: true));
            }
        }
        return candidates;
    }

    private static bool BelongsToTable(
        SqlQualifiedName name, TableAccessTarget target, TableMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> allTables, bool hasDerivedSources, out string columnName)
    {
        columnName = string.Empty;
        if (name.Parts.Count == 0) return false;
        var col = metadata.GetColumn(name.Parts[^1].Value);
        if (col == null) return false;

        if (name.Parts.Count == 1)
        {
            if (hasDerivedSources || allTables.Any(o => !o.Target.Equals(target) && o.Metadata.GetColumn(col.ColumnName) != null))
                return false;
            columnName = col.ColumnName;
            return true;
        }

        string qualifier = string.Join(".", name.Parts.Take(name.Parts.Count - 1).Select(p => p.Value));
        bool matches = !string.IsNullOrEmpty(target.Alias)
            ? string.Equals(qualifier, target.Alias, StringComparison.OrdinalIgnoreCase)
            : string.Equals(qualifier, target.TableName, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(qualifier, target.FullName, StringComparison.OrdinalIgnoreCase);
        if (!matches) return false;

        columnName = col.ColumnName;
        return true;
    }
}
