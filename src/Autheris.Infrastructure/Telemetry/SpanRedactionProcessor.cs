namespace Autheris.Infrastructure.Telemetry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autheris.Domain.Diagnostics;
using OpenTelemetry;

/// <summary>
/// First processor in the tracer pipeline enforcing SEC-OT06-01 fail-closed span redaction:
/// - Validates Autheris-source spans against <see cref="TelemetrySpanCatalog"/>.
/// - Strips queries, unapproved headers, and statement text from third-party instrumentation spans.
/// - Replaces free-text status descriptions with stable error codes.
/// </summary>
public sealed class SpanRedactionProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if (activity == null)
        {
            return;
        }

        var sourceName = activity.Source.Name;
        var isAutherisSource = sourceName.StartsWith("Autheris.", StringComparison.OrdinalIgnoreCase);

        var tags = activity.TagObjects.ToList();

        if (isAutherisSource)
        {
            foreach (var tag in tags)
            {
                if (!TelemetrySpanCatalog.IsAllowed(activity.OperationName, tag.Key))
                {
                    activity.SetTag(tag.Key, null);
                }
            }
        }
        else
        {
            foreach (var tag in tags)
            {
                var key = tag.Key;

                if (key.Equals("url.query", StringComparison.OrdinalIgnoreCase))
                {
                    activity.SetTag(key, null);
                }
                else if (key.Equals("url.full", StringComparison.OrdinalIgnoreCase) && tag.Value is string urlStr)
                {
                    var queryIdx = urlStr.IndexOf('?');
                    if (queryIdx >= 0)
                    {
                        activity.SetTag(key, urlStr[..queryIdx]);
                    }
                }
                else if ((key.StartsWith("http.request.header.", StringComparison.OrdinalIgnoreCase) ||
                          key.StartsWith("http.response.header.", StringComparison.OrdinalIgnoreCase)) &&
                         !key.EndsWith("content-type", StringComparison.OrdinalIgnoreCase))
                {
                    activity.SetTag(key, null);
                }
                else if (key.Equals("db.statement", StringComparison.OrdinalIgnoreCase))
                {
                    activity.SetTag(key, null);
                }
            }
        }

        if (activity.Status == ActivityStatusCode.Error && !string.IsNullOrWhiteSpace(activity.StatusDescription))
        {
            // Replace free-text status description with stable error code / sanitized description
            var errorTag = activity.GetTagItem(TelemetryAttributes.ErrorType) as string;
            activity.SetStatus(ActivityStatusCode.Error, errorTag ?? "error");
        }
    }
}
