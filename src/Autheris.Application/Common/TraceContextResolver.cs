namespace Autheris.Application.Common;

using System;
using System.Diagnostics;

/// <summary>
/// F-OPS-02: Resolves distributed W3C Trace context for correlating APM traces with WORM audit entries.
/// </summary>
public static class TraceContextResolver
{
    public static string GetCurrentTraceId()
    {
        var activity = Activity.Current;
        if (activity != null && activity.TraceId != default)
        {
            var hex = activity.TraceId.ToHexString();
            if (!string.IsNullOrWhiteSpace(hex) && hex.Length == 32)
            {
                return hex.ToLowerInvariant();
            }
        }

        if (activity != null && !string.IsNullOrWhiteSpace(activity.Id))
        {
            var sanitized = activity.Id.Replace("-", "").Replace("/", "").ToLowerInvariant();
            if (sanitized.Length >= 32)
            {
                return sanitized[..32];
            }
        }

        return Guid.NewGuid().ToString("N");
    }
}
