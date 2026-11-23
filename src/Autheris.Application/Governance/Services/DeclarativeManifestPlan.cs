namespace Autheris.Application.Governance.Services;

using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// The side-effect free outcome of validating a declarative manifest: the diff (add/update/revoke/prune) against the current grant set and a hash of
/// the grant state the diff was computed against. Applying the plan re-validates against the current state and refuses a stale plan.
/// </summary>
/// <param name="Manifest">The validated manifest.</param>
/// <param name="PruneMode">The prune mode the diff was computed with.</param>
/// <param name="CallerContext">The security context of the caller that validated the manifest.</param>
/// <param name="TenantId">The tenant derived server-side from the validating caller.</param>
/// <param name="ActorSid">The validating caller; only the same caller may apply the plan.</param>
/// <param name="StateHash">Hash of the grants the diff was computed against.</param>
/// <param name="Preview">The diff as a dry-run result.</param>
public sealed record DeclarativeManifestPlan(
    DeclarativeConsentManifest Manifest,
    DeclarativePruneMode PruneMode,
    SecurityPrincipalContext? CallerContext,
    TenantId TenantId,
    Sid ActorSid,
    string StateHash,
    DeclarativeApplyResult Preview);
