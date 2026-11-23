using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Autheris.Application.Governance.Notifications;

/// <summary>
/// Records the initial configured/unconfigured state of the Data Governance notification webhook channel at startup.
/// </summary>
public sealed class NotificationChannelStartupAuditHostedService(GovernanceNotificationDispatcher dispatcher) : IHostedService
{
    private readonly GovernanceNotificationDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task StartAsync(CancellationToken cancellationToken) =>
        _dispatcher.AuditStartupChannelStateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
