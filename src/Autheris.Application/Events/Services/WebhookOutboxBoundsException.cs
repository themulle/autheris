namespace Autheris.Application.Events.Services;

using System;

/// <summary>An outbox row would exceed a column or payload bound. The event is dropped, never truncated.</summary>
public sealed class WebhookOutboxBoundsException(string field) : InvalidOperationException($"Webhook outbox bound exceeded: {field}.")
{
    public string Field { get; } = field;
}
