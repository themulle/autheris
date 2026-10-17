using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Governance;

/// <summary>
/// Issue #107 (PLAN-GOV-107 C-4, C-5, AC-4, AC-14, SEC-RF107-14e): persistence contract of the row filter evaluation settings
/// (rule level in <c>CONSENT_ROW_FILTERS</c>, object level in <c>TABLE_ROW_FILTER_STRATEGIES</c>); run on SQLite (unit) and on PostgreSQL and
/// SQL Server (integration). Every test seeds its own tables, so tests share one database.
/// </summary>
public abstract class RowFilterEvaluationRepositoryContractTestsBase : IAsyncLifetime
{
    protected static readonly TenantId TenantA = new("tenant-a");
    protected static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid Admin = new("S-1-5-21-admin");

    protected IGovernanceRepository Repo { get; private set; } = null!;
    protected bool Available { get; private set; }

    protected abstract Task<IGovernanceRepository?> CreateRepositoryAsync();

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

    private static TableIdentifier NewTable(string prefix) => new("Sales", "Crm", prefix + "_" + Guid.NewGuid().ToString("N")[..10]);

    private async Task<TableMetadata> UpsertTableAsync(TableIdentifier id)
    {
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName,
            DisplayName = id.TableName, Sensitivity = "LOW", RequiresFourEyes = false, IsActive = true
        };
        await Repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id, Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "customer_id", DataType = "INT" }]
        });
        return (await Repo.GetTableMetadataAsync(id))!;
    }

    private static ConsentRowFilter CorrelatedFilter(Guid columnId, RowFilterEvaluationSettings? settings = null) => new()
    {
        FilterGroup = 0,
        TableColumnId = columnId,
        ColumnName = "customer_id",
        FilterType = RowFilterType.SubqueryCorrelated,
        DependentTable = new TableIdentifier("Crm", "Dbo", "customers"),
        ForeignKeyColumn = "customer_id",
        PrimaryKeyColumn = "id",
        SubqueryFilterPredicateJson = "[]",
        KeySetStrategy = settings?.Strategy,
        KeySetDurationSeconds = settings?.DurationSeconds,
        KeySetMaxKeys = settings?.MaxKeys
    };

    private async Task<(TableIdentifier Table, Consent Consent)> SeedConsentAsync(
        TenantId tenant, ConsentEffect effect = ConsentEffect.Allow, RowFilterEvaluationSettings? settings = null, Func<ConsentRowFilter, ConsentRowFilter>? shape = null)
    {
        var id = NewTable("orders");
        var meta = await UpsertTableAsync(id);
        var filter = CorrelatedFilter(meta.Columns[0].Id, settings);
        var consent = await Repo.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id, TableIdentifier = id, TenantId = tenant, GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-alice"), Effect = effect,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(10),
            RowFilters = [shape is null ? filter : shape(filter)]
        });
        return (id, consent);
    }

    private async Task<ConsentRowFilter> StoredFilterAsync(TableIdentifier table, Guid consentId)
    {
        var consents = await Repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-alice")], table, DateTimeOffset.UtcNow, null);
        return consents.Single(c => c.Id == consentId).RowFilters.Single();
    }

    // ---------------- object level ----------------

    [Fact]
    public async Task Object_Set_RoundTripsThroughMetadata_AndBumpsEpoch()
    {
        if (!Available) return;
        var id = NewTable("obj");
        await UpsertTableAsync(id);
        var epochBefore = await Repo.GetTableEpochAsync(id);

        var change = await Repo.SetRowFilterEvaluationAsync(id, new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 120, 500), Admin);

        change.ShouldNotBeNull();
        change.TargetTable.ShouldBe(id);
        change.RowFilterId.ShouldBeNull();
        change.Before.ShouldBe(RowFilterEvaluationSettings.Inherit);
        change.After.ShouldBe(new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 120, 500));
        change.NewEpoch.ShouldBeGreaterThan(epochBefore);
        (await Repo.GetTableEpochAsync(id)).ShouldBe(change.NewEpoch);
        (await Repo.GetTableMetadataAsync(id))!.RowFilterEvaluation.ShouldBe(new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 120, 500));
    }

    [Fact]
    public async Task Object_SecondSet_ReportsBefore_AndUpdatesInPlace()
    {
        if (!Available) return;
        var id = NewTable("obj");
        await UpsertTableAsync(id);
        await Repo.SetRowFilterEvaluationAsync(id, new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 60, null), Admin);

        var change = await Repo.SetRowFilterEvaluationAsync(id, new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.Exists, null, 10), Admin);

        change!.Before.ShouldBe(new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 60, null));
        (await Repo.GetTableMetadataAsync(id))!.RowFilterEvaluation.ShouldBe(new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.Exists, null, 10));
    }

    [Fact]
    public async Task Object_NullClears_AndBumpsEpoch()
    {
        if (!Available) return;
        var id = NewTable("obj");
        await UpsertTableAsync(id);
        await Repo.SetRowFilterEvaluationAsync(id, new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 60, 5), Admin);
        var epoch = await Repo.GetTableEpochAsync(id);

        var change = await Repo.SetRowFilterEvaluationAsync(id, null, Admin);

        change!.Before.Strategy.ShouldBe(RowFilterEvaluationStrategy.KeySetCache);
        change.After.ShouldBe(RowFilterEvaluationSettings.Inherit);
        change.NewEpoch.ShouldBeGreaterThan(epoch);
        (await Repo.GetTableMetadataAsync(id))!.RowFilterEvaluation.ShouldBeNull();
    }

    [Fact]
    public async Task Object_UnknownTable_ReturnsNull_AndWritesNothing()
    {
        if (!Available) return;
        var unknown = NewTable("missing");

        (await Repo.SetRowFilterEvaluationAsync(unknown, new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 60, 5), Admin)).ShouldBeNull();
        (await Repo.GetTableMetadataAsync(unknown)).ShouldBeNull();
    }

    [Fact]
    public async Task Object_MetadataUpsert_DoesNotOverwriteTheSetting()
    {
        // Ingestion upserts rewrite TABLES; the object setting lives in its own table (PLAN-GOV-107 section 1).
        if (!Available) return;
        var id = NewTable("obj");
        await UpsertTableAsync(id);
        await Repo.SetRowFilterEvaluationAsync(id, new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 60, 5), Admin);

        await UpsertTableAsync(id);

        (await Repo.GetTableMetadataAsync(id))!.RowFilterEvaluation.ShouldBe(new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 60, 5));
    }

    [Fact]
    public async Task Object_TableWithoutSetting_HasNullEvaluation()
    {
        if (!Available) return;
        var id = NewTable("obj");
        await UpsertTableAsync(id);

        (await Repo.GetTableMetadataAsync(id))!.RowFilterEvaluation.ShouldBeNull();
    }

    [Fact]
    public async Task Object_DirectlyLoadedInvalidValues_AreReadBackUnchanged()
    {
        // The repository stores what it is given (caps are configuration); validation happens in the admin service and on resolve (AC-14).
        if (!Available) return;
        var id = NewTable("obj");
        await UpsertTableAsync(id);
        await Repo.SetRowFilterEvaluationAsync(id, new RowFilterEvaluationSettings((RowFilterEvaluationStrategy)42, -1, 0), Admin);

        (await Repo.GetTableMetadataAsync(id))!.RowFilterEvaluation.ShouldBe(new RowFilterEvaluationSettings((RowFilterEvaluationStrategy)42, -1, 0));
    }

    // ---------------- rule level ----------------

    [Fact]
    public async Task Rule_CreateConsent_RoundTripsTheThreeColumns()
    {
        if (!Available) return;
        var (table, consent) = await SeedConsentAsync(TenantA, settings: new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 90, 400));

        var filter = await StoredFilterAsync(table, consent.Id);

        filter.KeySetStrategy.ShouldBe(RowFilterEvaluationStrategy.KeySetCache);
        filter.KeySetDurationSeconds.ShouldBe(90);
        filter.KeySetMaxKeys.ShouldBe(400);
    }

    [Fact]
    public async Task Rule_WithoutSettings_ReadsNulls()
    {
        if (!Available) return;
        var (table, consent) = await SeedConsentAsync(TenantA);

        var filter = await StoredFilterAsync(table, consent.Id);

        filter.KeySetStrategy.ShouldBeNull();
        filter.KeySetDurationSeconds.ShouldBeNull();
        filter.KeySetMaxKeys.ShouldBeNull();
    }

    [Fact]
    public async Task Rule_Id_IsStableAcrossReads()
    {
        // The admin endpoint addresses a rule by id; a fresh id per read would make it unreachable.
        if (!Available) return;
        var (table, consent) = await SeedConsentAsync(TenantA);

        var first = await StoredFilterAsync(table, consent.Id);
        var second = await StoredFilterAsync(table, consent.Id);

        second.Id.ShouldBe(first.Id);
        (await Repo.GetRowFilterAsync(first.Id))!.Value.Filter.Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task Rule_Get_ReturnsFilterEffectTenantAndTable()
    {
        if (!Available) return;
        var (table, consent) = await SeedConsentAsync(TenantA, ConsentEffect.Deny, new RowFilterEvaluationSettings(null, 30, null));
        var filter = await StoredFilterAsync(table, consent.Id);

        var found = await Repo.GetRowFilterAsync(filter.Id);

        found.ShouldNotBeNull();
        found.Value.Effect.ShouldBe(ConsentEffect.Deny);
        found.Value.Tenant.ShouldBe(TenantA);
        found.Value.Table.ShouldBe(table);
        found.Value.Filter.FilterType.ShouldBe(RowFilterType.SubqueryCorrelated);
        found.Value.Filter.KeySetDurationSeconds.ShouldBe(30);
    }

    [Fact]
    public async Task Rule_Get_UnknownId_ReturnsNull()
    {
        if (!Available) return;

        (await Repo.GetRowFilterAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task Rule_Update_PersistsAndReportsBeforeAfter_AndBumpsTheTargetEpoch()
    {
        if (!Available) return;
        var (table, consent) = await SeedConsentAsync(TenantA, settings: new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.Exists, 10, 10));
        var filter = await StoredFilterAsync(table, consent.Id);
        var epoch = await Repo.GetTableEpochAsync(table);
        var after = new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 300, 800);

        var change = await Repo.UpdateRowFilterEvaluationAsync(filter.Id, TenantA, after);

        change.ShouldNotBeNull();
        change.TargetTable.ShouldBe(table);
        change.Tenant.ShouldBe(TenantA);
        change.RowFilterId.ShouldBe(filter.Id);
        change.Before.ShouldBe(new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.Exists, 10, 10));
        change.After.ShouldBe(after);
        change.NewEpoch.ShouldBeGreaterThan(epoch);
        (await Repo.GetTableEpochAsync(table)).ShouldBe(change.NewEpoch);
        var stored = await StoredFilterAsync(table, consent.Id);
        stored.KeySetStrategy.ShouldBe(RowFilterEvaluationStrategy.KeySetCache);
        stored.KeySetDurationSeconds.ShouldBe(300);
        stored.KeySetMaxKeys.ShouldBe(800);
    }

    [Fact]
    public async Task Rule_Update_WithInherit_ClearsAllThree()
    {
        if (!Available) return;
        var (table, consent) = await SeedConsentAsync(TenantA, settings: new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 10, 10));
        var filter = await StoredFilterAsync(table, consent.Id);

        await Repo.UpdateRowFilterEvaluationAsync(filter.Id, TenantA, RowFilterEvaluationSettings.Inherit);

        var stored = await StoredFilterAsync(table, consent.Id);
        stored.KeySetStrategy.ShouldBeNull();
        stored.KeySetDurationSeconds.ShouldBeNull();
        stored.KeySetMaxKeys.ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Rule_Update_OtherTenant_ReturnsNull_AndChangesNothing_SEC_RF107_14a()
    {
        if (!Available) return;
        var (table, consent) = await SeedConsentAsync(TenantA, settings: new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.Exists, 10, 10));
        var filter = await StoredFilterAsync(table, consent.Id);
        var epoch = await Repo.GetTableEpochAsync(table);

        var change = await Repo.UpdateRowFilterEvaluationAsync(filter.Id, TenantB, new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 99, 99));

        change.ShouldBeNull();
        (await Repo.GetTableEpochAsync(table)).ShouldBe(epoch);
        var stored = await StoredFilterAsync(table, consent.Id);
        stored.KeySetStrategy.ShouldBe(RowFilterEvaluationStrategy.Exists);
        stored.KeySetDurationSeconds.ShouldBe(10);
    }

    [Fact]
    public async Task Rule_Update_UnknownId_ReturnsNull()
    {
        if (!Available) return;

        (await Repo.UpdateRowFilterEvaluationAsync(Guid.NewGuid(), TenantA, RowFilterEvaluationSettings.Inherit)).ShouldBeNull();
    }
}
