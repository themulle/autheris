using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Governance.Requests;

/// <summary>
/// Active persistence transaction passed to appliers during atomic compare-and-swap request decisions.
/// </summary>
public interface IGovernanceTransaction
{
    DbConnection Connection { get; }
    DbTransaction Transaction { get; }
}

/// <summary>
/// Persists column-level rules and write rules for an inserted data <see cref="Consent"/> within the active decision transaction.
/// </summary>
public interface IConsentExtrasWriter
{
    Task PersistDataGrantExtrasAsync(IGovernanceTransaction transaction, Consent consent, CancellationToken ct = default);
}

public sealed record ApprovedPermission(
    ConsentRequest Request,
    Sid DecidedBy,
    ApproverCapacity Capacity,
    bool SelfGrant,
    DateTimeOffset DecidedAt,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    string? Reason = null);

/// <summary>
/// Materializes the grant effect of one <see cref="ConsentRequestKind"/> within the decision's CAS transaction.
/// </summary>
public interface IGrantEffectApplier
{
    ConsentRequestKind RequestKind { get; }

    Task<IReadOnlyList<GrantRef>> ApplyAsync(
        IGovernanceTransaction transaction,
        ApprovedPermission approved,
        CancellationToken ct = default);
}
