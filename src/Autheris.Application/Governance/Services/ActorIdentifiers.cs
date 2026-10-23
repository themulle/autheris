namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Domain.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// SR-M-10 / GOV-04: the identifier forms of an acting administrator, shared by every write path that must refuse a self-grant
/// (declarative consent apply, bulk grants, access-plan apply).
/// </summary>
internal static class ActorIdentifiers
{
    private static readonly string[] ActorEmailClaimTypes = [ClaimTypes.Email, "email"];

    // Application identifiers of the actor: an app-only GitOps token must not grant to its own client id.
    private static readonly string[] ActorApplicationIdClaimTypes = ["appid", "azp", "client_id"];

    /// <summary>
    /// Every identifier of the actor (SIDs, oid, sub, UPN, e-mail, name, application ids), its group SIDs and its roles, taken from
    /// the claims and, when given, from the resolved request context (plugin-enriched groups and roles).
    /// </summary>
    internal static HashSet<string> Collect(ClaimsPrincipal? caller, Sid actorSid, SecurityPrincipalContext? callerContext)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(ids, actorSid.Value);
        if (caller is not null)
        {
            foreach (var id in caller.GetUserIdentifiers())
            {
                AddIdentifierForms(ids, id);
            }

            foreach (var claimType in ActorEmailClaimTypes.Concat(ActorApplicationIdClaimTypes))
            {
                foreach (var claim in caller.FindAll(claimType))
                {
                    AddIdentifierForms(ids, claim.Value);
                }
            }

            foreach (var group in caller.GetGroupSids())
            {
                AddIdentifierForms(ids, group.Value);
            }

            foreach (var role in caller.GetUserRoles())
            {
                AddIdentifierForms(ids, role);
            }
        }

        if (callerContext is not null)
        {
            AddIdentifierForms(ids, callerContext.UserSid.Value);
            foreach (var group in callerContext.GroupSids)
            {
                AddIdentifierForms(ids, group.Value);
            }

            foreach (var role in callerContext.TenantRoles.Concat(callerContext.ClusterRoles))
            {
                AddIdentifierForms(ids, role);
            }
        }

        return ids;
    }

    internal static bool IsActorSubject(HashSet<string> actorIdentifiers, string? granteeRef)
    {
        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(forms, granteeRef);
        return forms.Any(f => actorIdentifiers.Contains(f));
    }

    // Adds the value and its account-name forms: 'DOMAIN\user' -> 'user', 'user@domain' -> 'user'.
    internal static void AddIdentifierForms(HashSet<string> set, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var v = value.Trim();
        set.Add(v);
        var slash = v.LastIndexOf('\\');
        if (slash >= 0 && slash < v.Length - 1)
        {
            set.Add(v[(slash + 1)..]);
        }

        var at = v.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            set.Add(v[..at]);
        }
    }
}
