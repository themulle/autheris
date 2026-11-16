namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;

/// <summary>
/// Shared validation and approver-narrowing rules for governed consent write requests.
/// </summary>
public static class WriteRuleWorkflowHelper
{
    public static IReadOnlyList<WriteRule> ValidateRequestedWriteRules(
        TableMetadata meta,
        IReadOnlyList<WriteRule>? rules,
        TableIdentifier tableId,
        TenantId tenantId,
        IRowPolicyDependencyIndex? policyDependencyIndex)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (rules == null || rules.Count == 0)
        {
            return Array.Empty<WriteRule>();
        }

        if (policyDependencyIndex != null && policyDependencyIndex.IsPolicyDependency(tenantId, tableId))
        {
            throw new ConsentRequestException(
                "VALIDATION_ERROR",
                $"Table '{tableId}' is a row-policy dependency and cannot carry write grants.");
        }

        return ValidateRulesAgainstMetadata(meta, rules, tableId);
    }

    public static IReadOnlyList<WriteRule> ValidateRulesAgainstMetadata(
        TableMetadata meta,
        IReadOnlyList<WriteRule> rules,
        TableIdentifier tableId)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(rules);

        var colLookup = meta.Columns.ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);
        var pkColumns = new HashSet<string>(meta.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
        var normalized = new List<WriteRule>(rules.Count);

        foreach (var rule in rules)
        {
            if (rule == null)
            {
                throw new ConsentRequestException("VALIDATION_ERROR", "Write rule entry cannot be null.");
            }

            try
            {
                rule.Validate();
            }
            catch (InvalidOperationException ex)
            {
                throw new ConsentRequestException("VALIDATION_ERROR", ex.Message);
            }

            if (rule.Operation == WriteOperation.Delete)
            {
                throw new ConsentRequestException("VALIDATION_ERROR", "Delete write grants are disabled.");
            }

            var canonicalCols = new List<string>(rule.Columns.Count);
            foreach (var colName in rule.Columns)
            {
                if (!colLookup.TryGetValue(colName, out var colMeta))
                {
                    throw new ConsentRequestException("VALIDATION_ERROR", $"Column '{colName}' does not exist on table '{tableId}'.");
                }

                if (colMeta.WriteProtected)
                {
                    throw new ConsentRequestException(
                        "VALIDATION_ERROR",
                        $"Column '{colName}' is write-protected and cannot be granted for write access.");
                }

                if (rule.Operation == WriteOperation.Update && pkColumns.Contains(colName))
                {
                    throw new ConsentRequestException(
                        "VALIDATION_ERROR",
                        $"Primary key column '{colName}' cannot be updated.");
                }

                canonicalCols.Add(colMeta.ColumnName);
            }

            normalized.Add(rule with
            {
                Id = rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id,
                Columns = canonicalCols
            });
        }

        return normalized;
    }

    public static IReadOnlyList<WriteRule> NarrowWriteRules(
        IReadOnlyList<WriteRule> requestedRules,
        IReadOnlyList<WriteRule>? approvedInputs,
        bool isWriteOnlyRequest,
        TableMetadata? tableMeta,
        TableIdentifier tableId)
    {
        ArgumentNullException.ThrowIfNull(requestedRules);

        if (approvedInputs == null)
        {
            if (requestedRules.Count == 0)
            {
                return Array.Empty<WriteRule>();
            }

            if (tableMeta == null)
            {
                throw new ConsentRequestException(
                    "VALIDATION_ERROR",
                    $"Metadata for table '{tableId}' could not be resolved for write rule validation.");
            }

            return ValidateRulesAgainstMetadata(tableMeta, requestedRules, tableId);
        }

        if (requestedRules.Count == 0)
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "Approver cannot add write rules when none were requested.");
        }

        if (approvedInputs.Count == 0)
        {
            if (isWriteOnlyRequest)
            {
                throw new ConsentRequestException(
                    "VALIDATION_ERROR",
                    "Cannot approve a write-only consent request with zero write rules; reject the request instead.");
            }

            return Array.Empty<WriteRule>();
        }

        if (approvedInputs.Count > requestedRules.Count)
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "Approver cannot add write rules beyond those requested.");
        }

        var usedRequestedIndices = new HashSet<int>();
        var narrowed = new List<WriteRule>(approvedInputs.Count);

        foreach (var input in approvedInputs)
        {
            if (input == null)
            {
                throw new ConsentRequestException("VALIDATION_ERROR", "Write rule entry cannot be null.");
            }

            try
            {
                input.Validate();
            }
            catch (InvalidOperationException ex)
            {
                throw new ConsentRequestException("VALIDATION_ERROR", ex.Message);
            }

            if (input.Operation == WriteOperation.Delete)
            {
                throw new ConsentRequestException("VALIDATION_ERROR", "Delete write grants are disabled.");
            }

            int matchIdx = FindRequestedMatch(requestedRules, usedRequestedIndices, input);
            var requested = requestedRules[matchIdx];

            if (input.Operation != requested.Operation)
            {
                throw new ConsentRequestException(
                    "VALIDATION_ERROR",
                    $"Approver cannot widen or change write operation from '{requested.Operation}' to '{input.Operation}'.");
            }

            var requestedColSet = new HashSet<string>(requested.Columns, StringComparer.OrdinalIgnoreCase);
            foreach (var col in input.Columns)
            {
                if (!requestedColSet.Contains(col))
                {
                    throw new ConsentRequestException(
                        "VALIDATION_ERROR",
                        $"Approver cannot add column '{col}' beyond requested write columns.");
                }
            }

            if (requested.RowScope == WriteRowScope.OwnRows && input.RowScope == WriteRowScope.AllRows)
            {
                throw new ConsentRequestException(
                    "VALIDATION_ERROR",
                    "Approver cannot widen write row scope from OwnRows to AllRows.");
            }

            if (requested.MaxRowsPerStatement.HasValue)
            {
                if (!input.MaxRowsPerStatement.HasValue || input.MaxRowsPerStatement.Value > requested.MaxRowsPerStatement.Value)
                {
                    throw new ConsentRequestException(
                        "VALIDATION_ERROR",
                        "Approver cannot widen or remove MaxRowsPerStatement.");
                }
            }

            usedRequestedIndices.Add(matchIdx);
            narrowed.Add(input with
            {
                Id = requested.Id == Guid.Empty ? Guid.NewGuid() : requested.Id,
                RowFilter = requested.RowFilter
            });
        }

        if (tableMeta == null)
        {
            throw new ConsentRequestException(
                "VALIDATION_ERROR",
                $"Metadata for table '{tableId}' could not be resolved for write rule validation.");
        }

        return ValidateRulesAgainstMetadata(tableMeta, narrowed, tableId);
    }

    private static int FindRequestedMatch(
        IReadOnlyList<WriteRule> requestedRules,
        HashSet<int> usedIndices,
        WriteRule input)
    {
        if (input.Id != Guid.Empty)
        {
            for (int i = 0; i < requestedRules.Count; i++)
            {
                if (!usedIndices.Contains(i) && requestedRules[i].Id == input.Id)
                {
                    return i;
                }
            }

            throw new ConsentRequestException(
                "VALIDATION_ERROR",
                $"Approved write rule '{input.Id}' does not match an available requested write rule.");
        }

        int fallback = -1;
        for (int i = 0; i < requestedRules.Count; i++)
        {
            if (usedIndices.Contains(i) || requestedRules[i].Operation != input.Operation)
            {
                continue;
            }

            if (fallback < 0)
            {
                fallback = i;
            }

            var reqCols = new HashSet<string>(requestedRules[i].Columns, StringComparer.OrdinalIgnoreCase);
            if (input.Columns.All(c => reqCols.Contains(c)))
            {
                return i;
            }
        }

        if (fallback >= 0)
        {
            return fallback;
        }

        throw new ConsentRequestException(
            "VALIDATION_ERROR",
            $"Approved write rule for operation '{input.Operation}' cannot be added when no matching write rule was requested.");
    }
}
