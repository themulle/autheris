namespace Autheris.Application.Governance.Requests;

using System;
using System.Text;
using Autheris.Application.ConsentRequests;

/// <summary>
/// Centralized business-justification policy enforced across all request, decision, self-grant,
/// renewal, REST, GraphQL, MCP, and view-downgrade entry points.
/// </summary>
public static class JustificationPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 2000;
    public const string TooShortErrorCode = "JUSTIFICATION_TOO_SHORT";
    public const string TooLongErrorCode = "JUSTIFICATION_TOO_LONG";

    /// <summary>
    /// Strips control characters and trims leading/trailing whitespace so neither whitespace nor
    /// control-character padding counts toward <see cref="MinLength"/>, then enforces length bounds.
    /// Throws <see cref="ConsentRequestException"/> on violation.
    /// </summary>
    public static string Validate(string? justification)
    {
        if (!TryNormalize(justification, out var normalized, out var errorCode, out var message))
        {
            throw new ConsentRequestException(errorCode!, message!);
        }

        return normalized;
    }

    /// <summary>Attempts to normalize and validate a justification against policy bounds.</summary>
    public static bool TryValidate(string? justification, out string normalized, out string? errorCode)
    {
        var ok = TryNormalize(justification, out normalized, out errorCode, out _);
        return ok;
    }

    private static bool TryNormalize(string? justification, out string normalized, out string? errorCode, out string? message)
    {
        if (string.IsNullOrWhiteSpace(justification))
        {
            normalized = string.Empty;
            errorCode = TooShortErrorCode;
            message = $"A justification of at least {MinLength} characters is required.";
            return false;
        }

        var cleaned = StripControlCharacters(justification).Trim();
        if (cleaned.Length < MinLength)
        {
            normalized = string.Empty;
            errorCode = TooShortErrorCode;
            message = $"A justification of at least {MinLength} characters is required.";
            return false;
        }

        if (cleaned.Length > MaxLength)
        {
            normalized = string.Empty;
            errorCode = TooLongErrorCode;
            message = $"The justification must not exceed {MaxLength} characters.";
            return false;
        }

        normalized = cleaned;
        errorCode = null;
        message = null;
        return true;
    }

    private static string StripControlCharacters(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (!char.IsControl(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
