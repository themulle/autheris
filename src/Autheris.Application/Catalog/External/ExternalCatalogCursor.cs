namespace Autheris.Application.Catalog.External;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;

internal static class ExternalCatalogCursor
{
    public const int MaxCursorLength = 512;
    private const string CursorVersion = "v2";
    private const string DevSentinelKeyRef = "DEV_INSECURE_TEST_KEY_ONLY";
    private const string DevSentinelSaltRef = "dev-only-hmac-salt-secure-fallback";
    private static readonly byte[] PurposeInfo = "Autheris:ExternalCatalogCursor:v2"u8.ToArray();

    public static byte[]? ResolvePersistentKey(
        GatewayOptions options,
        IKeyVaultSecretProvider? secretProvider = null,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (TryResolveSecretRef(options.GovernanceDb?.AuditHmacKeyVaultRef, secretProvider, out var auditKey))
        {
            return DerivePurposeKey(auditKey);
        }

        if (TryResolveSecretRef(options.DataMasking?.HmacSecretKeyVaultRef, secretProvider, out var masterKey))
        {
            return DerivePurposeKey(masterKey);
        }

        var bulkKey = options.RestApi?.BulkStream?.SigningKey;
        if (string.IsNullOrWhiteSpace(bulkKey))
        {
            bulkKey = options.BulkStream?.SigningKey;
        }

        if (SecretKeyRequirements.TryValidateCursorSigningKey(bulkKey, out var bulkBytes, out _))
        {
            return DerivePurposeKey(bulkBytes);
        }

        var clusterSecret = configuration?["Gateway:Cluster:Secret"];
        if (!string.IsNullOrWhiteSpace(clusterSecret))
        {
            var trimmed = clusterSecret.Trim();
            if (!IsDevSentinel(trimmed) &&
                !SecretKeyRequirements.IsPlaceholderSecret(trimmed) &&
                Encoding.UTF8.GetByteCount(trimmed) >= SecretKeyRequirements.MinimumKeyBytes)
            {
                return DerivePurposeKey(Encoding.UTF8.GetBytes(trimmed));
            }
        }

        return null;
    }

    public static string ComputeBinding(
        ExternalCatalogListQuery query,
        string? normalizedKind,
        DiscoveryAccessState? accessFilter,
        string? searchText,
        TenantId tenantId,
        ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(user);

        var scopeHash = ComputeCallerScopeHash(user);
        var material = FormatBindingMaterial(query, normalizedKind, accessFilter, searchText, tenantId, scopeHash);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Base64UrlEncode(digest.AsSpan(0, 16));
    }

    internal static string FormatBindingMaterial(
        ExternalCatalogListQuery query,
        string? normalizedKind,
        DiscoveryAccessState? accessFilter,
        string? searchText,
        TenantId tenantId,
        string callerScopeHash)
    {
        var sb = new StringBuilder("ecb2");
        AppendField(sb, Normalize(searchText));
        AppendField(sb, accessFilter?.ToString().ToLowerInvariant());
        AppendField(sb, Normalize(normalizedKind));
        AppendField(sb, Normalize(query.Domain));
        AppendField(sb, Normalize(query.Schema));
        AppendField(sb, Normalize(query.Owner));
        AppendField(sb, Normalize(query.Tag));
        AppendField(sb, Normalize(query.Sensitivity));
        AppendField(sb, query.Pii.HasValue ? (query.Pii.Value ? "true" : "false") : null);
        AppendField(sb, query.ChangedSince.HasValue ? query.ChangedSince.Value.ToString(CultureInfo.InvariantCulture) : null);
        AppendField(sb, tenantId.Value);
        AppendField(sb, callerScopeHash);
        return sb.ToString();
    }

    public static string Encode(string lastKey, int? totalCount, string binding, byte[]? hmacKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lastKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);

        var countPart = hmacKey is not null && totalCount is >= 0
            ? totalCount.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

        var macPart = hmacKey is not null
            ? ComputeMac(hmacKey, lastKey, countPart, binding)
            : string.Empty;

        return PageCursor.Encode($"{CursorVersion}|{lastKey}|{countPart}|{binding}|{macPart}");
    }

    public static bool TryDecode(
        string? cursor,
        string expectedBinding,
        byte[]? hmacKey,
        out string lastKey,
        out int? count)
    {
        lastKey = string.Empty;
        count = null;

        if (string.IsNullOrEmpty(expectedBinding) ||
            !PageCursor.TryDecode(cursor, MaxCursorLength, out var raw))
        {
            return false;
        }

        if (!raw.StartsWith($"{CursorVersion}|", StringComparison.Ordinal))
        {
            return false;
        }

        var macPipe = raw.LastIndexOf("|", StringComparison.Ordinal);
        if (macPipe < 4)
        {
            return false;
        }

        var bindingPipe = raw.LastIndexOf("|", macPipe - 1, StringComparison.Ordinal);
        if (bindingPipe < 3)
        {
            return false;
        }

        var countPipe = raw.LastIndexOf("|", bindingPipe - 1, StringComparison.Ordinal);
        if (countPipe <= 3)
        {
            return false;
        }

        var keyPart = raw[3..countPipe];
        var countPart = raw[(countPipe + 1)..bindingPipe];
        var bindingPart = raw[(bindingPipe + 1)..macPipe];
        var macPart = raw[(macPipe + 1)..];

        if (string.IsNullOrWhiteSpace(keyPart) ||
            keyPart.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
        {
            return false;
        }

        if (hmacKey is not null)
        {
            if (string.IsNullOrEmpty(macPart))
            {
                return false;
            }

            var expectedMac = ComputeMac(hmacKey, keyPart, countPart, bindingPart);
            if (!FixedTimeEqualsStrings(expectedMac, macPart))
            {
                return false;
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(macPart) || !string.IsNullOrEmpty(countPart))
            {
                return false;
            }
        }

        if (!FixedTimeEqualsStrings(expectedBinding, bindingPart))
        {
            return false;
        }

        if (countPart.Length > 0)
        {
            if (!int.TryParse(countPart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCount) ||
                parsedCount < 0)
            {
                return false;
            }

            count = parsedCount;
        }

        lastKey = keyPart;
        return true;
    }

    internal static string ComputeCallerScopeHash(ClaimsPrincipal user)
    {
        var sb = new StringBuilder();
        if (user.Identity?.IsAuthenticated != true)
        {
            sb.Append("anon");
            AppendField(sb, FirstClaimValue(user, ClaimTypes.NameIdentifier) ?? FirstClaimValue(user, "sub"));
        }
        else if (AppOnlyPrincipal.Is(user))
        {
            sb.Append("app");
            AppendField(sb, AppOnlyPrincipal.GetClientId(user) ?? FirstClaimValue(user, ClaimTypes.NameIdentifier) ?? FirstClaimValue(user, "sub"));
            foreach (var claimValue in CollectSortedRoleAndScopeClaims(user))
            {
                AppendField(sb, claimValue);
            }
        }
        else
        {
            sb.Append("user");
            AppendField(sb, user.GetUserSid()?.Value ?? FirstClaimValue(user, ClaimTypes.NameIdentifier) ?? FirstClaimValue(user, "sub"));
            foreach (var role in user.GetUserRoles().OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ThenBy(static r => r, StringComparer.Ordinal))
            {
                AppendField(sb, "r:" + role);
            }

            foreach (var group in user.GetGroupSids().Select(static g => g.Value).OrderBy(static g => g, StringComparer.OrdinalIgnoreCase).ThenBy(static g => g, StringComparer.Ordinal))
            {
                AppendField(sb, "g:" + group);
            }

            foreach (var scope in CollectSortedScopeClaims(user))
            {
                AppendField(sb, "s:" + scope);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static IEnumerable<string> CollectSortedRoleAndScopeClaims(ClaimsPrincipal user)
    {
        var values = new List<string>();
        foreach (var claim in user.Claims)
        {
            if (claim.Type is "roles" or "role" or ClaimTypes.Role or "scope" or "scp")
            {
                values.Add(claim.Type + ":" + claim.Value);
            }
        }

        values.Sort(StringComparer.Ordinal);
        return values;
    }

    private static IEnumerable<string> CollectSortedScopeClaims(ClaimsPrincipal user)
    {
        var values = new List<string>();
        foreach (var claim in user.Claims)
        {
            if (string.Equals(claim.Type, "scope", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, "scp", StringComparison.OrdinalIgnoreCase))
            {
                values.Add(claim.Value);
            }
        }

        values.Sort(StringComparer.Ordinal);
        return values;
    }

    private static bool TryResolveSecretRef(string? secretRef, IKeyVaultSecretProvider? secretProvider, out byte[] secretBytes)
    {
        secretBytes = [];
        if (secretProvider is null || string.IsNullOrWhiteSpace(secretRef))
        {
            return false;
        }

        var trimmed = secretRef.Trim();
        if (IsDevSentinel(trimmed) || SecretKeyRequirements.IsPlaceholderSecret(trimmed))
        {
            return false;
        }

        try
        {
            var resolved = secretProvider.GetSecretBytes(trimmed);
            if (resolved is { Length: >= SecretKeyRequirements.MinimumKeyBytes })
            {
                secretBytes = resolved;
                return true;
            }
        }
        catch
        {
            // Ignore unreadable optional secret ref and continue fallback chain.
        }

        return false;
    }

    private static bool IsDevSentinel(string value) =>
        string.Equals(value, DevSentinelKeyRef, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, DevSentinelSaltRef, StringComparison.OrdinalIgnoreCase);

    private static byte[] DerivePurposeKey(byte[] ikm) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, info: PurposeInfo);

    private static string ComputeMac(byte[] key, string lastKey, string countPart, string binding)
    {
        var input = $"{CursorVersion}|{lastKey.Length.ToString(CultureInfo.InvariantCulture)}:{lastKey}|{countPart.Length.ToString(CultureInfo.InvariantCulture)}:{countPart}|{binding.Length.ToString(CultureInfo.InvariantCulture)}:{binding}";
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(input));
        return Base64UrlEncode(mac);
    }

    private static bool FixedTimeEqualsStrings(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static void AppendField(StringBuilder sb, string? value)
    {
        if (value is null)
        {
            sb.Append("|-");
            return;
        }

        sb.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    private static string? FirstClaimValue(ClaimsPrincipal user, string claimType) =>
        user.FindFirst(claimType)?.Value;
}
