namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Nodes;

public sealed partial class SecurityCoverageVerifier
{
    private sealed partial class Walker
    {
        private void CollectMetricViewMaskedSource(NamedTableSource named, ref List<(string Alias, ImmutableHashSet<string>? CatalogColumns)>? into)
        {
            if (owner._requirementOf(named.Name) is { IsMetricView: true, MaskedColumns.Count: > 0 } mvRequirement)
            {
                (into ??= new()).Add((named.Alias?.Value ?? named.Name.SimpleName, mvRequirement.CatalogColumns));
            }
        }

        private static void VerifyMetricViewShape(
            TableCoverageRequirement requirement,
            QuerySpecification spec,
            SelectStatement? enclosing,
            bool policyScope)
        {
            if (policyScope)
            {
                throw new SecurityCoverageException("A metric view cannot be referenced inside a policy subquery.");
            }

            if (enclosing is null || !ReferenceEquals(enclosing.Body, spec) || enclosing.With is not null)
            {
                throw new SecurityCoverageException("A metric view must be the single top-level query without CTEs.");
            }

            if (spec.From is not NamedTableSource)
            {
                throw new SecurityCoverageException("A metric view must reference a single named source.");
            }

            if (requirement.PerCell is not null)
            {
                throw new SecurityCoverageException("Per-cell column policies are not supported on a metric view.");
            }

            var masked = requirement.MaskedColumns;
            var selectAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in spec.Projections)
            {
                if (item is not ColumnSelectItem col)
                {
                    throw new SecurityCoverageException("A metric view must not be projected with a wildcard.");
                }

                if (col.Alias is { Value.Length: > 0 } alias)
                {
                    selectAliases.Add(alias.Value);
                }

                if (masked is { Count: > 0 } && ContainsRawMaskedColumnIgnoreCase(col.Expression, masked, excludedAliases: null))
                {
                    throw new SecurityCoverageException("A masked column is projected on a metric view.");
                }
            }

            if (masked is { Count: > 0 })
            {
                if (spec.Where is not null && ContainsRawMaskedColumnIgnoreCase(spec.Where, masked, excludedAliases: null) ||
                    spec.GroupBy is not null && ContainsRawMaskedColumnIgnoreCase(spec.GroupBy, masked, excludedAliases: null) ||
                    spec.Having is not null && ContainsRawMaskedColumnIgnoreCase(spec.Having, masked, excludedAliases: null) ||
                    enclosing.OrderBy is not null && ContainsRawMaskedColumnIgnoreCase(enclosing.OrderBy, masked, selectAliases))
                {
                    throw new SecurityCoverageException("A masked column is referenced in a metric view query.");
                }
            }
        }

        private static bool ContainsRawMaskedColumnIgnoreCase(SqlNode root, ImmutableHashSet<string> masked, HashSet<string>? excludedAliases)
        {
            bool leaksRaw = false;
            AstReflection.Walk(root, node =>
            {
                if (node is MaskExpression) return false;
                if (node is ColumnReference reference)
                {
                    if (excludedAliases is not null && reference.Name.Parts.Count == 1 && excludedAliases.Contains(reference.Name.Parts[0].Value))
                    {
                        return true;
                    }

                    string colName = reference.Name.SimpleName;
                    foreach (var m in masked)
                    {
                        if (string.Equals(m, colName, StringComparison.OrdinalIgnoreCase))
                        {
                            leaksRaw = true;
                            return false;
                        }
                    }
                }

                return !leaksRaw;
            });
            return leaksRaw;
        }
    }
}
