namespace Autheris.Domain.Model;

/// <summary>
/// Parsing of an external grantee type literal. The retired role literal is never mapped to another grantee type, and a
/// numeric literal is never accepted as an enum value, so every entry point refuses it with the same fixed message.
/// </summary>
public static class GranteeTypeParsing
{
    /// <summary>The refusal text for the retired role grantee (never echoes the supplied value).</summary>
    public const string RoleRefusedMessage = "Role grantees are not supported; use a user, group or service principal.";

    /// <summary>The stable error code of that refusal.</summary>
    public const string RoleRefusedCode = "ROLE_GRANTEE_UNSUPPORTED";

    public static bool IsRetiredRole(string? value) =>
        string.Equals(value?.Trim(), "role", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a defined grantee type by name; empty, numeric, undefined and retired values return false.</summary>
    public static bool TryParse(string? value, out GranteeType type)
    {
        type = default;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || char.IsDigit(text[0]) || text[0] is '-' or '+')
        {
            return false;
        }

        return Enum.TryParse(text, ignoreCase: true, out type) && Enum.IsDefined(type);
    }

    /// <summary>
    /// Parses a grantee type read from storage. A row that holds an undefined, numeric or retired literal fails the read
    /// with a fixed message (never echoing the stored text), so the row cannot be mistaken for a defined grantee.
    /// </summary>
    public static GranteeType ParseStored(string? value) =>
        TryParse(value, out var type)
            ? type
            : throw new ArgumentException("A stored grantee type is not a supported value.", nameof(value));
}
