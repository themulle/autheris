namespace Autheris.Application.Security;

using System;

/// <summary>
/// DEP-6 / POL-14 / R-DEP-1: cryptographic keys (HMAC masking master key, audit chain key, ForwardAuth shared secret)
/// need at least 32 bytes outside Development. Checked where the key is used, independent of the reference name.
/// </summary>
public static class SecretKeyRequirements
{
    public const int MinimumKeyBytes = 32;

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
                $"Sicherheitsfehler: {purpose} muss außerhalb der Entwicklungsumgebung mindestens {MinimumKeyBytes} Bytes lang sein (aktuelle Länge: {length}).");
        }
    }
}
