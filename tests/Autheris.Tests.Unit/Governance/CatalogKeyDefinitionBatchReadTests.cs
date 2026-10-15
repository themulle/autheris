using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #94 (M4): the key tables are probed once per repository, and the key rows of all tables are read in batches.</summary>
public sealed class CatalogKeyDefinitionBatchReadTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repo;

    public CatalogKeyDefinitionBatchReadTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=keybatch_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", JournalMode = "DELETE" }
        });
        _repo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
    }

    public void Dispose() => _repo.Dispose();

    private static TableMetadata Meta(string name, IReadOnlyList<string> pk, IReadOnlyList<TableIndexDefinition> indexes) => new()
    {
        Identifier = new TableIdentifier("d", "s", name),
        Table = new Table { SourceType = "sql", SourceName = "d", SchemaName = "s", TableName = name, DisplayName = name },
        Columns = [new TableColumn { ColumnName = "a", DataType = "integer" }, new TableColumn { ColumnName = "b", DataType = "integer" }],
        PrimaryKeyColumns = pk,
        Indexes = indexes,
        ReplaceKeyDefinition = true
    };

    [Fact]
    public async Task ReadBatch_ReturnsRowsPerTable_AcrossChunks()
    {
        await _repo.UpsertTableMetadataAsync(Meta("t1", ["a", "b"], [new TableIndexDefinition("ux", ["b"], true)]));
        await _repo.UpsertTableMetadataAsync(Meta("t2", ["b"], []));
        await _repo.UpsertTableMetadataAsync(Meta("t3", [], []));
        var conn = _repo.RawConnection();
        var ids = new List<Guid>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM TABLES WHERE table_name IN ('t1','t2','t3') ORDER BY table_name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ids.Add(Guid.Parse(r.GetString(0)));
            }
        }

        // Padding forces several chunks; the real ids are spread over them.
        var padded = new List<Guid> { ids[0] };
        padded.AddRange(Enumerable.Range(0, CatalogKeyDefinitionStore.BatchChunkSize + 7).Select(_ => Guid.NewGuid()));
        padded.Add(ids[1]);
        padded.AddRange(Enumerable.Range(0, CatalogKeyDefinitionStore.BatchChunkSize).Select(_ => Guid.NewGuid()));
        padded.Add(ids[2]);

        var rows = await CatalogKeyDefinitionStore.ReadBatchAsync(conn, null, CatalogKeyDefinitionStore.Sqlite, padded, CancellationToken.None);

        rows[ids[0]].PrimaryKey.Select(r => r.Ordinal).ShouldBe([1, 2]);
        rows[ids[0]].Indexes.ShouldHaveSingleItem().Name.ShouldBe("ux");
        rows[ids[1]].PrimaryKey.ShouldHaveSingleItem();
        rows[ids[1]].Indexes.ShouldBeEmpty();
        rows[ids[2]].PrimaryKey.ShouldBeEmpty();
        rows.Values.Any(v => v.Overflow).ShouldBeFalse();
    }

    [Fact]
    public async Task GetAllTables_InvalidTableOmitted_OthersServedWithTheirKeys()
    {
        await _repo.UpsertTableMetadataAsync(Meta("good", ["a"], []));
        await _repo.UpsertTableMetadataAsync(Meta("bad", ["a", "b"], []));
        using (var cmd = _repo.RawConnection().CreateCommand())
        {
            cmd.CommandText = "UPDATE TABLE_PRIMARY_KEY_COLUMNS SET key_ordinal = 9 WHERE key_ordinal = 2 AND table_id = (SELECT id FROM TABLES WHERE table_name = 'bad')";
            cmd.ExecuteNonQuery();
        }

        _repo.InvalidateMetadataCacheForTesting();
        var all = await _repo.GetAllTablesAsync();

        all.ShouldNotContain(t => t.Identifier.TableName == "bad");
        all.Single(t => t.Identifier.TableName == "good").PrimaryKeyColumns.ShouldBe(["a"]);
    }

    [Fact]
    public async Task Probe_CachesOnlyThePositiveResult()
    {
        var probe = new CatalogKeyDefinitionStore.KeyTableProbe();
        probe.Present.ShouldBeFalse();

        (await CatalogKeyDefinitionStore.TablesExistAsync(_repo.RawConnection(), null, CatalogKeyDefinitionStore.Sqlite, CancellationToken.None, probe)).ShouldBeTrue();

        probe.Present.ShouldBeTrue();
        // A cached positive result must not touch the connection again.
        (await CatalogKeyDefinitionStore.TablesExistAsync(null!, null, CatalogKeyDefinitionStore.Sqlite, CancellationToken.None, probe)).ShouldBeTrue();
    }
}
