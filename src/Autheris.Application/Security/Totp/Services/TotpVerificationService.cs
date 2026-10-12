namespace Autheris.Application.Security.Totp.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Model;

/// <summary>
/// Universal RFC 6238 TOTP verification service.
/// Fully compatible with Microsoft Authenticator, Google Authenticator, 1Password, and Bitwarden.
/// Enforces time-step verification (30s window), configurable drift tolerance (+/- 30s),
/// constant-time string comparisons, and cluster-wide replay protection (ADR-05, R-62).
/// </summary>
public sealed class TotpVerificationService : ITotpVerificationService
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _localConsumed = new(StringComparer.OrdinalIgnoreCase);

    public TotpVerificationService(IDistributedClusterStateProvider? clusterState = null)
    {
        _clusterState = clusterState;
    }

    public TotpEnrollmentResult GenerateEnrollment(string userSid, string email, string issuer = "Autheris")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var effectiveUser = string.IsNullOrWhiteSpace(email) ? userSid : email.Trim();
        var effectiveIssuer = string.IsNullOrWhiteSpace(issuer) ? "Autheris" : issuer.Trim();

        // 160-bit key (20 bytes) = standard for TOTP Authenticator apps
        var rawKey = RandomNumberGenerator.GetBytes(20);
        var secretBase32 = EncodeBase32(rawKey);

        var qrCodeUri = BuildQrCodeUri(effectiveIssuer, effectiveUser, secretBase32);

        // Human-readable formatted key: e.g. "ABCD EFGH IJKL MNOP QRST UVWX YZ23 4567"
        var formatted = FormatBase32Key(secretBase32);

        // Generate 8 cryptographically secure single-use recovery codes (formatted as XXXX-XXXX)
        var recoveryCodes = new List<string>(8);
        for (int i = 0; i < 8; i++)
        {
            var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            recoveryCodes.Add($"{hex[..4]}-{hex[4..]}");
        }

        return new TotpEnrollmentResult(secretBase32, qrCodeUri, formatted, recoveryCodes);
    }

    public string BuildQrCodeUri(string issuer, string user, string secretBase32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretBase32);

        var safeIssuer = issuer.Trim();
        var safeUser = user.Trim();
        var safeSecret = secretBase32.Trim().Replace(" ", "").Replace("-", "").ToUpperInvariant();

        var label = $"{Uri.EscapeDataString(safeIssuer)}:{Uri.EscapeDataString(safeUser)}";
        return $"otpauth://totp/{label}?secret={safeSecret}&issuer={Uri.EscapeDataString(safeIssuer)}&algorithm=SHA1&digits=6&period=30";
    }

    public bool VerifyTotp(string secretBase32, string totpCode, int toleranceSteps = 1)
    {
        return TryMatchStep(secretBase32, totpCode, toleranceSteps, DateTimeOffset.UtcNow, out _);
    }

    public async Task<bool> VerifyAndConsumeTotpAsync(
        string userSid,
        string secretBase32,
        string totpCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userSid) ||
            string.IsNullOrWhiteSpace(secretBase32) ||
            string.IsNullOrWhiteSpace(totpCode))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (!TryMatchStep(secretBase32, totpCode, toleranceSteps: 1, now, out var matchedStep))
        {
            return false;
        }

        // Replay key: totp:consumed:{userSid}:{epochStep}
        var replayKey = $"totp:consumed:{userSid.Trim().ToLowerInvariant()}:{matchedStep}";

        // Check local replay cache
        PurgeExpiredLocalReplay();
        if (_localConsumed.TryGetValue(replayKey, out var expiry) && now < expiry)
        {
            return false; // Replay attempt rejected
        }

        // Check distributed cluster state if present
        if (_clusterState != null)
        {
            try
            {
                var existing = await _clusterState.GetAsync<string>(replayKey, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(existing))
                {
                    return false; // Replay attempt rejected by distributed state
                }
            }
            catch
            {
                // Fall back to local check if cluster state is unreachable
            }
        }

        // Mark as consumed for 90 seconds (well past the 30s +/- 30s tolerance window)
        var ttl = TimeSpan.FromSeconds(90);
        _localConsumed[replayKey] = now.Add(ttl);

        if (_clusterState != null)
        {
            try
            {
                await _clusterState.SetAsync(replayKey, "1", ttl, ct).ConfigureAwait(false);
            }
            catch
            {
                // Silently tolerate store write issue; local protection is active
            }
        }

        return true;
    }

    public string GenerateTotpCode(string secretBase32, DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretBase32);
        var key = DecodeBase32(secretBase32);
        var time = timestamp ?? DateTimeOffset.UtcNow;
        var step = time.ToUnixTimeSeconds() / 30;
        return ComputeTotp(key, step, HashAlgorithmName.SHA1, 6);
    }

    private bool TryMatchStep(
        string secretBase32,
        string totpCode,
        int toleranceSteps,
        DateTimeOffset now,
        out long matchedStep)
    {
        matchedStep = 0;
        if (string.IsNullOrWhiteSpace(totpCode) || string.IsNullOrWhiteSpace(secretBase32))
        {
            return false;
        }

        var cleanCode = totpCode.Trim().Replace(" ", "").Replace("-", "");
        if (cleanCode.Length != 6 || !int.TryParse(cleanCode, out _))
        {
            return false;
        }

        byte[] key;
        try
        {
            key = DecodeBase32(secretBase32);
        }
        catch
        {
            return false;
        }

        var currentStep = now.ToUnixTimeSeconds() / 30;
        var clampedTolerance = Math.Clamp(toleranceSteps, 0, 5);

        for (int i = -clampedTolerance; i <= clampedTolerance; i++)
        {
            var step = currentStep + i;

            // RFC 6238 check SHA1 (standard default for authenticators)
            var sha1Candidate = ComputeTotp(key, step, HashAlgorithmName.SHA1, 6);
            if (FixedTimeEquals(cleanCode, sha1Candidate))
            {
                matchedStep = step;
                return true;
            }

            // Also check SHA256 in case the authenticator is configured with SHA256
            var sha256Candidate = ComputeTotp(key, step, HashAlgorithmName.SHA256, 6);
            if (FixedTimeEquals(cleanCode, sha256Candidate))
            {
                matchedStep = step;
                return true;
            }
        }

        return false;
    }

    private static string ComputeTotp(byte[] key, long step, HashAlgorithmName algorithm, int digits)
    {
        var counterBytes = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        byte[] hash = algorithm == HashAlgorithmName.SHA256
            ? HMACSHA256.HashData(key, counterBytes)
#pragma warning disable CA5350 // RFC 6238 specifies HMAC-SHA1 as the baseline algorithm for Authenticator apps
            : HMACSHA1.HashData(key, counterBytes);
#pragma warning restore CA5350

        // Dynamic truncation (RFC 4226 Section 5.4)
        int offset = hash[^1] & 0x0F;
        int binaryCode = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);

        int mod = digits == 8 ? 100_000_000 : 1_000_000;
        int otp = binaryCode % mod;

        return otp.ToString(digits == 8 ? "D8" : "D6");
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }

    private void PurgeExpiredLocalReplay()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _localConsumed)
        {
            if (now >= kvp.Value)
            {
                _localConsumed.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string FormatBase32Key(string base32)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < base32.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                sb.Append(' ');
            }
            sb.Append(base32[i]);
        }
        return sb.ToString();
    }

    public static string EncodeBase32(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                int index = (buffer >> bitsLeft) & 0x1F;
                sb.Append(Base32Alphabet[index]);
            }
        }

        if (bitsLeft > 0)
        {
            int index = (buffer << (5 - bitsLeft)) & 0x1F;
            sb.Append(Base32Alphabet[index]);
        }

        return sb.ToString();
    }

    public static byte[] DecodeBase32(string base32)
    {
        if (string.IsNullOrWhiteSpace(base32))
        {
            return Array.Empty<byte>();
        }

        var clean = base32.Trim().TrimEnd('=').Replace(" ", "").Replace("-", "").ToUpperInvariant();
        var result = new List<byte>((clean.Length * 5) / 8);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (char c in clean)
        {
            int val;
            if (c >= 'A' && c <= 'Z')
            {
                val = c - 'A';
            }
            else if (c >= '2' && c <= '7')
            {
                val = c - '2' + 26;
            }
            else
            {
                throw new FormatException($"Invalid Base32 character: '{c}'");
            }

            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                result.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        return result.ToArray();
    }
}
