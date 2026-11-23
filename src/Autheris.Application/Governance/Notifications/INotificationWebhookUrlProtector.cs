using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Autheris.Application.Governance.Notifications;

/// <summary>
/// Protects and unprotects configured Data Governance notification webhook URLs at rest.
/// </summary>
public interface INotificationWebhookUrlProtector
{
    string Protect(string plaintextUrl);

    string Unprotect(string protectedUrl);
}

/// <summary>
/// Strict HTTPS validation and non-secret digest helpers for governance notification webhook URLs.
/// Error messages and audit payloads never include the raw URL.
/// </summary>
public static class NotificationWebhookUrlUtility
{
    public const int MaxUrlLength = 2048;

    public static bool TryValidateHttpsUrl(string? rawUrl, out Uri? uri, out string? error)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "Notification webhook URL is empty.";
            return false;
        }

        var trimmed = rawUrl.Trim();
        if (trimmed.Length > MaxUrlLength || trimmed.Any(char.IsControl))
        {
            error = "Notification webhook URL exceeds maximum length or contains control characters.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "Notification webhook URL must be an absolute HTTPS URL without userinfo.";
            return false;
        }

        uri = parsed;
        error = null;
        return true;
    }

    public static string? ExtractSafeHost(Uri? uri) =>
        uri is null || string.IsNullOrWhiteSpace(uri.IdnHost) ? null : uri.IdnHost.ToLowerInvariant();

    public static string? ComputeHostHash(Uri? uri)
    {
        var host = ExtractSafeHost(uri);
        if (host is null)
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(host));
        return Convert.ToHexStringLower(hash);
    }

    public static string? ComputeHostHash(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(host.Trim().ToLowerInvariant()));
        return Convert.ToHexStringLower(hash);
    }
}
