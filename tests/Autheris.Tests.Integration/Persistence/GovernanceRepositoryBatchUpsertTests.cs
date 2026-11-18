namespace Autheris.Tests.Integration.Persistence;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Integration.Governance;
using Npgsql;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class GovernanceRepositoryBatchUpsertTests
{
    private readonly string _dbName;

    /// <summary>A fresh clone of the migrated PostgreSQL governance template; the harness records every command and transaction outcome.</summary>
    public GovernanceRepositoryBatchUpsertTests()
    {
        _dbName = GovernanceTestDatabase.Create("batch_upsert");
    }

    [Fact]
    public async Task EmptyBatch_ReturnsEmpty_AndOpensNoConnections()
    {
        var harness = new RecordingBatchHarness(_dbName);
        var repo = CreateRepository(harness);

        var results = await repo.UpsertTableMetadataBatchAsync(Array.Empty<TableMetadata>(), CancellationToken.None);

        results.ShouldBeEmpty();
        harness.ConnectionOpenCount.ShouldBe(0);
        harness.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidRowInBatch_FailsPreFlight_BeforeAnyDbWork()
    {
        var harness = new RecordingBatchHarness(_dbName);
        var repo = CreateRepository(harness);

        var valid = BuildTable("sales", "crm", "customers");
        var reservedSchema = BuildTable("sales", "pg_catalog", "pg_class");
        var missingKeyColumn = BuildTable("sales", "crm", "orders", primaryKey: ["nonexistent_col"], replaceKeyDef: true);

        await Should.ThrowAsync<Exception>(() => repo.UpsertTableMetadataBatchAsync([valid, reservedSchema], CancellationToken.None));
        harness.ConnectionOpenCount.ShouldBe(0);
        harness.Commands.ShouldBeEmpty();

        await Should.ThrowAsync<Exception>(() => repo.UpsertTableMetadataBatchAsync([valid, missingKeyColumn], CancellationToken.None));
        harness.ConnectionOpenCount.ShouldBe(0);
        harness.Commands.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DuplicateIdentifiers_LatestWins_AndLocksTablesInDeterministicOrder()
    {
        var harness = new RecordingBatchHarness(_dbName);
        var repo = CreateRepository(harness);

        var zTable = BuildTable("zeta", "dbo", "t_z", description: "Z desc");
        var aFirst = BuildTable("alpha", "dbo", "t_a", description: "Old A", sensitivity: "LOW");
        var mTable = BuildTable("mid", "dbo", "t_m", description: "M desc");
        var aSecond = BuildTable("alpha", "dbo", "t_a", description: "Latest A", sensitivity: "HIGH");

        var results = await repo.UpsertTableMetadataBatchAsync([zTable, aFirst, mTable, aSecond], CancellationToken.None);

        results.Count.ShouldBe(4);
        results[1].Metadata.Table.Description.ShouldBe("Latest A");
        results[1].Metadata.Table.Sensitivity.ShouldBe("HIGH");
        results[3].Metadata.Table.Description.ShouldBe("Latest A");
        results[3].Metadata.Table.Sensitivity.ShouldBe("HIGH");

        var tableUpserts = harness.Commands
            .Where(c => c.Sql.StartsWith("INSERT INTO TABLES ", StringComparison.Ordinal))
            .Select(c => $"{c.Params["@domain"]}.{c.Params["@schema"]}.{c.Params["@tableName"]}")
            .ToList();

        tableUpserts.ShouldBe(["alpha.dbo.t_a", "mid.dbo.t_m", "zeta.dbo.t_z"]);
    }

    [Fact]
    public async Task LargeBatch_ChunksByParameterBudget_AndBoundsCommandParameters()
    {
        var harness = new RecordingBatchHarness(_dbName);
        var repo = CreateRepository(harness);

        var batch = Enumerable.Range(0, 502)
            .Select(i => BuildTable("bulk", "dbo", $"table_{i:D4}", includeColumns: false))
            .ToList();

        var results = await repo.UpsertTableMetadataBatchAsync(batch, CancellationToken.None);

        results.Count.ShouldBe(502);
        results.ShouldAllBe(r => r.Kind == CatalogUpsertKind.Created && r.Classification == CatalogWriteClassification.Additive);

        var lockSelects = harness.Commands
            .Where(c => c.Sql.Contains("FOR UPDATE", StringComparison.Ordinal))
            .ToList();
        lockSelects.Count.ShouldBe(2);
        lockSelects[0].Params.Count.ShouldBe(1500);
        lockSelects[1].Params.Count.ShouldBe(6);

        var budgetHarness = new RecordingBatchHarness(_dbName);
        var budgetProvider = new FixedChunkBudgetProvider(chunkSize: 2);
        var budgetDialect = new CustomBudgetDialect(budgetProvider, () => budgetHarness.CreateConnection());
        var customRepo = new GovernanceRepository(budgetDialect, options: Options.Create(new GatewayOptions()));

        var smallBatch = Enumerable.Range(0, 5)
            .Select(i => BuildTable("budget", "dbo", $"b_{i}", includeColumns: false))
            .ToList();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            // Verify chunking calls budget provider and caps lock SELECT parameter counts at <= 2 * 3 = 6.
            try
            {
                await customRepo.UpsertTableMetadataBatchAsync(smallBatch, CancellationToken.None);
            }
            catch (DbException ex)
            {
                // The empty upsert statements of the stub dialect do not execute; inspect the pre-write chunk SELECT commands first.
                throw new InvalidOperationException(ex.Message, ex);
            }
        });

        budgetProvider.Calls.ShouldBeGreaterThanOrEqualTo(1);
        var customSelects = budgetHarness.Commands
            .Where(c => c.Sql.Contains("UPDLOCK", StringComparison.Ordinal))
            .ToList();
        customSelects.ShouldNotBeEmpty();
        customSelects[0].Params.Count.ShouldBeLessThanOrEqualTo(6);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AllUnchangedChunk_RollsBackWithZeroEpochOrVersionBump()
    {
        var harness = new RecordingBatchHarness(_dbName);
        var epochService = new RecordingEpochValidationService();
        var eventBus = new RecordingEventBus();
        var repo = CreateRepository(harness, epochService: epochService, eventBus: eventBus);

        var t1 = BuildTable("core", "dbo", "users", description: "Users table");
        var t2 = BuildTable("core", "dbo", "accounts", description: "Accounts table");

        await repo.UpsertTableMetadataBatchAsync([t1, t2], CancellationToken.None);
        var versionBefore = await repo.GetCatalogVersionAsync(CancellationToken.None);
        var epoch1Before = await repo.GetTableEpochAsync(t1.Identifier, CancellationToken.None);
        var epoch2Before = await repo.GetTableEpochAsync(t2.Identifier, CancellationToken.None);

        harness.ResetCounters();
        epochService.Invalidations.Clear();
        eventBus.CatalogChangedCount = 0;

        var rerun = await repo.UpsertTableMetadataBatchAsync([t1, t2], CancellationToken.None);

        rerun.Count.ShouldBe(2);
        rerun.ShouldAllBe(r => r.Kind == CatalogUpsertKind.Unchanged && r.Classification == CatalogWriteClassification.Unchanged);
        harness.CommittedTransactions.ShouldBe(0);
        harness.RolledBackTransactions.ShouldBe(1);

        (await repo.GetCatalogVersionAsync(CancellationToken.None)).ShouldBe(versionBefore);
        (await repo.GetTableEpochAsync(t1.Identifier, CancellationToken.None)).ShouldBe(epoch1Before);
        (await repo.GetTableEpochAsync(t2.Identifier, CancellationToken.None)).ShouldBe(epoch2Before);
        epochService.Invalidations.ShouldBeEmpty();
        eventBus.CatalogChangedCount.ShouldBe(0);
    }

    [Fact]
    public async Task MixedChunk_AndDocumentationPrecedence_BumpsEpochOnlyForChangedTables_AndPreservesGovernanceFields()
    {
        var harness = new RecordingBatchHarness(_dbName);
        var epochService = new RecordingEpochValidationService();
        var options = new GatewayOptions
        {
            Catalog = new DataCatalogOptions { DefaultVisibility = DatasetVisibility.Summary }
        };
        var repo = CreateRepository(harness, options: options, epochService: epochService);

        var tUnchanged = BuildTable("crm", "dbo", "static_tbl", description: "Manual text", docSource: "Manual");
        var tUpdated = BuildTable("crm", "dbo", "dynamic_tbl", description: "Old text", docSource: "dbt");
        await repo.UpsertTableMetadataBatchAsync([tUnchanged, tUpdated], CancellationToken.None);

        // Governance-set visibility tier (Hidden) must never be overwritten by later ingestion batch upserts.
        await repo.SetDatasetVisibilityAsync(tUpdated.Identifier, DatasetVisibility.Hidden, new Sid("S-1-5-21-admin"), CancellationToken.None);

        var epochUnchangedBefore = await repo.GetTableEpochAsync(tUnchanged.Identifier, CancellationToken.None);
        var epochUpdatedBefore = await repo.GetTableEpochAsync(tUpdated.Identifier, CancellationToken.None);
        epochService.Invalidations.Clear();

        // Lower-precedence dbt attempt on tUnchanged leaves Manual description intact (-> Unchanged); tUpdated updates description.
        var dbtAttempt = BuildTable("crm", "dbo", "static_tbl", description: "Overwritten by dbt?", docSource: "dbt");
        var newDbt = BuildTable("crm", "dbo", "dynamic_tbl", description: "Fresh dbt desc", docSource: "dbt");

        var results = await repo.UpsertTableMetadataBatchAsync([dbtAttempt, newDbt], CancellationToken.None);

        var unchangedResult = results[0];
        unchangedResult.Kind.ShouldBe(CatalogUpsertKind.Unchanged);
        unchangedResult.Classification.ShouldBe(CatalogWriteClassification.Unchanged);
        unchangedResult.Metadata.Table.Description.ShouldBe("Manual text");

        results[1].Kind.ShouldBe(CatalogUpsertKind.Updated);
        results[1].Metadata.Table.Description.ShouldBe("Fresh dbt desc");

        (await repo.GetTableEpochAsync(tUnchanged.Identifier, CancellationToken.None)).ShouldBe(epochUnchangedBefore);
        (await repo.GetTableEpochAsync(tUpdated.Identifier, CancellationToken.None)).ShouldBeGreaterThan(epochUpdatedBefore);

        var vis = await ReadStoredVisibilityAsync(tUpdated.Identifier);
        vis.ShouldBe((int)DatasetVisibility.Hidden);

        epochService.Invalidations.ShouldNotContain(tUnchanged.Identifier);
        epochService.Invalidations.Count(id => id.Equals(tUpdated.Identifier)).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CatalogWriteScope_DefersAdditiveChunk_AndFlushesOnRestrictiveItemInChunk()
    {
        var harness = new RecordingBatchHarness(_dbName);
        var eventBus = new RecordingEventBus();
        var repo = CreateRepository(harness, eventBus: eventBus);
        var dirty = new CatalogDirtyFlag();
        var scopeFactory = new CatalogWriteScopeFactory(repo, dirty, timeProvider: new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)));

        var initial = await repo.GetCatalogVersionAsync(CancellationToken.None);
        var add1 = BuildTable("scope", "dbo", "t_1");
        var add2 = BuildTable("scope", "dbo", "t_2");

        await using (scopeFactory.Begin("additive-batch"))
        {
            var res1 = await repo.UpsertTableMetadataBatchAsync([add1, add2], CancellationToken.None);
            res1.ShouldAllBe(r => r.Classification == CatalogWriteClassification.Additive);

            // Additive batch inside scope commits tables/epochs but defers CATALOG_STATE version bump & event until scope exit.
            (await repo.GetCatalogVersionAsync(CancellationToken.None)).ShouldBe(initial);
            eventBus.CatalogChangedCount.ShouldBe(0);
        }

        var afterAdditive = await repo.GetCatalogVersionAsync(CancellationToken.None);
        afterAdditive.StructureVersion.ShouldBeGreaterThan(initial.StructureVersion);
        eventBus.CatalogChangedCount.ShouldBe(1);

        // Seed t_restrict then mix an Additive new table with a Restrictive update (masking rule added) in one chunk.
        var restrictBase = BuildTable("scope", "dbo", "t_restrict");
        await repo.UpsertTableMetadataBatchAsync([restrictBase], CancellationToken.None);
        var beforeMixed = await repo.GetCatalogVersionAsync(CancellationToken.None);
        eventBus.CatalogChangedCount = 0;

        await using (scopeFactory.Begin("mixed-batch"))
        {
            // Queue a prior additive write in the scope before executing the mixed batch.
            var addQueued = BuildTable("scope", "dbo", "t_queued");
            await repo.UpsertTableMetadataBatchAsync([addQueued], CancellationToken.None);
            (await repo.GetCatalogVersionAsync(CancellationToken.None)).ShouldBe(beforeMixed);

            var add3 = BuildTable("scope", "dbo", "t_3");
            var restrictMasked = BuildTable("scope", "dbo", "t_restrict", masks: new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = new MaskingRule { RuleType = "REDACT" }
            });

            var resMixed = await repo.UpsertTableMetadataBatchAsync([add3, restrictMasked], CancellationToken.None);
            resMixed.ShouldContain(r => r.Classification == CatalogWriteClassification.Restrictive);

            // Restrictive item in chunk forces immediate CATALOG_STATE bump + event + pending scope flush BEFORE exiting using block.
            (await repo.GetCatalogVersionAsync(CancellationToken.None)).StructureVersion.ShouldBeGreaterThan(beforeMixed.StructureVersion);
            eventBus.CatalogChangedCount.ShouldBe(2);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TransientDeadlockConflict_RetriesUpToThreeTimes_WithZeroPrematureEpochOrInvalidation()
    {
        var epochService = new RecordingEpochValidationService();
        var eventBus = new RecordingEventBus();
        var harness = new RecordingBatchHarness(_dbName);
        var repo = CreateRepository(harness, epochService: epochService, eventBus: eventBus);

        // Fail attempts 1 & 2 right at COMMIT, succeed on attempt 3.
        harness.TransientFailuresRemaining = 2;
        var items = new[]
        {
            BuildTable("retry", "dbo", "orders"),
            BuildTable("retry", "dbo", "items")
        };

        var res = await repo.UpsertTableMetadataBatchAsync(items, CancellationToken.None);

        res.Count.ShouldBe(2);
        harness.RolledBackTransactions.ShouldBe(2);
        harness.CommittedTransactions.ShouldBe(1);
        epochService.Invalidations.Count(id => id.Equals(items[0].Identifier)).ShouldBe(1);
        epochService.Invalidations.Count(id => id.Equals(items[1].Identifier)).ShouldBe(1);
        eventBus.CatalogChangedCount.ShouldBe(1);

        // When 3 consecutive attempts fail, rethrow cleanly with zero commits, zero events, and zero invalidations.
        harness.ResetCounters();
        epochService.Invalidations.Clear();
        eventBus.CatalogChangedCount = 0;
        harness.TransientFailuresRemaining = 5;

        var failing = BuildTable("retry", "dbo", "always_deadlocks");
        await Should.ThrowAsync<FakeTransientDeadlockException>(() => repo.UpsertTableMetadataBatchAsync([failing], CancellationToken.None));

        harness.CommittedTransactions.ShouldBe(0);
        harness.RolledBackTransactions.ShouldBe(3);
        (await repo.GetTableMetadataAsync(failing.Identifier, CancellationToken.None)).ShouldBeNull();
        epochService.Invalidations.ShouldBeEmpty();
        eventBus.CatalogChangedCount.ShouldBe(0);
    }

    private GovernanceRepository CreateRepository(
        RecordingBatchHarness harness,
        GatewayOptions? options = null,
        IEpochValidationService? epochService = null,
        IEventBus? eventBus = null)
    {
        var dialect = new PostgreSqlGovernanceDialect(() => harness.CreateConnection());
        return new GovernanceRepository(
            dialect,
            epochValidationService: epochService,
            options: Options.Create(options ?? new GatewayOptions()),
            eventBus: eventBus);
    }

    private async Task<int?> ReadStoredVisibilityAsync(TableIdentifier id)
    {
        await using var connection = new NpgsqlConnection(_dbName);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT d.visibility
            FROM TABLE_CATALOG_SETTINGS d
            INNER JOIN TABLES t ON d.table_id = t.id
            WHERE LOWER(t.source_name) = LOWER(@d) AND LOWER(t.schema_name) = LOWER(@s) AND LOWER(t.table_name) = LOWER(@t)
            """;
        cmd.Parameters.AddWithValue("d", id.Domain);
        cmd.Parameters.AddWithValue("s", id.Schema);
        cmd.Parameters.AddWithValue("t", id.TableName);
        var scalar = await cmd.ExecuteScalarAsync();
        return scalar is null or DBNull ? null : Convert.ToInt32(scalar);
    }

    private static TableMetadata BuildTable(
        string domain,
        string schema,
        string tableName,
        string sensitivity = "NORMAL",
        string? description = null,
        string? docSource = null,
        bool includeColumns = true,
        Dictionary<string, MaskingRule>? masks = null,
        IReadOnlyList<string>? primaryKey = null,
        bool replaceKeyDef = false)
    {
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, schema, tableName),
            Table = new Table
            {
                Id = tableId,
                SourceType = "PostgreSQL",
                SourceName = domain,
                SchemaName = schema,
                TableName = tableName,
                DisplayName = tableName,
                Sensitivity = sensitivity,
                RequiresFourEyes = false,
                IsActive = true,
                DataSourceType = DataSourceType.Sql,
                Description = description,
                DocumentationSource = docSource
            },
            Columns = includeColumns
                ?
                [
                    new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "id", DataType = "integer" },
                    new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "name", DataType = "text" }
                ]
                : [],
            ColumnMaskingRules = masks ?? new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase),
            PrimaryKeyColumns = primaryKey ?? [],
            ReplaceKeyDefinition = replaceKeyDef
        };
    }
}
