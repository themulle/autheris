namespace Autheris.Application.Governance.Ownership;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Filter of a deputy relation read; every set criterion must hold.</summary>
/// <param name="Source">Only relations of this source.</param>
/// <param name="DeputyKeys">Only relations whose deputy (A) key or alternative key is one of these.</param>
/// <param name="PrincipalKeys">Only relations whose principal (B) key or alternative key is one of these.</param>
/// <param name="SubjectKeys">Only relations where A or B (any key) is one of these (deprovisioning).</param>
/// <param name="ActiveAt">Only relations whose window contains this instant.</param>
public sealed record DeputyRelationFilter(
    string? Source = null,
    IReadOnlyCollection<string>? DeputyKeys = null,
    IReadOnlyCollection<string>? PrincipalKeys = null,
    IReadOnlyCollection<string>? SubjectKeys = null,
    DateTimeOffset? ActiveAt = null);

/// <summary>One atomic change set of deputy relations of a single tenant.</summary>
public sealed record DeputyRelationChanges(
    IReadOnlyList<DeputyRelation> Inserts,
    IReadOnlyList<(DeputyRelation Before, DeputyRelation After)> Updates,
    IReadOnlyList<DeputyRelation> Deletes)
{
    public bool IsEmpty => Inserts.Count == 0 && Updates.Count == 0 && Deletes.Count == 0;
}

/// <summary>
/// Persistence of externally sourced deputy relations (<c>governance_deputy_relations</c>, issue #1126). Every read and write is bound to
/// a validated, non-reserved tenant. A change set is applied with its audit entries in one transaction: an update or delete applies only
/// while the stored row still equals its "before" state, and any conflict, storage or audit failure rolls the whole set back.
/// </summary>
public interface IDeputyRelationRepository
{
    /// <summary>Relations of <paramref name="tenant"/> matching <paramref name="filter"/>, ordered by source and external id; at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<DeputyRelation>> ListAsync(TenantId tenant, DeputyRelationFilter filter, int max, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="changes"/> with <paramref name="audit"/> atomically. False (nothing changed) when a row changed concurrently
    /// or an insert collides with an existing (source, external id).
    /// </summary>
    Task<bool> ApplyAsync(TenantId tenant, DeputyRelationChanges changes, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct = default);
}

/// <summary>Fallback when no SQL governance store is configured: no relations exist and nothing can be written (fail closed).</summary>
public sealed class EmptyDeputyRelationRepository : IDeputyRelationRepository
{
    public static readonly EmptyDeputyRelationRepository Instance = new();

    public Task<IReadOnlyList<DeputyRelation>> ListAsync(TenantId tenant, DeputyRelationFilter filter, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeputyRelation>>([]);

    public Task<bool> ApplyAsync(TenantId tenant, DeputyRelationChanges changes, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct = default) =>
        throw new InvalidOperationException("Deputy relations need the SQL governance store.");
}
