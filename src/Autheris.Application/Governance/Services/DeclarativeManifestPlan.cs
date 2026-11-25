namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
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
/// <param name="Diff">The diff per grant (grantee, object, period and justification).</param>
public sealed record DeclarativeManifestPlan(
    DeclarativeConsentManifest Manifest,
    DeclarativePruneMode PruneMode,
    SecurityPrincipalContext? CallerContext,
    TenantId TenantId,
    Sid ActorSid,
    string StateHash,
    DeclarativeApplyResult Preview,
    IReadOnlyList<DeclarativeDiffEntry> Diff)
{
    /// <summary>The manifest proposal this plan confirms; every resulting grant audit entry references it.</summary>
    public Guid? ProposalId { get; init; }
}

/// <summary>What applying a manifest entry does to the grant set.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeclarativeDiffAction>))]
public enum DeclarativeDiffAction
{
    /// <summary>A new grant is created.</summary>
    Add,

    /// <summary>The existing grant is revoked and replaced by a new one.</summary>
    Replace,

    /// <summary>Only the justification of the existing grant changes.</summary>
    Update,

    /// <summary>The existing grant already matches.</summary>
    Unchanged,

    /// <summary>The object is not in the catalog; nothing is granted.</summary>
    Skipped,

    /// <summary>A declarative grant of a manifest grantee that the manifest no longer lists is revoked.</summary>
    Prune,

    /// <summary>Dataset catalog settings change (no grant).</summary>
    Catalog
}

/// <summary>One line of a manifest diff.</summary>
public sealed record DeclarativeDiffEntry(
    DeclarativeDiffAction Action,
    string? GranteeType,
    string Grantee,
    string Target,
    string? Effect,
    DateTimeOffset? ValidUntil,
    string? Justification);
