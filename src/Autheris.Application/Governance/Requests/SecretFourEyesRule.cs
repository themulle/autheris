namespace Autheris.Application.Governance.Requests;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Issue #1121: four-eyes review for grants on <c>secret</c> data. It partially reverses #1009 D8 for the <see cref="ProtectionClass.Secret"/>
/// class only and is the single place that decides whether a grant needs two approvals.
/// <list type="bullet">
/// <item>Trigger: the maximum effective class of the grant target (table and all of its columns, #1009) is <c>Secret</c>.
/// Data, unmask and profile-assignment requests are covered; catalog visibility requests are not grants on data.</item>
/// <item>The first eligible approval records an approval step and leaves the request in <see cref="PendingSecondApproval"/>; a second
/// approval by a different eligible decider (owner-group member or Data Governance, never IT) completes it. A rejection ends it.</item>
/// <item>Declarative paths (Data Governance manifest apply, IT manifest proposal confirmation, MCP access plans) have no second decider,
/// so they refuse <c>secret</c> targets with <see cref="GrantErrorCodes.SecretRequiresFourEyes"/>: such a grant is requested through the
/// permission request engine. Pattern (wildcard) grants never cover a <c>secret</c> table.</item>
/// <item>Renewal of a <c>secret</c> grant stays a single decision (owner decision, #1121 answer 2).</item>
/// </list>
/// </summary>
public static class SecretFourEyesRule
{
    /// <summary>Status of a request whose first approval is recorded and whose second, distinct approval is outstanding.</summary>
    public const string PendingSecondApproval = "PENDING_SECOND_APPROVAL";

    /// <summary>Number of distinct approvals a grant on <c>secret</c> data needs.</summary>
    public const int RequiredApprovals = 2;

    /// <summary>Maximum effective class over the table and all of its columns (#1009).</summary>
    public static ProtectionClass MaxEffectiveClass(EffectiveProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        var max = protection.TableClass;
        foreach (var column in protection.Columns.Values)
        {
            if (column.EffectiveClass > max)
            {
                max = column.EffectiveClass;
            }
        }

        return max;
    }

    /// <summary>True when a grant of <paramref name="kind"/> on a target of <paramref name="maxClass"/> needs two distinct approvals.</summary>
    public static bool Applies(ConsentRequestKind kind, ProtectionClass maxClass) =>
        maxClass == ProtectionClass.Secret &&
        kind is ConsentRequestKind.Data or ConsentRequestKind.Unmask or ConsentRequestKind.ProfileAssignment;

    /// <summary>Only owner-group members and Data Governance decide grants on <c>secret</c> data (owner answer 1); IT never does.</summary>
    public static bool IsEligibleCapacity(ApproverCapacity capacity) =>
        capacity is ApproverCapacity.DataOwner or ApproverCapacity.Governance;

    /// <summary>
    /// Every personal identifier form of the decider (SID, UPN, object id, e-mail, subject, application id; trimmed and Unicode-normalised).
    /// Group and role identifiers are excluded: two different members of one group are two different people.
    /// </summary>
    public static IReadOnlyList<string> ApproverIdentity(SecurityPrincipalContext decider)
    {
        ArgumentNullException.ThrowIfNull(decider);
        var principal = decider.Principal;
        var clientIds = string.IsNullOrWhiteSpace(decider.Caller?.ClientId) ? null : new[] { decider.Caller!.ClientId! };
        var forms = SeparationOfDutiesService.CollectServicePrincipalIdentifiers(principal, decider.UserSid.Value, clientIds);
        return forms.OrderBy(f => f, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// True when <paramref name="currentIdentity"/> is the person who recorded <paramref name="step"/>: any shared identifier form counts,
    /// so a second approval under another identifier form of the same principal is detected. A step without stored forms is compared by its SID.
    /// </summary>
    public static bool IsSameApprover(ApprovalStep step, IReadOnlyCollection<string> currentIdentity)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(currentIdentity);
        var stored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SeparationOfDutiesService.AddIdentifierForms(stored, step.ApproverSid.Value);
        foreach (var form in step.ApproverIdentifiers)
        {
            SeparationOfDutiesService.AddIdentifierForms(stored, form);
        }

        foreach (var form in currentIdentity)
        {
            if (SeparationOfDutiesService.AnyOverlap(stored, form))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The recorded approval steps of <paramref name="request"/> (rejections and other decisions excluded).</summary>
    public static IReadOnlyList<ApprovalStep> RecordedApprovals(ConsentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ApprovalSteps
            .Where(s => string.Equals(s.Decision, "APPROVED", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>The refusal a declarative or single-actor grant path raises for a <c>secret</c> target.</summary>
    public static GrantPolicyViolationException DeclarativeRefusal(string target) =>
        new(GrantDecision.Deny(
            GrantErrorCodes.SecretRequiresFourEyes,
            $"A grant on secret data ('{target}') needs two distinct approvals; submit a permission request instead of a declarative grant."));
}
