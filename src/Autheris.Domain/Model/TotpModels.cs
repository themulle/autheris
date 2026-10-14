namespace Autheris.Domain.Model;

using System.Collections.Generic;

/// <summary>
/// RFC 6238 TOTP 2FA Enrollment Result containing the Base32 shared secret,
/// otpauth:// URI for QR code generation, human-readable formatted key,
/// and single-use recovery codes.
/// </summary>
public sealed record TotpEnrollmentResult(
    string SecretBase32,
    string QrCodeUri,
    string FormattedKey,
    IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// Request to verify or activate TOTP 2FA. The seed is never client-supplied (SR-M-01): the code is checked against the
/// server-side pending seed. <paramref name="CurrentTotpCode"/> is required to replace an already active secret.
/// </summary>
public sealed record TotpVerifyRequest(
    string TotpCode,
    string? CurrentTotpCode = null);

/// <summary>
/// Verification result for TOTP 2FA.
/// </summary>
public sealed record TotpVerifyResult(
    bool IsValid,
    string? ErrorMessage = null);
