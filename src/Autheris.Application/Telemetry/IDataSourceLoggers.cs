namespace Autheris.Application.Telemetry;

using Microsoft.Extensions.Logging;

/// <summary>
/// Provides cached, categorized loggers for individual data sources (<c>Autheris.DataSource.&lt;normalized name&gt;</c>).
/// </summary>
public interface IDataSourceLoggers
{
    /// <summary>
    /// Gets or creates a cached logger for the specified data source name.
    /// Category: <c>Autheris.DataSource.&lt;normalized name&gt;</c>
    /// </summary>
    ILogger Get(string? dataSourceName);
}
