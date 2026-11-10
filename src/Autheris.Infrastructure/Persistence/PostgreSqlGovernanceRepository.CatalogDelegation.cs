namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public partial class PostgreSqlGovernanceRepository : IPhysicalIndexRepository
{
    private GovernanceRepository Gov
    {
        get
        {
            ThrowIfNotReady();
            return _governanceRepository;
        }
    }

    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table,
        CancellationToken ct = default) =>
        Gov.GetTableMetadataAsync(table, ct);

    public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
        Gov.GetAllTablesAsync(ct);

    public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata,
        CancellationToken ct = default) =>
        Gov.UpsertTableMetadataAsync(metadata, ct);

    public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
        CancellationToken ct = default) =>
        Gov.ReplaceTableKeyDefinitionAsync(table, primaryKey, indexes, primaryKeyDirections, ct);

    public Task<RowFilterEvaluationChange?> SetRowFilterEvaluationAsync(
        TableIdentifier table,
        RowFilterEvaluationSettings? settings,
        Sid actor,
        CancellationToken ct = default) =>
        Gov.SetRowFilterEvaluationAsync(table, settings, actor, ct);

    public Task<DatasetVisibilityCounts> GetDatasetVisibilityCountsAsync(CancellationToken ct = default) =>
        Gov.GetDatasetVisibilityCountsAsync(ct);

    public Task<DatasetCatalogSettings?> GetDatasetCatalogSettingsAsync(
        TableIdentifier table,
        bool strict = true,
        CancellationToken ct = default) =>
        Gov.GetDatasetCatalogSettingsAsync(table, strict, ct);

    public Task<DatasetCatalogChange?> ReplaceDatasetTagsAsync(
        TableIdentifier table,
        IReadOnlyList<string> tags,
        DatasetTagSource source,
        Sid actor,
        CancellationToken ct = default) =>
        Gov.ReplaceDatasetTagsAsync(table, tags, source, actor, ct);

    public Task<DatasetCatalogChange?> ChangeDatasetTagsAsync(
        TableIdentifier table,
        IReadOnlyList<string> add,
        IReadOnlyList<string> remove,
        Sid actor,
        CancellationToken ct = default) =>
        Gov.ChangeDatasetTagsAsync(table, add, remove, actor, ct);

    public Task<DatasetCatalogChange?> SetDatasetVisibilityAsync(
        TableIdentifier table,
        DatasetVisibility visibility,
        Sid actor,
        CancellationToken ct = default) =>
        Gov.SetDatasetVisibilityAsync(table, visibility, actor, ct);

    public Task<DatasetCatalogChange?> SetDatasetPrivacyAsync(
        TableIdentifier table,
        int? minGroupSize,
        bool? nonPersonal,
        Sid actor,
        CancellationToken ct = default) =>
        Gov.SetDatasetPrivacyAsync(table, minGroupSize, nonPersonal, actor, ct);

    public Task<DatasetCatalogChange?> SetDatasetDiscoveryAsync(
        TableIdentifier table,
        bool? discoveryHidden,
        bool? samplesDisabled,
        IReadOnlyList<string>? hiddenColumns,
        Sid actor,
        CancellationToken ct = default) =>
        Gov.SetDatasetDiscoveryAsync(table, discoveryHidden, samplesDisabled, hiddenColumns, actor, ct);

    public Task<DiscoveryArtifacts?> GetDiscoveryArtifactsAsync(Guid tableId, CancellationToken ct = default) =>
        Gov.GetDiscoveryArtifactsAsync(tableId, ct);

    public Task<bool> UpsertDiscoveryArtifactsAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default) =>
        Gov.UpsertDiscoveryArtifactsAsync(artifacts, actor, ct);

    public Task<bool> DeleteDiscoveryArtifactsAsync(Guid tableId, Sid actor, CancellationToken ct = default) =>
        Gov.DeleteDiscoveryArtifactsAsync(tableId, actor, ct);

    public Task<TableIdentifier?> GetActiveTableIdentifierAsync(TableIdentifier table,
        CancellationToken ct = default) =>
        Gov.GetActiveTableIdentifierAsync(table, ct);

    public Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(Guid tableId,
        CancellationToken ct = default) =>
        Gov.GetActiveTableIdentifierByIdAsync(tableId, ct);

    public Task<long> GetTableEpochAsync(TableIdentifier table,
        CancellationToken ct = default) =>
        Gov.GetTableEpochAsync(table, ct);

    public Task<long> IncrementTableEpochAsync(TableIdentifier table,
        CancellationToken ct = default) =>
        Gov.IncrementTableEpochAsync(table, ct);

    public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable,
        CancellationToken ct = default) =>
        Gov.GetRelationsForTableAsync(parentTable, ct);

    public Task<RelationWriteResult> CreateRelationAsync(TableRelation relation,
        CancellationToken ct = default) =>
        Gov.CreateRelationAsync(relation, ct);

    public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table,
        CancellationToken ct = default) =>
        Gov.GetDataOwnersForTableAsync(table, ct);

    public Task<bool> IsAuthorizedApproverForTableAsync(
        TableIdentifier table,
        Sid approverSid,
        CancellationToken ct = default) =>
        Gov.IsAuthorizedApproverForTableAsync(table, approverSid, ct);

    public Task<bool> IsAuthorizedApproverForTableAsync(
        TableIdentifier table,
        Sid approverSid,
        string? itsmApproverAccount,
        CancellationToken ct = default) =>
        Gov.IsAuthorizedApproverForTableAsync(table, approverSid, itsmApproverAccount, ct);

    public Task<IReadOnlySet<string>> GetTransitiveRolesAsync(Sid subjectSid,
        CancellationToken ct = default) =>
        Gov.GetTransitiveRolesAsync(subjectSid, ct);

    public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation,
        CancellationToken ct = default) =>
        Gov.DelegateDataOwnershipAsync(delegation, ct);

    public Task<bool> SetRelationCrossDataSourceOptInAsync(
        Guid relationId,
        bool allowCrossDataSource,
        CancellationToken ct = default) =>
        Gov.SetRelationCrossDataSourceOptInAsync(relationId, allowCrossDataSource, ct);

    public Task<Autheris.Application.Catalog.CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) =>
        Gov.GetCatalogVersionAsync(ct);

    public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default) =>
        Gov.GetAllRelationsAsync(ct);

    public Task<bool> ReplacePhysicalIndexesAsync(
        TableIdentifier table,
        IReadOnlyList<PhysicalIndexDefinition> indexes,
        CancellationToken ct = default) =>
        Gov.ReplacePhysicalIndexesAsync(table, indexes, ct);

}
