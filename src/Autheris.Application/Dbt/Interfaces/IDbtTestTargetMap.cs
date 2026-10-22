namespace Autheris.Application.Dbt.Interfaces;

using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// EXT-08: In-memory, per-tenant map resolving dbt test and model unique IDs to full <see cref="TableIdentifier"/>s.
/// Populated by manifest sync.
/// </summary>
public interface IDbtTestTargetMap
{
    void Replace(
        TenantId tenant,
        IReadOnlyDictionary<string, TableIdentifier> modelToTable,
        IReadOnlyDictionary<string, IReadOnlyList<string>> testToModels);

    bool TryResolveTest(TenantId tenant, string testUniqueId, out IReadOnlyList<TableIdentifier> tables);

    bool TryResolveModel(TenantId tenant, string modelUniqueId, out TableIdentifier table);

    IReadOnlyDictionary<string, TableIdentifier> GetKnownModels(TenantId tenant);
}
