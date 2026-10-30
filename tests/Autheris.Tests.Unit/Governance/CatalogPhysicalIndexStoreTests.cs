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

/// <summary>Issue #404 (WP-404-1): the physical non-unique index store round trip, fail-closed reads and write isolation on SQLite.</summary>
public sealed class CatalogPhysicalIndexStoreTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repo;

    public CatalogPhysicalIndexStoreTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=physidx_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", JournalMode = "DELETE" }
        });
        _repo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
    }

    public void Dispose() => _repo.Dispose();

    private static TableIdentifier Id(string name) => new("d", "s", name);

    private static TableMetadata Meta(string name) => new()
    {
        Identifier = Id(name),
        Table = new Table { SourceType = "sql", SourceName = "d", SchemaName = "s", TableName = name, DisplayName = name },
        Columns =
        [
            new TableColumn { ColumnName = "a", DataType = "integer" },
            new TableColumn { ColumnName = "b", DataType = "integer" },
            new TableColumn { ColumnName = "c", DataType = "integer" }
        ],
        PrimaryKeyColumns = ["a"],
        Indexes = [new TableIndexDefinition("ix_declared", ["b"], false)],
        ReplaceKeyDefinition = true
    };

    private static PhysicalIndexDefinition Idx(string[] columns, params KeyColumnDirection[] directions) => new(columns, directions);

    private async Task<TableMetadata> ReadAsync(string name) => (await _repo.GetTableMetadataAsync(Id(name)))!;

    private long Scalar(string sql)
    {
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Replace_ThenRead_RoundTripsColumnsAndDirections()
    {
        await _repo.UpsertTableMetadataAsync(Meta("t1"));

        (await _repo.ReplacePhysicalIndexesAsync(Id("t1"), [Idx(["b", "c"], KeyColumnDirection.Ascending, KeyColumnDirection.Descending), Idx(["c"])])).ShouldBeTrue();

        var read = (await ReadAsync("t1")).PhysicalIndexes;
        read.Count.ShouldBe(2);
        read[0].Columns.ShouldBe(["b", "c"]);
        read[0].Directions.ShouldBe([KeyColumnDirection.Ascending, KeyColumnDirection.Descending]);
        read[1].Columns.ShouldBe(["c"]);
        read[1].Directions.ShouldBeEmpty();

        await _repo.ReplacePhysicalIndexesAsync(Id("t1"), []);
        (await ReadAsync("t1")).PhysicalIndexes.ShouldBeEmpty();
        Scalar("SELECT COUNT(*) FROM TABLE_PHYSICAL_INDEX_COLUMNS").ShouldBe(0);
    }

    [Fact]
    public async Task Replace_UnknownTable_ReturnsFalse()
    {
        (await _repo.ReplacePhysicalIndexesAsync(Id("missing"), [Idx(["a"])])).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task I404_T_07_Read_UnresolvedColumn_DropsWholeIndex()
    {
        await _repo.UpsertTableMetadataAsync(Meta("t2"));
        await _repo.ReplacePhysicalIndexesAsync(Id("t2"), [Idx(["b", "c"]), Idx(["a"])]);
        using (var cmd = _repo.RawConnection().CreateCommand())
        {
            cmd.CommandText = "DELETE FROM TABLE_COLUMNS WHERE column_name = 'c'";
            cmd.ExecuteNonQuery();
        }

        _repo.InvalidateMetadataCacheForTesting();

        var read = (await ReadAsync("t2")).PhysicalIndexes;

        read.ShouldHaveSingleItem().Columns.ShouldBe(["a"], "the index over the vanished column is dropped whole, never partially");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task I404_T_09_Store_Sql_BoundParametersOnly()
    {
        var hostile = "t');DROP TABLE TABLE_PHYSICAL_INDEXES;--";
        await _repo.UpsertTableMetadataAsync(Meta(hostile));

        (await _repo.ReplacePhysicalIndexesAsync(Id(hostile), [Idx(["b"])])).ShouldBeTrue();

        Scalar("SELECT COUNT(*) FROM TABLE_PHYSICAL_INDEXES").ShouldBe(1, "identifier text never reaches the SQL text");
        (await ReadAsync(hostile)).PhysicalIndexes.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task I404_T_S06_Store_Replace_RejectsOverCapOrdinalGapOrDuplicateColumn_WritesNothing()
    {
        await _repo.UpsertTableMetadataAsync(Meta("t3"));
        await _repo.ReplacePhysicalIndexesAsync(Id("t3"), [Idx(["b"])]);

        var tooMany = Enumerable.Range(0, TableKeyDefinitionValidator.MaxPhysicalIndexesPerTable + 1).Select(_ => Idx(["b"])).ToList();
        await Should.ThrowAsync<InvalidOperationException>(() => _repo.ReplacePhysicalIndexesAsync(Id("t3"), tooMany));
        await Should.ThrowAsync<InvalidOperationException>(() => _repo.ReplacePhysicalIndexesAsync(Id("t3"), [Idx(["b", "B"])]));
        await Should.ThrowAsync<InvalidOperationException>(() => _repo.ReplacePhysicalIndexesAsync(Id("t3"), [Idx([])]));
        await Should.ThrowAsync<InvalidOperationException>(() => _repo.ReplacePhysicalIndexesAsync(Id("t3"), [Idx(["b", "c"], KeyColumnDirection.Descending)]));
        await Should.ThrowAsync<InvalidOperationException>(() => _repo.ReplacePhysicalIndexesAsync(Id("t3"), [Idx(["nope"])]));

        var read = (await ReadAsync("t3")).PhysicalIndexes;
        read.ShouldHaveSingleItem().Columns.ShouldBe(["b"], "the stored list is untouched after every rejected replace");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task I404_T_S07_Store_Replace_CrossTenantTable_Isolated_AndNeverTouchesDeclaredIndexes()
    {
        await _repo.UpsertTableMetadataAsync(Meta("ta"));
        await _repo.UpsertTableMetadataAsync(Meta("tb"));
        await _repo.ReplacePhysicalIndexesAsync(Id("tb"), [Idx(["c"])]);
        var declaredBefore = Scalar("SELECT COUNT(*) FROM TABLE_INDEXES");

        await _repo.ReplacePhysicalIndexesAsync(Id("ta"), [Idx(["b"]), Idx(["c"])]);

        (await ReadAsync("ta")).PhysicalIndexes.Count.ShouldBe(2);
        (await ReadAsync("tb")).PhysicalIndexes.ShouldHaveSingleItem().Columns.ShouldBe(["c"], "the other table is untouched");
        Scalar("SELECT COUNT(*) FROM TABLE_INDEXES").ShouldBe(declaredBefore);
        (await ReadAsync("ta")).Indexes.ShouldHaveSingleItem().Name.ShouldBe("ix_declared");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I404-SC-01")]
    public void I404_SC_01_PhysicalIndexTables_HaveNoNameOrTimestampColumn()
    {
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_table_info('TABLE_PHYSICAL_INDEXES') UNION ALL SELECT name FROM pragma_table_info('TABLE_PHYSICAL_INDEX_COLUMNS'))";
        var columns = Convert.ToString(cmd.ExecuteScalar())!.Split(',');
        columns.OrderBy(c => c).ShouldBe(["column_ordinal", "id", "index_id", "index_ordinal", "is_descending", "table_column_id", "table_id"]);
    }
}
