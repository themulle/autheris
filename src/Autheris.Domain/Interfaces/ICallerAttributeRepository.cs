namespace Autheris.Domain.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Outcome of a four-eyes step in the store. Every writer goes through these steps, so the guard cannot be bypassed (BC-129-10).</summary>
public enum CallerAttributeChangeResult
{
    Applied = 0,
    NotFound = 1,
    NothingPending = 2,
    HashMismatch = 3,
    SameSubject = 4,
    AlreadyExists = 5
}

/// <summary>
/// Storage of the caller attribute registry and the service sets (PLAN-GOV-129 C-3). A draft is stored beside the active row; approving
/// is one transaction that compares the hash the approver saw and refuses an approver equal to the draft's creator, so the repository
/// itself never activates an unapproved definition. Every successful write increments the generation once.
/// </summary>
public interface ICallerAttributeRepository
{
    Task<CallerAttributeStoreSnapshot> LoadCallerAttributeSnapshotAsync(CancellationToken ct = default);

    Task<long> GetCallerAttributeGenerationAsync(CancellationToken ct = default);

    /// <summary>A new attribute (stored pending) or, for an existing one, a draft beside the active definition.</summary>
    Task<CallerAttributeChangeResult> SubmitDefinitionAsync(CallerAttributeDefinition definition, string actor, DateTimeOffset at, CancellationToken ct = default);

    Task<CallerAttributeChangeResult> RequestDeletionAsync(TenantId tenant, string name, string actor, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Activates the draft (or the pending first version) or performs the pending deletion; <paramref name="expectedHash"/> is the hash the approver reviewed.</summary>
    Task<CallerAttributeChangeResult> ApproveDefinitionAsync(TenantId tenant, string name, string approver, string expectedHash, DateTimeOffset at, CancellationToken ct = default);

    Task<CallerAttributeChangeResult> RejectDefinitionAsync(TenantId tenant, string name, string actor, DateTimeOffset at, CancellationToken ct = default);

    Task<CallerAttributeChangeResult> SubmitServiceSetAsync(TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at, CancellationToken ct = default);

    Task<CallerAttributeChangeResult> ApproveServiceSetAsync(TenantId tenant, string applicationId, string attributeName, string approver, string expectedHash, DateTimeOffset at, CancellationToken ct = default);

    Task<CallerAttributeChangeResult> RejectServiceSetAsync(TenantId tenant, string applicationId, string attributeName, string actor, DateTimeOffset at, CancellationToken ct = default);
}
