namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using TableDecision = (Autheris.Domain.Interfaces.TableAccessDecision Decision, Autheris.Domain.Model.TableMetadata Meta, System.Collections.Generic.IReadOnlySet<string>? SensitivityOnlyMasked);

public sealed partial class GovernedProcedureExecutionService
{
    private (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool AnyMasked) GovernResult(
        ProcedureDefinition definition,
        RawProcedureResult raw,
        Dictionary<string, TableDecision> decisions,
        IReadOnlyDictionary<string, ResultColumnSource>? sources,
        TenantId tenantId)
    {
        TableDecision? mapped = null;
        string? resultSchema = null;
        if (definition.ResultTable != null)
        {
            var rParts = definition.ResultTable.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rParts.Length >= 2)
            {
                resultSchema = rParts[^2];
            }

            if (decisions.TryGetValue(definition.ResultTable, out var m))
            {
                mapped = m;
            }
            else if (TryFindDecision(decisions, resultSchema ?? "dbo", rParts[^1], out var fm))
            {
                mapped = fm;
            }
        }

        var cleared = new HashSet<string>(definition.ClearedResultColumns, StringComparer.OrdinalIgnoreCase);

        // Plan per raw column: index, output name, action
        var plan = new List<(int Index, string Name, ColumnAccessLevel Level, MaskingRule? Rule)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < raw.Columns.Count; i++)
        {
            string col = raw.Columns[i];
            if (string.IsNullOrWhiteSpace(col))
            {
                continue; // unnamed columns cannot be governed
            }

            if (!seen.Add(col))
            {
                throw new InvalidOperationException("The procedure returned duplicate column names; the result cannot be governed.");
            }

            // Always drop undeclared columns when outputs are declared.
            if (definition.DeclaredOutputs.Count > 0 &&
                !definition.DeclaredOutputs.Contains(col, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var (decision, meta, sourceColumn, foreign) = (default(TableAccessDecision)!, default(TableMetadata)!, col, false);
            IReadOnlySet<string>? sensitivityOnly = null;
            if (sources != null &&
                sources.TryGetValue(col, out var src) &&
                !string.IsNullOrWhiteSpace(src.Table) &&
                !string.IsNullOrWhiteSpace(src.Column))
            {
                var schema = !string.IsNullOrWhiteSpace(src.Schema) ? src.Schema : (resultSchema ?? "dbo");
                if (!TryFindDecision(decisions, schema, src.Table, out var srcMapped) || !srcMapped.Meta.HasColumn(src.Column))
                {
                    continue; // source table not governed: fail closed
                }

                (decision, meta, sensitivityOnly) = srcMapped;
                sourceColumn = src.Column;
                var key = $"{schema}.{src.Table}";
                foreign = definition.ResultTable == null ||
                          (!string.Equals(key, definition.ResultTable, StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(src.Table, definition.ResultTable, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (mapped == null || !mapped.Value.Meta.HasColumn(col))
                {
                    continue; // unknown column: removed (fail-closed)
                }

                (decision, meta, sensitivityOnly) = mapped.Value;
            }

            // If source table access is denied, fail closed
            if (!decision.IsAllowed)
            {
                continue;
            }

            var level = decision.GetEffectiveColumnAccess(sourceColumn, meta);
            // Zero-Trust: ClearedResultColumns lifts only a Mask that stems from catalog sensitivity alone; an explicit
            // base or scope-specific Mask and any Deny stay; an unknown sensitivity set lifts nothing.
            if (cleared.Contains(col) && level == ColumnAccessLevel.Mask && sensitivityOnly != null && sensitivityOnly.Contains(sourceColumn))
            {
                level = ColumnAccessLevel.Clear;
            }

            if (level == ColumnAccessLevel.Deny)
            {
                continue;
            }

            if (foreign && level != ColumnAccessLevel.Clear)
            {
                continue;
            }

            MaskingRule? rule = null;
            if (level == ColumnAccessLevel.Mask)
            {
                var catalogColumn = meta.GetColumn(sourceColumn);
                if (!meta.ColumnMaskingRules.TryGetValue(sourceColumn, out rule) && catalogColumn != null)
                {
                    meta.ColumnMaskingRules.TryGetValue(catalogColumn.ColumnName, out rule);
                }

                rule ??= new MaskingRule { RuleType = "REDACT" };
                rule = Autheris.Application.Services.GatewayExecutionService.ScopeRuleForTenant(
                    rule, tenantId.Value, _options.Value.DataMasking?.HmacKeyId);
            }

            plan.Add((i, col, level, rule));
        }

        if (plan.Count == 0 && raw.Columns.Count > 0)
        {
            _logger?.LogWarning(
                "Procedure endpoint '{Endpoint}': all result columns were removed by governance. Declare result_table and outputs.",
                definition.Name);
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>(raw.Rows.Count);
        foreach (var row in raw.Rows)
        {
            var output = new Dictionary<string, object?>(plan.Count, StringComparer.Ordinal);
            foreach (var (index, colName, level, rule) in plan)
            {
                object? value = row[index];
                if (level == ColumnAccessLevel.Mask)
                {
                    value = _masking != null && rule != null ? _masking.MaskValue(colName, value, rule) : null;
                }

                output[colName] = value;
            }

            rows.Add(output);
        }

        return (plan.Select(p => p.Name).ToList(), rows, rows.Count > 0 && plan.Any(p => p.Level == ColumnAccessLevel.Mask));
    }

    private static bool TryFindDecision(
        Dictionary<string, TableDecision> decisions,
        string schema,
        string table,
        out TableDecision result)
    {
        var keyWithSchema = $"{(string.IsNullOrWhiteSpace(schema) ? "dbo" : schema)}.{table}";
        if (decisions.TryGetValue(keyWithSchema, out result))
        {
            return true;
        }

        if (decisions.TryGetValue(table, out result))
        {
            return true;
        }

        foreach (var kvp in decisions)
        {
            if (string.Equals(kvp.Key, keyWithSchema, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, table, StringComparison.OrdinalIgnoreCase) ||
                (kvp.Value.Meta != null &&
                 string.Equals(kvp.Value.Meta.Identifier.TableName, table, StringComparison.OrdinalIgnoreCase) &&
                 (string.IsNullOrWhiteSpace(schema) || string.Equals(kvp.Value.Meta.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase))))
            {
                result = kvp.Value;
                return true;
            }
        }

        result = default;
        return false;
    }
}
