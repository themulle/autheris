namespace Autheris.Application.Governance.Services;

using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

internal sealed class AccessPlanTokenSigner
{
    private const string SigningKeyInfo = "autheris/access-plan/confirmation-token/v1";
    private readonly byte[] _hmacKey;

    public AccessPlanTokenSigner(IKeyVaultSecretProvider? provider, string? signingKeyRef, bool clusterWide)
    {
        _hmacKey = ResolveSigningKey(provider, signingKeyRef, clusterWide);
    }

    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static byte[] ResolveSigningKey(IKeyVaultSecretProvider? provider, string? signingKeyRef, bool clusterWide)
    {
        if (string.IsNullOrWhiteSpace(signingKeyRef))
        {
            if (clusterWide)
            {
                throw new InvalidOperationException(
                    $"{AccessPlanOptions.SectionName}:SigningKeyRef is required when the cluster state is shared across replicas.");
            }

            return RandomNumberGenerator.GetBytes(32);
        }

        if (provider == null)
        {
            throw new InvalidOperationException(
                $"{AccessPlanOptions.SectionName}:SigningKeyRef is configured but no IKeyVaultSecretProvider is registered.");
        }

        var ikm = provider.GetSecretBytes(signingKeyRef);
        if (ikm == null || ikm.Length == 0)
        {
            throw new InvalidOperationException($"The secret referenced by {AccessPlanOptions.SectionName}:SigningKeyRef is empty.");
        }

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, info: Encoding.UTF8.GetBytes(SigningKeyInfo));
    }

    private static string B64(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private string Sign(string signedPart)
        => Convert.ToHexString(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(signedPart)));

    public string GenerateConfirmationToken(string planId, string adminSid, DateTimeOffset expiresAt)
    {
        var signedPart = $"v1.{B64(planId)}.{B64(adminSid)}.{expiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return $"{signedPart}.{Sign(signedPart)}";
    }

    public bool ValidateConfirmationToken(string token, string expectedPlanId, string expectedAdminSid, out DateTimeOffset expiresAt)
    {
        expiresAt = DateTimeOffset.MinValue;
        var parts = token.Split('.');
        if (parts.Length != 5 || parts[0] != "v1") return false;

        var signedPart = string.Join('.', parts[0], parts[1], parts[2], parts[3]);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(parts[4]),
            Encoding.UTF8.GetBytes(Sign(signedPart))))
        {
            return false;
        }

        if (!long.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var unix)) return false;

        string planId, adminSid;
        try
        {
            planId = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
            adminSid = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[2]));
        }
        catch (FormatException)
        {
            return false;
        }

        if (!string.Equals(planId, expectedPlanId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(adminSid, expectedAdminSid, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return true;
    }
}
