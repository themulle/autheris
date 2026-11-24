namespace Autheris.Domain.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Outcome of a registry write in the store.</summary>
public enum CallerAttributeChangeResult
{
    Applied = 0,
    NotFound = 1
}

/// <summary>
/// Storage of the caller attribute registry and the service sets (129 C-3). A save takes effect at once (single approval: the
/// service checks the writer's capacity before it calls the store). Every successful write increments the generation once.
/// </summary>
public interface ICallerAttributeRepository
{
    Task<CallerAttributeStoreSnapshot> LoadCallerAttributeSnapshotAsync(CancellationToken ct = default);

    Task<long> GetCallerAttributeGenerationAsync(CancellationToken ct = default);

    /// <summary>Inserts or replaces the active definition; <paramref name="audit"/> is written in the same transaction after the write.</summary>
    Task<CallerAttributeChangeResult> SaveDefinitionAsync(
        CallerAttributeDefinition definition, string actor, DateTimeOffset at, AuditLogEntry? audit = null, CancellationToken ct = default);

    /// <summary>Deletes the definition; <paramref name="audit"/> is written in the same transaction, and only when a row was deleted.</summary>
    Task<CallerAttributeChangeResult> DeleteDefinitionAsync(TenantId tenant, string name, AuditLogEntry? audit = null, CancellationToken ct = default);

    /// <summary>Inserts or replaces the active service set; <paramref name="audit"/> is written in the same transaction after the write.</summary>
    Task<CallerAttributeChangeResult> SaveServiceSetAsync(
        TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at,
        AuditLogEntry? audit = null, CancellationToken ct = default);
}
