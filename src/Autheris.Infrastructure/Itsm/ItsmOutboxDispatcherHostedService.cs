namespace Autheris.Infrastructure.Itsm;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Workflows;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Background worker processing persistent ITSM outbox messages with exponential backoff and dead-letter queue (DLQ) triage.
/// Ensures resilient delivery of external governance tickets to ServiceNow and Jira.
/// </summary>
public sealed class ItsmOutboxDispatcherHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ItsmOutboxDispatcherHostedService> _logger;
    private readonly TimeSpan _pollingInterval;

    public ItsmOutboxDispatcherHostedService(
        IServiceProvider serviceProvider,
        ILogger<ItsmOutboxDispatcherHostedService> logger,
        TimeSpan? pollingInterval = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ITSM Outbox Dispatcher hosted service started. Interval: {Interval}s", _pollingInterval.TotalSeconds);

        using var timer = new PeriodicTimer(_pollingInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMessagesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in ITSM Outbox Dispatcher loop.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("ITSM Outbox Dispatcher hosted service stopped.");
    }

    public async Task<int> ProcessPendingMessagesAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var outboxRepo = scope.ServiceProvider.GetRequiredService<IItsmOutboxRepository>();
        var dispatcher = scope.ServiceProvider.GetService<ItsmWorkflowDispatcher>();
        var approvalRepo = scope.ServiceProvider.GetService<IConsentApprovalRepository>();

        if (dispatcher == null)
        {
            return 0;
        }

        var pending = await outboxRepo.GetPendingMessagesAsync(batchSize: 20, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0) return 0;

        _logger.LogDebug("Processing {Count} pending ITSM outbox messages.", pending.Count);
        int processedCount = 0;

        foreach (var msg in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var preferredSystem = Enum.TryParse<ItsmSystemType>(msg.PreferredSystem, true, out var sys)
                    ? sys
                    : ItsmSystemType.ServiceNow;

                var ticketRequest = JsonSerializer.Deserialize<ItsmTicketRequest>(msg.PayloadJson);
                if (ticketRequest == null)
                {
                    _logger.LogError("Corrupted payload in ITSM outbox message {Id}. Moving to dead-letter queue.", msg.Id);
                    await outboxRepo.MarkFailedAsync(msg.Id, "Corrupted JSON payload", TimeSpan.Zero, deadLetter: true, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var result = await dispatcher.DispatchTicketRequestAsync(ticketRequest, preferredSystem, cancellationToken).ConfigureAwait(false);
                if (result.Success && result.TicketReference != null)
                {
                    await outboxRepo.MarkCompletedAsync(msg.Id, result.TicketReference.TicketId, cancellationToken).ConfigureAwait(false);
                    processedCount++;

                    if (Guid.TryParse(msg.RequestId, out var reqGuid) && approvalRepo != null)
                    {
                        try
                        {
                            await approvalRepo.UpdateConsentRequestTicketIdAsync(reqGuid, result.TicketReference.TicketId, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to update ConsentRequest ticket ID for request {RequestId}", msg.RequestId);
                        }
                    }

                    _logger.LogInformation("Successfully dispatched ITSM outbox message {Id} to {System}. Ticket: {TicketId}",
                        msg.Id, preferredSystem, result.TicketReference.TicketId);
                }
                else
                {
                    var isDeadLetter = (msg.RetryCount + 1) >= msg.MaxRetries;
                    var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, msg.RetryCount + 1)));
                    var error = result.ErrorMessage ?? "Dispatch failed without specific error.";

                    _logger.LogWarning("ITSM dispatch failed for message {Id} (Attempt {Attempt}/{Max}). Moving to DLQ: {IsDlq}. Error: {Error}",
                        msg.Id, msg.RetryCount + 1, msg.MaxRetries, isDeadLetter, error);

                    await outboxRepo.MarkFailedAsync(msg.Id, error, delay, deadLetter: isDeadLetter, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                var isDeadLetter = (msg.RetryCount + 1) >= msg.MaxRetries;
                var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, msg.RetryCount + 1)));

                _logger.LogWarning(ex, "Exception during ITSM dispatch for message {Id}. Moving to DLQ: {IsDlq}.", msg.Id, isDeadLetter);
                await outboxRepo.MarkFailedAsync(msg.Id, ex.Message, delay, deadLetter: isDeadLetter, cancellationToken).ConfigureAwait(false);
            }
        }

        return processedCount;
    }
}
