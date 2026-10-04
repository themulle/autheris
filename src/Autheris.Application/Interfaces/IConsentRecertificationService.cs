namespace Autheris.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

public interface IConsentRecertificationService
{
    Task<int> ScanAndTriggerExpiringConsentRecertificationsAsync(
        TimeSpan? warningWindow = null,
        CancellationToken ct = default);

    Task<bool> ExtendConsentExpiryAsync(
        Guid consentId,
        TimeSpan extensionDuration,
        Sid approverSid,
        string justification,
        CancellationToken ct = default);
}
