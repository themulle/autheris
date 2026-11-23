namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;

/// <summary>Application request records that are named <c>*Command</c> but are plain data carriers, not database commands.</summary>
internal static class CommandRecordExemptions
{
    public static readonly HashSet<string> DataCarriers = new(StringComparer.Ordinal)
    {
        "Autheris.Application.Governance.Requests.PermissionDecisionCommand",
        "Autheris.Application.Governance.Requests.SelfGrantCommand",
        "Autheris.Application.Governance.Requests.GrantRenewalCommand"
    };
}
