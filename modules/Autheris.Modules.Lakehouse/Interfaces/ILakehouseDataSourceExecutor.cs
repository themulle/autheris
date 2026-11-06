namespace Autheris.Extensions.Lakehouse.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// High-throughput query executor scanning pruned Lakehouse data files with zero-trust governance.
/// </summary>
public interface ILakehouseDataSourceExecutor
{
    /// <summary>
    /// Executes a pruned scan on an Apache Iceberg table and applies column masking and tenant isolation.
    /// </summary>
    ValueTask<LakehouseScanResult> ExecuteScanAsync(
        LakehouseScanRequest request,
        CancellationToken cancellationToken = default);
}
