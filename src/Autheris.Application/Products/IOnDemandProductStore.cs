namespace Autheris.Application.Products;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// The <c>onDemand</c> views of every tenant, ordered by tenant, schema and name (keyset after <paramref name="after"/>). A cross-tenant read
/// that serves only the idle-drop worker: it drops each cached result under the product's own tenant, on every node of the cluster.
/// </summary>
public interface IOnDemandProductStore
{
    Task<IReadOnlyList<DataProduct>> ListOnDemandProductsAsync(DataProduct? after, int limit, CancellationToken ct = default);
}
