namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed class DbtTestTargetMap : IDbtTestTargetMap
{
    private sealed record TenantMapping(
        Dictionary<string, TableIdentifier> ModelToTable,
        Dictionary<string, IReadOnlyList<string>> TestToModels
    );

    private readonly ConcurrentDictionary<string, TenantMapping> _tenantMaps = new(StringComparer.OrdinalIgnoreCase);

    public void Replace(
        TenantId tenant,
        IReadOnlyDictionary<string, TableIdentifier> modelToTable,
        IReadOnlyDictionary<string, IReadOnlyList<string>> testToModels)
    {
        var modelDict = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in modelToTable)
        {
            modelDict[k] = v;
        }

        var testDict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in testToModels)
        {
            testDict[k] = v;
        }

        _tenantMaps[tenant.Value] = new TenantMapping(modelDict, testDict);
    }

    public bool TryResolveTest(TenantId tenant, string testUniqueId, out IReadOnlyList<TableIdentifier> tables)
    {
        tables = Array.Empty<TableIdentifier>();
        if (!_tenantMaps.TryGetValue(tenant.Value, out var mapping))
        {
            return false;
        }

        if (mapping.TestToModels.TryGetValue(testUniqueId, out var modelIds))
        {
            var resolved = new List<TableIdentifier>();
            foreach (var mId in modelIds)
            {
                if (mapping.ModelToTable.TryGetValue(mId, out var tid))
                {
                    if (!resolved.Contains(tid))
                    {
                        resolved.Add(tid);
                    }
                }
            }

            if (resolved.Count > 0)
            {
                tables = resolved;
                return true;
            }
        }

        return false;
    }

    public bool TryResolveModel(TenantId tenant, string modelUniqueId, out TableIdentifier table)
    {
        table = default;
        if (!_tenantMaps.TryGetValue(tenant.Value, out var mapping))
        {
            return false;
        }

        if (mapping.ModelToTable.TryGetValue(modelUniqueId, out table))
        {
            return true;
        }

        var parts = modelUniqueId.Split('.');
        if (parts.Length > 0 && mapping.ModelToTable.TryGetValue(parts[^1], out table))
        {
            return true;
        }

        return false;
    }

    public IReadOnlyDictionary<string, TableIdentifier> GetKnownModels(TenantId tenant)
    {
        if (_tenantMaps.TryGetValue(tenant.Value, out var mapping))
        {
            return mapping.ModelToTable;
        }

        return new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase);
    }
}
