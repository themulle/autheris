namespace Autheris.Application.Governance.Validation;

using System;
using System.Collections.Generic;

/// <summary>
/// The scope entries of a consent column rule. The store keeps a rule's scopes comma-separated and reads them back split, trimmed and without
/// empty entries, so a blank entry would turn a scope rule into a base rule for every scope and a comma would split one entry into several
/// scopes. Such entries are rejected before anything is written (fail closed).
/// </summary>
public static class ConsentRuleScopeValidator
{
    /// <summary>True when every entry names a scope: not blank and without a comma. No list or an empty list (a base rule) is valid.</summary>
    public static bool AreValid(IReadOnlyList<string?>? scopes)
    {
        if (scopes is null)
        {
            return true;
        }

        foreach (var scope in scopes)
        {
            if (string.IsNullOrWhiteSpace(scope) || scope.Contains(',', StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
