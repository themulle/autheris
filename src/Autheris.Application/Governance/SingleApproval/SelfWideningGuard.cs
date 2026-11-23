namespace Autheris.Application.Governance.SingleApproval;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Security;

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
/// grant or widen access for the writer's own identifiers or groups, a mapping never confers a role outside the realm's allowed
/// gateway roles nor IT or governance capacity, and every change is audited fail closed with a before/after hash. A failing check or
/// audit denies the change.
/// </summary>
public sealed class SelfWideningGuard
{
    /// <summary>Roles that carry IT or governance capacity; a mapping never confers them.</summary>
    private static readonly HashSet<string> CapacityRoleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(GatewayRole.ClusterAdmin),
        nameof(GatewayRole.TenantAdmin),
        nameof(GatewayRole.GovernanceAdmin),
        nameof(GatewayRole.CallerAttributeApprover),
        "ItAdmin"
    };

    private readonly IAuditLogRepository _audit;

    public SelfWideningGuard(IAuditLogRepository audit)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

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

    /// <summary>Refuses a mapping onto a role outside the realm's allowed gateway roles or onto IT or governance capacity.</summary>
    /// <exception cref="SingleApprovalRefusedException">The role may not be conferred.</exception>
    public static void EnsureRoleMayBeConferred(string role, IReadOnlySet<GatewayRole> realmAllowedRoles)
    {
        ArgumentNullException.ThrowIfNull(realmAllowedRoles);
        if (string.IsNullOrWhiteSpace(role) || CapacityRoleNames.Contains(role.Trim()))
        {
            throw new SingleApprovalRefusedException("CapacityRoleNotMappable", "A mapping never confers IT or governance capacity.");
        }

        if (!GatewayRoleExtensions.TryParseGlobalRole(role.Trim(), out var parsed) || !realmAllowedRoles.Contains(parsed))
        {
            throw new SingleApprovalRefusedException("RoleOutsideRealm", "The role is outside the roles the realm may confer.");
        }
    }

    /// <summary>Writes the audit entry of a change; a failing audit denies the change.</summary>
    /// <exception cref="SingleApprovalRefusedException">The audit entry could not be written.</exception>
    public async Task RecordAsync(SingleApprovalWriter writer, SingleApprovalChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(change);
        try
        {
            await _audit.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = change.Tenant,
                EventType = change.EventType,
                ActorSid = new Sid(writer.SubjectKey),
                TargetTable = change.Target,
                Decision = "ALLOW",
                TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty,
                DetailsJson = JsonSerializer.Serialize(new { before = change.BeforeHash, after = change.AfterHash })
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SingleApprovalRefusedException("AuditFailed", "The change was refused because it could not be audited.", ex);
        }
    }
}
