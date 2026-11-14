namespace Autheris.Application.Security;

using System;
using System.Text;

/// <summary>
/// DEP-6 / POL-14 / R-DEP-1: cryptographic keys (HMAC masking master key, audit chain key, ForwardAuth shared secret,
/// keyset cursor signing key) need at least 32 bytes outside Development. Checked where the key is used, independent of the reference name.
/// </summary>
public static class SecretKeyRequirements
{
    public const int MinimumKeyBytes = 32;

    private static readonly string[] PlaceholderTokens =
    [
        "changeme",
        "change-me",
        "change_me",
        "placeholder",
        "replace-me",
        "replace_me",
        "replace-with",
        "replace_with",
        "default-secret",
        "default_secret",
        "your-secret",
        "your_secret",
        "your-signing-key",
        "dev_insecure",
        "dev-insecure"
    ];

    public static void EnsureMinimumLength(byte[]? key, string purpose, bool isDevelopment)
    {
        if (isDevelopment)
        {
            return;
        }

        var length = key?.Length ?? 0;
        if (length < MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"Security error: {purpose} must be at least {MinimumKeyBytes} bytes long outside the development environment (current length: {length}).");
        }
    }

    /// <summary>
    /// Validates a configured keyset cursor signing key for non-Development use: trims leading and trailing whitespace,
    /// rejects blank or placeholder values, and enforces at least <see cref="MinimumKeyBytes"/> UTF-8 bytes without echoing the value.
    /// </summary>
    public static bool TryValidateCursorSigningKey(string? rawKey, out byte[] keyBytes, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            keyBytes = [];
            errorMessage =
                $"Gateway:RestApi:BulkStream:SigningKey must be configured outside Development (minimum {MinimumKeyBytes} UTF-8 bytes after trim).";
            return false;
        }

        var trimmed = rawKey.Trim();
        if (IsPlaceholderSecret(trimmed))
        {
            keyBytes = [];
            errorMessage =
                $"Gateway:RestApi:BulkStream:SigningKey cannot use a placeholder value outside Development (minimum {MinimumKeyBytes} UTF-8 bytes).";
            return false;
        }

        var byteCount = Encoding.UTF8.GetByteCount(trimmed);
        if (byteCount < MinimumKeyBytes)
        {
            keyBytes = [];
            errorMessage =
                $"Gateway:RestApi:BulkStream:SigningKey must be at least {MinimumKeyBytes} bytes long after trim outside Development (current length: {byteCount} bytes).";
            return false;
        }

        keyBytes = Encoding.UTF8.GetBytes(trimmed);
        errorMessage = null;
        return true;
    }

    public static bool IsPlaceholderSecret(string trimmedValue)
    {
        foreach (var token in PlaceholderTokens)
        {
            if (trimmedValue.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
