namespace Autheris.Application.Governance.Interfaces;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>Two-step declarative manifest flow shared by the direct Data Governance apply and manifest proposals.</summary>
public interface IDeclarativeManifestPlanner
{
    /// <summary>Validates the whole manifest and computes the diff without writing anything.</summary>
    Task<DeclarativeManifestPlan> ValidateAndDiffAsync(
        ClaimsPrincipal caller,
        DeclarativeConsentManifest manifest,
        SecurityPrincipalContext? callerContext = null,
        DeclarativePruneMode pruneMode = DeclarativePruneMode.False,
        CancellationToken ct = default);

    /// <summary>
    /// Applies a validated plan in one transaction after re-validating it against the current state
    /// (<see cref="DeclarativePlanStaleException"/> when the grant set changed).
    /// </summary>
    Task<DeclarativeApplyResult> ApplyAsync(
        DeclarativeManifestPlan plan,
        ClaimsPrincipal caller,
        CancellationToken ct = default);
}
