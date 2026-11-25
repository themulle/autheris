namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public enum AccessOperation
{
    Read,
    Insert,
    Update,
    Delete,
    Execute,
    ExecuteWrite,
    Discover
}

public enum AccessTransport
{
    GraphQl,
    Rest,
    OData,
    WebSql,
    Trino,
    Mcp,
    Procedure,
    Analytics,
    Olap,
    Federation,
    ArrowExport,
    Streaming,
    Lakehouse,
    EnvoyExtAuthz,
    Discovery,
    Internal
}

public sealed record WriteGrantQuery(
    ClaimsPrincipal? User = null,
    TenantId? Tenant = null,
    TableMetadata? Table = null,
    IReadOnlySet<string>? WrittenColumns = null);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Unified authorization model subject-object-action contract (ADR-020).")]
public sealed record AccessRequest(
    SecurityPrincipalContext Subject,
    TableMetadata Object,
    AccessOperation Operation,
    AccessTransport Transport,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<Consent>? SupplementalConsents = null,
    WriteGrantQuery? Write = null,
    bool Trace = false)
{
    /// <summary>
    /// Issue #1043: the column classification and mask override rows of this object, read once for a batch of decisions
    /// (<see cref="IAccessDecisionPoint.DecideManyAsync"/>). Null reads them per decision.
    /// </summary>
    public Autheris.Application.Policy.Classification.ColumnPolicyRowPrefetch? ColumnRowPrefetch { get; init; }

    /// <summary>
    /// Issue #1043: the caller's consents for the objects of a batch of decisions (<see cref="IAccessDecisionPoint.DecideManyAsync"/>),
    /// read once. Null reads them per decision.
    /// </summary>
    public Autheris.Application.Policy.ConsentBatchPrefetch? ConsentPrefetch { get; init; }

    /// <summary>
    /// The caller tenant's effective access policy for a batch of decisions (<see cref="IAccessDecisionPoint.DecideManyAsync"/>),
    /// read once. Null reads it per decision.
    /// </summary>
    public Autheris.Application.Policy.TenantPolicyPrefetch? TenantPolicyPrefetch { get; init; }

    /// <summary>
    /// The protection snapshots of a batch of decisions (<see cref="IAccessDecisionPoint.DecideManyAsync"/>), resolved once.
    /// Null resolves the snapshot per decision.
    /// </summary>
    public Autheris.Application.Policy.ProtectionBatchPrefetch? ProtectionPrefetch { get; init; }

    /// <summary>
    /// Issue #1047: only <see cref="TableAccessDecision.IsAllowed"/> of the result is used (set by
    /// <see cref="IAccessDecisionPoint.DecideAllowedManyAsync"/> only). The policy may skip steps that only set column levels, so the
    /// decision's column states, masks and row filters are incomplete and must never be used.
    /// </summary>
    internal bool AllowOnly { get; init; }
}

public interface IAccessDecisionPoint
{
    ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default);

    /// <summary>
    /// Issue #1047: <see cref="DecideManyAsync(SecurityPrincipalContext, IReadOnlyList{TableMetadata}, AccessOperation, AccessTransport, CancellationToken)"/>
    /// for several operations on the same objects; the result has one list per operation, in order. The decision point may share its
    /// batch reads between the operations; each decision is the one the single-operation call returns.
    /// </summary>
    async ValueTask<IReadOnlyList<IReadOnlyList<TableAccessDecision>>> DecideOperationsManyAsync(SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, IReadOnlyList<AccessOperation> operations, AccessTransport transport, CancellationToken ct = default)
    {
        var results = new List<IReadOnlyList<TableAccessDecision>>(operations.Count);
        foreach (var operation in operations)
        {
            results.Add(await DecideManyAsync(subject, objects, operation, transport, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Issue #1047: whether <see cref="AccessOperation.Discover"/> on the discovery transport is allowed for each object, as
    /// <see cref="DecideManyAsync"/> would decide it. Only the allow bit is returned: it selects discovery set membership (the
    /// <c>accessStatus</c> filter) and never replaces a decision for a page item or a data request.
    /// </summary>
    async ValueTask<IReadOnlyList<bool>> DecideAllowedManyAsync(SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, CancellationToken ct = default)
        => (await DecideManyAsync(subject, objects, AccessOperation.Discover, AccessTransport.Discovery, ct).ConfigureAwait(false)).Select(static d => d.IsAllowed).ToList();

    /// <summary>
    /// Like <see cref="DecideAllowedManyAsync"/>, plus whether a fail-closed denial came from a transient infrastructure failure
    /// (#1063 §4c) so callers can still deny now without persisting transient outages into long-lived view caches.
    /// </summary>
    async ValueTask<IReadOnlyList<AllowOnlyDecision>> DecideAllowedManyWithTransientAsync(
        SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, CancellationToken ct = default)
        => (await DecideManyAsync(subject, objects, AccessOperation.Discover, AccessTransport.Discovery, ct).ConfigureAwait(false))
            .Select(static d => new AllowOnlyDecision(d.IsAllowed, !d.IsAllowed && d.IsTransientFailure)).ToList();

    ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default);
}

/// <summary>Allow bit and transient-failure classification for an allow-only discovery check.</summary>
public readonly record struct AllowOnlyDecision(bool IsAllowed, bool IsTransientFailure);

public sealed record ColumnStateExplanation(string ColumnName, ColumnAccessLevel Level, string? MaskType = null);

public sealed record EffectiveGroupsExplanation(IReadOnlyList<string> GroupSids, string Source, string Status);

public sealed record AccessExplanation(
    bool Allowed,
    IReadOnlyList<DecisionReason> Trace,
    IReadOnlyList<ColumnStateExplanation> Columns,
    EffectiveGroupsExplanation Groups,
    IReadOnlyList<DecisionGrantRef> Grants,
    long PolicyEpoch,
    bool SubjectResolved = true);
