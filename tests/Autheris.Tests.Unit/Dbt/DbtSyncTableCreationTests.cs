namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #282: a dbt sync registers manifest items that have no catalog entry (inactive by default).</summary>
public sealed class DbtSyncTableCreationTests
{
    private sealed record Col(string Name, string? Type = "varchar", string? Description = null);

    private sealed record Item(
        string Database,
        string Schema,
        string Name,
        string Materialized = "table",
        string Kind = "model",
        Col[]? Columns = null,
        string? Description = null);

    private static string Manifest(params Item[] items)
    {
        var nodes = new Dictionary<string, object>();
        var sources = new Dictionary<string, object>();
        var index = 0;
        foreach (var item in items)
        {
            var columns = (item.Columns ?? [new Col("id")]).ToDictionary(
                c => c.Name,
                c => (object)new { name = c.Name, data_type = c.Type, description = c.Description, tags = Array.Empty<string>(), meta = new { } });
            index++;
            if (item.Kind == "source")
            {
                sources[$"source.proj.{item.Database}.{item.Schema}.{item.Name}.{index}"] = new
                {
                    name = item.Name, database = item.Database, schema = item.Schema, description = item.Description,
                    tags = new[] { "raw" }, meta = new { }, columns
                };
            }
            else
            {
                nodes[$"model.proj.{item.Name}.{index}"] = new
                {
                    name = item.Name, database = item.Database, schema = item.Schema, description = item.Description,
                    config = new { materialized = item.Materialized }, tags = new[] { "finance" }, meta = new { }, columns,
                    depends_on = new { nodes = Array.Empty<string>() }
                };
            }
        }

        return JsonSerializer.Serialize(new { metadata = new { dbt_version = "1.8.0" }, nodes, sources });
    }

    private static GatewayOptions Options(
        bool create = true,
        bool activate = false,
        Dictionary<string, string>? mappings = null,
        params (string Name, string Provider)[] connections)
    {
        var dbt = new DbtOptions
        {
            CreateMissingTables = create,
            ActivateNewTables = activate,
            DatabaseMappings = new Dictionary<string, string>(mappings ?? [], StringComparer.OrdinalIgnoreCase)
        };
        var sources = new SqlDataSourceOptions();
        foreach (var (name, provider) in connections.Length == 0 ? [("warehouse", "PostgreSql")] : connections)
        {
            sources.Connections[name] = new DataSourceConnectionOptions { Provider = provider };
        }

        return new GatewayOptions { Dbt = dbt, DataSources = sources };
    }

    private sealed class Harness
    {
        public Dictionary<string, TableMetadata> Store { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ITableMetadataRepository Repo { get; } = Substitute.For<ITableMetadataRepository>();
        public IDatasetTagImportService Tags { get; } = Substitute.For<IDatasetTagImportService>();
        public DbtMetadataIngestionService Service { get; }

        public Harness(GatewayOptions options)
        {
            Repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Store.GetValueOrDefault(call.Arg<TableIdentifier>().ToString())));
            Repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var m = call.Arg<TableMetadata>();
                    Store[m.Identifier.ToString()] = m;
                    return Task.FromResult(m);
                });
            Tags.ImportDbtTagsAsync(Arg.Any<TableIdentifier>(), Arg.Any<IEnumerable<string?>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(new DatasetTagImportResult(false, 0, 0, false)));
            Service = Build(Repo, options, Tags);
        }
    }

    private static DbtMetadataIngestionService Build(ITableMetadataRepository repo, GatewayOptions options, IDatasetTagImportService? tags = null)
        => new(
            proposalRepository: new InMemoryDbtProposalRepository(),
            metadataRepository: repo,
            lineageGraphStore: Substitute.For<ILineageGraphStore>(),
            epochRepository: null,
            gatewayOptions: Microsoft.Extensions.Options.Options.Create(options),
            sqlEndpointLoader: null,
            relationRepository: null,
            virtualFilterAdmin: null,
            rebacStore: null,
            tagImport: tags,
            syntheticRowGenerator: null,
            logger: NullLogger<DbtMetadataIngestionService>.Instance);

    private static Task<DbtSyncResult> Sync(DbtMetadataIngestionService service, string manifest, bool dryRun = false)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        return service.IngestManifestStreamAsClusterAdminAsync(stream, dryRun);
    }

    private static Item Model(string name = "orders", string db = "warehouse", string schema = "public") => new(db, schema, name);

    // AC-1
    [Fact]
    public async Task Sync_EmptyCatalog_TwoModelsOneSource_CreatesThreeInactiveTables()
    {
        var h = new Harness(Options());
        var result = await Sync(h.Service, Manifest(
            Model("orders"), Model("customers"),
            new Item("warehouse", "raw", "contacts", "source", "source", [new Col("id"), new Col("email")])));

        result.Success.ShouldBeTrue();
        result.CreatedTables.Count.ShouldBe(3);
        result.SkippedItems.ShouldBeEmpty();
        h.Store.Count.ShouldBe(3);
        h.Store.Values.ShouldAllBe(m => !m.Table.IsActive && m.Table.SourceName == "warehouse" && m.Table.SourceType == "PostgreSql");
        h.Store["warehouse.raw.contacts"].Columns.Count.ShouldBe(2);
    }

    // AC-2
    [Fact]
    public async Task Sync_ActivateNewTablesTrue_CreatesActiveTables()
    {
        var h = new Harness(Options(activate: true));
        var result = await Sync(h.Service, Manifest(Model()));

        result.CreatedTables.Count.ShouldBe(1);
        h.Store.Values.Single().Table.IsActive.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Contains("without masks or consents"));
    }

    // AC-3 / SEC-282-3
    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_UnmappedDatabase_SkipsWithNoDataSourceMapping()
    {
        var h = new Harness(Options());
        var result = await Sync(h.Service, Manifest(Model(db: "elsewhere")));

        result.CreatedTables.ShouldBeEmpty();
        result.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.NoDataSourceMapping);
        h.Store.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_UnmappedDatabase_NoFallbackToDefaultConnection()
    {
        // SEC-282-3: with one configured connection (and a "*" mapping), an unknown database never lands on it.
        var h = new Harness(Options(mappings: new() { ["*"] = "warehouse" }));
        var result = await Sync(h.Service, Manifest(Model(db: "other"), Model("star", db: "*")));

        result.CreatedTables.ShouldBeEmpty();
        result.SkippedItems.ShouldAllBe(s => s.Reason == DbtSyncSkipReasons.NoDataSourceMapping);
        h.Store.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_MappingTargetMissingFromConnections_SkipsAndCreatesNothing()
    {
        var h = new Harness(Options(mappings: new() { ["lake"] = "does-not-exist" }));
        var result = await Sync(h.Service, Manifest(Model(db: "lake")));

        result.CreatedTables.ShouldBeEmpty();
        result.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.NoDataSourceMapping);
        await h.Repo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sync_DatabaseMappingsEntry_ResolvesConnection()
    {
        var h = new Harness(Options(mappings: new() { ["lake"] = "WAREHOUSE" }));
        var result = await Sync(h.Service, Manifest(Model(db: "lake")));

        result.CreatedTables.Count.ShouldBe(1);
        var table = h.Store["lake.public.orders"].Table;
        table.SourceName.ShouldBe("warehouse"); // canonical casing of the configured key, not the manifest or mapping string
    }

    [Fact]
    public async Task Sync_UnsupportedProvider_SkipsWithDataSourceNotSql()
    {
        var h = new Harness(Options(connections: [("warehouse", "Excel")]));
        var result = await Sync(h.Service, Manifest(Model()));

        result.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.DataSourceNotSql);
        h.Store.ShouldBeEmpty();
    }

    // AC-4 / SEC-282-5
    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_ExistingEntry_IsActiveSensitivityMasksConsentsUnchanged()
    {
        var h = new Harness(Options());
        var id = new TableIdentifier("warehouse", "public", "orders");
        h.Store[id.ToString()] = new TableMetadata
        {
            Identifier = id,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "warehouse", SchemaName = "public", TableName = "orders", Sensitivity = "HIGH", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var result = await Sync(h.Service, Manifest(Model()));

        result.CreatedTables.ShouldBeEmpty();
        result.SkippedItems.ShouldBeEmpty();
        var stored = h.Store[id.ToString()];
        stored.Table.IsActive.ShouldBeTrue();
        stored.Table.Sensitivity.ShouldBe("HIGH");
        stored.Columns.Single().DataType.ShouldBe("int");
    }

    [Fact]
    public async Task Sync_SecondRun_CreatesNothingAndKeepsIsActiveAndSensitivity()
    {
        var h = new Harness(Options());
        await Sync(h.Service, Manifest(Model()));
        var table = h.Store["warehouse.public.orders"].Table;
        var activated = new Table { Id = table.Id, SourceType = table.SourceType, SourceName = table.SourceName, SchemaName = "public", TableName = "orders", Sensitivity = "HIGH", IsActive = true };
        h.Store["warehouse.public.orders"] = new TableMetadata { Identifier = h.Store["warehouse.public.orders"].Identifier, Table = activated, Columns = h.Store["warehouse.public.orders"].Columns };

        var second = await Sync(h.Service, Manifest(Model()));

        second.CreatedTables.ShouldBeEmpty();
        h.Store["warehouse.public.orders"].Table.IsActive.ShouldBeTrue();
        h.Store["warehouse.public.orders"].Table.Sensitivity.ShouldBe("HIGH");
    }

    // SEC-282-2
    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_NewTable_IsInactiveNormalAndHasNoMasksOrConsents()
    {
        var h = new Harness(Options());
        await Sync(h.Service, Manifest(Model()));

        var created = h.Store["warehouse.public.orders"];
        created.Table.IsActive.ShouldBeFalse();
        created.Table.Sensitivity.ShouldBe("NORMAL");
        created.Table.RequiresFourEyes.ShouldBeFalse();
        created.ColumnMaskingRules.ShouldBeEmpty();
        created.Table.RlsExpression.ShouldBeNull();
    }

    // AC-7 / SEC-282-1
    [Theory]
    [InlineData("orders\"; DROP TABLE x;--")]
    [InlineData("or'ders")]
    [InlineData("a;b")]
    [Trait("Category", "Security")]
    public async Task Sync_TableNameWithQuoteOrSemicolon_SkipsWithInvalidIdentifier(string name)
    {
        var h = new Harness(Options());
        var result = await Sync(h.Service, Manifest(Model(name)));

        result.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.InvalidIdentifier);
        h.Store.ShouldBeEmpty();
    }

    // The TableIdentifier constructor already refuses dots and control characters, so these reach the factory directly.
    [Theory]
    [InlineData("orders\n")]
    [InlineData("my.orders")]
    [InlineData("or\u0000ders")]
    [Trait("Category", "Security")]
    public void Sync_TableNameWithNewlineSuffix_SkipsWithInvalidIdentifier(string name)
    {
        var model = new DbtModelDefinition(
            "model.proj.x", name, "warehouse", "public", "table", null, [], new Dictionary<string, string>(),
            new Dictionary<string, DbtColumnDefinition> { ["id"] = new("id", "int", null, [], new Dictionary<string, string>()) }, []);

        DbtCatalogTableFactory.TryBuild(model, Options(), out var metadata, out var reason).ShouldBeFalse();
        metadata.ShouldBeNull();
        reason.ShouldBe(DbtSyncSkipReasons.InvalidIdentifier);
    }

    [Theory]
    [InlineData("ordérs")]
    [InlineData("orders٣")] // Arabic-Indic digit
    [InlineData("Ａbc")] // fullwidth letter
    [Trait("Category", "Security")]
    public async Task Sync_UnicodeDigitOrLetterInIdentifier_SkipsWithInvalidIdentifier(string name)
    {
        var h = new Harness(Options());
        var result = await Sync(h.Service, Manifest(Model(name)));

        result.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.InvalidIdentifier);
        h.Store.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("my orders")]
    [InlineData("my-orders")]
    [Trait("Category", "Security")]
    public async Task Sync_QuotedIdentifierWithSpaceOrDot_SkipsWithInvalidIdentifier(string name)
    {
        var h = new Harness(Options());
        var result = await Sync(h.Service, Manifest(Model(name), Model("ok", schema: name)));

        result.SkippedItems.Count.ShouldBe(2);
        result.SkippedItems.ShouldAllBe(s => s.Reason == DbtSyncSkipReasons.InvalidIdentifier);
        h.Store.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_InvalidColumnName_SkipsWholeTable()
    {
        var h = new Harness(Options());
        var item = Model() with { Columns = [new Col("id"), new Col("bad col")] };
        var result = await Sync(h.Service, Manifest(item));

        result.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.InvalidIdentifier);
        h.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_MixedCaseIdentifiers_ArePreserved()
    {
        var h = new Harness(Options());
        await Sync(h.Service, Manifest(Model("Orders_2024", schema: "Sales")));

        var table = h.Store.Values.Single().Table;
        table.TableName.ShouldBe("Orders_2024");
        table.SchemaName.ShouldBe("Sales");
    }

    // AC-8 / SEC-282-7
    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_DryRun_PerformsNoRepositoryWrites()
    {
        var h = new Harness(Options());
        var result = await Sync(h.Service, Manifest(Model("orders"), Model("bad name"), Model("x", db: "elsewhere")), dryRun: true);

        result.CreatedTables.ShouldBe(["warehouse.public.orders"]);
        result.SkippedItems.Count.ShouldBe(2);
        h.Store.ShouldBeEmpty();
        await h.Repo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
        await h.Tags.DidNotReceive().ImportDbtTagsAsync(Arg.Any<TableIdentifier>(), Arg.Any<IEnumerable<string?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sync_CreateMissingTablesFalse_SkipsWithCreationDisabled()
    {
        var h = new Harness(Options(create: false));
        var result = await Sync(h.Service, Manifest(Model()));

        result.CreatedTables.ShouldBeEmpty();
        result.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.CreationDisabled);
        h.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_EphemeralModel_SkipsWithIneligible()
    {
        var h = new Harness(Options());
        var result = await Sync(h.Service, Manifest(Model() with { Materialized = "ephemeral" }));

        result.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.Ineligible);
        h.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_DuplicateIdentifier_SkipsSecondWithDuplicate()
    {
        var repo = Substitute.For<ITableMetadataRepository>(); // always "missing", so the second item reaches the creation branch too
        var service = Build(repo, Options());
        var result = await Sync(service, Manifest(Model(), Model()), dryRun: true);

        result.CreatedTables.Count.ShouldBe(1);
        result.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.Duplicate);
    }

    [Fact]
    public async Task Sync_UpsertThrows_RecordsCreateFailedAndContinues()
    {
        var h = new Harness(Options());
        h.Repo.UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m => m.Identifier.TableName == "broken"), Arg.Any<CancellationToken>())
            .Returns<Task<TableMetadata>>(_ => throw new InvalidOperationException("db down"));

        var result = await Sync(h.Service, Manifest(Model("broken"), Model("fine")));

        result.Success.ShouldBeTrue();
        result.CreatedTables.ShouldBe(["warehouse.public.fine"]);
        result.SkippedItems.Single().ShouldBe(new DbtSyncSkippedItem("warehouse.public.broken", "model", DbtSyncSkipReasons.CreateFailed));
    }

    [Fact]
    public async Task Sync_MissingDataType_StoresString()
    {
        var h = new Harness(Options());
        await Sync(h.Service, Manifest(Model() with { Columns = [new Col("id", null), new Col("name", "text")] }));

        var columns = h.Store.Values.Single().Columns;
        columns.Single(c => c.ColumnName == "id").DataType.ShouldBe("string");
        columns.Single(c => c.ColumnName == "name").DataType.ShouldBe("text");
    }

    [Fact]
    public async Task Sync_CreatedTable_GetsManifestDescriptionsAndTags()
    {
        var h = new Harness(Options());
        var item = Model() with { Description = "All orders", Columns = [new Col("id", "int", "Order id")] };
        await Sync(h.Service, Manifest(item));

        var created = h.Store["warehouse.public.orders"];
        created.Table.Description.ShouldBe("All orders");
        created.Columns.Single().Description.ShouldBe("Order id");
        await h.Tags.Received(1).ImportDbtTagsAsync(
            Arg.Is<TableIdentifier>(t => t.TableName == "orders"), Arg.Any<IEnumerable<string?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_MetadataUnavailable_NeverCreates()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns<Task<TableMetadata?>>(call => throw new TableMetadataUnavailableException(call.Arg<TableIdentifier>()));
        var service = Build(repo, Options());

        var result = await Sync(service, Manifest(Model()));

        result.CreatedTables.ShouldBeEmpty();
        result.SkippedItems.ShouldBeEmpty();
        await repo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_UpsertRaceExistingRowAppears_DoesNotOverwriteGovernance()
    {
        var id = new TableIdentifier("warehouse", "public", "orders");
        var existing = new TableMetadata
        {
            Identifier = id,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "warehouse", SchemaName = "public", TableName = "orders", Sensitivity = "HIGH", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        // First read: missing. Every later read: the row another writer created meanwhile.
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(null), Task.FromResult<TableMetadata?>(existing));
        repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<TableMetadata>()));
        var service = Build(repo, Options());

        var result = await Sync(service, Manifest(Model()));

        result.CreatedTables.ShouldBeEmpty();
        await repo.DidNotReceive().UpsertTableMetadataAsync(
            Arg.Is<TableMetadata>(m => m.Table.IsActive == false || m.Table.Sensitivity == "NORMAL"), Arg.Any<CancellationToken>());
    }

    // SEC-282-6
    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_OtherTenantModel_RejectedAndNotCreated()
    {
        var h = new Harness(Options(connections: [("warehouse", "PostgreSql"), ("tenantb", "PostgreSql")]));
        var caller = new DbtGovernanceImportCaller(
            new VirtualFilterActor(new Sid("S-1-5-21-1"), IsSync: false, null), new TenantId("tenanta"), IsClusterAdmin: false);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Manifest(Model(db: "tenantb"), Model("mine", db: "tenanta"))));

        var result = await h.Service.IngestManifestStreamAsync(stream, caller);

        result.RejectedModels.ShouldContain(r => r.Contains("tenantb"));
        h.Store.Keys.ShouldNotContain(k => k.StartsWith("tenantb", StringComparison.OrdinalIgnoreCase));
        result.SkippedItems.ShouldNotContain(s => s.Name.StartsWith("tenantb", StringComparison.OrdinalIgnoreCase));
    }

    // SEC-282-8
    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_ExceedsTableOrColumnCap_SkipsExcess()
    {
        var h = new Harness(Options());
        var wide = Model("wide") with { Columns = Enumerable.Range(0, 1001).Select(i => new Col($"c{i}")).ToArray() };
        var wideResult = await Sync(h.Service, Manifest(wide, Model("ok")));
        wideResult.CreatedTables.ShouldBe(["warehouse.public.ok"]);
        wideResult.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.Ineligible);

        var many = Enumerable.Range(0, 5001).Select(i => Model($"t{i}")).ToArray();
        var dry = await Sync(Build(Substitute.For<ITableMetadataRepository>(), Options()), Manifest(many), dryRun: true);
        dry.CreatedTables.Count.ShouldBe(5000);
        dry.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.Ineligible);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_SkippedItemsAreCappedWithTruncatedFlag()
    {
        var items = Enumerable.Range(0, 501).Select(i => Model($"t{i}", db: "elsewhere")).ToArray();
        var result = await Sync(Build(Substitute.For<ITableMetadataRepository>(), Options()), Manifest(items), dryRun: true);

        result.SkippedItems.Count.ShouldBe(500);
        result.SkippedItemsTruncated.ShouldBeTrue();
    }

    // SEC-282-9
    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_LogsAndAuditSanitizeControlCharsInNames()
    {
        // Control characters cannot reach the service through a TableIdentifier; the reported name still goes through ForLog (cap 128).
        var h = new Harness(Options());
        var result = await Sync(h.Service, Manifest(Model(new string('a', 200) + "-x")));

        var name = result.SkippedItems.Single().Name;
        name.Any(char.IsControl).ShouldBeFalse();
        name.Length.ShouldBeLessThanOrEqualTo(128);
        DbtIdentifierSanitizer.ForLog("evil\u0001\u001bname\r\nforged").Any(char.IsControl).ShouldBeFalse();
    }

    [Theory]
    [InlineData("my.orders")]
    [InlineData("or\u0001ders")]
    public async Task Sync_DottedOrControlCharName_SkipsItemAndContinues(string name)
    {
        var h = new Harness(Options());
        var result = await Sync(h.Service, Manifest(Model(name), Model("fine")));

        result.Success.ShouldBeTrue();
        result.CreatedTables.ShouldBe(["warehouse.public.fine"]);
        result.SkippedItems.Single().Reason.ShouldBe(DbtSyncSkipReasons.InvalidIdentifier);
        result.SkippedItems.Single().Name.Any(char.IsControl).ShouldBeFalse();
    }
}
