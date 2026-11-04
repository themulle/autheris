// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Analytics;

public sealed partial class GovernedAggregationService
{
    internal static EffectiveMinGroupSize Resolve(
        TableMetadata metadata,
        AggregationQuery query,
        OperatorFloor operatorFloor,
        AnalyticsPrivacyOptions? privacy = null,
        ILogger? logger = null)
    {
        return Resolve([metadata], query, operatorFloor, privacy, logger);
    }

    internal static EffectiveMinGroupSize Resolve(
        IReadOnlyList<TableMetadata> tables,
        AggregationQuery query,
        OperatorFloor operatorFloor,
        AnalyticsPrivacyOptions? privacy = null,
        ILogger? logger = null)
    {
        if (tables is null || tables.Count == 0)
        {
            int defaultVal = Math.Max(1, operatorFloor.Value);
            return new EffectiveMinGroupSize(defaultVal, operatorFloor.IsExplicit ? MinGroupSizeSource.Operator : MinGroupSizeSource.Default);
        }

        privacy ??= new AnalyticsPrivacyOptions();
        int floor = operatorFloor.Value;
        var source = operatorFloor.IsExplicit ? MinGroupSizeSource.Operator : MinGroupSizeSource.Default;
        bool operatorExplicit = operatorFloor.IsExplicit;

        // 1. Check if any catalog value raises the floor
        foreach (var table in tables)
        {
            if (table.EffectiveMinGroupSize is { } c && c > floor)
            {
                floor = c;
                source = MinGroupSizeSource.Catalog;
            }
        }

        // 2. Check catalog lowering when no explicit operator floor was set
        if (!operatorExplicit && floor == operatorFloor.Value)
        {
            bool allEligible = true;
            int? minCatalogValue = null;

            foreach (var table in tables)
            {
                var c = table.EffectiveMinGroupSize;
                if (!c.HasValue || c.Value >= floor)
                {
                    allEligible = false;
                    break;
                }

                if (!IsCatalogLoweringEligible(table, c.Value, privacy, logger))
                {
                    allEligible = false;
                    break;
                }

                minCatalogValue = minCatalogValue.HasValue
                    ? Math.Max(minCatalogValue.Value, c.Value)
                    : c.Value;
            }

            if (allEligible && minCatalogValue.HasValue && minCatalogValue.Value < floor)
            {
                floor = minCatalogValue.Value;
                source = MinGroupSizeSource.Catalog;
            }
        }

        // 3. Request override may only raise
        if (query?.MinGroupSizeOverride is { } req && req > floor)
        {
            floor = req;
            source = MinGroupSizeSource.Request;
        }

        return new EffectiveMinGroupSize(Math.Max(1, floor), source);
    }

    private static bool IsCatalogLoweringEligible(
        TableMetadata metadata,
        int candidateFloor,
        AnalyticsPrivacyOptions? privacy,
        ILogger? logger)
    {
        try
        {
            if (privacy?.AllowCatalogLowering != true)
            {
                logger?.LogDebug("Lowering refused for table {Table}: capability disabled", metadata.Table.TableName);
                return false;
            }

            if (metadata.NonPersonal == false)
            {
                logger?.LogDebug("Lowering refused for table {Table}: governance overlay NonPersonal is false", metadata.Table.TableName);
                return false;
            }

            if (metadata.NonPersonal == true)
            {
                // Governance overlay explicitly marks table non-personal
            }
            else if (metadata.Table.NonPersonal)
            {
                if (privacy.AllowSourceDeclaredLowering != true)
                {
                    logger?.LogDebug("Lowering refused for table {Table}: source-declared NonPersonal requires AllowSourceDeclaredLowering", metadata.Table.TableName);
                    return false;
                }
            }
            else
            {
                logger?.LogDebug("Lowering refused for table {Table}: not non-personal", metadata.Table.TableName);
                return false;
            }

            int minFloor = privacy.CatalogMinGroupSizeFloor;
            if (candidateFloor < minFloor)
            {
                logger?.LogDebug("Lowering refused for table {Table}: below catalog floor ceiling", metadata.Table.TableName);
                return false;
            }

            if (metadata.Columns is null || metadata.Columns.Count == 0)
            {
                logger?.LogDebug("Lowering refused for table {Table}: columns unloaded or empty", metadata.Table.TableName);
                return false;
            }

            if (metadata.Table.IsHighlySensitive)
            {
                logger?.LogDebug("Lowering refused for table {Table}: highly sensitive", metadata.Table.TableName);
                return false;
            }

            if (metadata.Table.Tags is { Count: > 0 } tags)
            {
                foreach (var tag in tags)
                {
                    if (tag.Contains("pii", StringComparison.OrdinalIgnoreCase) ||
                        tag.Contains("sensitive", StringComparison.OrdinalIgnoreCase))
                    {
                        logger?.LogDebug("Lowering refused for table {Table}: table-level sensitivity tags present", metadata.Table.TableName);
                        return false;
                    }
                }
            }

            foreach (var col in metadata.Columns)
            {
                if (TableAccessDecision.IsCatalogSensitive(col.ColumnName, metadata))
                {
                    logger?.LogDebug("Lowering refused for table {Table}: sensitive columns present", metadata.Table.TableName);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Lowering refused for table {Table}: exception during evaluation", metadata.Table?.TableName ?? "<unknown>");
            return false;
        }
    }

    internal static int ResolveEffectiveMinGroupSize(TableMetadata metadata, AggregationQuery query, int defaultMinGroupSize = 1)
    {
        return Resolve(metadata, query, new OperatorFloor(defaultMinGroupSize, false)).Value;
    }

    private EffectiveMinGroupSize ResolveEffectiveMinGroupSize(AggregationQuery query, TableMetadata metadata)
    {
        int resolved = _hasExplicitAnalyticsOptions
            ? _analyticsOptions.Privacy.DefaultMinGroupSize
            : 1;
        bool explicitOverride = false;

        foreach (var candidateKey in CandidateTableKeys(query.Table))
        {
            if (_analyticsOptions.TableMinimumGroupSizes.TryGetValue(candidateKey, out var tableMin))
            {
                resolved = tableMin;
                explicitOverride = true;
                break;
            }
        }

        foreach (var candidateKey in CandidateTableKeys(query.Table))
        {
            if (_analyticsOptions.Privacy.Datasets.TryGetValue(candidateKey, out var dsOverride) && dsOverride.MinGroupSize.HasValue)
            {
                resolved = dsOverride.MinGroupSize.Value;
                explicitOverride = true;
                break;
            }
        }

        // Issue #448: Catalog-owned minimum group size resolution with lowering support for non-personal tables.
        return Resolve(metadata, query, new OperatorFloor(resolved, explicitOverride), _analyticsOptions.Privacy, _logger);
    }
}
