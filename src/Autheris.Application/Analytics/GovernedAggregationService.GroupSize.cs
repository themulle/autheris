// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Analytics;

public sealed partial class GovernedAggregationService
{
    internal static EffectiveMinGroupSize Resolve(
        TableMetadata metadata,
        AggregationQuery query,
        OperatorFloor operatorFloor,
        AnalyticsPrivacyOptions? privacy = null,
        ILogger? logger = null,
        EffectiveProtection? protection = null)
    {
        IReadOnlyDictionary<TableIdentifier, EffectiveProtection>? protections =
            protection is null || metadata is null
                ? null
                : new Dictionary<TableIdentifier, EffectiveProtection> { [metadata.Identifier] = protection };
        return Resolve([metadata!], query, operatorFloor, privacy, logger, protections);
    }

    internal static EffectiveMinGroupSize Resolve(
        IReadOnlyList<TableMetadata> tables,
        AggregationQuery query,
        OperatorFloor operatorFloor,
        AnalyticsPrivacyOptions? privacy = null,
        ILogger? logger = null,
        IReadOnlyDictionary<TableIdentifier, EffectiveProtection>? protections = null)
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

                EffectiveProtection? tableProtection = null;
                protections?.TryGetValue(table.Identifier, out tableProtection);
                if (!IsCatalogLoweringEligible(table, c.Value, privacy, logger, tableProtection))
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
        ILogger? logger,
        EffectiveProtection? protection = null)
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

            if (protection is not null)
            {
                if (protection.TableClass.NormalizeOrConfidential() > ProtectionClass.Internal)
                {
                    logger?.LogDebug("Lowering refused for table {Table}: protection class {Class} exceeds INTERNAL", metadata.Table.TableName, protection.TableClass);
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(protection.TableMaskCategory))
                {
                    logger?.LogDebug("Lowering refused for table {Table}: table mask category present", metadata.Table.TableName);
                    return false;
                }
            }

            foreach (var col in metadata.Columns)
            {
                if (protection is not null && protection.Columns.TryGetValue(col.ColumnName, out var cp) &&
                    (cp.EffectiveClass.NormalizeOrConfidential() > ProtectionClass.Internal || !string.IsNullOrWhiteSpace(cp.EffectiveMaskCategory)))
                {
                    logger?.LogDebug("Lowering refused for table {Table}: protected or masked columns present", metadata.Table.TableName);
                    return false;
                }

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

    private async Task<EffectiveMinGroupSize> ResolveEffectiveMinGroupSizeAsync(
        TenantId tenantId,
        AggregationQuery query,
        TableMetadata metadata,
        CancellationToken ct)
    {
        EffectiveProtection? protection = null;
        if (_protectionProvider is not null)
        {
            try
            {
                protection = await _protectionProvider.GetAsync(tenantId, metadata.Identifier, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Fail closed: deny catalog lowering when protection metadata is unavailable.
                protection = new EffectiveProtection(
                    new ContainerRef(ContainerKind.Domain, Guid.Empty),
                    null,
                    ProtectionClass.Confidential,
                    ProtectionClass.Confidential,
                    null,
                    new Dictionary<string, ColumnProtection>(),
                    CatalogVisibilityMode.OnRequest);
            }
        }

        return ResolveEffectiveMinGroupSize(query, metadata, protection);
    }

    private EffectiveMinGroupSize ResolveEffectiveMinGroupSize(AggregationQuery query, TableMetadata metadata, EffectiveProtection? protection = null)
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
        return Resolve(metadata, query, new OperatorFloor(resolved, explicitOverride), _analyticsOptions.Privacy, _logger, protection);
    }
}
