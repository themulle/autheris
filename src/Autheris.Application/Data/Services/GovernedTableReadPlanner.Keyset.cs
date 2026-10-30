namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autheris.Application.Data.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed partial class GovernedTableReadPlanner
{
    private static readonly HashSet<string> DisallowedSqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "join", "inner", "outer", "cross", "left", "right", "full", "select", "insert", "update", "delete", "drop",
        "alter", "create", "truncate", "union", "intersect", "except", "where", "from", "into", "exec", "execute"
    };

    private readonly IKeysetCursorEncoder _cursorEncoder;

    /// <summary>
    /// Creates the planner. <paramref name="cursorEncoder"/> must be the DI <see cref="IKeysetCursorEncoder"/> (keyed by
    /// <c>RestApi:BulkStream:SigningKey</c>) — the same instance the streaming endpoint issues cursors with. The fallback
    /// (a process-local key) exists only for direct construction in tests and in non-streaming planner uses.
    /// </summary>
    public GovernedTableReadPlanner(IKeysetCursorEncoder? cursorEncoder = null)
    {
        _cursorEncoder = cursorEncoder ?? new KeysetCursorEncoder();
    }

    public PlannedTableRead PlanKeysetStream(
        BulkTableStreamRequest request,
        TableMetadata metadata,
        TableAccessDecision decision,
        string? tenantId = null,
        string? callerSid = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);

        if (request.Limit <= 0)
        {
            throw new ArgumentException("The stream slice limit must be positive.", nameof(request));
        }

        if (!decision.IsAllowed)
        {
            var reasons = decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "Access denied by governance policy.";
            throw new UnauthorizedAccessException($"Access to table '{metadata.Identifier.ToQualifiedName()}' is forbidden: {reasons}");
        }

        // 1. Resolve & Validate Primary Key / Unique Index
        var streamKey = BulkStreamKey.TryResolve(metadata)
            ?? throw new KeysetStreamingUnavailableException("KEYSET_STREAMING_UNAVAILABLE: Table requires a primary key or unique index.");
        IReadOnlyList<string> pkCols = streamKey.Columns;

        // SEC-DATA178-04 / SEC-DATA178-07: Masked or denied PK columns fail closed
        foreach (var pk in pkCols)
        {
            var access = decision.GetEffectiveColumnAccess(pk, metadata);
            if (access == ColumnAccessLevel.Mask || access == ColumnAccessLevel.Deny)
            {
                throw new KeysetStreamingUnavailableException(
                    $"KEYSET_STREAMING_UNAVAILABLE: Primary key column '{pk}' is masked or denied for caller.");
            }
        }

        // 2. Resolve Select Columns
        List<string> columnsToSelect;
        if (request.Select is { Count: > 0 } requested)
        {
            foreach (var col in requested)
            {
                ValidateIdentifier(col, nameof(request.Select));
                var colMeta = metadata.GetColumn(col);
                if (colMeta == null || decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata) == ColumnAccessLevel.Deny)
                {
                    throw new ArgumentException($"Column '{col}' does not exist on table '{metadata.Identifier.ToQualifiedName()}'.", nameof(request));
                }
            }

            columnsToSelect = new List<string>(requested);
            // Ensure all PK columns are in the query projection for cursor computation
            foreach (var pk in pkCols)
            {
                var canonPk = metadata.GetColumn(pk)!.ColumnName;
                if (!columnsToSelect.Any(c => string.Equals(c, canonPk, StringComparison.OrdinalIgnoreCase)))
                {
                    columnsToSelect.Add(canonPk);
                }
            }
        }
        else
        {
            columnsToSelect = metadata.Columns
                .Where(c => decision.GetEffectiveColumnAccess(c.ColumnName, metadata) != ColumnAccessLevel.Deny)
                .Select(c => c.ColumnName)
                .ToList();

            if (columnsToSelect.Count == 0)
            {
                throw new UnauthorizedAccessException($"No accessible columns available on table '{metadata.Identifier.ToQualifiedName()}'.");
            }
        }

        // 3. Enforce Required Filter Columns & Groups (#163 / #221 / #236 / SEC-DATA178-04)
        if (metadata.RequiredFilterColumns.Length > 0 || metadata.RequiredFilterColumnGroups.Length > 0)
        {
            // MISC-04: the row filter text counts only through whole identifiers (shared rule); the typed client filter keeps its own check.
            bool IsSatisfied(string req) =>
                (request.Filter != null && IsColumnConstrained(request.Filter, req)) ||
                RequiredFilterColumnRule.IsColumnSatisfied(req, metadata, decision, clientFilterColumns: null, tenantColumnCounts: false);

            if (metadata.RequiredFilterColumnGroups.Length > 0)
            {
                bool anyGroupSatisfied = false;
                foreach (var group in metadata.RequiredFilterColumnGroups)
                {
                    if (group.Length == 0) continue;
                    if (group.All(IsSatisfied))
                    {
                        anyGroupSatisfied = true;
                        break;
                    }
                }

                if (!anyGroupSatisfied)
                {
                    throw new RequiredFilterColumnMissingException("REQUIRED_FILTER_COLUMN_MISSING: Table requires filter satisfying at least one required filter column group.");
                }
            }

            if (metadata.RequiredFilterColumns.Length > 0)
            {
                foreach (var req in metadata.RequiredFilterColumns)
                {
                    if (!IsSatisfied(req))
                    {
                        throw new RequiredFilterColumnMissingException("REQUIRED_FILTER_COLUMN_MISSING: Table requires a filter on a required column.");
                    }
                }
            }
        }

        // 4. Translate Typed Filter Node to Parameterized SQL
        // VF-06: policy text and parameters are never spliced into the client SQL; the governed rewriter (PlannedTableRead) applies the row policy once.
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);

        int paramCounter = 0;
        string? clientFilterSql = null;
        if (request.Filter != null)
        {
            clientFilterSql = TranslateTypedFilterNode(request.Filter, metadata, decision, parameters, ref paramCounter);
        }

        // 5. Build Keyset Cursor Seek Predicate
        string? seekSql = null;
        if (!string.IsNullOrWhiteSpace(request.Cursor))
        {
            // The context is built by the same factory the endpoint issues cursors with (tenant, caller, table, key,
            // direction, filter and projection), and verified with the same DI encoder; any mismatch fails closed.
            var cursorContext = KeysetCursorContextFactory.ForBulkStream(tenantId, metadata, pkCols, callerSid, request, streamKey.Directions);
            var pkValues = _cursorEncoder.DecodeCursor(request.Cursor, metadata, cursorContext);

            var cursorParamNames = new List<string>(pkCols.Count);
            for (int i = 0; i < pkCols.Count; i++)
            {
                var canonPk = metadata.GetColumn(pkCols[i])!.ColumnName;
                var paramName = $"@cursor_{i}";
                parameters[paramName] = pkValues[canonPk];
                cursorParamNames.Add(paramName);
            }

            // I390-SC-07/09: the seek operator and the ORDER BY direction of a column both come from streamKey.IsDescending (one source):
            // '<' seeks past a descending column, '>' past an ascending one, in the single-column case and in every branch of the OR chain.
            if (pkCols.Count == 1)
            {
                var canonCurr = metadata.GetColumn(pkCols[0])!.ColumnName;
                seekSql = $"{DataApiFilterTranslator.QuoteIdentifier(canonCurr)} {(streamKey.IsDescending(0) ? "<" : ">")} {cursorParamNames[0]}";
            }
            else
            {
                var orBranches = new List<string>();
                for (int i = 0; i < pkCols.Count; i++)
                {
                    var andTerms = new List<string>();
                    for (int j = 0; j < i; j++)
                    {
                        var canonPrev = metadata.GetColumn(pkCols[j])!.ColumnName;
                        andTerms.Add($"{DataApiFilterTranslator.QuoteIdentifier(canonPrev)} = {cursorParamNames[j]}");
                    }

                    var canonCurr = metadata.GetColumn(pkCols[i])!.ColumnName;
                    andTerms.Add($"{DataApiFilterTranslator.QuoteIdentifier(canonCurr)} {(streamKey.IsDescending(i) ? "<" : ">")} {cursorParamNames[i]}");

                    orBranches.Add($"({string.Join(" AND ", andTerms)})");
                }

                seekSql = $"({string.Join(" OR ", orBranches)})";
            }
        }

        // 6. Conjoin WHERE Clauses
        var whereClauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(clientFilterSql))
        {
            whereClauses.Add(clientFilterSql.StartsWith('(') && clientFilterSql.EndsWith(')') ? clientFilterSql : $"({clientFilterSql})");
        }

        if (!string.IsNullOrWhiteSpace(seekSql))
        {
            whereClauses.Add(seekSql.StartsWith('(') && seekSql.EndsWith(')') ? seekSql : $"({seekSql})");
        }

        // 7. Assemble Complete Single-Table SELECT Statement
        var sb = new StringBuilder();
        sb.Append("SELECT ")
          .Append(string.Join(", ", columnsToSelect.Select(c => DataApiFilterTranslator.QuoteIdentifier(metadata.GetColumn(c)!.ColumnName))))
          .Append(" FROM ")
          .Append(metadata.Identifier.ToString());

        if (whereClauses.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", whereClauses));
        }

        sb.Append(" ORDER BY ")
          .Append(string.Join(", ", pkCols.Select((c, i) => DataApiFilterTranslator.QuoteIdentifier(metadata.GetColumn(c)!.ColumnName) + (streamKey.IsDescending(i) ? " DESC" : " ASC"))));

        int sliceLimit = request.Limit is > 0 ? Math.Min(request.Limit.Value, 10000) : 5000;
        sb.Append(" LIMIT ").Append((sliceLimit + 1).ToString(CultureInfo.InvariantCulture));

        return new PlannedTableRead(sb.ToString(), parameters, columnsToSelect, DefaultOrderingKind.Explicit);
    }

    private static string TranslateTypedFilterNode(
        TypedFilterNode node,
        TableMetadata metadata,
        TableAccessDecision decision,
        Dictionary<string, object?> parameters,
        ref int paramCounter)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!string.IsNullOrWhiteSpace(node.Field))
        {
            ValidateIdentifier(node.Field, "filter.field");
            var colMeta = metadata.GetColumn(node.Field)
                ?? throw new ArgumentException($"Column '{node.Field}' does not exist on table '{metadata.Identifier.ToQualifiedName()}'.");

            var access = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
            if (access == ColumnAccessLevel.Deny)
            {
                throw new ArgumentException($"Column '{node.Field}' does not exist on table '{metadata.Identifier.ToQualifiedName()}'.");
            }

            if (access == ColumnAccessLevel.Mask)
            {
                throw new ArgumentException($"Filtering on masked column '{node.Field}' is rejected to prevent oracle inference attacks.");
            }

            var quotedCol = DataApiFilterTranslator.QuoteIdentifier(colMeta.ColumnName);
            var op = (node.Op ?? "eq").Trim().ToLowerInvariant();

            switch (op)
            {
                case "eq":
                case "=":
                {
                    var paramName = $"@p{paramCounter++}";
                    parameters[paramName] = ExtractJsonPrimitive(node.Value, colMeta);
                    return $"{quotedCol} = {paramName}";
                }
                case "ne":
                case "<>":
                case "!=":
                {
                    var paramName = $"@p{paramCounter++}";
                    parameters[paramName] = ExtractJsonPrimitive(node.Value, colMeta);
                    return $"{quotedCol} <> {paramName}";
                }
                case "gt":
                case ">":
                {
                    var paramName = $"@p{paramCounter++}";
                    parameters[paramName] = ExtractJsonPrimitive(node.Value, colMeta);
                    return $"{quotedCol} > {paramName}";
                }
                case "gte":
                case "ge":
                case ">=":
                {
                    var paramName = $"@p{paramCounter++}";
                    parameters[paramName] = ExtractJsonPrimitive(node.Value, colMeta);
                    return $"{quotedCol} >= {paramName}";
                }
                case "lt":
                case "<":
                {
                    var paramName = $"@p{paramCounter++}";
                    parameters[paramName] = ExtractJsonPrimitive(node.Value, colMeta);
                    return $"{quotedCol} < {paramName}";
                }
                case "lte":
                case "le":
                case "<=":
                {
                    var paramName = $"@p{paramCounter++}";
                    parameters[paramName] = ExtractJsonPrimitive(node.Value, colMeta);
                    return $"{quotedCol} <= {paramName}";
                }
                case "like":
                {
                    var paramName = $"@p{paramCounter++}";
                    parameters[paramName] = ExtractJsonPrimitive(node.Value, colMeta);
                    return $"{quotedCol} LIKE {paramName}";
                }
                case "isnull":
                {
                    return $"{quotedCol} IS NULL";
                }
                case "isnotnull":
                {
                    return $"{quotedCol} IS NOT NULL";
                }
                case "in":
                case "notin":
                {
                    bool isNotIn = op == "notin";
                    if (!node.Value.HasValue || node.Value.Value.ValueKind != JsonValueKind.Array)
                    {
                        throw new ArgumentException($"Operator '{op}' requires a JSON array value.");
                    }

                    var arrayElements = node.Value.Value.EnumerateArray().ToList();
                    if (arrayElements.Count == 0)
                    {
                        return isNotIn ? "1 = 1" : "1 = 0";
                    }

                    var itemParamNames = new List<string>(arrayElements.Count);
                    foreach (var elem in arrayElements)
                    {
                        var paramName = $"@p{paramCounter++}";
                        parameters[paramName] = ExtractJsonPrimitive(elem, colMeta);
                        itemParamNames.Add(paramName);
                    }

                    var inClause = string.Join(", ", itemParamNames);
                    return isNotIn ? $"{quotedCol} NOT IN ({inClause})" : $"{quotedCol} IN ({inClause})";
                }
                default:
                    throw new ArgumentException($"Unsupported filter operator '{node.Op}'.");
            }
        }

        if (node.And is { Count: > 0 })
        {
            var childClauses = new List<string>();
            foreach (var child in node.And)
            {
                var clause = TranslateTypedFilterNode(child, metadata, decision, parameters, ref paramCounter);
                if (!string.IsNullOrWhiteSpace(clause))
                {
                    childClauses.Add(clause);
                }
            }

            if (childClauses.Count == 0) return string.Empty;
            return childClauses.Count == 1 ? childClauses[0] : $"({string.Join(" AND ", childClauses)})";
        }

        if (node.Or is { Count: > 0 })
        {
            var childClauses = new List<string>();
            foreach (var child in node.Or)
            {
                var clause = TranslateTypedFilterNode(child, metadata, decision, parameters, ref paramCounter);
                if (!string.IsNullOrWhiteSpace(clause))
                {
                    childClauses.Add(clause);
                }
            }

            if (childClauses.Count == 0) return string.Empty;
            return childClauses.Count == 1 ? childClauses[0] : $"({string.Join(" OR ", childClauses)})";
        }

        if (node.Not != null)
        {
            var inner = TranslateTypedFilterNode(node.Not, metadata, decision, parameters, ref paramCounter);
            if (string.IsNullOrWhiteSpace(inner)) return string.Empty;
            return $"NOT ({inner})";
        }

        throw new ArgumentException("Filter node must specify a field or a logical combinator (and, or, not).");
    }

    private static void ValidateIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Column identifier cannot be null or whitespace.", paramName);
        }

        var trimmed = identifier.Trim();
        if (trimmed.Contains('.') || trimmed.Contains(';') || trimmed.Contains('/') ||
            trimmed.Contains('\\') || trimmed.Contains('\'') || trimmed.Contains('"') ||
            trimmed.Contains("/*") || trimmed.Contains("--") || trimmed.Contains(' '))
        {
            throw new ArgumentException($"Identifier '{identifier}' contains invalid characters or dots. Joins and dotted table identifiers are strictly prohibited.", paramName);
        }

        if (DisallowedSqlKeywords.Contains(trimmed))
        {
            throw new ArgumentException($"Identifier '{identifier}' is a reserved SQL keyword and cannot be used as an expression target.", paramName);
        }
    }

    private static bool IsColumnConstrained(TypedFilterNode node, string columnName)
    {
        if (node == null) return false;

        if (!string.IsNullOrWhiteSpace(node.Field))
        {
            // VF-06: a tautology ({op: notin, value: []} renders "1 = 1") constrains nothing.
            if (string.Equals(node.Op?.Trim(), "notin", StringComparison.OrdinalIgnoreCase) &&
                node.Value is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() == 0)
            {
                return false;
            }

            return string.Equals(node.Field, columnName, StringComparison.OrdinalIgnoreCase);
        }

        if (node.And is { Count: > 0 })
        {
            return node.And.Any(child => IsColumnConstrained(child, columnName));
        }

        if (node.Or is { Count: > 0 })
        {
            return node.Or.All(child => IsColumnConstrained(child, columnName));
        }

        return false;
    }

    private static object? ExtractJsonPrimitive(JsonElement? elementNullable, TableColumn column)
    {
        if (!elementNullable.HasValue) return null;
        var element = elementNullable.Value;

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var dataType = column.DataType?.Trim().ToLowerInvariant() ?? "varchar";

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (dataType.Contains("int", StringComparison.Ordinal) || dataType.Contains("serial", StringComparison.Ordinal))
            {
                if (dataType.Contains("bigint", StringComparison.Ordinal) || dataType.Contains("int8", StringComparison.Ordinal) || dataType.Contains("long", StringComparison.Ordinal))
                {
                    return element.GetInt64();
                }
                return element.GetInt32();
            }

            if (dataType.Contains("decimal", StringComparison.Ordinal) || dataType.Contains("numeric", StringComparison.Ordinal) || dataType.Contains("money", StringComparison.Ordinal))
            {
                return element.GetDecimal();
            }

            return element.GetDouble();
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (dataType.Contains("guid", StringComparison.Ordinal) || dataType.Contains("uuid", StringComparison.Ordinal))
            {
                if (Guid.TryParse(str, out var g)) return g;
            }

            if (dataType.Contains("date", StringComparison.Ordinal) || dataType.Contains("time", StringComparison.Ordinal))
            {
                if (DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)) return dto;
            }

            return str;
        }

        return element.ToString();
    }
}
