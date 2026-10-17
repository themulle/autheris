using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Governance;

/// <summary>
/// Issue #111 persistence contract of dataset tags and the catalog visibility tier (<c>TABLE_TAGS</c>, <c>TABLE_CATALOG_SETTINGS</c>); run on SQLite
/// (unit) and on PostgreSQL and SQL Server (integration). Covers T-1 items 3-5 and 7, M-1, M-9 (read part), T-R2-7, T-R3-1 and the per-engine
/// "Table.Tags stays empty" guard (F-C10).
/// </summary>
public abstract class CatalogSettingsRepositoryContractTestsBase : IAsyncLifetime
{
    private static readonly Sid Admin = new("S-1-5-21-admin");

    protected IGovernanceRepository Repo { get; private set; } = null!;
    protected bool Available { get; private set; }

    /// <summary>A repository on a fresh database; the new-table default visibility is Hidden.</summary>
    protected abstract Task<IGovernanceRepository?> CreateRepositoryAsync();

    /// <summary>A further repository instance on the SAME database (restart or replica with its own metadata cache).</summary>
    protected abstract Task<IGovernanceRepository?> CreateAdditionalRepositoryAsync(DatasetVisibility newTableDefault = DatasetVisibility.Hidden);

    /// <summary>Runs raw SQL against the governance database (used to corrupt rows behind the repository).</summary>
    protected abstract Task ExecuteRawAsync(string sql);

    protected abstract Task DisposeRepositoryAsync();

    public async Task InitializeAsync()
    {
        var repo = await CreateRepositoryAsync();
        Available = repo != null;
        if (repo != null)
        {
            Repo = repo;
        }
    }

    public Task DisposeAsync() => DisposeRepositoryAsync();

    private static TableIdentifier NewTable(string prefix = "orders") => new("Sales", "Crm", prefix + "_" + Guid.NewGuid().ToString("N")[..10]);

    private static async Task<TableMetadata> UpsertTableAsync(IGovernanceRepository repo, TableIdentifier id, bool active = true)
    {
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName,
            DisplayName = id.TableName, Sensitivity = "LOW", RequiresFourEyes = false, IsActive = active
        };
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id, Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "customer_id", DataType = "INT" }]
        });

        return (await repo.GetTableMetadataAsync(id))!;
    }

    private static async Task<TableMetadata?> FromAllAsync(IGovernanceRepository repo, TableIdentifier id) =>
        (await repo.GetAllTablesAsync()).SingleOrDefault(t => t.Identifier == id);

    private static string[] TagNames(DatasetCatalogSettings s) => s.Tags.Select(t => t.Tag).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task AdminTags_AreReadBackBySecondInstance_ThroughBothReaders_AndTableTagsStayEmpty()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);

        var change = await Repo.ReplaceDatasetTagsAsync(id, ["finance", "pii"], DatasetTagSource.Admin, Admin);

        change.ShouldNotBeNull();
        change.Changed.ShouldBeTrue();
        change.Before.Tags.ShouldBeEmpty();
        TagNames(change.After).ShouldBe(["finance", "pii"]);

        var restarted = (await CreateAdditionalRepositoryAsync())!;
        var single = (await restarted.GetTableMetadataAsync(id))!;
        var listed = (await FromAllAsync(restarted, id))!;
        foreach (var meta in new[] { single, listed })
        {
            meta.CatalogTags.Order(StringComparer.Ordinal).ShouldBe(["finance", "pii"]);
            meta.Table.Tags.ShouldBeEmpty(); // F-C10 / T-R3-1: tags are not policy inputs
        }
    }

    [Fact]
    public async Task Visibility_IsPersisted_MissingRowIsHidden_AndOverlaidIntoTableVisibility()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);

        var initial = await Repo.GetDatasetCatalogSettingsAsync(id);
        initial.ShouldNotBeNull();
        initial.Visibility.ShouldBe(DatasetVisibility.Hidden);
        initial.Tags.ShouldBeEmpty();

        var change = await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Summary, Admin);

        change.ShouldNotBeNull();
        change.Before.Visibility.ShouldBe(DatasetVisibility.Hidden);
        change.After.Visibility.ShouldBe(DatasetVisibility.Summary);
        var restarted = (await CreateAdditionalRepositoryAsync())!;
        (await restarted.GetTableMetadataAsync(id))!.Table.Visibility.ShouldBe(DatasetVisibility.Summary);
        (await FromAllAsync(restarted, id))!.Table.Visibility.ShouldBe(DatasetVisibility.Summary);
    }

    [Fact]
    public async Task UnknownOrInactiveTable_ReturnsNull_ForEveryMethod()
    {
        if (!Available) { return; }

        var unknown = NewTable("unknown");
        var inactive = NewTable("inactive");
        await UpsertTableAsync(Repo, inactive, active: false);

        foreach (var id in new[] { unknown, inactive })
        {
            (await Repo.GetDatasetCatalogSettingsAsync(id)).ShouldBeNull();
            (await Repo.ReplaceDatasetTagsAsync(id, ["a"], DatasetTagSource.Admin, Admin)).ShouldBeNull();
            (await Repo.ChangeDatasetTagsAsync(id, ["a"], [], Admin)).ShouldBeNull();
            (await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Full, Admin)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task DbtReplace_ReplacesOnlyDbtRows_AdminRowsAndTagsInBothStayAdmin()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);
        await Repo.ReplaceDatasetTagsAsync(id, ["owner-set", "shared"], DatasetTagSource.Admin, Admin);
        await Repo.ReplaceDatasetTagsAsync(id, ["shared", "dbt-one", "dbt-two"], DatasetTagSource.Dbt, Admin);

        var afterFirst = (await Repo.GetDatasetCatalogSettingsAsync(id))!;
        afterFirst.Tags.Single(t => t.Tag == "shared").Source.ShouldBe(DatasetTagSource.Admin);
        afterFirst.Tags.Single(t => t.Tag == "dbt-one").Source.ShouldBe(DatasetTagSource.Dbt);

        await Repo.ReplaceDatasetTagsAsync(id, ["dbt-two"], DatasetTagSource.Dbt, Admin);

        var afterSecond = (await Repo.GetDatasetCatalogSettingsAsync(id))!;
        TagNames(afterSecond).ShouldBe(["dbt-two", "owner-set", "shared"]);
        afterSecond.Tags.Single(t => t.Tag == "owner-set").Source.ShouldBe(DatasetTagSource.Admin);
        afterSecond.Tags.Single(t => t.Tag == "shared").Source.ShouldBe(DatasetTagSource.Admin);
    }

    [Fact]
    public async Task AdminReplace_KeepsDbtRows_AndTakesOverATagFromDbt()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);
        await Repo.ReplaceDatasetTagsAsync(id, ["from-dbt", "also-dbt"], DatasetTagSource.Dbt, Admin);

        await Repo.ReplaceDatasetTagsAsync(id, ["from-dbt", "mine"], DatasetTagSource.Admin, Admin);

        var settings = (await Repo.GetDatasetCatalogSettingsAsync(id))!;
        TagNames(settings).ShouldBe(["also-dbt", "from-dbt", "mine"]);
        settings.Tags.Single(t => t.Tag == "from-dbt").Source.ShouldBe(DatasetTagSource.Admin);
        settings.Tags.Single(t => t.Tag == "also-dbt").Source.ShouldBe(DatasetTagSource.Dbt);

        // A later dbt run that no longer lists the tag does not remove the admin-owned row.
        await Repo.ReplaceDatasetTagsAsync(id, [], DatasetTagSource.Dbt, Admin);
        TagNames((await Repo.GetDatasetCatalogSettingsAsync(id))!).ShouldBe(["from-dbt", "mine"]);
    }

    [Fact]
    public async Task ChangeTags_AddsAndRemovesAdminTags_DbtRowSurvivesRemove()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);
        await Repo.ReplaceDatasetTagsAsync(id, ["dbt-tag"], DatasetTagSource.Dbt, Admin);
        await Repo.ReplaceDatasetTagsAsync(id, ["a", "b"], DatasetTagSource.Admin, Admin);

        var change = await Repo.ChangeDatasetTagsAsync(id, ["c"], ["a", "dbt-tag"], Admin);

        change.ShouldNotBeNull();
        change.Changed.ShouldBeTrue();
        TagNames(change.After).ShouldBe(["b", "c", "dbt-tag"]);
        change.After.Tags.Single(t => t.Tag == "dbt-tag").Source.ShouldBe(DatasetTagSource.Dbt);

        var noop = await Repo.ChangeDatasetTagsAsync(id, ["c"], ["not-there"], Admin);
        noop!.Changed.ShouldBeFalse();
    }

    [Fact]
    public async Task IdempotentWrites_ReportNoChange()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);
        await Repo.ReplaceDatasetTagsAsync(id, ["x", "y"], DatasetTagSource.Admin, Admin);
        await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Summary, Admin);

        (await Repo.ReplaceDatasetTagsAsync(id, ["y", "x"], DatasetTagSource.Admin, Admin))!.Changed.ShouldBeFalse();
        (await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Summary, Admin))!.Changed.ShouldBeFalse();
    }

    [Fact]
    public async Task InvalidTag_IsRejected_NothingIsWritten()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);

        await Should.ThrowAsync<ArgumentException>(() => Repo.ReplaceDatasetTagsAsync(id, ["ok", "Not Valid"], DatasetTagSource.Admin, Admin));
        await Should.ThrowAsync<ArgumentException>(() => Repo.ChangeDatasetTagsAsync(id, ["UPPER"], [], Admin));

        (await Repo.GetDatasetCatalogSettingsAsync(id))!.Tags.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reingestion_Upsert_LeavesTagsAndVisibilityUntouched()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);
        await Repo.ReplaceDatasetTagsAsync(id, ["keep-me"], DatasetTagSource.Admin, Admin);
        await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Full, Admin);

        // Re-ingestion: the same table identifier is upserted again, with a different description and a "Tags" literal that must be ignored.
        var existing = (await Repo.GetTableMetadataAsync(id))!;
        await Repo.UpsertTableMetadataAsync(existing with
        {
            Table = existing.Table with { Description = "changed by ingestion", Tags = ["ingestion-tag"], Visibility = DatasetVisibility.Hidden }
        });

        var after = (await Repo.GetTableMetadataAsync(id))!;
        after.Table.Visibility.ShouldBe(DatasetVisibility.Full);
        after.CatalogTags.ShouldBe(["keep-me"]);
        after.Table.Tags.ShouldBeEmpty();
        TagNames((await Repo.GetDatasetCatalogSettingsAsync(id))!).ShouldBe(["keep-me"]);
    }

    [Fact]
    public async Task M1_TierChange_IsEffectiveImmediately_OnTheSameInstance_AndOnAReplicaWithItsOwnCache()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);
        var replica = (await CreateAdditionalRepositoryAsync())!;

        await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Full, Admin);
        await Repo.ReplaceDatasetTagsAsync(id, ["pii"], DatasetTagSource.Admin, Admin);
        foreach (var repo in new[] { Repo, replica })
        {
            (await repo.GetTableMetadataAsync(id))!.Table.Visibility.ShouldBe(DatasetVisibility.Full);
            (await FromAllAsync(repo, id))!.Table.Visibility.ShouldBe(DatasetVisibility.Full);
        }

        // Restricting: both instances have a warm structural cache entry; the next read must show Hidden without waiting for the TTL.
        await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Hidden, Admin);
        await Repo.ReplaceDatasetTagsAsync(id, [], DatasetTagSource.Admin, Admin);

        foreach (var repo in new[] { Repo, replica })
        {
            var single = (await repo.GetTableMetadataAsync(id))!;
            single.Table.Visibility.ShouldBe(DatasetVisibility.Hidden);
            single.CatalogTags.ShouldBeEmpty();
            var listed = (await FromAllAsync(repo, id))!;
            listed.Table.Visibility.ShouldBe(DatasetVisibility.Hidden);
            listed.CatalogTags.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task T_R2_7_OverlayOnCachedRows_ReadsFresh_AndNeverMutatesAPreviouslyReturnedRecord()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);
        var replica = (await CreateAdditionalRepositoryAsync())!;
        var warm = (await replica.GetTableMetadataAsync(id))!; // structural entry cached on the replica
        warm.Table.Visibility.ShouldBe(DatasetVisibility.Hidden);

        await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Full, Admin);
        await Repo.ReplaceDatasetTagsAsync(id, ["fresh"], DatasetTagSource.Admin, Admin);

        var listed = (await FromAllAsync(replica, id))!; // GetAllTablesAsync with a warm cache
        listed.Table.Visibility.ShouldBe(DatasetVisibility.Full);
        listed.CatalogTags.ShouldBe(["fresh"]);
        warm.Table.Visibility.ShouldBe(DatasetVisibility.Hidden);
        warm.CatalogTags.ShouldBeEmpty();
    }

    [Fact]
    public async Task Degradation_CorruptVisibilityInteger_YieldsHiddenAndNoTags_OthersUnaffected_NeverThrows()
    {
        if (!Available) { return; }

        var broken = NewTable("broken");
        var healthy = NewTable("healthy");
        var brokenMeta = await UpsertTableAsync(Repo, broken);
        await UpsertTableAsync(Repo, healthy);
        await Repo.SetDatasetVisibilityAsync(broken, DatasetVisibility.Full, Admin);
        await Repo.ReplaceDatasetTagsAsync(broken, ["pii"], DatasetTagSource.Admin, Admin);
        await Repo.SetDatasetVisibilityAsync(healthy, DatasetVisibility.Summary, Admin);
        await ExecuteRawAsync($"UPDATE TABLE_CATALOG_SETTINGS SET visibility = 3 WHERE table_id = '{brokenMeta.Table.Id}'");
        var reader = (await CreateAdditionalRepositoryAsync())!;

        var all = await reader.GetAllTablesAsync();

        var brokenRow = all.Single(t => t.Identifier == broken);
        brokenRow.Table.Visibility.ShouldBe(DatasetVisibility.Hidden);
        brokenRow.CatalogTags.ShouldBeEmpty();
        all.Single(t => t.Identifier == healthy).Table.Visibility.ShouldBe(DatasetVisibility.Summary);
        (await reader.GetTableMetadataAsync(broken))!.Table.Visibility.ShouldBe(DatasetVisibility.Hidden);
    }

    [Fact]
    public async Task Degradation_TagViolatingTheRules_YieldsHiddenAndNoTagsForThatTableOnly()
    {
        if (!Available) { return; }

        var broken = NewTable("broken");
        var healthy = NewTable("healthy");
        var brokenMeta = await UpsertTableAsync(Repo, broken);
        await UpsertTableAsync(Repo, healthy);
        await Repo.SetDatasetVisibilityAsync(broken, DatasetVisibility.Full, Admin);
        await Repo.SetDatasetVisibilityAsync(healthy, DatasetVisibility.Full, Admin);
        await Repo.ReplaceDatasetTagsAsync(healthy, ["good"], DatasetTagSource.Admin, Admin);
        await ExecuteRawAsync(
            $"INSERT INTO TABLE_TAGS (table_id, tag, source, created_by, created_at) VALUES ('{brokenMeta.Table.Id}', 'Bad Tag!', 'admin', 'x', '2026-01-01T00:00:00.0000000+00:00')");
        var reader = (await CreateAdditionalRepositoryAsync())!;

        var all = await reader.GetAllTablesAsync();

        var brokenRow = all.Single(t => t.Identifier == broken);
        brokenRow.Table.Visibility.ShouldBe(DatasetVisibility.Hidden);
        brokenRow.CatalogTags.ShouldBeEmpty();
        var healthyRow = all.Single(t => t.Identifier == healthy);
        healthyRow.Table.Visibility.ShouldBe(DatasetVisibility.Full);
        healthyRow.CatalogTags.ShouldBe(["good"]);
    }

    [Fact]
    public async Task Degradation_DroppedTagTable_YieldsHiddenForEveryRow_NeverFull_NeverThrows()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);
        await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Full, Admin);
        var reader = (await CreateAdditionalRepositoryAsync())!; // created before the drop: a new instance would fail its shape check
        await ExecuteRawAsync("DROP TABLE TABLE_TAGS");

        var all = await reader.GetAllTablesAsync();

        all.ShouldNotBeEmpty();
        all.ShouldAllBe(t => t.Table.Visibility == DatasetVisibility.Hidden && t.CatalogTags.Count == 0);
        (await reader.GetTableMetadataAsync(id))!.Table.Visibility.ShouldBe(DatasetVisibility.Hidden);
    }

    [Fact]
    public async Task StrictRead_ThrowsOnStoreError_InsteadOfAFakeHidden()
    {
        if (!Available) { return; }

        var id = NewTable();
        await UpsertTableAsync(Repo, id);
        var reader = (await CreateAdditionalRepositoryAsync())!; // created before the drop: a new instance would fail its shape check
        await ExecuteRawAsync("DROP TABLE TABLE_CATALOG_SETTINGS");

        await Should.ThrowAsync<Exception>(() => reader.GetDatasetCatalogSettingsAsync(id, strict: true));
        var lenient = await reader.GetDatasetCatalogSettingsAsync(id, strict: false);
        lenient.ShouldNotBeNull();
        lenient.Visibility.ShouldBe(DatasetVisibility.Hidden);
    }

    [Fact]
    public async Task StrictRead_ThrowsOnACorruptRow_InsteadOfAFakeHidden()
    {
        if (!Available) { return; }

        var id = NewTable();
        var meta = await UpsertTableAsync(Repo, id);
        await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Summary, Admin);
        await ExecuteRawAsync($"UPDATE TABLE_CATALOG_SETTINGS SET visibility = 9 WHERE table_id = '{meta.Table.Id}'");
        var reader = (await CreateAdditionalRepositoryAsync())!;

        await Should.ThrowAsync<Exception>(() => reader.GetDatasetCatalogSettingsAsync(id, strict: true));
    }

    [Fact]
    public async Task DefaultVisibilitySummary_NewTableGetsASummaryRow_ExistingTablesAndHiddenDefaultWriteNoRow()
    {
        if (!Available) { return; }

        var existing = NewTable("existing");
        await UpsertTableAsync(Repo, existing); // created under the Hidden default
        var summaryRepo = (await CreateAdditionalRepositoryAsync(DatasetVisibility.Summary))!;
        var created = NewTable("created");
        await UpsertTableAsync(summaryRepo, created);
        // Re-ingestion of the existing table under the Summary default must not change it.
        var existingMeta = (await summaryRepo.GetTableMetadataAsync(existing))!;
        await summaryRepo.UpsertTableMetadataAsync(existingMeta);
        var hiddenCreated = NewTable("hidden");
        await UpsertTableAsync(Repo, hiddenCreated);

        (await summaryRepo.GetTableMetadataAsync(created))!.Table.Visibility.ShouldBe(DatasetVisibility.Summary);
        (await summaryRepo.GetTableMetadataAsync(existing))!.Table.Visibility.ShouldBe(DatasetVisibility.Hidden);
        (await Repo.GetTableMetadataAsync(hiddenCreated))!.Table.Visibility.ShouldBe(DatasetVisibility.Hidden);
    }
}
