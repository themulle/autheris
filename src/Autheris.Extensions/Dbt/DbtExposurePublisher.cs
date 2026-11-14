namespace Autheris.Extensions.Dbt;

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

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
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var emitter = new Emitter(writer, EmitterSettings.Default);

        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, isImplicit: true));
        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block));

        EmitKey(emitter, "version");
        EmitPlainScalar(emitter, "2");

        EmitKey(emitter, "exposures");
        emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Block));

        foreach (var table in tables)
        {
            var expName = $"autheris_gateway_{table.Identifier.Schema}_{table.Identifier.TableName}".ToLowerInvariant();
            var description = $"Managed table '{table.Identifier}' governed by Enterprise GraphQL Gateway.";

            emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block));

            EmitKey(emitter, "name");
            EmitDoubleQuotedScalar(emitter, expName);

            EmitKey(emitter, "type");
            EmitPlainScalar(emitter, "application");

            EmitKey(emitter, "maturity");
            EmitPlainScalar(emitter, "high");

            EmitKey(emitter, "url");
            EmitDoubleQuotedScalar(emitter, "https://gateway.corp.local/graphql");

            EmitKey(emitter, "description");
            EmitDoubleQuotedScalar(emitter, description);

            EmitKey(emitter, "depends_on");
            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Block));
            EmitRefScalar(emitter, table.Identifier.TableName);
            emitter.Emit(new SequenceEnd());

            EmitKey(emitter, "owner");
            emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block));
            EmitKey(emitter, "name");
            EmitDoubleQuotedScalar(emitter, "Gateway Governance Team");
            EmitKey(emitter, "email");
            EmitDoubleQuotedScalar(emitter, "governance-team@corp.local");
            emitter.Emit(new MappingEnd());

            if (_telemetryProvider != null)
            {
                var metrics = await _telemetryProvider.GetTableMetricsAsync(table.Identifier, ct).ConfigureAwait(false);
                EmitKey(emitter, "meta");
                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block));

                EmitKey(emitter, "monthly_queries");
                EmitPlainScalar(emitter, metrics.MonthlyQueries.ToString(CultureInfo.InvariantCulture));

                EmitKey(emitter, "p99_latency_ms");
                var latency = double.IsFinite(metrics.P99LatencyMs) ? metrics.P99LatencyMs : 0.0;
                EmitPlainScalar(emitter, latency.ToString("F1", CultureInfo.InvariantCulture));

                EmitKey(emitter, "top_consumers");
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Block));
                foreach (var consumer in metrics.TopConsumers)
                {
                    EmitDoubleQuotedScalar(emitter, consumer ?? string.Empty);
                }
                emitter.Emit(new SequenceEnd());

                EmitKey(emitter, "governance_tier");
                EmitDoubleQuotedScalar(emitter, metrics.GovernanceTier ?? string.Empty);

                emitter.Emit(new MappingEnd());
            }

            emitter.Emit(new MappingEnd());
        }

        emitter.Emit(new SequenceEnd());
        emitter.Emit(new MappingEnd());
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());

        return writer.ToString();
    }

    private static void EmitKey(Emitter emitter, string key) =>
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, key, ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));

    private static void EmitPlainScalar(Emitter emitter, string value) =>
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value, ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));

    private static void EmitDoubleQuotedScalar(Emitter emitter, string value) =>
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value, ScalarStyle.DoubleQuoted, isPlainImplicit: false, isQuotedImplicit: true));

    private static void EmitRefScalar(Emitter emitter, string tableName)
    {
        var refText = $"ref('{tableName}')";
        var style = IsSafePlainIdentifier(tableName) ? ScalarStyle.Plain : ScalarStyle.DoubleQuoted;
        emitter.Emit(new Scalar(
            AnchorName.Empty,
            TagName.Empty,
            refText,
            style,
            isPlainImplicit: style == ScalarStyle.Plain,
            isQuotedImplicit: style != ScalarStyle.Plain));
    }

    private static bool IsSafePlainIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '.'))
            {
                return false;
            }
        }

        return true;
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
