using Autheris.Application.Governance.Notifications;
using Microsoft.AspNetCore.DataProtection;

namespace Autheris.Api.Security;

/// <summary>
/// ASP.NET Data Protection based protector for Data Governance notification webhook URLs.
/// </summary>
public sealed class DataProtectionNotificationWebhookUrlProtector : INotificationWebhookUrlProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionNotificationWebhookUrlProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector("Autheris.Governance.NotificationWebhookUrl.v1");
    }

    public string Protect(string plaintextUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextUrl);
        return _protector.Protect(plaintextUrl);
    }

    public string Unprotect(string protectedUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedUrl);
        return _protector.Unprotect(protectedUrl);
    }
}
