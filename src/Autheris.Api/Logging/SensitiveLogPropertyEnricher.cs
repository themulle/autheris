using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Autheris.Api.Logging;

/// <summary>
/// DEP-8: query strings carry filter literals (<c>$filter=email eq '...'</c>) and must not reach the log sinks.
/// Replaces the <c>QueryString</c> property and strips the query part of URL properties logged by ASP.NET Core.
/// </summary>
public sealed class SensitiveLogPropertyEnricher : ILogEventEnricher
{
    internal const string Redacted = "[redacted]";
    private static readonly string[] UrlProperties = ["Uri", "Url", "RequestUri", "Path"];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (logEvent.Properties.TryGetValue("QueryString", out var qs) && qs is ScalarValue { Value: string s } && s.Length > 0)
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty("QueryString", new ScalarValue(Redacted)));
        }

        foreach (var name in UrlProperties)
        {
            if (logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: string url })
            {
                var q = url.IndexOf('?', StringComparison.Ordinal);
                if (q >= 0)
                {
                    logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(url[..q] + "?" + Redacted)));
                }
            }
        }
    }

    /// <summary>
    /// DEP-8: Serilog replaces the Microsoft LoggerFactory, so <c>Logging:LogLevel</c> has no effect. These code defaults
    /// keep framework request logging (full URLs) at Warning; the <c>Serilog</c> configuration section can override them.
    /// </summary>
    public static LoggerConfiguration ApplyDefaults(LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.With<SensitiveLogPropertyEnricher>();
    }
}
