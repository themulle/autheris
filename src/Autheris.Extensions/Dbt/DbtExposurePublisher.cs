namespace Autheris.Extensions.Dbt;

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Microsoft.Extensions.Logging;

public sealed class DbtExposurePublisher(
    ITableMetadataRepository metadataRepository,
    ILogger<DbtExposurePublisher> logger,
    ITelemetryMetricsProvider? telemetryProvider = null) : IDbtExposurePublisher
{
    private readonly ITableMetadataRepository _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
    private readonly ILogger<DbtExposurePublisher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ITelemetryMetricsProvider? _telemetryProvider = telemetryProvider;

    public async Task<string> GenerateExposuresYamlAsync(CancellationToken ct = default)
    {
        var tables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var sb = new StringBuilder();

        sb.AppendLine("version: 2");
        sb.AppendLine();
        sb.AppendLine("exposures:");

        foreach (var table in tables)
        {
            var expName = EscapeYamlString($"autheris_gateway_{table.Identifier.Schema}_{table.Identifier.TableName}".ToLowerInvariant());
            var tableName = EscapeYamlString(table.Identifier.TableName);
            var identifierStr = EscapeYamlString(table.Identifier.ToString());

            sb.AppendLine($"  - name: \"{expName}\"");
            sb.AppendLine("    type: application");
            sb.AppendLine("    maturity: high");
            sb.AppendLine("    url: \"https://gateway.corp.local/graphql\"");
            sb.AppendLine($"    description: \"Managed table '{identifierStr}' governed by Enterprise GraphQL Gateway.\"");
            sb.AppendLine("    depends_on:");
            sb.AppendLine($"      - ref('{tableName}')");
            sb.AppendLine("    owner:");
            sb.AppendLine("      name: \"Gateway Governance Team\"");
            sb.AppendLine("      email: \"governance-team@corp.local\"");

            if (_telemetryProvider != null)
            {
                var metrics = await _telemetryProvider.GetTableMetricsAsync(table.Identifier, ct).ConfigureAwait(false);
                sb.AppendLine("    meta:");
                sb.AppendLine($"      monthly_queries: {metrics.MonthlyQueries}");
                sb.AppendLine($"      p99_latency_ms: {metrics.P99LatencyMs:F1}");
                sb.AppendLine("      top_consumers:");
                foreach (var consumer in metrics.TopConsumers)
                {
                    sb.AppendLine($"        - \"{EscapeYamlString(consumer)}\"");
                }
                sb.AppendLine($"      governance_tier: \"{EscapeYamlString(metrics.GovernanceTier)}\"");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task ExportExposuresFileAsync(string outputFilePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);

        var fullPath = Path.GetFullPath(outputFilePath);
        ValidateSafeFilePath(fullPath);

        var yaml = await GenerateExposuresYamlAsync(ct).ConfigureAwait(false);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, yaml, Encoding.UTF8, ct).ConfigureAwait(false);
        _logger.LogInformation("Exported dbt exposures YAML to {Path}", fullPath);
    }

    private static string EscapeYamlString(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", " ");
    }

    private static void ValidateSafeFilePath(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/').ToLowerInvariant();
        if (normalized.StartsWith("/etc") ||
            normalized.StartsWith("/proc") ||
            normalized.StartsWith("/sys") ||
            normalized.StartsWith("/dev") ||
            normalized.StartsWith("/root/.ssh") ||
            normalized.Contains("/.ssh/") ||
            normalized.Contains("/appsettings") ||
            normalized.Contains("windows/system32"))
        {
            throw new System.Security.SecurityException($"Access to restricted path '{fullPath}' is strictly forbidden.");
        }
    }
}
