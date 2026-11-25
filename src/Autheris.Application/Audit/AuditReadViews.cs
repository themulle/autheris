namespace Autheris.Application.Audit;

using System;
using System.Collections.Generic;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;

/// <summary>The named, filtered audit views. A view only ever narrows what the caller's scope already allows.</summary>
public enum AuditReadView
{
    All = 0,
    Decisions = 1,
    SelfGrants = 2,
    DecisionsByServicePrincipals = 3
}

public static class AuditReadViews
{
    public const string DecisionsName = "decisions";
    public const string SelfGrantsName = "self-grants";
    public const string DecisionsByServicePrincipalsName = "decisions-by-service-principals";

    private static readonly string[] DecisionEventTypes =
    [
        AuditEventTypes.PermissionApprovalRecorded,
        AuditEventTypes.PermissionApproved,
        AuditEventTypes.PermissionRejected,
        AuditEventTypes.PermissionDecisionConflict
    ];

    private static readonly string[] SelfGrantEventTypes =
    [
        AuditEventTypes.SelfGrant,
        AuditEventTypes.SelfGrantDenied
    ];

    /// <summary>A missing view is <see cref="AuditReadView.All"/>; an unknown name is rejected (never mapped to a wider view).</summary>
    public static bool TryParse(string? raw, out AuditReadView view)
    {
        view = AuditReadView.All;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case DecisionsName:
                view = AuditReadView.Decisions;
                return true;
            case SelfGrantsName:
                view = AuditReadView.SelfGrants;
                return true;
            case DecisionsByServicePrincipalsName:
                view = AuditReadView.DecisionsByServicePrincipals;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The event types a view is restricted to; null when the view does not restrict by event type.</summary>
    public static IReadOnlyCollection<string>? EventTypes(AuditReadView view) => view switch
    {
        AuditReadView.Decisions or AuditReadView.DecisionsByServicePrincipals => DecisionEventTypes,
        AuditReadView.SelfGrants => SelfGrantEventTypes,
        _ => null
    };

    public static bool NeedsActorKindFilter(AuditReadView view) => view == AuditReadView.DecisionsByServicePrincipals;

    public static bool Matches(AuditReadView view, AuditLogEntry entry) => view switch
    {
        AuditReadView.DecisionsByServicePrincipals =>
            string.Equals(AuditActorKindReader.Read(entry), nameof(PermissionAuditActorKind.ServicePrincipal), StringComparison.Ordinal),
        _ => true
    };
}
