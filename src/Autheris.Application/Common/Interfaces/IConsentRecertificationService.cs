namespace Autheris.Application.Common.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

public interface IConsentRecertificationService
{
    Task<int> ScanAndTriggerExpiringConsentRecertificationsAsync(
        TimeSpan? warningWindow = null,
        CancellationToken ct = default);
}
