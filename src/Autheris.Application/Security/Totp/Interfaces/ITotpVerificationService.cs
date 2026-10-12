namespace Autheris.Application.Security.Totp.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// RFC 6238 TOTP Two-Factor Authentication Engine.
/// Provides key generation, otpauth:// URI creation, RFC 6238 validation (HMAC-SHA1/SHA256, 30s window),
/// tolerance intervals, and cluster-wide replay protection.
/// </summary>
public interface ITotpVerificationService
{
    TotpEnrollmentResult GenerateEnrollment(string userSid, string email, string issuer = "Autheris");

    string BuildQrCodeUri(string issuer, string user, string secretBase32);

    bool VerifyTotp(string secretBase32, string totpCode, int toleranceSteps = 1);

    Task<bool> VerifyAndConsumeTotpAsync(string userSid, string secretBase32, string totpCode, CancellationToken ct = default);

    string GenerateTotpCode(string secretBase32, DateTimeOffset? timestamp = null);
}
