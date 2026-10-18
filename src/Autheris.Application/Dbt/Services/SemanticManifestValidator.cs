namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Application.Analytics;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>Outcome of <see cref="SemanticManifestValidator.Validate"/>. Errors are fixed codes: no name, column or template text is ever echoed.</summary>
public sealed record SemanticManifestValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static readonly SemanticManifestValidationResult Valid = new(true, []);
}

/// <summary>
/// Validates a parsed semantic manifest before any side effect (#112 SI-112-5, M-2, SEC-112-08/-14/-16/-17). Pure: no store, no catalog,
/// no table write. Metric filter templates are parsed with the engine's policy parser against the columns of the model's base table;
/// the parse context has no catalog (no subqueries) and no parameters (no <c>:name</c> placeholders, no caller attributes).
/// </summary>
public sealed partial class SemanticManifestValidator
{
    public const int MaxModels = 1_000;
    public const int MaxMetrics = 5_000;
    public const int MaxTextLength = 4_096;
    public const int MaxNameLength = 128;
    public const int MaxMinGroupSize = 1_000_000;
    public const int MaxPersistedJsonBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> StandardGranularities = new(StringComparer.OrdinalIgnoreCase)
    {
        "hour", "day", "week", "month", "quarter", "year"
    };

    private static readonly string[] PrivacyKeyFragments = ["group_size", "strategy", "suppress", "noise", "epsilon", "privacy", "bypass", "consent", "anonym"];
    private static readonly HashSet<string> AllowedPrivacyKeys = new(StringComparer.OrdinalIgnoreCase) { "min_group_size" };

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex MetricNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelNamePattern();

    private readonly IPolicyExpressionParser _parser;
    private readonly SemanticOptions _options;

    public SemanticManifestValidator(IPolicyExpressionParser parser, IOptions<SemanticOptions> options)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <param name="manifest">The parsed manifest.</param>
    /// <param name="baseTables">Resolved base table per model name; needed only for models whose metrics carry a filter template.</param>
    public SemanticManifestValidationResult Validate(DbtSemanticManifest manifest, IReadOnlyDictionary<string, TableMetadata> baseTables)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(baseTables);

        var errors = new List<string>();
        void Fail(string code)
        {
            if (!errors.Contains(code))
            {
                errors.Add(code);
            }
        }

        if (manifest.SemanticModels.Count > MaxModels)
        {
            Fail("MODEL_COUNT");
        }

        if (manifest.Metrics.Count > MaxMetrics)
        {
            Fail("METRIC_COUNT");
        }

        if (errors.Count > 0)
        {
            return new SemanticManifestValidationResult(false, errors);
        }

        CheckText(manifest.Version, Fail, required: false);
        if (SemanticManifestJson.Serialize(manifest).Length > MaxPersistedJsonBytes)
        {
            Fail("MANIFEST_SIZE");
        }

        var modelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var measureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dimensionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metric_time" };
        foreach (var model in manifest.SemanticModels)
        {
            if (model.Name is null || !ModelNamePattern().IsMatch(model.Name))
            {
                Fail("MODEL_NAME");
            }
            else if (!modelNames.Add(model.Name))
            {
                Fail("MODEL_DUPLICATE");
            }

            CheckText(model.Description, Fail, required: false);
            CheckText(model.NodeRelation, Fail, required: false);
            foreach (var entity in model.Entities)
            {
                CheckText(entity.Name, Fail, required: true);
                CheckText(entity.Type, Fail, required: false);
                CheckText(entity.Role, Fail, required: false);
                dimensionNames.Add(entity.Name ?? string.Empty);
            }

            foreach (var measure in model.Measures)
            {
                CheckText(measure.Name, Fail, required: true);
                CheckText(measure.Aggregation, Fail, required: false);
                CheckText(measure.Expr, Fail, required: false);
                CheckText(measure.Description, Fail, required: false);
                CheckText(measure.Unit, Fail, required: false);
                CheckMinGroupSize(measure.MinGroupSize, Fail);
                measureNames.Add(measure.Name ?? string.Empty);
            }

            foreach (var dimension in model.Dimensions)
            {
                CheckText(dimension.Name, Fail, required: true);
                CheckText(dimension.Type, Fail, required: false);
                CheckText(dimension.Description, Fail, required: false);
                CheckText(dimension.TimeGranularity, Fail, required: false);
                CheckGranularities(dimension.AllowedTimeGranularities, Fail);
                dimensionNames.Add(dimension.Name ?? string.Empty);
            }
        }

        var metricNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var metric in manifest.Metrics)
        {
            if (metric.Name is null || !MetricNamePattern().IsMatch(metric.Name))
            {
                Fail("METRIC_NAME");
            }
            else if (!metricNames.Add(metric.Name))
            {
                Fail("METRIC_DUPLICATE");
            }

            CheckText(metric.Type, Fail, required: false);
            CheckText(metric.Description, Fail, required: false);
            CheckText(metric.Label, Fail, required: false);
            CheckText(metric.TypeParams, Fail, required: false);
            CheckText(metric.Unit, Fail, required: false);
            CheckText(metric.Format, Fail, required: false);
            CheckMinGroupSize(metric.MinGroupSize, Fail);
            CheckGranularities(metric.AllowedTimeGranularities, Fail);

            foreach (var measure in metric.Measures)
            {
                CheckText(measure, Fail, required: true);
                if (!measureNames.Contains(measure))
                {
                    Fail("MEASURE_REFERENCE");
                }
            }

            foreach (var dimension in metric.Dimensions)
            {
                CheckText(dimension, Fail, required: true);
                var leaf = dimension.Contains("__", StringComparison.Ordinal) ? dimension[(dimension.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : dimension;
                if (!dimensionNames.Contains(dimension) && !dimensionNames.Contains(leaf))
                {
                    Fail("DIMENSION_REFERENCE");
                }
            }

            foreach (var (key, value) in metric.Meta)
            {
                CheckText(key, Fail, required: true);
                CheckText(value, Fail, required: false);
                if (!AllowedPrivacyKeys.Contains(key) && PrivacyKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    Fail("PRIVACY_KEY");
                }
            }

            if (!string.IsNullOrWhiteSpace(metric.Filter))
            {
                CheckFilterTemplate(manifest, metric, baseTables, Fail);
            }
        }

        return errors.Count == 0 ? SemanticManifestValidationResult.Valid : new SemanticManifestValidationResult(false, errors);
    }

    private static void CheckText(string? value, Action<string> fail, bool required)
    {
        if (value is null)
        {
            if (required)
            {
                fail("TEXT_INVALID");
            }

            return;
        }

        if (value.Length > MaxTextLength || (required && value.Length == 0))
        {
            fail("TEXT_INVALID");
            return;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not ('\n' or '\r' or '\t'))
            {
                fail("TEXT_INVALID");
                return;
            }
        }
    }

    private static void CheckMinGroupSize(int? value, Action<string> fail)
    {
        if (value is < 1 or > MaxMinGroupSize)
        {
            fail("MIN_GROUP_SIZE");
        }
    }

    private static void CheckGranularities(IReadOnlyList<string>? granularities, Action<string> fail)
    {
        if (granularities is null)
        {
            return;
        }

        foreach (var g in granularities)
        {
            if (!StandardGranularities.Contains(g ?? string.Empty))
            {
                fail("GRANULARITY");
            }
        }
    }

    private void CheckFilterTemplate(
        DbtSemanticManifest manifest,
        DbtMetric metric,
        IReadOnlyDictionary<string, TableMetadata> baseTables,
        Action<string> fail)
    {
        var raw = metric.Filter!;
        if (raw.Length > _options.MaxFilterTemplateLength)
        {
            fail("TEMPLATE_INVALID");
            return;
        }

        var cleaned = SemanticMetricQueryService.CleanDbtFilterTemplate(raw);
        if (!IsSafeTemplateText(cleaned))
        {
            fail("TEMPLATE_INVALID");
            return;
        }

        DbtSemanticModel model;
        try
        {
            model = SemanticMetricQueryService.ResolveModelAndMeasures(metric, manifest.SemanticModels).Model;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            fail("BASE_TABLE");
            return;
        }

        if (!baseTables.TryGetValue(model.Name, out var table))
        {
            fail("BASE_TABLE");
            return;
        }

        try
        {
            TemplateParser.Parse(_parser, cleaned, table);
        }
        catch (Exception ex) when (ex is PolicyParseException or ArgumentException or InvalidOperationException)
        {
            fail("TEMPLATE_INVALID");
        }
    }

    /// <summary>Structural pre-scan: no statement separator, NUL, control character, comment token or unbalanced quote.</summary>
    internal static bool IsSafeTemplateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains("--", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal) ||
            text.Contains("*/", StringComparison.Ordinal))
        {
            return false;
        }

        var single = false;
        var dbl = false;
        foreach (var ch in text)
        {
            if (ch == '\0' || (char.IsControl(ch) && ch is not (' ' or '\t' or '\r' or '\n')))
            {
                return false;
            }

            if (ch == '\'' && !dbl)
            {
                single = !single;
            }
            else if (ch == '"' && !single)
            {
                dbl = !dbl;
            }
            else if (ch == ';' && !single && !dbl)
            {
                return false;
            }
        }

        return !single && !dbl && !text.Contains(';', StringComparison.Ordinal);
    }

    /// <summary>
    /// The one parse of a metric filter template: policy parser, base-table columns, the row-policy function list, no parameters, no catalog.
    /// Used at import (here) and again at query time so a manifest stored before a schema change still fails closed (SEC-112-16 d).
    /// </summary>
    internal static class TemplateParser
    {
        public static PolicyPredicate Parse(IPolicyExpressionParser parser, string cleanedTemplate, TableMetadata table)
        {
            var target = PolicyParameterNamer.ToTargetDialect(table.Dialect);
            var context = new PolicyParseContext(
                new TableIdentity(table.Table.SchemaName, table.Table.TableName),
                table.Columns.Select(c => c.ColumnName).ToList(),
                SqlFunctionAllowlists.BuildForPolicy(target, null),
                new Dictionary<string, PolicyValue>(),
                CatalogVersion: 0)
            {
                Catalog = null,
                TargetDialect = target
            };
            return parser.Parse(cleanedTemplate, context);
        }
    }
}
