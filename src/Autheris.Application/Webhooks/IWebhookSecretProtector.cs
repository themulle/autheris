namespace Autheris.Application.Webhooks;

using System;

/// <summary>The webhook signing secret cannot be protected or unprotected (no usable key, wrong key, or a malformed value).</summary>
public sealed class WebhookSecretUnavailableException : Exception
{
    public WebhookSecretUnavailableException()
        : base("The webhook secret could not be protected or unprotected.")
    {
    }

    public WebhookSecretUnavailableException(Exception inner)
        : base("The webhook secret could not be protected or unprotected.", inner)
    {
    }
}

/// <summary>
/// Protects subscription HMAC secrets at rest with an operator-provisioned key (WP-ARC-A-03). The protected value is bound to the
/// owning tenant and subscription, so a ciphertext copied to another row never decrypts.
/// </summary>
public interface IWebhookSecretProtector
{
    /// <summary><c>false</c> when no usable key is configured. Never throws.</summary>
    bool IsAvailable { get; }

    /// <exception cref="WebhookSecretUnavailableException">No usable key.</exception>
    string Protect(string tenantId, string subscriptionId, string secret);

    /// <exception cref="WebhookSecretUnavailableException">No usable key, wrong key or tenant/subscription, or malformed value.</exception>
    string Unprotect(string tenantId, string subscriptionId, string protectedValue);
}
