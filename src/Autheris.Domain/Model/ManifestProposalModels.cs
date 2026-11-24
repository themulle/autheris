namespace Autheris.Domain.Model;

using System;
using Autheris.Domain.Common;

/// <summary>Lifecycle state of a declarative manifest proposal (Amendment 6).</summary>
public enum ManifestProposalStatus
{
    /// <summary>Submitted; has no effect until one Data Governance confirmation.</summary>
    Pending = 0,

    /// <summary>Confirmed by Data Governance and applied.</summary>
    Confirmed = 1,

    /// <summary>Rejected by Data Governance.</summary>
    Rejected = 2,

    /// <summary>Not decided within the proposal lifetime.</summary>
    Expired = 3,

    /// <summary>Replaced by a newer proposal for the same tenant and manifest scope.</summary>
    Superseded = 4
}

/// <summary>
/// A declarative consent manifest submitted by IT administration (or Data Governance) and stored until one Data Governance member decides it.
/// <see cref="StateHash"/> is the hash of the grant state the diff was computed against; a confirmation against a different state is stale.
/// </summary>
public sealed class ManifestProposal
{
    public Guid Id { get; init; }

    public TenantId TenantId { get; init; }

    /// <summary>The manifest scope (manifest name) within the tenant.</summary>
    public string ScopeName { get; init; } = string.Empty;

    public Sid SubmittedBy { get; init; }

    public DateTimeOffset SubmittedAt { get; init; }

    /// <summary>The submitted manifest and the prune mode it was submitted with, as JSON.</summary>
    public string ManifestJson { get; init; } = string.Empty;

    /// <summary>The computed diff per grant, as JSON.</summary>
    public string DiffJson { get; set; } = string.Empty;

    public string StateHash { get; set; } = string.Empty;

    public ManifestProposalStatus Status { get; set; }

    public Sid? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public string? RejectReason { get; set; }

    public DateTimeOffset ExpiresAt { get; init; }
}
