namespace Autheris.Application.Governance.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IPolicySimulationService
{
    /// <summary>
    /// Replays historical audit logs of <paramref name="effectiveTenant"/> against a draft policy.
    /// The effective tenant MUST be derived server-side from the authenticated principal (SEC H-05).
    /// </summary>
    Task<PolicySimulationResult> SimulateAsync(PolicySimulationRequest request, TenantId effectiveTenant, CancellationToken cancellationToken = default);
}
