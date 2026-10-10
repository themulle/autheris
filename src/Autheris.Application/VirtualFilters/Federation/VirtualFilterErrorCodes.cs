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
    public const string EnforcementIncomplete = "VF_ENFORCEMENT_INCOMPLETE";

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
