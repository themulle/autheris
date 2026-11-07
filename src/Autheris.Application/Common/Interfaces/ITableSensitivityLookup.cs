namespace Autheris.Application.Common.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Evaluates table sensitivity for degraded epoch validation and zero-tolerance cache revocation (AR-14).
/// </summary>
public interface ITableSensitivityLookup
{
    ValueTask<bool> IsSensitiveAsync(TableIdentifier table, CancellationToken ct = default);
}
