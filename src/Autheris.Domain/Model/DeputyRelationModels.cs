using System;
using Autheris.Domain.Common;

namespace Autheris.Domain.Model;

/// <summary>
/// An externally sourced, person-to-person deputy relation (issue #1126): "<see cref="DeputyRef"/> (A) deputizes for
/// <see cref="PrincipalRef"/> (B) inside [<see cref="ValidFrom"/>, <see cref="ValidTo"/>)". Autheris reconciles these relations per
/// <see cref="Source"/>; inside the window A is an eligible decider exactly where B is a container owner-group member. A never receives
/// B's Data Governance capacity and a relation never chains (A does not deputize for anyone B deputizes for).
/// </summary>
public sealed record DeputyRelation
{
    public required Guid Id { get; init; }

    public required TenantId TenantId { get; init; }

    /// <summary>The external system the relation comes from (reconciled as a whole per source and tenant).</summary>
    public required string Source { get; init; }

    /// <summary>The identifier of the relation in its source.</summary>
    public required string ExternalId { get; init; }

    /// <summary>B: the resolved, realm-qualified identity (<c>realm|userKey</c>) of the person who is deputized for.</summary>
    public required string PrincipalRef { get; init; }

    /// <summary>Normalized lookup key of <see cref="PrincipalRef"/>.</summary>
    public required string PrincipalKey { get; init; }

    /// <summary>Normalized realm-qualified key of B's directory identifier (object SID or Entra object id), when known.</summary>
    public string? PrincipalAltKey { get; init; }

    /// <summary>A: the resolved, realm-qualified identity (<c>realm|userKey</c>) of the deputy.</summary>
    public required string DeputyRef { get; init; }

    /// <summary>Normalized lookup key of <see cref="DeputyRef"/>.</summary>
    public required string DeputyKey { get; init; }

    /// <summary>Normalized realm-qualified key of A's directory identifier (object SID or Entra object id), when known.</summary>
    public string? DeputyAltKey { get; init; }

    public required DateTimeOffset ValidFrom { get; init; }

    public required DateTimeOffset ValidTo { get; init; }

    public required Sid CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public Sid? UpdatedBy { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>True only inside the half-open window; reaching <see cref="ValidTo"/> ends the capacity.</summary>
    public bool IsActiveAt(DateTimeOffset at) => ValidFrom <= at && at < ValidTo;

    /// <summary>True when the relation states the same thing as <paramref name="other"/> (identities and window).</summary>
    public bool SameContentAs(DeputyRelation other) =>
        string.Equals(PrincipalRef, other.PrincipalRef, StringComparison.Ordinal) &&
        string.Equals(PrincipalKey, other.PrincipalKey, StringComparison.Ordinal) &&
        string.Equals(PrincipalAltKey, other.PrincipalAltKey, StringComparison.Ordinal) &&
        string.Equals(DeputyRef, other.DeputyRef, StringComparison.Ordinal) &&
        string.Equals(DeputyKey, other.DeputyKey, StringComparison.Ordinal) &&
        string.Equals(DeputyAltKey, other.DeputyAltKey, StringComparison.Ordinal) &&
        ValidFrom == other.ValidFrom && ValidTo == other.ValidTo;
}
