using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using FluentAssertions;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Engine-agnostic contract suite for <c>ITableMetadataRepository</c>, <c>IPolicyEpochRepository</c>,
/// and <c>IPhysicalIndexRepository</c>.
/// </summary>
public abstract class CatalogContract : GovernanceContractBase
{
    private static readonly Sid AdminSid = new("S-1-5-21-700-1");

    private static TableMetadata BuildTable(
        string name,
        IReadOnlyList<string>? primaryKey = null,
        IReadOnlyList<TableIndexDefinition>? indexes = null)
    {
        var id = new TableIdentifier("catalog", "dbo", name + "_" + Guid.NewGuid().ToString("N")[..8]);
        var emailColId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = id,
            ReplaceKeyDefinition = true,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = id.TableName,
                Description = "Catalog contract table",
                Sensitivity = "NORMAL",
                RequiresFourEyes = false,
                IsActive = true
            },
            Columns =
            [
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnName = "id",
                    DataType = "bigint",
                    IsSensitive = false
                },
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnName = "tenant_code",
                    DataType = "varchar",
                    IsSensitive = false
                },
                new TableColumn
                {
                    Id = emailColId,
                    ColumnName = "email",
                    DataType = "varchar",
                    IsSensitive = true
                }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule
                {
                    Id = Guid.NewGuid(),
                    TableColumnId = emailColId,
                    RuleType = "PARTIAL_MASK",
                    KeepPrefix = 2,
                    KeepSuffix = 2
                }
            },
            PrimaryKeyColumns = primaryKey ?? ["id"],
            Indexes = indexes ?? []
        };
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var tableA = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("tenant_a_cat"));
        var tableB = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("tenant_b_cat"));

        _ = await Store.Consents.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = tableA.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = AdminSid,
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            ValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = Store.TenantA
        });
        _ = await Store.Consents.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = tableB.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = AdminSid,
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            ValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = Store.TenantB
        });

        var visibleToA = await Store.Consents.GetTablesWithConsentsForTenantAsync(Store.TenantA);
        visibleToA.Should().Contain(tableA.Identifier).And.NotContain(tableB.Identifier);

        var visibleToB = await Store.Consents.GetTablesWithConsentsForTenantAsync(Store.TenantB);
        visibleToB.Should().Contain(tableB.Identifier).And.NotContain(tableA.Identifier);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var tableA = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("isolated_a"));
        var tableB = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("isolated_b"));
        var epochBBefore = await Store.PolicyEpochs.GetTableEpochAsync(tableB.Identifier);

        _ = await Store.TableMetadata.SetDatasetVisibilityAsync(tableA.Identifier, DatasetVisibility.Full, AdminSid);
        _ = await Store.TableMetadata.ReplaceDatasetTagsAsync(tableA.Identifier, ["finance", "governed"], DatasetTagSource.Admin, AdminSid);
        _ = await Store.TableMetadata.ReplaceTableKeyDefinitionAsync(
            tableA.Identifier,
            ["id", "tenant_code"],
            [new TableIndexDefinition("ix_email", ["email"], false)]);

        var settingsB = await Store.TableMetadata.GetDatasetCatalogSettingsAsync(tableB.Identifier);
        settingsB.Should().NotBeNull();
        settingsB!.Visibility.Should().Be(DatasetVisibility.Hidden);
        settingsB.Tags.Should().BeEmpty();

        var reloadedB = await Store.TableMetadata.GetTableMetadataAsync(tableB.Identifier);
        reloadedB!.PrimaryKeyColumns.Should().Equal("id");
        (await Store.PolicyEpochs.GetTableEpochAsync(tableB.Identifier)).Should().Be(epochBBefore);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override Task Missing_or_unparseable_tenant_id_throws()
    {
        var emptyAct = () => new TenantId(string.Empty);
        emptyAct.Should().Throw<ArgumentException>();

        var reservedAct = () => new TenantId("_platform_reserved");
        reservedAct.Should().Throw<ArgumentException>();

        var writableAct = () => StoredTenant.RequireWritable(default);
        writableAct.Should().Throw<ArgumentException>();

        var parseAct = () => StoredTenant.Parse("   ");
        parseAct.Should().Throw<StoredTenantIntegrityException>();

        return Task.CompletedTask;
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var definition = BuildTable(
            "idem_tbl",
            primaryKey: ["id", "tenant_code"],
            indexes: [new TableIndexDefinition("ix_email", ["email"], true, [KeyColumnDirection.Descending])]);

        var first = await Store.TableMetadata.UpsertTableMetadataAsync(definition);
        var second = await Store.TableMetadata.UpsertTableMetadataAsync(definition);

        second.Table.Id.Should().Be(first.Table.Id);
        second.PrimaryKeyColumns.Should().Equal("id", "tenant_code");
        second.Indexes.Should().ContainSingle(i => i.Name == "ix_email" && i.IsUnique);
        second.Columns.Should().HaveCount(3);
        second.ColumnMaskingRules.Should().ContainKey("email");

        var all = await Store.TableMetadata.GetAllTablesAsync();
        all.Count(t => t.Identifier.Equals(definition.Identifier)).Should().Be(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var table = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("dup_idx_tbl"));
        var epochBefore = await Store.PolicyEpochs.GetTableEpochAsync(table.Identifier);

        var dupIndexAct = () => Store.TableMetadata.ReplaceTableKeyDefinitionAsync(
            table.Identifier,
            ["id"],
            [
                new TableIndexDefinition("ix_dup", ["email"], false),
                new TableIndexDefinition("IX_DUP", ["tenant_code"], false)
            ]);
        await dupIndexAct.Should().ThrowAsync<TableDefinitionException>();

        var unknownColAct = () => Store.TableMetadata.ReplaceTableKeyDefinitionAsync(
            table.Identifier,
            ["non_existent_column"],
            []);
        await unknownColAct.Should().ThrowAsync<TableDefinitionException>();

        (await Store.PolicyEpochs.GetTableEpochAsync(table.Identifier)).Should().Be(epochBefore);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        var t1 = await Store.TableMetadata.UpsertTableMetadataAsync(
            BuildTable("ord_1", primaryKey: ["tenant_code", "id"]));
        var t2 = await Store.TableMetadata.UpsertTableMetadataAsync(
            BuildTable("ord_2", primaryKey: ["id", "tenant_code"]));

        _ = await Store.TableMetadata.SetDatasetVisibilityAsync(t1.Identifier, DatasetVisibility.Summary, AdminSid);
        _ = await Store.TableMetadata.SetDatasetVisibilityAsync(t2.Identifier, DatasetVisibility.Full, AdminSid);

        var loaded1 = await Store.TableMetadata.GetTableMetadataAsync(t1.Identifier);
        loaded1!.PrimaryKeyColumns.Should().Equal("tenant_code", "id");

        var loaded2 = await Store.TableMetadata.GetTableMetadataAsync(t2.Identifier);
        loaded2!.PrimaryKeyColumns.Should().Equal("id", "tenant_code");

        var counts = await Store.TableMetadata.GetDatasetVisibilityCountsAsync();
        counts.Summary.Should().BeGreaterOrEqualTo(1);
        counts.Full.Should().BeGreaterOrEqualTo(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        var table = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("phys_cap_tbl"));
        var overCapDeclaredIndexes = Enumerable.Range(0, TableKeyDefinitionValidator.MaxIndexesPerTable + 1)
            .Select(i => new TableIndexDefinition($"ix_cap_{i}", ["id"], false))
            .ToList();
        var declaredAct = () => Store.TableMetadata.UpsertTableMetadataAsync(
            table with { Indexes = overCapDeclaredIndexes, ReplaceKeyDefinition = true });
        await declaredAct.Should().ThrowAsync<TableDefinitionException>();

        var overCapIndexes = Enumerable.Range(0, TableKeyDefinitionValidator.MaxPhysicalIndexesPerTable + 1)
            .Select(_ => new PhysicalIndexDefinition(["id"], [KeyColumnDirection.Ascending]))
            .ToList();

        var act = () => Store.PhysicalIndexes.ReplacePhysicalIndexesAsync(table.Identifier, overCapIndexes);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        var table = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("inactive_cat"));
        await Store.ExecuteNonQueryAsync(
            "UPDATE TABLES SET is_active = 0 WHERE id = @id",
            ("@id", table.Table.Id.ToString()));
        Store.InvalidateMetadataCache();

        (await Store.TableMetadata.GetActiveTableIdentifierAsync(table.Identifier)).Should().BeNull();
        (await Store.TableMetadata.GetActiveTableIdentifierByIdAsync(table.Table.Id)).Should().BeNull();
        (await Store.TableMetadata.GetDatasetCatalogSettingsAsync(table.Identifier)).Should().BeNull();
        (await Store.TableMetadata.SetDatasetVisibilityAsync(table.Identifier, DatasetVisibility.Full, AdminSid)).Should().BeNull();
        (await Store.TableMetadata.ReplaceDatasetTagsAsync(table.Identifier, ["tag1"], DatasetTagSource.Admin, AdminSid)).Should().BeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Corrupt_key_definition_fails_closed_but_allows_narrow_repair()
    {
        if (!Store.IsAvailable) return;

        var table = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("corrupt_keys"));
        await Store.ExecuteNonQueryAsync(
            "UPDATE TABLE_PRIMARY_KEY_COLUMNS SET key_ordinal = 2 WHERE table_id = @tid",
            ("@tid", table.Table.Id.ToString()));
        Store.InvalidateMetadataCache();

        var getAct = () => Store.TableMetadata.GetTableMetadataAsync(table.Identifier);
        await getAct.Should().ThrowAsync<TableMetadataUnavailableException>();

        var allTables = await Store.TableMetadata.GetAllTablesAsync();
        allTables.Select(t => t.Identifier).Should().NotContain(table.Identifier);

        var activeId = await Store.TableMetadata.GetActiveTableIdentifierAsync(table.Identifier);
        activeId.Should().Be(table.Identifier);

        var repaired = await Store.TableMetadata.ReplaceTableKeyDefinitionAsync(
            table.Identifier,
            ["id"],
            [new TableIndexDefinition("ix_tenant", ["tenant_code"], false)]);
        repaired.Should().NotBeNull();

        var afterRepair = await Store.TableMetadata.GetTableMetadataAsync(table.Identifier);
        afterRepair.Should().NotBeNull();
        afterRepair!.PrimaryKeyColumns.Should().Equal("id");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Dataset_catalog_visibility_and_tag_ownership_preserve_admin_over_dbt()
    {
        if (!Store.IsAvailable) return;

        var table = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("tag_ownership"));

        _ = await Store.TableMetadata.ReplaceDatasetTagsAsync(table.Identifier, ["pii", "shared"], DatasetTagSource.Admin, AdminSid);
        _ = await Store.TableMetadata.ReplaceDatasetTagsAsync(table.Identifier, ["shared", "dbt-only"], DatasetTagSource.Dbt, AdminSid);

        var settings = await Store.TableMetadata.GetDatasetCatalogSettingsAsync(table.Identifier);
        settings!.Tags.Select(t => t.Tag).Should().BeEquivalentTo(["dbt-only", "pii", "shared"]);
        settings.Tags.Single(t => t.Tag == "shared").Source.Should().Be(DatasetTagSource.Admin);
        settings.Tags.Single(t => t.Tag == "dbt-only").Source.Should().Be(DatasetTagSource.Dbt);

        _ = await Store.TableMetadata.ReplaceDatasetTagsAsync(table.Identifier, [], DatasetTagSource.Dbt, AdminSid);
        var afterDbtClear = await Store.TableMetadata.GetDatasetCatalogSettingsAsync(table.Identifier);
        afterDbtClear!.Tags.Select(t => t.Tag).Should().BeEquivalentTo(["pii", "shared"]);

        _ = await Store.TableMetadata.SetDatasetVisibilityAsync(table.Identifier, DatasetVisibility.Full, AdminSid);
        _ = await Store.TableMetadata.UpsertTableMetadataAsync(table);

        var afterReingest = await Store.TableMetadata.GetDatasetCatalogSettingsAsync(table.Identifier);
        afterReingest!.Visibility.Should().Be(DatasetVisibility.Full);
        afterReingest.Tags.Select(t => t.Tag).Should().BeEquivalentTo(["pii", "shared"]);

        await Store.ExecuteNonQueryAsync(
            "UPDATE TABLE_CATALOG_SETTINGS SET visibility = 99 WHERE table_id = @tid",
            ("@tid", table.Table.Id.ToString()));

        var strictAct = () => Store.TableMetadata.GetDatasetCatalogSettingsAsync(table.Identifier, strict: true);
        await strictAct.Should().ThrowAsync<InvalidOperationException>();

        var lenient = await Store.TableMetadata.GetDatasetCatalogSettingsAsync(table.Identifier, strict: false);
        lenient.Should().NotBeNull();
        lenient!.Visibility.Should().Be(DatasetVisibility.Hidden);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Epoch_increment_with_missing_policy_epoch_row_advances_and_keys_by_table_id()
    {
        if (!Store.IsAvailable) return;

        var table = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("epoch_missing_row"));
        var tableId = table.Table.Id.ToString();
        var epochBefore = await Store.PolicyEpochs.GetTableEpochAsync(table.Identifier);
        await Store.ExecuteNonQueryAsync("DELETE FROM POLICY_EPOCHS WHERE table_id = @tid", ("@tid", tableId));

        var first = await Store.PolicyEpochs.IncrementTableEpochAsync(table.Identifier);
        first.Should().BeGreaterThan(epochBefore);
        var second = await Store.PolicyEpochs.IncrementTableEpochAsync(table.Identifier);
        second.Should().BeGreaterThan(first);

        var rows = await Store.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM POLICY_EPOCHS WHERE LOWER(table_name) = LOWER(@name)", ("@name", table.Identifier.TableName));
        Convert.ToInt64(rows).Should().Be(1);
        var keyed = await Store.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM POLICY_EPOCHS WHERE table_id = @tid", ("@tid", tableId));
        Convert.ToInt64(keyed).Should().Be(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Physical_indexes_and_policy_epochs_round_trip_atomically()
    {
        if (!Store.IsAvailable) return;

        var table = await Store.TableMetadata.UpsertTableMetadataAsync(BuildTable("phys_epoch"));
        var epoch0 = await Store.PolicyEpochs.GetTableEpochAsync(table.Identifier);

        var epoch1 = await Store.PolicyEpochs.IncrementTableEpochAsync(table.Identifier);
        epoch1.Should().Be(epoch0 + 1);

        var replaced = await Store.PhysicalIndexes.ReplacePhysicalIndexesAsync(
            table.Identifier,
            [
                new PhysicalIndexDefinition(["tenant_code", "id"], [KeyColumnDirection.Ascending, KeyColumnDirection.Descending]),
                new PhysicalIndexDefinition(["email"], [])
            ]);
        replaced.Should().BeTrue();

        var epoch2 = await Store.PolicyEpochs.GetTableEpochAsync(table.Identifier);
        epoch2.Should().Be(epoch1 + 1);

        var loaded = await Store.TableMetadata.GetTableMetadataAsync(table.Identifier);
        loaded!.PhysicalIndexes.Should().HaveCount(2);
        loaded.PhysicalIndexes[0].Columns.Should().Equal("tenant_code", "id");
        loaded.PhysicalIndexes[0].Directions.Should().Equal(KeyColumnDirection.Ascending, KeyColumnDirection.Descending);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlCatalogContractTests(GovernancePostgresFixture fixture) : CatalogContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "catalog_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerCatalogContractTests(GovernanceSqlServerFixture fixture) : CatalogContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "catalog_contract");
}
