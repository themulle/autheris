namespace Autheris.Application.Governance.SingleApproval;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;

/// <summary>A single approval was refused; <see cref="Code"/> is stable and the message never contains a value.</summary>
public sealed class SingleApprovalRefusedException : Exception
{
    public SingleApprovalRefusedException(string code, string message) : base(message)
    {
        Code = code;
    }

    public SingleApprovalRefusedException(string code, string message, Exception inner) : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>The person who saves a filter, profile, caller attribute value, service set or functional-right mapping: immutable subject key, own identifiers and own groups.</summary>
public sealed record SingleApprovalWriter(string SubjectKey, IReadOnlyCollection<string> OwnIdentifiers, IReadOnlyCollection<string> OwnGroups)
{
    public static SingleApprovalWriter Of(string subjectKey) => new(subjectKey, [], []);
}

/// <summary>What a change did, as hashes only (never values), for the fail-closed audit entry.</summary>
public sealed record SingleApprovalChange(TenantId Tenant, string EventType, string Target, string? BeforeHash, string? AfterHash);

/// <summary>
/// Compensating controls of the single approval (no second approver, still per person): a change takes effect on save, but it can never
/// grant or widen access for the writer's own identifiers or groups, and every change is audited with a before/after hash in the
/// transaction of the write (<see cref="AuditEntry"/>). A failing check or audit denies the change.
/// </summary>
public static class SelfWideningGuard
{
    /// <summary>Refuses the change when one of the <paramref name="grantees"/> is the writer or one of the writer's groups.</summary>
    /// <exception cref="SingleApprovalRefusedException">The change would grant or widen access for the writer.</exception>
    public static void EnsureDoesNotWidenOwnAccess(SingleApprovalWriter writer, IEnumerable<string> grantees)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(grantees);
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { writer.SubjectKey };
        own.UnionWith(writer.OwnIdentifiers.Where(i => !string.IsNullOrWhiteSpace(i)));
        own.UnionWith(writer.OwnGroups.Where(g => !string.IsNullOrWhiteSpace(g)));
        foreach (var grantee in grantees)
        {
            if (!string.IsNullOrWhiteSpace(grantee) && own.Contains(grantee.Trim()))
            {
                throw new SingleApprovalRefusedException("SelfWidening", "A change cannot grant or widen access for the writer's own identifiers or groups.");
            }
        }
    }

    /// <summary>The audit entry of a change (hashes only, never values), for a repository that writes it in the transaction of the change.</summary>
    public static AuditLogEntry AuditEntry(SingleApprovalWriter writer, SingleApprovalChange change)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(change);
        return new AuditLogEntry
        {
            TenantId = change.Tenant,
            EventType = change.EventType,
            ActorSid = new Sid(writer.SubjectKey),
            TargetTable = change.Target,
            Decision = "ALLOW",
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty,
            DetailsJson = JsonSerializer.Serialize(new { before = change.BeforeHash, after = change.AfterHash })
        };
    }
}
