namespace Autheris.Application.Products;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;

/// <summary>Writes the PII classification of the columns of a product catalog object: every PII column carries the PII code, every other column does not.</summary>
internal static class ProductColumnClassifications
{
    private const string ClassificationSource = "product";

    public static async Task SyncAsync(IClassificationRepository classifications, DataProduct product, TableMetadata stored, DateTimeOffset now, CancellationToken ct)
    {
        var byName = stored.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        foreach (var column in product.Columns)
        {
            if (!byName.TryGetValue(column.Name, out var row))
            {
                throw new InvalidOperationException("The stored catalog object lacks a product column.");
            }

            var id = row.Id.ToString();
            if (column.Class == ProductColumnClass.NonPii)
            {
                await classifications.RemoveColumnClassificationAsync(ClassificationScopes.Global, id, ClassificationScopes.PiiCode, ct).ConfigureAwait(false);
                continue;
            }

            await classifications.AssignColumnClassificationAsync(new ColumnClassification
            {
                Scope = ClassificationScopes.Global,
                TableColumnId = id,
                Code = ClassificationScopes.PiiCode,
                Source = ClassificationSource,
                AssignedBy = product.ProductOwner.Value,
                AssignedAt = now
            }, ct).ConfigureAwait(false);
        }
    }
}
