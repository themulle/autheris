namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;

/// <summary>
/// F-GOV-14: stable error codes of Virtual Filter denials. They are the <c>ErrorCode</c> of the
/// <see cref="Autheris.Domain.Exceptions.GatewaySecurityException"/> (HTTP 403) and the audit <c>reasonCode</c>.
/// A resolver deny reason that starts with <c>CODE:</c> carries the code to the channel that raises the exception.
/// </summary>
public static class VirtualFilterErrorCodes
{
    /// <summary>A target pattern could not be evaluated (invalid, unsupported, overlong or timed out).</summary>
    public const string PatternEvaluationFailed = "VF_PATTERN_EVALUATION_FAILED";

    /// <summary>A table of the query has no decision, or an applied Virtual Filter is not enforced.</summary>
    public const string EnforcementIncomplete = Autheris.Domain.Interfaces.TableAccessDecision.VirtualFilterEnforcementIncompleteCode;

    /// <summary>A key column has a catalog type that cannot be compared safely, or the key set has other key types.</summary>
    public const string KeyTypeMismatch = "VF_KEY_TYPE_MISMATCH";

    /// <summary>The filter cannot be evaluated in its key source (sql definition, validity window, non-SQL or untenanted table).</summary>
    public const string KeySourceUnsupported = "VF_KEY_SOURCE_UNSUPPORTED";

    /// <summary>The authorized key set exceeds <c>MaxAuthorizedKeys</c> (never truncated).</summary>
    public const string KeySetTooLarge = "VF_KEY_SET_TOO_LARGE";

    /// <summary>A binding maps a key to a column the protected object does not have, or to an invalid identifier.</summary>
    public const string BindingMisconfigured = "VF_BINDING_MISCONFIGURED";

    /// <summary>The key source could not be read (connection, query or timeout).</summary>
    public const string SourceUnavailable = "VIRTUAL_FILTER_SOURCE_UNAVAILABLE";

    private static readonly IReadOnlyList<string> CodesInDenyReasons = [PatternEvaluationFailed];

    /// <summary>The Virtual Filter error code a deny reason starts with (<c>CODE: ...</c>), if any.</summary>
    public static bool TryGetCode(string? denyReason, out string code)
    {
        foreach (var candidate in CodesInDenyReasons)
        {
            if (denyReason != null &&
                denyReason.Length > candidate.Length &&
                denyReason[candidate.Length] == ':' &&
                denyReason.StartsWith(candidate, StringComparison.Ordinal))
            {
                code = candidate;
                return true;
            }
        }

        code = string.Empty;
        return false;
    }
}
