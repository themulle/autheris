namespace Autheris.Application.Products;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;

/// <summary>Run-time check of a load that every base object of the product still exists and is active.</summary>
public interface IProductBaseObjectCheck
{
    Task<bool> AllActiveAsync(IReadOnlyList<TableIdentifier> baseObjects, CancellationToken ct = default);
}

public sealed class ProductBaseObjectCheck(ITableMetadataRepository tables) : IProductBaseObjectCheck
{
    public async Task<bool> AllActiveAsync(IReadOnlyList<TableIdentifier> baseObjects, CancellationToken ct = default)
    {
        if (baseObjects.Count == 0)
        {
            return false;
        }

        var found = await tables.GetTableMetadataBatchAsync(baseObjects, ct).ConfigureAwait(false);
        return found.Count == baseObjects.Count && found.All(static t => t is { Table.IsActive: true });
    }
}
