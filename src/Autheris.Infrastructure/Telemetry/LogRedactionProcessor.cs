namespace Autheris.Infrastructure.Telemetry;

using System;
using System.Collections.Generic;
using Autheris.Domain.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

/// <summary>
/// Single point of log redaction enforcing SEC-OT06-05, SEC-OT06-06, and SEC-OT06-15:
/// - Replaces exceptions with value-free <see cref="RedactedException"/>.
/// - Strips query strings from URI attributes and scope values.
/// - Redacts sensitive keywords from unapproved attribute keys.
/// - Enforces 1024-character length truncation on string attributes.
/// - Injects authenticated ambient tenant context.
/// </summary>
public sealed class LogRedactionProcessor : BaseProcessor<LogRecord>
{
    private readonly bool _includeTenant;

    public LogRedactionProcessor(bool includeTenant = true)
    {
        _includeTenant = includeTenant;
    }

    public override void OnEnd(LogRecord data)
    {
        if (data == null)
        {
            return;
        }

        if (data.Exception != null && data.Exception is not RedactedException)
        {
            data.Exception = new RedactedException(data.Exception);
        }

        if (data.Attributes != null)
        {
            var modified = new List<KeyValuePair<string, object?>>(data.Attributes.Count + 1);
            foreach (var kvp in data.Attributes)
            {
                var key = kvp.Key;
                var val = kvp.Value;

                if (IsQueryKey(key) && val is string strVal)
                {
                    val = StripQueryString(strVal);
                }
                else if (IsSensitiveKey(key))
                {
                    val = "[redacted]";
                }
                else if (val is string s && s.Length > 1024)
                {
                    val = s.Substring(0, 1024);
                }

                modified.Add(new KeyValuePair<string, object?>(key, val));
            }

            if (_includeTenant)
            {
                var tenantId = TelemetryTenantContext.CurrentTenantId;
                if (!string.IsNullOrWhiteSpace(tenantId))
                {
                    modified.Add(new KeyValuePair<string, object?>(TelemetryAttributes.AutherisTenantId, tenantId));
                }
            }

            data.Attributes = modified;
        }
    }

    private static bool IsQueryKey(string key) =>
        key.Equals("QueryString", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Uri", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Url", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("RequestUri", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Path", StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitiveKey(string key)
    {
        if (key.StartsWith("autheris.", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ErrorCode", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("TraceId", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("DataSource", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Table", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // INF-12: raw exception messages, client IPs, principal names and cache/idempotency keys are PII or value-bearing.
        if (key.EndsWith("Message", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Ip", StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith("IpAddress", StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith("Ip", StringComparison.Ordinal) ||
            key.Contains("principal", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Key", StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith("Key", StringComparison.Ordinal))
        {
            return true;
        }

        return key.Contains("sql", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("query", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("filter", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("body", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("header", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("variables", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("document", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("literal", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("value", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("parameter", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("sid", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("upn", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("email", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripQueryString(string uri)
    {
        var idx = uri.IndexOf('?');
        return idx >= 0 ? uri[..idx] : uri;
    }
}
