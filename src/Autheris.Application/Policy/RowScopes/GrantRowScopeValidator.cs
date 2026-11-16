namespace Autheris.Application.Policy.RowScopes;

using System;
using System.Collections.Generic;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Thrown when a grant row-scope entry references an inactive/missing policy, invalid parameters, or an unknown mapped column.</summary>
public sealed class RowScopeValidationException : InvalidOperationException
{
    public string Code { get; }

    public RowScopeValidationException(string code, string message) : base(message)
    {
        Code = code;
    }

    public RowScopeValidationException(string code, string message, Exception innerException) : base(message, innerException)
    {
        Code = code;
    }
}

/// <summary>Validates grant and access-profile <see cref="GrantRowScopeEntry"/> lists at write time.</summary>
public static class GrantRowScopeValidator
{
    public const string PolicyNotFoundCode = "ROW_POLICY_NOT_FOUND";
    public const string PolicyNotActiveCode = "ROW_POLICY_NOT_ACTIVE";
    public const string PolicyColumnUnknownCode = "ROW_POLICY_COLUMN_UNKNOWN";
    public const string InvalidParameterCode = "ROW_POLICY_PARAMETER_INVALID";

    /// <summary>
    /// Validates every entry in <paramref name="entries"/> against the tenant's active row policies in <paramref name="snapshot"/>.
    /// When <paramref name="targetTable"/> is supplied (single-object grant), also verifies cross-source KeySet requirements
    /// and that every mapped key column exists on the target table.
    /// </summary>
    public static void Validate(
        IReadOnlyList<GrantRowScopeEntry>? entries,
        TenantId tenantId,
        VirtualFilterSnapshot snapshot,
        TableMetadata? targetTable = null)
    {
        if (entries is null || entries.Count == 0)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.PolicyName))
            {
                throw new RowScopeValidationException(
                    InvalidParameterCode,
                    "A row-scope entry must specify a policy name.");
            }

            if (!Enum.IsDefined(entry.Uncovered))
            {
                throw new RowScopeValidationException(
                    InvalidParameterCode,
                    $"Row-scope entry '{entry.PolicyName}' has an invalid uncovered policy value.");
            }

            var policy = FindFilter(snapshot, tenantId, entry.PolicyName)
                ?? throw new RowScopeValidationException(
                    PolicyNotFoundCode,
                    $"Row policy '{entry.PolicyName}' does not exist in the tenant.");

            if (policy.Status != FilterApprovalStatus.Active)
            {
                throw new RowScopeValidationException(
                    PolicyNotActiveCode,
                    $"Row policy '{entry.PolicyName}' is not active (status: {policy.Status}).");
            }

            try
            {
                RowPolicyParameterBinder.ValidateFilterDeclarations(policy);
                _ = RowPolicyParameterBinder.BindValues(policy, entry.Parameters);
            }
            catch (VirtualFilterBindingException ex)
            {
                throw new RowScopeValidationException(ex.Code, ex.Message, ex);
            }

            if (targetTable is not null)
            {
                try
                {
                    RowPolicyParameterBinder.EnsureCrossSourceAllowed(policy, targetTable.Identifier.Domain);
                }
                catch (VirtualFilterBindingException ex)
                {
                    throw new RowScopeValidationException(ex.Code, ex.Message, ex);
                }

                ValidateTargetColumns(entry, policy, targetTable);
            }
        }
    }

    private static VirtualFilter? FindFilter(VirtualFilterSnapshot snapshot, TenantId tenantId, string name)
    {
        foreach (var filter in snapshot.Filters)
        {
            if (filter.TenantId.Equals(tenantId) &&
                string.Equals(filter.Name, name, StringComparison.Ordinal))
            {
                return filter;
            }
        }

        return null;
    }

    private static void ValidateTargetColumns(GrantRowScopeEntry entry, VirtualFilter policy, TableMetadata targetTable)
    {
        if (entry.ColumnMap is not { Count: > 0 } columnMap)
        {
            return;
        }

        var declaredKeys = new HashSet<string>(policy.TargetKeyColumns, StringComparer.OrdinalIgnoreCase);
        var tableColumns = new HashSet<string>(
            targetTable.Columns.Select(c => c.ColumnName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in columnMap)
        {
            if (declaredKeys.Count > 0 && !declaredKeys.Contains(mapping.Key))
            {
                throw new RowScopeValidationException(
                    PolicyColumnUnknownCode,
                    $"Row policy '{policy.Name}' does not declare key column '{mapping.Key}'.");
            }

            if (!tableColumns.Contains(mapping.Value))
            {
                throw new RowScopeValidationException(
                    PolicyColumnUnknownCode,
                    $"Target table '{targetTable.Identifier}' does not contain column '{mapping.Value}' mapped by row policy '{policy.Name}'.");
            }
        }
    }
}
