namespace Autheris.Modules.Dbt.Tests;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #674: dbt sync skips unchanged tables with zero catalog writes, zero version/epoch bumps, and zero catalog:changed events.</summary>
public sealed class DbtSyncSkipUnchangedTests
{
    private sealed class CountingHarness
    {
        public Dictionary<string, TableMetadata> Store { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<DatasetTag>> TagStore { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, DiscoveryArtifacts> ArtifactStoreData { get; } = new();
        public Dictionary<string, List<GoldenQueryStoreRecord>> GoldenQueries { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<ColumnClassification>> Classifications { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int UpsertCalls { get; private set; }
        public int UpsertedTableCount { get; private set; }
        public int ReplaceTagsCalls { get; private set; }
        public int ArtifactUpsertCalls { get; private set; }
        public int AuditRecordCalls { get; private set; }
        public int GoldenQueryReplaceCalls { get; private set; }
        public int ClassificationAssignCalls { get; private set; }
        public int CatalogVersionBumps { get; private set; }
        public int TableEpochBumps { get; private set; }
        public int CatalogChangedEvents { get; private set; }

        public ITableMetadataRepository Repo { get; } = Substitute.For<ITableMetadataRepository>();
        public IDiscoveryArtifactStore ArtifactStore { get; } = Substitute.For<IDiscoveryArtifactStore>();
        public IAuditWriter AuditWriter { get; } = Substitute.For<IAuditWriter>();
        public IGoldenQueryRepository GoldenQueryRepo { get; } = Substitute.For<IGoldenQueryRepository>();
        public IClassificationRepository ClassificationRepo { get; } = Substitute.For<IClassificationRepository>();
        public ICatalogVersionFlusher VersionFlusher { get; } = Substitute.For<ICatalogVersionFlusher>();
        public ICatalogDirtyFlag DirtyFlag { get; } = Substitute.For<ICatalogDirtyFlag>();
        public DbtMetadataIngestionService Service { get; }

        public CountingHarness(bool withExtras = true)
        {
            var options = new GatewayOptions
            {
                Dbt = new DbtOptions { CreateMissingTables = true, ActivateNewTables = false },
                Tenancy = new TenancyOptions { DefaultTenantId = "warehouse" }
            };
            options.DataSources.Connections["warehouse"] = new DataSourceConnectionOptions { Provider = "PostgreSql" };

            Repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var id = call.Arg<TableIdentifier>().ToString();
                    if (!Store.TryGetValue(id, out var meta))
                    {
                        return Task.FromResult<TableMetadata?>(null);
                    }

                    var tagNames = TagStore.TryGetValue(id, out var tags)
                        ? tags.Select(t => t.Tag).OrderBy(t => t, StringComparer.Ordinal).ToArray()
                        : Array.Empty<string>();
                    return Task.FromResult<TableMetadata?>(meta with { CatalogTags = tagNames });
                });

            Repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    UpsertCalls++;
                    UpsertedTableCount++;
                    var m = call.Arg<TableMetadata>();
                    Store[m.Identifier.ToString()] = m;
                    CatalogVersionBumps++;
                    TableEpochBumps++;
                    CatalogChangedEvents++;
                    return Task.FromResult(m);
                });

            Repo.UpsertTableMetadataBatchAsync(Arg.Any<IReadOnlyList<TableMetadata>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var batch = call.Arg<IReadOnlyList<TableMetadata>>();
                    UpsertCalls++;
                    UpsertedTableCount += batch.Count;
                    var results = new List<CatalogTableUpsertResult>(batch.Count);
                    foreach (var m in batch)
                    {
                        Store[m.Identifier.ToString()] = m;
                        results.Add(new CatalogTableUpsertResult(m, CatalogUpsertKind.Updated, CatalogWriteClassification.Additive));
                    }

                    if (batch.Count > 0)
                    {
                        CatalogVersionBumps++;
                        TableEpochBumps += batch.Count;
                        CatalogChangedEvents++;
                    }

                    return Task.FromResult<IReadOnlyList<CatalogTableUpsertResult>>(results);
                });

            Repo.GetDatasetCatalogSettingsAsync(Arg.Any<TableIdentifier>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var id = call.Arg<TableIdentifier>().ToString();
                    if (!Store.ContainsKey(id))
                    {
                        return Task.FromResult<DatasetCatalogSettings?>(null);
                    }

                    var tags = TagStore.TryGetValue(id, out var list) ? list.ToArray() : Array.Empty<DatasetTag>();
                    return Task.FromResult<DatasetCatalogSettings?>(new DatasetCatalogSettings(tags, null, false));
                });

            Repo.ReplaceDatasetTagsAsync(Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<DatasetTagSource>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    ReplaceTagsCalls++;
                    var tid = call.Arg<TableIdentifier>();
                    var id = tid.ToString();
                    var incoming = call.Arg<IReadOnlyList<string>>();
                    var source = call.Arg<DatasetTagSource>();
                    var existing = TagStore.TryGetValue(id, out var cur) ? cur : [];
                    var beforeSettings = new DatasetCatalogSettings(existing.ToArray(), null, false);
                    var kept = existing.Where(t => t.Source != source).ToList();
                    foreach (var tag in incoming)
                    {
                        kept.Add(new DatasetTag(tag, source));
                    }

                    var changed = !existing.Where(t => t.Source == source).Select(t => t.Tag).OrderBy(x => x, StringComparer.Ordinal)
                        .SequenceEqual(incoming.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);
                    TagStore[id] = kept;
                    if (changed)
                    {
                        CatalogVersionBumps++;
                        TableEpochBumps++;
                        CatalogChangedEvents++;
                    }

                    var afterSettings = new DatasetCatalogSettings(kept.ToArray(), null, false);
                    return Task.FromResult<DatasetCatalogChange?>(new DatasetCatalogChange(tid, beforeSettings, afterSettings));
                });

            ArtifactStore.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(ArtifactStoreData.GetValueOrDefault(call.Arg<Guid>())));
            ArtifactStore.UpsertAsync(Arg.Any<DiscoveryArtifacts>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    ArtifactUpsertCalls++;
                    var a = call.Arg<DiscoveryArtifacts>();
                    ArtifactStoreData[a.TableId] = a;
                    return Task.FromResult(true);
                });

            AuditWriter.RecordAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    AuditRecordCalls++;
                    return Task.CompletedTask;
                });

            GoldenQueryRepo.GetForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var all = GoldenQueries.Values.SelectMany(x => x).ToList();
                    return Task.FromResult<IReadOnlyList<GoldenQueryStoreRecord>>(all);
                });
            GoldenQueryRepo.ReplaceForTableAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<GoldenQuerySource>(), Arg.Any<IReadOnlyList<GoldenQueryStoreEntry>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    GoldenQueryReplaceCalls++;
                    var tableId = call.Arg<string>();
                    var source = call.Arg<GoldenQuerySource>();
                    var entries = call.Arg<IReadOnlyList<GoldenQueryStoreEntry>>();
                    GoldenQueries[tableId] = entries.Select(e => new GoldenQueryStoreRecord(
                        Id: Guid.NewGuid().ToString(),
                        TableId: tableId,
                        Domain: "warehouse",
                        Schema: "analytics",
                        TableName: "customers",
                        Source: source,
                        SourceKey: e.SourceKey,
                        Title: e.Title,
                        Description: e.Description,
                        QueryText: e.QueryText,
                        VariablesJson: e.VariablesJson,
                        ContentHash: e.ComputeContentHash(),
                        UpdatedAt: DateTimeOffset.UtcNow)).ToList();
                    return Task.CompletedTask;
                });

            ClassificationRepo.GetClassificationsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<DataClassification>>([
                    new DataClassification { Code = "PII", DisplayName = "PII", IsActive = true },
                    new DataClassification { Code = "PUBLIC", DisplayName = "Public", IsActive = true }
                ]));
            ClassificationRepo.GetColumnClassificationsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var colId = call.ArgAt<string>(0);
                    return Task.FromResult<IReadOnlyList<ColumnClassification>>(
                        Classifications.TryGetValue(colId, out var list) ? list : Array.Empty<ColumnClassification>());
                });
            ClassificationRepo.AssignColumnClassificationAsync(Arg.Any<ColumnClassification>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    ClassificationAssignCalls++;
                    var c = call.Arg<ColumnClassification>();
                    if (!Classifications.TryGetValue(c.TableColumnId, out var list))
                    {
                        list = [];
                        Classifications[c.TableColumnId] = list;
                    }

                    list.Add(c);
                    return Task.CompletedTask;
                });

            VersionFlusher.FlushCatalogVersionAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    CatalogVersionBumps++;
                    CatalogChangedEvents++;
                    return Task.CompletedTask;
                });

            var scopeFactory = new CatalogWriteScopeFactory(VersionFlusher, DirtyFlag, NullLogger<CatalogWriteScopeFactory>.Instance);
            var optionsMonitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
            optionsMonitor.CurrentValue.Returns(options);
            var tagImport = new DatasetTagImportService(Repo, AuditWriter, optionsMonitor, NullLogger<DatasetTagImportService>.Instance);

            Service = new DbtMetadataIngestionService(
                proposalRepository: new InMemoryDbtProposalRepository(),
                metadataRepository: Repo,
                lineageGraphStore: Substitute.For<ILineageGraphStore>(),
                epochRepository: null,
                gatewayOptions: Options.Create(options),
                sqlEndpointLoader: null,
                relationRepository: null,
                virtualFilterAdmin: null,
                tagImport: withExtras ? tagImport : null,
                artifactStore: withExtras ? ArtifactStore : null,
                testTargetMap: null,
                logger: NullLogger<DbtMetadataIngestionService>.Instance,
                auditLog: Substitute.For<IAuditLogRepository>(),
                goldenQueryRepository: withExtras ? GoldenQueryRepo : null,
                auditWriter: withExtras ? AuditWriter : null,
                classificationRepository: withExtras ? ClassificationRepo : null,
                catalogWriteScopeFactory: scopeFactory);
        }

        public void ResetCounters()
        {
            UpsertCalls = 0;
            UpsertedTableCount = 0;
            ReplaceTagsCalls = 0;
            ArtifactUpsertCalls = 0;
            AuditRecordCalls = 0;
            GoldenQueryReplaceCalls = 0;
            ClassificationAssignCalls = 0;
            CatalogVersionBumps = 0;
            TableEpochBumps = 0;
            CatalogChangedEvents = 0;
        }
    }

    private static string BuildRichManifest(
        string description = "Customer dimension",
        string[]? tags = null,
        string? sampleCity = "Berlin",
        string[]? requiredFilterColumnGroups = null)
    {
        var modelMeta = new Dictionary<string, object?>
        {
            ["synthetic_samples"] = new[]
            {
                new Dictionary<string, object?> { ["id"] = 1, ["city"] = sampleCity ?? "Berlin" }
            },
            ["golden_queries"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["title"] = "Count customers",
                    ["description"] = "List customers.",
                    ["query"] = "{ customers { id city } }"
                }
            }
        };
        if (requiredFilterColumnGroups is not null)
        {
            modelMeta["required_filter_column_groups"] = new[] { requiredFilterColumnGroups };
        }

        var manifest = new
        {
            metadata = new { dbt_version = "1.8.0" },
            nodes = new Dictionary<string, object>
            {
                ["model.proj.customers"] = new
                {
                    name = "customers",
                    database = "warehouse",
                    schema = "analytics",
                    description,
                    config = new { materialized = "table" },
                    tags = tags ?? ["finance"],
                    meta = modelMeta,
                    constraints = new[]
                    {
                        new { type = "primary_key", columns = new[] { "id" } }
                    },
                    columns = new Dictionary<string, object>
                    {
                        ["id"] = new { name = "id", data_type = "bigint", description = "Customer ID", tags = Array.Empty<string>(), meta = new { } },
                        ["city"] = new { name = "city", data_type = "varchar", description = "City", tags = Array.Empty<string>(), meta = new Dictionary<string, string> { ["classification"] = "PUBLIC" } }
                    },
                    depends_on = new { nodes = Array.Empty<string>() }
                }
            }
        };

        return JsonSerializer.Serialize(manifest);
    }

    private static Task<DbtSyncResult> SyncAsync(DbtMetadataIngestionService service, string manifestJson)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
        return service.IngestManifestStreamAsClusterAdminAsync(stream);
    }

    [Fact]
    public async Task SecondSync_WithIdenticalManifest_PerformsZeroWritesZeroBumpsZeroEvents()
    {
        var h = new CountingHarness();
        var manifest = BuildRichManifest();

        var first = await SyncAsync(h.Service, manifest);
        first.Success.ShouldBeTrue();
        first.CreatedTables.ShouldBe(["warehouse.analytics.customers"]);
        h.UpsertCalls.ShouldBeGreaterThan(0);
        h.ReplaceTagsCalls.ShouldBe(1);
        h.ArtifactUpsertCalls.ShouldBe(1);
        h.GoldenQueryReplaceCalls.ShouldBe(1);
        h.ClassificationAssignCalls.ShouldBe(1);

        h.ResetCounters();

        var second = await SyncAsync(h.Service, manifest);
        second.Success.ShouldBeTrue();
        second.CreatedTables.ShouldBeEmpty();
        h.UpsertCalls.ShouldBe(0);
        h.UpsertedTableCount.ShouldBe(0);
        h.ReplaceTagsCalls.ShouldBe(0);
        h.ArtifactUpsertCalls.ShouldBe(0);
        h.AuditRecordCalls.ShouldBe(0);
        h.GoldenQueryReplaceCalls.ShouldBe(0);
        h.ClassificationAssignCalls.ShouldBe(0);
        h.CatalogVersionBumps.ShouldBe(0);
        h.TableEpochBumps.ShouldBe(0);
        h.CatalogChangedEvents.ShouldBe(0);
    }

    [Fact]
    public async Task SecondSync_WithOneModifiedTable_OnlyUpsertsModifiedTable()
    {
        var h = new CountingHarness(withExtras: false);

        string BuildThreeModels(string secondDesc) => JsonSerializer.Serialize(new
        {
            metadata = new { dbt_version = "1.8.0" },
            nodes = new Dictionary<string, object>
            {
                ["model.proj.t1"] = new { name = "t1", database = "warehouse", schema = "public", description = "Desc 1", config = new { materialized = "table" }, tags = Array.Empty<string>(), meta = new { }, columns = new { id = new { name = "id", data_type = "int", tags = Array.Empty<string>(), meta = new { } } }, depends_on = new { nodes = Array.Empty<string>() } },
                ["model.proj.t2"] = new { name = "t2", database = "warehouse", schema = "public", description = secondDesc, config = new { materialized = "table" }, tags = Array.Empty<string>(), meta = new { }, columns = new { id = new { name = "id", data_type = "int", tags = Array.Empty<string>(), meta = new { } } }, depends_on = new { nodes = Array.Empty<string>() } },
                ["model.proj.t3"] = new { name = "t3", database = "warehouse", schema = "public", description = "Desc 3", config = new { materialized = "table" }, tags = Array.Empty<string>(), meta = new { }, columns = new { id = new { name = "id", data_type = "int", tags = Array.Empty<string>(), meta = new { } } }, depends_on = new { nodes = Array.Empty<string>() } }
            }
        });

        await SyncAsync(h.Service, BuildThreeModels("Desc 2"));
        h.ResetCounters();

        await SyncAsync(h.Service, BuildThreeModels("Desc 2 updated"));

        h.UpsertCalls.ShouldBe(1);
        h.UpsertedTableCount.ShouldBe(1);
        h.Store["warehouse.public.t2"].Table.Description.ShouldBe("Desc 2 updated");
    }

    [Fact]
    public async Task SecondSync_WithChangedTagsOnly_UpdatesTagsWithoutUpsertingTableMetadata()
    {
        var h = new CountingHarness();
        await SyncAsync(h.Service, BuildRichManifest(tags: ["finance"]));
        h.ResetCounters();

        await SyncAsync(h.Service, BuildRichManifest(tags: ["finance", "core"]));

        h.UpsertCalls.ShouldBe(0);
        h.ReplaceTagsCalls.ShouldBe(1);
        h.TagStore["warehouse.analytics.customers"].Select(t => t.Tag).OrderBy(x => x).ShouldBe(["core", "finance"]);
    }

    [Fact]
    public async Task SecondSync_WithChangedSyntheticSamplesOnly_UpdatesSamplesWithoutUpsertingTableMetadata()
    {
        var h = new CountingHarness();
        await SyncAsync(h.Service, BuildRichManifest(sampleCity: "Berlin"));
        h.ResetCounters();

        await SyncAsync(h.Service, BuildRichManifest(sampleCity: "Munich"));

        h.UpsertCalls.ShouldBe(0);
        h.ArtifactUpsertCalls.ShouldBe(1);
        h.AuditRecordCalls.ShouldBe(1);
    }

    [Fact]
    public async Task SecondSync_WithChangedRequiredFilterColumnGroups_IsNotSkipped()
    {
        var h = new CountingHarness();
        await SyncAsync(h.Service, BuildRichManifest(requiredFilterColumnGroups: null));
        h.ResetCounters();

        await SyncAsync(h.Service, BuildRichManifest(requiredFilterColumnGroups: ["city"]));

        h.UpsertCalls.ShouldBe(1);
        h.UpsertedTableCount.ShouldBe(1);
        h.Store["warehouse.analytics.customers"].RequiredFilterColumnGroups.Length.ShouldBe(1);
    }

    [Fact]
    public async Task SecondSync_With3000UnchangedTables_CompletesUnderOneMinuteWithZeroWrites()
    {
        const int tableCount = 3000;
        var h = new CountingHarness(withExtras: false);

        var nodes = new Dictionary<string, object>(tableCount);
        for (var i = 0; i < tableCount; i++)
        {
            var tableName = $"tbl_{i:D4}";
            nodes[$"model.proj.{tableName}"] = new
            {
                name = tableName,
                database = "warehouse",
                schema = "analytics",
                description = $"Table {i}",
                config = new { materialized = "table" },
                tags = Array.Empty<string>(),
                meta = new Dictionary<string, object?>(),
                columns = new Dictionary<string, object>
                {
                    ["id"] = new { name = "id", data_type = "bigint", description = "PK", tags = Array.Empty<string>(), meta = new Dictionary<string, string>() },
                    ["amount"] = new { name = "amount", data_type = "decimal(18,2)", description = "Amount", tags = Array.Empty<string>(), meta = new Dictionary<string, string>() }
                },
                depends_on = new { nodes = Array.Empty<string>() }
            };
        }

        var manifestJson = JsonSerializer.Serialize(new { metadata = new { dbt_version = "1.8.0" }, nodes });

        var first = await SyncAsync(h.Service, manifestJson);
        first.Success.ShouldBeTrue();
        first.ParsedModelsCount.ShouldBe(tableCount);
        h.ResetCounters();

        var sw = Stopwatch.StartNew();
        var second = await SyncAsync(h.Service, manifestJson);
        sw.Stop();

        second.Success.ShouldBeTrue();
        second.ParsedModelsCount.ShouldBe(tableCount);
        second.CreatedTables.ShouldBeEmpty();
        h.UpsertCalls.ShouldBe(0);
        h.UpsertedTableCount.ShouldBe(0);
        h.ReplaceTagsCalls.ShouldBe(0);
        h.CatalogVersionBumps.ShouldBe(0);
        h.TableEpochBumps.ShouldBe(0);
        h.CatalogChangedEvents.ShouldBe(0);
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
    }
}
