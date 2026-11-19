namespace Autheris.Application.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// The single ordering and selection rule for the owner-authored consent description a caller sees on an object:
/// most specific grantee first (user or service principal, then group, then role), then the earliest expiry, then the id.
/// MCP and REST share it so they cannot drift. Output only through <see cref="ConsentDescriptionText.ForOutput"/>.
/// </summary>
internal static class ConsentDescriptionSelector
{
    internal static int GranteeSpecificity(GranteeType type) => type switch
    {
        GranteeType.User or GranteeType.ServicePrincipal => 0,
        GranteeType.Group => 1,
        GranteeType.Role => 2,
        _ => 3
    };

    internal static IEnumerable<Consent> Order(IEnumerable<Consent> applicableConsents) =>
        applicableConsents
            .OrderBy(static c => GranteeSpecificity(c.GranteeType))
            .ThenBy(static c => c.EffectiveValidTo)
            .ThenBy(static c => c.Id);

    /// <summary>The first description that passes the output check, in selection order; <c>null</c> when none does (fail closed).</summary>
    internal static string? SelectOne(IEnumerable<Consent> applicableConsents, int maxLength)
    {
        foreach (var consent in Order(applicableConsents))
        {
            var text = ConsentDescriptionText.ForOutput(consent.Description, maxLength);
            if (text is not null)
            {
                return text;
            }
        }

        return null;
    }
}
