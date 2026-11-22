namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#674: an upsert that leaves every persisted field as it was bumps no epoch and no catalog version.</summary>
[Trait("Category", "Security")]
public sealed class CatalogNoOpRepositoryTests : IDisposable
{
    private readonly PostgreSqlGovernanceRepository _repo = GovernanceTestDatabase.CreateRepository(Substitute.For<IEpochValidationService>());
    private readonly TableIdentifier _identifier = new("sales", "crm", "noop_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() => _repo.Dispose();

    private TableMetadata Build(string description = "Customers", IReadOnlyDictionary<string, MaskingRule>? masks = null)
    {
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Table = new Table
            {
                Id = tableId,
                SourceType = "PostgreSQL",
                SourceName = _identifier.Domain,
                SchemaName = _identifier.Schema,
                TableName = _identifier.TableName,
                DisplayName = "Customers",
                Description = description,
                IsActive = true
            },
            Identifier = _identifier,
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "id", DataType = "TEXT" },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "name", DataType = "TEXT" }
            ],
            ColumnMaskingRules = masks ?? new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase),
            PrimaryKeyColumns = ["id"]
        };
    }

    [Fact]
    public async Task UpsertUnchangedTable_NoEpochNoVersionNoEvent()
    {
        var created = await _repo.UpsertTableMetadataWithResultAsync(Build());
        created.Kind.ShouldBe(CatalogUpsertKind.Created);
        var epoch = await _repo.GetTableEpochAsync(_identifier);
        var version = await _repo.GetCatalogVersionAsync();

        var again = await _repo.UpsertTableMetadataWithResultAsync(Build());

        again.Kind.ShouldBe(CatalogUpsertKind.Unchanged);
        again.Classification.ShouldBe(CatalogWriteClassification.Unchanged);
        (await _repo.GetTableEpochAsync(_identifier)).ShouldBe(epoch);
        (await _repo.GetCatalogVersionAsync()).ShouldBe(version);
    }

    [Fact]
    public async Task DescriptionChange_IsAdditive_AndBumps()
    {
        await _repo.UpsertTableMetadataWithResultAsync(Build());
        var epoch = await _repo.GetTableEpochAsync(_identifier);
        var version = await _repo.GetCatalogVersionAsync();

        var changed = await _repo.UpsertTableMetadataWithResultAsync(Build(description: "Changed"));

        changed.Kind.ShouldBe(CatalogUpsertKind.Updated);
        changed.Classification.ShouldBe(CatalogWriteClassification.Additive);
        (await _repo.GetTableEpochAsync(_identifier)).ShouldBeGreaterThan(epoch);
        (await _repo.GetCatalogVersionAsync()).ShouldNotBe(version);
    }

    [Fact]
    public async Task MaskingRuleAdded_IsRestrictive_AndBumps()
    {
        await _repo.UpsertTableMetadataWithResultAsync(Build());
        var epoch = await _repo.GetTableEpochAsync(_identifier);

        var masked = await _repo.UpsertTableMetadataWithResultAsync(Build(masks: new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new MaskingRule { RuleType = "REDACT" }
        }));

        masked.Kind.ShouldBe(CatalogUpsertKind.Updated);
        masked.Classification.ShouldBe(CatalogWriteClassification.Restrictive);
        (await _repo.GetTableEpochAsync(_identifier)).ShouldBeGreaterThan(epoch);
        masked.Metadata.ColumnMaskingRules.ShouldContainKey("name");
    }

    [Fact]
    public async Task CachedStaleCopy_DoesNotCauseFalseUnchanged()
    {
        await _repo.UpsertTableMetadataWithResultAsync(Build());
        (await _repo.GetTableMetadataAsync(_identifier)).ShouldNotBeNull(); // warms the metadata cache with the unmasked state

        var masked = await _repo.UpsertTableMetadataWithResultAsync(Build(masks: new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new MaskingRule { RuleType = "REDACT" }
        }));

        masked.Kind.ShouldBe(CatalogUpsertKind.Updated);
        masked.Metadata.ColumnMaskingRules.ShouldContainKey("name");
    }

    [Fact]
    public async Task DocumentationPrecedence_LowerSourceDoesNotOverwriteOrBump()
    {
        var manual = Build(description: "Manual description");
        manual = manual with { Table = manual.Table with { DocumentationSource = "Manual" } };
        await _repo.UpsertTableMetadataWithResultAsync(manual);
        var epoch = await _repo.GetTableEpochAsync(_identifier);
        var version = await _repo.GetCatalogVersionAsync();

        var dbtAttempt = Build(description: "dbt description");
        dbtAttempt = dbtAttempt with { Table = dbtAttempt.Table with { DocumentationSource = "dbt" } };
        var result = await _repo.UpsertTableMetadataWithResultAsync(dbtAttempt);

        result.Kind.ShouldBe(CatalogUpsertKind.Unchanged);
        result.Classification.ShouldBe(CatalogWriteClassification.Unchanged);
        result.Metadata.Table.Description.ShouldBe("Manual description");
        (await _repo.GetTableEpochAsync(_identifier)).ShouldBe(epoch);
        (await _repo.GetCatalogVersionAsync()).ShouldBe(version);
    }


    [Fact]
    public async Task CreateRelationAsync_IdenticalRelation_DoesNotBumpVersion()
    {
        var parentId = _identifier;
        var childId = new TableIdentifier("sales", "crm", "child_" + Guid.NewGuid().ToString("N")[..8]);
        await _repo.UpsertTableMetadataWithResultAsync(Build());
        await _repo.UpsertTableMetadataWithResultAsync(new TableMetadata
        {
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceType = "PostgreSQL",
                SourceName = childId.Domain,
                SchemaName = childId.Schema,
                TableName = childId.TableName,
                DisplayName = "Child",
                IsActive = true
            },
            Identifier = childId,
            Columns = [new TableColumn { ColumnName = "id", DataType = "TEXT" }],
            PrimaryKeyColumns = ["id"]
        });

        var relation = new TableRelation
        {
            Id = Guid.NewGuid(),
            ParentTableIdentifier = parentId,
            ChildTableIdentifier = childId,
            RelationName = "fk_parent",
            Cardinality = RelationCardinality.OneToMany,
            JoinKeysParent = ["id"],
            JoinKeysChild = ["id"]
        };

        var created = await _repo.CreateRelationAsync(relation);
        created.Created.ShouldBeTrue();
        var versionAfterCreate = await _repo.GetCatalogVersionAsync();

        var second = await _repo.CreateRelationAsync(relation);
        second.StoredId.ShouldBe(created.StoredId);
        second.Created.ShouldBeFalse();
        second.KeysChanged.ShouldBeFalse();
        (await _repo.GetCatalogVersionAsync()).ShouldBe(versionAfterCreate);

        var updatedCardinality = await _repo.CreateRelationAsync(new TableRelation
        {
            Id = relation.Id,
            ParentTableIdentifier = parentId,
            ChildTableIdentifier = childId,
            RelationName = "fk_parent",
            Cardinality = RelationCardinality.OneToOne,
            JoinKeysParent = ["id"],
            JoinKeysChild = ["id"]
        });
        updatedCardinality.Created.ShouldBeFalse();
        (await _repo.GetCatalogVersionAsync()).ShouldNotBe(versionAfterCreate);
    }

    [Fact]
    public async Task SetDatasetVisibility_HiddenFlagChangeOnly_BumpsVersion()
    {
        await _repo.UpsertTableMetadataWithResultAsync(Build());
        var actor = new Sid("S-1-5-21-admin");
        await _repo.SetDatasetDiscoveryAsync(_identifier, true, null, null, actor);
        var afterFull = await _repo.GetCatalogVersionAsync();

        var unchanged = await _repo.SetDatasetDiscoveryAsync(_identifier, true, null, null, actor);
        unchanged.ShouldNotBeNull();
        unchanged.Changed.ShouldBeFalse();
        (await _repo.GetCatalogVersionAsync()).ShouldBe(afterFull);

        var hidden = await _repo.SetDatasetDiscoveryAsync(_identifier, false, null, null, actor);
        hidden.ShouldNotBeNull();
        hidden.Changed.ShouldBeTrue();
        (await _repo.GetCatalogVersionAsync()).ShouldNotBe(afterFull);
    }

}
