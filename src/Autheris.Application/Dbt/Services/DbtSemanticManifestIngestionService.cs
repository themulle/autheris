namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class DbtSemanticManifestIngestionService : IDbtSemanticManifestIngestionService
{
    private readonly ISemanticCatalogService _catalogService;
    private readonly ILogger<DbtSemanticManifestIngestionService> _logger;
    private readonly ITableMetadataRepository? _tableRepository;

    public DbtSemanticManifestIngestionService(
        ISemanticCatalogService catalogService,
        ILogger<DbtSemanticManifestIngestionService> logger,
        ITableMetadataRepository? tableRepository = null)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tableRepository = tableRepository;
    }

    public Task<DbtSemanticManifest> ParseAndIngestAsync(string manifestJson, CancellationToken cancellationToken = default) =>
        ParseAndIngestAsync(manifestJson, TenantId.LegacySingleTenant, cancellationToken);

    public async Task<DbtSemanticManifest> ParseAndIngestAsync(string manifestJson, TenantId tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);

        using var doc = JsonDocument.Parse(manifestJson);
        var root = doc.RootElement;

        var semanticModels = new List<DbtSemanticModel>();
        if (root.TryGetProperty("semantic_models", out var modelsElem) && modelsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in modelsElem.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var desc = m.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var nodeRel = m.TryGetProperty("node_relation", out var nr) && nr.TryGetProperty("relation_name", out var rn)
                    ? rn.GetString() ?? ""
                    : "";

                var entities = new List<DbtEntity>();
                if (m.TryGetProperty("entities", out var entitiesElem) && entitiesElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in entitiesElem.EnumerateArray())
                    {
                        var eName = e.TryGetProperty("name", out var en) ? en.GetString() ?? "" : "";
                        var eType = e.TryGetProperty("type", out var et) ? et.GetString() ?? "" : "";
                        var eRole = e.TryGetProperty("role", out var er) ? er.GetString() : null;
                        entities.Add(new DbtEntity(eName, eType, eRole));
                    }
                }

                var measures = new List<DbtMeasure>();
                if (m.TryGetProperty("measures", out var measuresElem) && measuresElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ms in measuresElem.EnumerateArray())
                    {
                        var msName = ms.TryGetProperty("name", out var msn) ? msn.GetString() ?? "" : "";
                        var agg = ms.TryGetProperty("agg", out var ag) ? ag.GetString() ?? "sum" : "sum";
                        var expr = ms.TryGetProperty("expr", out var ex) ? ex.GetString() : null;
                        var msDesc = ms.TryGetProperty("description", out var msd) ? msd.GetString() : null;
                        string? msUnit = ms.TryGetProperty("unit", out var msu) && msu.ValueKind == JsonValueKind.String ? msu.GetString() : null;
                        int? msMinGroupSize = ms.TryGetProperty("min_group_size", out var msmg) && msmg.TryGetInt32(out var msmgVal) ? msmgVal : null;
                        measures.Add(new DbtMeasure(msName, agg, expr, msDesc, msUnit, msMinGroupSize));
                    }
                }

                var dimensions = new List<DbtDimension>();
                if (m.TryGetProperty("dimensions", out var dimsElem) && dimsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dim in dimsElem.EnumerateArray())
                    {
                        var dimName = dim.TryGetProperty("name", out var dn) ? dn.GetString() ?? "" : "";
                        var dimType = dim.TryGetProperty("type", out var dt) ? dt.GetString() ?? "categorical" : "categorical";
                        var dimDesc = dim.TryGetProperty("description", out var dd) ? dd.GetString() : null;
                        var isPart = dim.TryGetProperty("is_partition", out var ip) && ip.GetBoolean();
                        string? dimTimeGranularity = null;
                        if (dim.TryGetProperty("type_params", out var dtp) && dtp.ValueKind == JsonValueKind.Object &&
                            dtp.TryGetProperty("time_granularity", out var dtg) && dtg.ValueKind == JsonValueKind.String)
                        {
                            dimTimeGranularity = dtg.GetString();
                        }
                        dimensions.Add(new DbtDimension(dimName, dimType, dimDesc, isPart, TimeGranularity: dimTimeGranularity));
                    }
                }

                semanticModels.Add(new DbtSemanticModel(name, desc, nodeRel, entities, measures, dimensions));
            }
        }

        string manifestVersion = "1.0";
        if (root.TryGetProperty("version", out var vElem) && vElem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(vElem.GetString()))
        {
            manifestVersion = vElem.GetString()!;
        }
        else if (root.TryGetProperty("project_configuration", out var pcElem) && pcElem.ValueKind == JsonValueKind.Object &&
                 pcElem.TryGetProperty("version", out var pcvElem) && pcvElem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pcvElem.GetString()))
        {
            manifestVersion = pcvElem.GetString()!;
        }

        var metrics = new List<DbtMetric>();
        if (root.TryGetProperty("metrics", out var metricsElem) && metricsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var mt in metricsElem.EnumerateArray())
            {
                var name = mt.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : "";
                var type = mt.TryGetProperty("type", out var ty) ? ty.GetString() ?? "simple" : "simple";
                var desc = mt.TryGetProperty("description", out var md) ? md.GetString() : null;
                var label = mt.TryGetProperty("label", out var ml) ? ml.GetString() : null;
                var filter = mt.TryGetProperty("filter", out var mf)
                    ? (mf.ValueKind == JsonValueKind.String ? mf.GetString() : mf.ToString())
                    : null;

                var dims = new List<string>();
                if (mt.TryGetProperty("dimensions", out var de) && de.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in de.EnumerateArray())
                    {
                        if (d.GetString() is { } ds) dims.Add(ds);
                    }
                }

                string? typeParamsExpr = null;
                var measures = new List<string>();
                if (mt.TryGetProperty("type_params", out var tp) && tp.ValueKind == JsonValueKind.Object)
                {
                    if (tp.TryGetProperty("expr", out var tpExpr) && tpExpr.ValueKind == JsonValueKind.String)
                    {
                        typeParamsExpr = tpExpr.GetString();
                    }

                    if (tp.TryGetProperty("measure", out var meas))
                    {
                        if (meas.ValueKind == JsonValueKind.String && meas.GetString() is { } msStr)
                        {
                            measures.Add(msStr);
                        }
                        else if (meas.ValueKind == JsonValueKind.Object && meas.TryGetProperty("name", out var mnProp) && mnProp.GetString() is { } mnpStr)
                        {
                            measures.Add(mnpStr);
                        }
                    }

                    if (tp.TryGetProperty("measures", out var measArr) && measArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var mItem in measArr.EnumerateArray())
                        {
                            if (mItem.ValueKind == JsonValueKind.String && mItem.GetString() is { } mStr)
                            {
                                measures.Add(mStr);
                            }
                            else if (mItem.ValueKind == JsonValueKind.Object && mItem.TryGetProperty("name", out var mName) && mName.GetString() is { } mnStr)
                            {
                                measures.Add(mnStr);
                            }
                        }
                    }

                    if (tp.TryGetProperty("numerator", out var numProp))
                    {
                        var numName = numProp.ValueKind == JsonValueKind.String
                            ? numProp.GetString()
                            : numProp.ValueKind == JsonValueKind.Object && numProp.TryGetProperty("name", out var nn) ? nn.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(numName))
                        {
                            measures.Add(numName!);
                        }
                    }

                    if (tp.TryGetProperty("denominator", out var denProp))
                    {
                        var denName = denProp.ValueKind == JsonValueKind.String
                            ? denProp.GetString()
                            : denProp.ValueKind == JsonValueKind.Object && denProp.TryGetProperty("name", out var dn) ? dn.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(denName))
                        {
                            measures.Add(denName!);
                        }
                    }
                }

                var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (mt.TryGetProperty("meta", out var metaElem) && metaElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in metaElem.EnumerateObject())
                    {
                        meta[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();
                    }
                }

                int? metricMinGroupSize = null;
                if (mt.TryGetProperty("min_group_size", out var mgProp) && mgProp.TryGetInt32(out var mgVal))
                {
                    metricMinGroupSize = mgVal;
                }
                else if (meta.TryGetValue("min_group_size", out var metaMg) && int.TryParse(metaMg, out var metaMgVal))
                {
                    metricMinGroupSize = metaMgVal;
                }

                IReadOnlyList<string>? allowedGranularities = null;
                if (mt.TryGetProperty("allowed_time_granularities", out var atgProp) && atgProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var g in atgProp.EnumerateArray())
                    {
                        if (g.ValueKind == JsonValueKind.String && g.GetString() is { } gs)
                        {
                            list.Add(gs);
                        }
                    }
                    allowedGranularities = list;
                }

                var unit = mt.TryGetProperty("unit", out var uProp) && uProp.ValueKind == JsonValueKind.String
                    ? uProp.GetString()
                    : meta.TryGetValue("unit", out var metaUnit) ? metaUnit : null;

                metrics.Add(new DbtMetric(
                    name,
                    type,
                    desc,
                    label,
                    filter,
                    typeParamsExpr,
                    dims,
                    measures,
                    meta,
                    Unit: unit,
                    MinGroupSize: metricMinGroupSize,
                    AllowedTimeGranularities: allowedGranularities,
                    Version: manifestVersion));
            }
        }

        var manifest = new DbtSemanticManifest(semanticModels, metrics, manifestVersion);
        await _catalogService.RegisterManifestAsync(manifest, tenantId, cancellationToken).ConfigureAwait(false);

        if (_tableRepository != null)
        {
            foreach (var model in semanticModels)
            {
                if (string.IsNullOrWhiteSpace(model.Name))
                {
                    continue;
                }

                var columns = new List<TableColumn>();
                var primaryKeys = new List<string>();
                var seenCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entity in model.Entities)
                {
                    if (string.IsNullOrWhiteSpace(entity.Name) || !seenCols.Add(entity.Name))
                    {
                        continue;
                    }

                    columns.Add(new TableColumn
                    {
                        ColumnName = entity.Name,
                        DataType = "varchar",
                        Description = entity.Role ?? $"Entity ({entity.Type})",
                        DocumentationSource = "dbt"
                    });

                    if (string.Equals(entity.Type, "primary", StringComparison.OrdinalIgnoreCase))
                    {
                        primaryKeys.Add(entity.Name);
                    }
                }

                foreach (var dim in model.Dimensions)
                {
                    if (string.IsNullOrWhiteSpace(dim.Name) || !seenCols.Add(dim.Name))
                    {
                        continue;
                    }

                    columns.Add(new TableColumn
                    {
                        ColumnName = dim.Name,
                        DataType = string.Equals(dim.Type, "time", StringComparison.OrdinalIgnoreCase) ? "timestamp" : "varchar",
                        Description = dim.Description,
                        DocumentationSource = "dbt"
                    });
                }

                foreach (var measure in model.Measures)
                {
                    if (string.IsNullOrWhiteSpace(measure.Name) || !seenCols.Add(measure.Name))
                    {
                        continue;
                    }

                    columns.Add(new TableColumn
                    {
                        ColumnName = measure.Name,
                        DataType = measure.Aggregation.Contains("count", StringComparison.OrdinalIgnoreCase) ? "bigint" : "decimal",
                        Description = measure.Description,
                        DocumentationSource = "dbt"
                    });
                }

                if (primaryKeys.Count == 0 && columns.Count > 0)
                {
                    primaryKeys.Add(columns[0].ColumnName);
                }

                var tableMeta = new TableMetadata
                {
                    Identifier = new TableIdentifier("semantic", "semantic", model.Name),
                    Table = new Table
                    {
                        SchemaName = "semantic",
                        TableName = model.Name,
                        DisplayName = model.Name,
                        Description = model.Description,
                        DocumentationSource = "dbt",
                        IsActive = true,
                        Tags = ["dbt", "semantic-model"]
                    },
                    Columns = columns,
                    PrimaryKeyColumns = primaryKeys
                };

                await _tableRepository.UpsertTableMetadataAsync(tableMeta, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Successfully ingested dbt semantic manifest with {ModelCount} models and {MetricCount} metrics",
            semanticModels.Count, metrics.Count);

        return manifest;
    }
}
