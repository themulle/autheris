namespace Autheris.Application.Catalog.Services;

using System;
using System.Globalization;
using Autheris.Application.Catalog.Semantic;

/// <summary>
/// Issue #448 (SEC-MGS-09): Strict parsing and validation rules for catalog minimum group size
/// and non-personal table flags.
/// </summary>
public static class CatalogMinGroupSizeRules
{
    /// <summary>
    /// Parses a minimum group size string using strict invariant parsing (<see cref="NumberStyles.None"/>).
    /// Rejects whitespace, signs, decimals, and values outside 1..<see cref="SemanticManifestValidator.MaxMinGroupSize"/>.
    /// The keyword <c>none</c> is accepted to clear the value only when <paramref name="allowNone"/> is true.
    /// </summary>
    public static bool TryParseMinGroupSize(string? text, out int? value, out string? error, bool allowNone = false)
    {
        value = null;
        error = null;

        if (text is null)
        {
            return true;
        }

        if (allowNone && string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return true;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            error = "Minimum group size must be an unsigned integer.";
            return false;
        }

        if (parsed < 1 || parsed > SemanticManifestValidator.MaxMinGroupSize)
        {
            error = $"Minimum group size must be between 1 and {SemanticManifestValidator.MaxMinGroupSize}.";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Parses a non-personal boolean string. Accepts strictly <c>true</c>, <c>false</c>, <c>1</c>, or <c>0</c> (case-insensitive).
    /// </summary>
    public static bool TryParseNonPersonal(string? text, out bool? value)
    {
        value = null;

        if (text is null)
        {
            return true;
        }

        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1")
        {
            value = true;
            return true;
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0")
        {
            value = false;
            return true;
        }

        return false;
    }
}
