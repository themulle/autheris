namespace Autheris.Infrastructure.Telemetry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

/// <summary>
/// Custom W3C trace context propagator enforcing SEC-OT06-09 and SEC-OT06-11:
/// - Validates and extracts W3C traceparent (version 00, non-zero trace and span IDs).
/// - Validates tracestate (length &lt;= 512 bytes, &lt;= 32 members).
/// - Completely drops incoming and outbound baggage.
/// - Injects traceparent only (no tracestate, no baggage).
/// </summary>
public sealed class AutherisDistributedContextPropagator : DistributedContextPropagator
{
    private static readonly string[] PropagatorFields = ["traceparent"];
    private static readonly Regex TraceparentRegex = new(
        @"^00-([0-9a-f]{32})-([0-9a-f]{16})-([0-9a-f]{2})\z",
        RegexOptions.Compiled);

    public static readonly AutherisDistributedContextPropagator Instance = new();

    public override IReadOnlyCollection<string> Fields => PropagatorFields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (activity == null || carrier == null || setter == null)
        {
            return;
        }

        var traceId = activity.TraceId.ToHexString();
        var spanId = activity.SpanId.ToHexString();
        var flags = ((byte)activity.ActivityTraceFlags).ToString("x2");
        var traceParent = $"00-{traceId}-{spanId}-{flags}";

        setter(carrier, "traceparent", traceParent);
    }

    public override void ExtractTraceIdAndState(
        object? carrier,
        PropagatorGetterCallback? getter,
        out string? traceId,
        out string? traceState)
    {
        traceId = null;
        traceState = null;

        if (carrier == null || getter == null)
        {
            return;
        }

        getter(carrier, "traceparent", out var rawTraceParent, out _);
        if (!string.IsNullOrWhiteSpace(rawTraceParent))
        {
            var trimmed = rawTraceParent.Trim();
            if (trimmed.Length == 55)
            {
                var match = TraceparentRegex.Match(trimmed);
                if (match.Success)
                {
                    var traceIdPart = match.Groups[1].Value;
                    var spanIdPart = match.Groups[2].Value;

                    if (!traceIdPart.Equals("00000000000000000000000000000000", StringComparison.Ordinal) &&
                        !spanIdPart.Equals("0000000000000000", StringComparison.Ordinal))
                    {
                        traceId = trimmed;
                    }
                }
            }
        }

        getter(carrier, "tracestate", out var rawTraceState, out _);
        if (!string.IsNullOrWhiteSpace(rawTraceState))
        {
            var trimmedState = rawTraceState.Trim();
            if (trimmedState.Length <= 512)
            {
                var memberCount = 1;
                for (var i = 0; i < trimmedState.Length; i++)
                {
                    if (trimmedState[i] == ',')
                    {
                        memberCount++;
                    }
                }

                if (memberCount <= 32)
                {
                    traceState = trimmedState;
                }
            }
        }
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier,
        PropagatorGetterCallback? getter)
    {
        // SEC-OT06-11: Baggage is never extracted
        return null;
    }
}
