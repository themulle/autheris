namespace Autheris.GraphQL.Types;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using HotChocolate;

/// <summary>GraphQL input for requesting or narrowing a consent write rule.</summary>
public sealed class ConsentWriteRuleInput
{
    public Guid? Id { get; init; }
    public WriteOperation Operation { get; init; }
    public List<string> Columns { get; init; } = [];
    public WriteRowScope RowScope { get; init; } = WriteRowScope.OwnRows;
    public long? MaxRowsPerStatement { get; init; }
}

internal static class ConsentWriteRuleWorkflow
{
    public static IReadOnlyList<WriteRule>? ToWriteRules(IReadOnlyList<ConsentWriteRuleInput>? inputs)
    {
        if (inputs == null)
        {
            return null;
        }

        return inputs
            .Select(i => new WriteRule(
                i.Id ?? Guid.Empty,
                i.Operation,
                i.Columns ?? [],
                i.RowScope,
                null,
                i.MaxRowsPerStatement))
            .ToList();
    }

    public static string SerializeForFingerprint(IReadOnlyList<ConsentWriteRuleInput>? inputs) =>
        inputs == null ? string.Empty : JsonSerializer.Serialize(inputs);

    public static IReadOnlyList<WriteRule> ResolveApprovedWriteRules(
        ConsentRequest req,
        IReadOnlyList<ConsentWriteRuleInput>? writeRulesInput,
        TableMetadata? tableMeta)
    {
        try
        {
            return WriteRuleWorkflowHelper.NarrowWriteRules(
                req.RequestedWriteRules ?? [],
                ToWriteRules(writeRulesInput),
                req.IsWriteOnlyRequest,
                tableMeta,
                req.TableIdentifier);
        }
        catch (ConsentRequestException ex)
        {
            var code = string.Equals(ex.Code, "FORBIDDEN", StringComparison.Ordinal) ? "FORBIDDEN" : "BAD_REQUEST";
            throw new GraphQLException(ErrorBuilder.New().SetCode(code).SetMessage(ex.Message).Build());
        }
    }

    public static List<ConsentColumnRule> BuildApprovedColumnRules(
        ConsentRequest req,
        TableMetadata? tableMeta,
        ColumnMaskingInput? columnMasking)
    {
        if (!req.ShouldGrantReadColumns)
        {
            if (columnMasking?.Rules != null && columnMasking.Rules.Count > 0)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("BAD_REQUEST")
                    .SetMessage("Column masking cannot be supplied on a write-only consent request.")
                    .Build());
            }

            return new List<ConsentColumnRule>();
        }

        if (columnMasking != null && columnMasking.Rules != null && columnMasking.Rules.Count > 0)
        {
            if (tableMeta == null)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("BAD_REQUEST")
                    .SetMessage($"Metadata for table '{req.TableIdentifier}' could not be resolved for column masking validation.")
                    .Build());
            }

            var validCols = tableMeta.Columns.ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);
            foreach (var rule in columnMasking.Rules)
            {
                if (!validCols.ContainsKey(rule.ColumnName))
                {
                    throw new GraphQLException(ErrorBuilder.New()
                        .SetCode("BAD_REQUEST")
                        .SetMessage($"Column '{rule.ColumnName}' does not exist on table '{req.TableIdentifier}'.")
                        .Build());
                }
            }
        }

        var columnRules = new List<ConsentColumnRule>();
        if (tableMeta != null && tableMeta.Columns.Count > 0)
        {
            var baseRules = ConsentColumnSnapshot.FromMetadata(tableMeta);
            if (columnMasking?.Rules != null && columnMasking.Rules.Count > 0)
            {
                var maskMap = columnMasking.Rules.ToDictionary(r => r.ColumnName, r => r, StringComparer.OrdinalIgnoreCase);
                columnRules.AddRange(baseRules.Select(r =>
                {
                    if (maskMap.TryGetValue(r.ColumnName, out var maskRule))
                    {
                        var lvl = string.Equals(maskRule.MaskingType, "None", StringComparison.OrdinalIgnoreCase)
                            ? ColumnAccessLevel.Clear
                            : ColumnAccessLevel.Mask;
                        return new ConsentColumnRule
                        {
                            Id = r.Id,
                            ConsentId = r.ConsentId,
                            TableColumnId = r.TableColumnId,
                            ColumnName = r.ColumnName,
                            AccessLevel = lvl,
                            ApplicableScopes = r.ApplicableScopes
                        };
                    }

                    return r;
                }));
            }
            else
            {
                columnRules.AddRange(baseRules);
            }
        }

        return columnRules;
    }
}
