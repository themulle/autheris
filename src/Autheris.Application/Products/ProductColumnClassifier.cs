namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Result of a declassification attempt: the updated column or the error code that refused it.</summary>
public sealed record ProductDeclassification(DataProductColumn? Column, string? ErrorCode);

/// <summary>
/// Column classification of data products. A column derived from any PII column, or with unresolved lineage, is <c>PiiMasked</c> by
/// default; only a creation-authority holder may declassify it, with a mandatory reason (audited by the caller).
/// </summary>
public static class ProductColumnClassifier
{
    public const int MaxReasonLength = 500;

    /// <summary>
    /// Classifies the output columns. <paramref name="previous"/> carries earlier declassifications over only for a column whose name and
    /// lineage are unchanged; a changed derivation falls back to the default.
    /// </summary>
    public static IReadOnlyList<DataProductColumn> Classify(
        IReadOnlyList<ProductOutputColumn> outputs,
        Func<ColumnLineage, bool> isPii,
        IReadOnlyList<DataProductColumn>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(isPii);
        var columns = new List<DataProductColumn>(outputs.Count);
        foreach (var output in outputs)
        {
            var kept = previous?.FirstOrDefault(p =>
                p.DeclassifiedBy is not null &&
                string.Equals(p.Name, output.Name, StringComparison.OrdinalIgnoreCase) &&
                SameLineage(p.Lineage, output.Lineage));
            if (kept is not null && !output.Unresolved)
            {
                columns.Add(kept with { DataType = output.DataType ?? kept.DataType });
                continue;
            }

            var pii = output.Unresolved || output.Lineage.Any(isPii);
            columns.Add(new DataProductColumn(
                output.Name,
                output.DataType ?? "unknown",
                pii ? ProductColumnClass.PiiMasked : ProductColumnClass.NonPii,
                output.Lineage,
                null,
                null));
        }

        return columns;
    }

    /// <summary>
    /// Sets <paramref name="target"/> on <paramref name="column"/>. Lowering the protection (anything but <c>PiiMasked</c>) needs a reason;
    /// returning to <c>PiiMasked</c> needs none and clears the declassification record.
    /// </summary>
    public static ProductDeclassification Declassify(DataProductColumn column, ProductColumnClass target, string? reason, Sid actor)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (target == ProductColumnClass.PiiMasked)
        {
            return new ProductDeclassification(column with { Class = target, DeclassifiedBy = null, DeclassificationReason = null }, null);
        }

        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxReasonLength || string.IsNullOrWhiteSpace(actor.Value))
        {
            return new ProductDeclassification(null, ProductErrorCodes.ReasonRequired);
        }

        return new ProductDeclassification(column with { Class = target, DeclassifiedBy = actor.Value, DeclassificationReason = trimmed }, null);
    }

    private static bool SameLineage(IReadOnlyList<ColumnLineage> a, IReadOnlyList<ColumnLineage> b) =>
        a.Count == b.Count && a.All(x => b.Any(y => x.Table.Equals(y.Table) && string.Equals(x.Column, y.Column, StringComparison.OrdinalIgnoreCase)));
}
