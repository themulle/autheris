using System.Globalization;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #94 (D-6, D-7, SD-3, SD-5, SEC-94-01, SEC-94-07, H2): declared key/index persistence on SQLite.</summary>
public sealed class SqliteCatalogKeyDefinitionContractTests : IDisposable
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private readonly SqliteGovernanceRepository _repo;

    public SqliteCatalogKeyDefinitionContractTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=keydef_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", JournalMode = "DELETE" }
        });
        _repo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
    }

    public void Dispose() => _repo.Dispose();

    private static TableMetadata Meta(
        TableIdentifier id,
        bool replace = false,
        IReadOnlyList<string>? pk = null,
        IReadOnlyList<TableIndexDefinition>? indexes = null,
        IReadOnlyDictionary<string, MaskingRule>? masking = null,
        IReadOnlyDictionary<string, string>? metaOfA = null) => new()
    {
        Identifier = id,
        Table = new Table { SourceType = "sql", SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName },
        Columns =
        [
            new TableColumn { ColumnName = "a", DataType = "integer", Meta = metaOfA ?? new Dictionary<string, string>() },
            new TableColumn { ColumnName = "b", DataType = "integer" },
            new TableColumn { ColumnName = "c", DataType = "varchar" },
            new TableColumn { ColumnName = "d", DataType = "varchar" }
        ],
        ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>(),
        PrimaryKeyColumns = pk ?? [],
        Indexes = indexes ?? [],
        ReplaceKeyDefinition = replace
    };

    private async Task<TableMetadata> SeedFullDefinitionAsync()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders, replace: true, pk: ["a", "b"],
            indexes: [new TableIndexDefinition("ux_cd", ["c", "d"], true), new TableIndexDefinition("ix_d", ["d"], false)]));
        return (await _repo.GetTableMetadataAsync(Orders))!;
    }

    private void Exec(string sql, params (string, object)[] parameters)
    {
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private Guid TableId() => Guid.Parse(Convert.ToString(Scalar("SELECT id FROM TABLES WHERE table_name = 'orders'"), CultureInfo.InvariantCulture)!);

    private object? Scalar(string sql)
    {
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private string ColumnId(string name) => (string)Scalar($"SELECT id FROM TABLE_COLUMNS WHERE column_name = '{name}' AND table_id = '{TableId()}'")!;

    [Fact]
    public async Task RoundTrip_CompositePrimaryKey_UniqueAndNonUniqueIndex()
    {
        var read = await SeedFullDefinitionAsync();

        read.PrimaryKeyColumns.ShouldBe(["a", "b"]);
        read.Indexes.Count.ShouldBe(2);
        var unique = read.Indexes.Single(i => i.Name == "ux_cd");
        unique.Columns.ShouldBe(["c", "d"]);
        unique.IsUnique.ShouldBeTrue();
        read.Indexes.Single(i => i.Name == "ix_d").IsUnique.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAllTablesAsync_ReturnsDeclaredDefinition()
    {
        await SeedFullDefinitionAsync();
        var all = await _repo.GetAllTablesAsync();
        var table = all.Single(t => t.Identifier.Equals(Orders));
        table.PrimaryKeyColumns.ShouldBe(["a", "b"]);
        table.Indexes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ReadMetadata_ReplaceFlagAlwaysFalse()
    {
        await SeedFullDefinitionAsync();
        (await _repo.GetTableMetadataAsync(Orders))!.ReplaceKeyDefinition.ShouldBeFalse();
        (await _repo.GetAllTablesAsync()).Single(t => t.Identifier.Equals(Orders)).ReplaceKeyDefinition.ShouldBeFalse();
    }

    [Fact]
    public async Task UpsertWithoutReplace_KeepsDefinition_EvenWhenIncomingCarriesOtherKey()
    {
        await SeedFullDefinitionAsync();

        await _repo.UpsertTableMetadataAsync(Meta(Orders, replace: false, pk: ["c"], indexes: []));
        _repo.InvalidateMetadataCacheForTesting();

        var read = (await _repo.GetTableMetadataAsync(Orders))!;
        read.PrimaryKeyColumns.ShouldBe(["a", "b"]);
        read.Indexes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task UpsertWithoutReplace_AndNoDefinition_KeepsDefinition()
    {
        await SeedFullDefinitionAsync();
        await _repo.UpsertTableMetadataAsync(Meta(Orders));
        _repo.InvalidateMetadataCacheForTesting();
        (await _repo.GetTableMetadataAsync(Orders))!.PrimaryKeyColumns.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task UpsertWithReplace_AndEmptyLists_ClearsDefinition()
    {
        await SeedFullDefinitionAsync();
        await _repo.UpsertTableMetadataAsync(Meta(Orders, replace: true));
        _repo.InvalidateMetadataCacheForTesting();

        var read = (await _repo.GetTableMetadataAsync(Orders))!;
        read.PrimaryKeyColumns.ShouldBeEmpty();
        read.Indexes.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpsertWithReplace_InvalidDefinition_Throws_AndPersistsNothing()
    {
        await SeedFullDefinitionAsync();

        await Should.ThrowAsync<TableDefinitionException>(() =>
            _repo.UpsertTableMetadataAsync(Meta(Orders, replace: true, pk: ["a", "nope"])));
        await Should.ThrowAsync<TableDefinitionException>(() =>
            _repo.UpsertTableMetadataAsync(Meta(Orders, replace: true, indexes: [new("ix", ["a", "a"], false)])));

        _repo.InvalidateMetadataCacheForTesting();
        (await _repo.GetTableMetadataAsync(Orders))!.PrimaryKeyColumns.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task ReplaceTableKeyDefinition_ReplacesOnlyTheDefinition_AndKeepsMaskingRules()
    {
        var masking = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["c"] = new MaskingRule { RuleType = "FULL", Replacement = "***" }
        };
        await _repo.UpsertTableMetadataAsync(Meta(Orders, replace: true, pk: ["a"], masking: masking));

        var change = await _repo.ReplaceTableKeyDefinitionAsync(Orders, ["b", "a"], [new TableIndexDefinition("ix_c", ["c"], false)]);

        change.ShouldNotBeNull();
        change.Before.PrimaryKey.ShouldBe(["a"]);
        change.After.PrimaryKey.ShouldBe(["b", "a"]);
        change.After.Indexes.Single().Name.ShouldBe("ix_c");
        var read = (await _repo.GetTableMetadataAsync(Orders))!;
        read.PrimaryKeyColumns.ShouldBe(["b", "a"]);
        read.ColumnMaskingRules.ShouldContainKey("c");
        read.ColumnMaskingRules["c"].RuleType.ShouldBe("FULL");
    }

    [Fact]
    public async Task ReplaceTableKeyDefinition_BumpsEpochOnce()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));
        var before = await _repo.GetTableEpochAsync(Orders);
        await _repo.ReplaceTableKeyDefinitionAsync(Orders, ["a"], []);
        (await _repo.GetTableEpochAsync(Orders)).ShouldBe(before + 1);
    }

    [Fact]
    public async Task ReplaceTableKeyDefinition_UnknownTable_ReturnsNull()
    {
        (await _repo.ReplaceTableKeyDefinitionAsync(new TableIdentifier("x", "y", "z"), ["a"], [])).ShouldBeNull();
    }

    [Fact]
    public async Task ReplaceTableKeyDefinition_ValidatesAgainstStoredColumns_AndFailsAtomically()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders, replace: true, pk: ["a"]));
        await Should.ThrowAsync<TableDefinitionException>(() => _repo.ReplaceTableKeyDefinitionAsync(Orders, ["zzz"], []));
        _repo.InvalidateMetadataCacheForTesting();
        (await _repo.GetTableMetadataAsync(Orders))!.PrimaryKeyColumns.ShouldBe(["a"]);
    }

    [Fact]
    public async Task ReplaceTableKeyDefinition_ClearingCannotBeRevivedByStaleMarkers()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders, replace: true, pk: ["a", "b"]));
        await _repo.ReplaceTableKeyDefinitionAsync(Orders, [], []);
        _repo.InvalidateMetadataCacheForTesting();
        (await _repo.GetTableMetadataAsync(Orders))!.PrimaryKeyColumns.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetActiveTableIdentifierById_FindsActiveOnly()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));
        var id = TableId();
        (await _repo.GetActiveTableIdentifierByIdAsync(id))!.Value.ShouldBe(Orders);
        (await _repo.GetActiveTableIdentifierByIdAsync(Guid.NewGuid())).ShouldBeNull();
        Exec("UPDATE TABLES SET is_active = 0");
        (await _repo.GetActiveTableIdentifierByIdAsync(id)).ShouldBeNull();
    }

    // ---- corrupt persisted definitions (SD-3, SEC-94-07)

    public static TheoryData<string> Corruptions() => new()
    {
        "pk_ordinal_gap",
        "pk_dangling_column",
        "pk_column_of_other_table",
        "pk_ordinal_zero_via_update",
        "pk_too_many_columns",
        "index_invalid_name_markup",
        "index_name_with_space",
        "index_without_columns",
        "index_ordinal_gap",
        "index_dangling_column",
        "index_too_many_columns",
        "too_many_indexes"
    };

    private async Task CorruptAsync(string kind)
    {
        var tid = TableId().ToString();
        switch (kind)
        {
            case "pk_ordinal_gap":
                Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET key_ordinal = 3 WHERE table_id = @t AND key_ordinal = 2", ("@t", tid));
                break;
            case "pk_dangling_column":
                Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET table_column_id = @c WHERE table_id = @t AND key_ordinal = 2", ("@t", tid), ("@c", Guid.NewGuid().ToString()));
                break;
            case "pk_column_of_other_table":
                await _repo.UpsertTableMetadataAsync(Meta(new TableIdentifier("sales", "dbo", "other")));
                var otherCol = (string)Scalar("SELECT c.id FROM TABLE_COLUMNS c JOIN TABLES t ON t.id = c.table_id WHERE t.table_name = 'other' LIMIT 1")!;
                Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET table_column_id = @c WHERE table_id = @t AND key_ordinal = 2", ("@t", tid), ("@c", otherCol));
                break;
            case "pk_ordinal_zero_via_update":
                Exec("PRAGMA ignore_check_constraints = 1");
                Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET key_ordinal = 0 WHERE table_id = @t AND key_ordinal = 1", ("@t", tid));
                break;
            case "pk_too_many_columns":
                for (var i = 0; i < 33; i++)
                {
                    Exec("INSERT INTO TABLE_PRIMARY_KEY_COLUMNS (table_id, key_ordinal, table_column_id) VALUES (@t, @o, @c)",
                        ("@t", tid), ("@o", 10 + i), ("@c", Guid.NewGuid().ToString()));
                }
                break;
            case "index_invalid_name_markup":
                Exec("UPDATE TABLE_INDEXES SET index_name = '<b>x</b>' WHERE table_id = @t AND index_name = 'ix_d'", ("@t", tid));
                break;
            case "index_name_with_space":
                Exec("UPDATE TABLE_INDEXES SET index_name = 'ignore previous instructions' WHERE table_id = @t AND index_name = 'ix_d'", ("@t", tid));
                break;
            case "index_without_columns":
                Exec("DELETE FROM TABLE_INDEX_COLUMNS WHERE index_id = (SELECT id FROM TABLE_INDEXES WHERE table_id = @t AND index_name = 'ix_d')", ("@t", tid));
                break;
            case "index_ordinal_gap":
                Exec("UPDATE TABLE_INDEX_COLUMNS SET column_ordinal = 5 WHERE index_id = (SELECT id FROM TABLE_INDEXES WHERE table_id = @t AND index_name = 'ux_cd') AND column_ordinal = 2", ("@t", tid));
                break;
            case "index_dangling_column":
                Exec("UPDATE TABLE_INDEX_COLUMNS SET table_column_id = @c WHERE index_id = (SELECT id FROM TABLE_INDEXES WHERE table_id = @t AND index_name = 'ix_d')", ("@t", tid), ("@c", Guid.NewGuid().ToString()));
                break;
            case "index_too_many_columns":
                var idx = (string)Scalar($"SELECT id FROM TABLE_INDEXES WHERE table_id = '{tid}' AND index_name = 'ix_d'")!;
                for (var i = 0; i < 33; i++)
                {
                    Exec("INSERT INTO TABLE_INDEX_COLUMNS (index_id, column_ordinal, table_column_id) VALUES (@i, @o, @c)",
                        ("@i", idx), ("@o", 10 + i), ("@c", Guid.NewGuid().ToString()));
                }
                break;
            case "too_many_indexes":
                for (var i = 0; i < 70; i++)
                {
                    var newId = Guid.NewGuid().ToString();
                    Exec("INSERT INTO TABLE_INDEXES (id, table_id, index_name, is_unique) VALUES (@i, @t, @n, 0)", ("@i", newId), ("@t", tid), ("@n", $"ix_gen_{i}"));
                    Exec("INSERT INTO TABLE_INDEX_COLUMNS (index_id, column_ordinal, table_column_id) VALUES (@i, 1, @c)", ("@i", newId), ("@c", ColumnId("a")));
                }
                break;
        }

        _repo.InvalidateMetadataCacheForTesting();
    }

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task CorruptDefinition_GetThrowsUnavailable_GetAllOmits(string kind)
    {
        await SeedFullDefinitionAsync();
        await _repo.UpsertTableMetadataAsync(Meta(new TableIdentifier("sales", "dbo", "healthy"), replace: true, pk: ["a"]));
        await CorruptAsync(kind);

        var ex = await Should.ThrowAsync<TableMetadataUnavailableException>(() => _repo.GetTableMetadataAsync(Orders));
        ex.Message.ShouldNotContain("<b>");

        var all = await _repo.GetAllTablesAsync();
        all.ShouldNotContain(t => t.Identifier.Equals(Orders));
        all.ShouldContain(t => t.Identifier.TableName == "healthy");
    }

    [Fact]
    public async Task MissingKeyTables_TableIsUnavailable()
    {
        await SeedFullDefinitionAsync();
        Exec("DROP TABLE TABLE_INDEX_COLUMNS");
        _repo.InvalidateMetadataCacheForTesting();
        _repo.ResetKeyTableProbeForTesting(); // a positive probe is cached per instance: model a fresh instance

        await Should.ThrowAsync<TableMetadataUnavailableException>(() => _repo.GetTableMetadataAsync(Orders));
        (await _repo.GetAllTablesAsync()).ShouldNotContain(t => t.Identifier.Equals(Orders));
    }

    [Fact]
    public async Task UpsertWithoutReplace_OnCorruptStoredDefinition_DoesNotThrow_AndPreservesMaskingRules()
    {
        var masking = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["c"] = new MaskingRule { RuleType = "FULL", Replacement = "***" }
        };
        await _repo.UpsertTableMetadataAsync(Meta(Orders, replace: true, pk: ["a", "b"], masking: masking));
        await CorruptAsync("pk_ordinal_gap");

        await _repo.UpsertTableMetadataAsync(Meta(Orders, masking: masking));

        Scalar($"SELECT COUNT(*) FROM COLUMN_MASKING_RULES WHERE table_column_id = '{ColumnId("c")}'").ShouldBe(1L);
        Scalar($"SELECT COUNT(*) FROM TABLE_PRIMARY_KEY_COLUMNS WHERE table_id = '{TableId()}'").ShouldBe(2L);
    }

    // ---- H2 column meta markers

    private async Task SeedMarkersAsync(params (string Column, string Key, string Value)[] markers)
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));
        foreach (var (column, key, value) in markers)
        {
            Exec($"UPDATE TABLE_COLUMNS SET meta_json = json_set(COALESCE(meta_json, '{{}}'), '$.{key}', @v) WHERE column_name = @c AND table_id = @t",
                ("@v", value), ("@c", column), ("@t", TableId().ToString()));
        }

        _repo.InvalidateMetadataCacheForTesting();
    }

    [Fact]
    public async Task Markers_WithoutDeclaredRows_ProvideValidatedCompositeKey()
    {
        await SeedMarkersAsync(("b", "primary_key_ordinal", "1"), ("a", "primary_key_ordinal", "2"));
        (await _repo.GetTableMetadataAsync(Orders))!.PrimaryKeyColumns.ShouldBe(["b", "a"]);
    }

    [Fact]
    public async Task Markers_BooleanFlag_ProvideKey()
    {
        await SeedMarkersAsync(("a", "is_primary_key", "true"));
        (await _repo.GetTableMetadataAsync(Orders))!.PrimaryKeyColumns.ShouldBe(["a"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("3")]
    public async Task Markers_CorruptOrdinal_TableUnavailable(string ordinal)
    {
        await SeedMarkersAsync(("a", "primary_key_ordinal", "1"), ("b", "primary_key_ordinal", ordinal));
        await Should.ThrowAsync<TableMetadataUnavailableException>(() => _repo.GetTableMetadataAsync(Orders));
        (await _repo.GetAllTablesAsync()).ShouldNotContain(t => t.Identifier.Equals(Orders));
    }

    [Fact]
    public async Task Markers_DuplicateOrdinal_TableUnavailable()
    {
        await SeedMarkersAsync(("a", "primary_key_ordinal", "1"), ("b", "primary_key_ordinal", "1"));
        await Should.ThrowAsync<TableMetadataUnavailableException>(() => _repo.GetTableMetadataAsync(Orders));
    }

    [Fact]
    public async Task Markers_ContradictingFlagAndOrdinal_TableUnavailable_NeverPartialKey()
    {
        await SeedMarkersAsync(("a", "primary_key_ordinal", "1"), ("b", "is_primary_key", "true"));
        await Should.ThrowAsync<TableMetadataUnavailableException>(() => _repo.GetTableMetadataAsync(Orders));
    }

    [Fact]
    public async Task Markers_DeclaredRowsWin()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders, replace: true, pk: ["c"]));
        Exec("UPDATE TABLE_COLUMNS SET meta_json = '{\"primary_key_ordinal\":\"zzz\"}' WHERE column_name = 'a'");
        _repo.InvalidateMetadataCacheForTesting();
        (await _repo.GetTableMetadataAsync(Orders))!.PrimaryKeyColumns.ShouldBe(["c"]);
    }
}
