namespace Autheris.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Evaluates table sensitivity for degraded epoch validation and zero-tolerance cache revocation (SEC-EPOCH-01 / AR-14).
/// </summary>
public interface ITableSensitivityLookup
{
    ValueTask<bool> IsSensitiveAsync(TableIdentifier table, CancellationToken ct = default);
}
