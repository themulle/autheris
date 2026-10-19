namespace Autheris.Application.Telemetry;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Allowlist of outbound integration client names permitted to receive trace context propagation.
/// </summary>
public static class OutboundIntegrations
{
    public static readonly FrozenSet<string> Internal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Internal",
        "GovernanceStore",
        "Redis",
        "EventBus"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Evaluates fail-closed trace context propagation policies for outbound calls and webhooks.
/// </summary>
public static class OutboundPropagationPolicy
{
    /// <summary>
    /// Checks whether the specified outbound integration client is permitted to propagate trace context.
    /// Fail-closed: only integrations registered in <see cref="OutboundIntegrations.Internal"/> are allowed.
    /// </summary>
    public static bool ShouldPropagate(string? integrationName)
    {
        if (string.IsNullOrWhiteSpace(integrationName))
        {
            return false;
        }

        return OutboundIntegrations.Internal.Contains(integrationName);
    }

    /// <summary>
    /// Checks whether an outbound webhook is permitted to carry W3C traceparent context.
    /// Allowed strictly for configured subscriptions with explicit opt-in (<paramref name="propagateTraceContext"/> = true).
    /// CloudEvents subscriptions are never permitted to propagate in BL-06.
    /// </summary>
    public static bool ShouldPropagateWebhook(bool propagateTraceContext, WebhookSubscriptionSource source)
    {
        return propagateTraceContext && source == WebhookSubscriptionSource.Configured;
    }
}
