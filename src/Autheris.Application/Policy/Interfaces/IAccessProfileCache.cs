namespace Autheris.Application.Policy.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// AR-01: Multi-node cluster-consistent access profile cache abstraction with epoch-keying.
/// </summary>
public interface IAccessProfileCache
{
    /// <summary>
    /// Liefert die Profile des Subjekts. Wirft <see cref="Autheris.Application.Policy.Exceptions.AccessProfileSourceUnavailableException"/>,
    /// wenn weder ein epoch-validierter Cache-Eintrag noch die DB verfügbar ist (Aufrufer: Deny).
    /// </summary>
    ValueTask<IReadOnlyList<AccessProfile>> GetProfilesAsync(TenantId tenantId, string subject, CancellationToken ct = default);

    /// <summary>
    /// Bump des Tenant-Epochs im Cluster-Store + Best-Effort-Publish. Wirft bei Store-Ausfall.
    /// </summary>
    Task InvalidateTenantAsync(TenantId tenantId, CancellationToken ct = default);
}
