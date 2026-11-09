namespace Autheris.Application.Identity;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;

public enum GroupResolutionStatus
{
    Complete = 1,
    Degraded = 2
}

/// <summary>
/// The effective group SIDs of a caller. <see cref="GroupSids"/> is empty when <see cref="Status"/> is <see cref="GroupResolutionStatus.Degraded"/>
/// (never a truncated or partial set). <see cref="Source"/> is a fixed label, never a value.
/// </summary>
public sealed record GroupResolution(IReadOnlySet<Sid> GroupSids, GroupResolutionStatus Status, string Source, DateTimeOffset ExpiresAt);

/// <summary>
/// Resolves canonical group SIDs from the validated identity of one scheme. A pure function of its inputs: plugin enrichment output is
/// not an input and never part of the result.
/// </summary>
public interface IGroupSidResolver
{
    ValueTask<GroupResolution> ResolveAsync(ClaimsPrincipal normalized, IdentityRealm realm, TenantId tenant, string scheme, CancellationToken ct);
}
