namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #94 (D-6, D-7, SD-3, SD-5, SEC-94-01, SEC-94-07, H2): declared key/index contract of the governance repositories on a real
/// PostgreSQL / SQL Server. One database per class; every test uses its own table. Without Docker the tests return early.
/// </summary>
public abstract class CatalogKeyDefinitionContractTestsBase
{
    protected abstract bool Available { get; }
    protected abstract IGovernanceRepository Repo { get; }
    protected abstract DbConnection OpenRaw();
    protected abstract void InvalidateCache();

    private static TableIdentifier NewId(string prefix = "t") => new("sales", "dbo", $"{prefix}_{Guid.NewGuid():N}");

    private static TableMetadata Meta(
        TableIdentifier id,
        bool replace = false,
        IReadOnlyList<string>? pk = null,
        IReadOnlyList<TableIndexDefinition>? indexes = null,
        IReadOnlyDictionary<string, MaskingRule>? masking = null) => new()
    {
        Identifier = id,
        Table = new Table { SourceType = "sql", SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName },
        Columns =
        [
            new TableColumn { ColumnName = "a", DataType = "integer" },
            new TableColumn { ColumnName = "b", DataType = "integer" },
            new TableColumn { ColumnName = "c", DataType = "varchar" },
            new TableColumn { ColumnName = "d", DataType = "varchar" }
        ],
        ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>(),
        PrimaryKeyColumns = pk ?? [],
        Indexes = indexes ?? [],
        ReplaceKeyDefinition = replace
    };

    private void Exec(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = n;
            p.Value = v;
            cmd.Parameters.Add(p);
        }

        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = n;
            p.Value = v;
            cmd.Parameters.Add(p);
        }

        return cmd.ExecuteScalar();
    }

    private string TableId(TableIdentifier id) => (string)Scalar("SELECT id FROM TABLES WHERE table_name = @n", ("@n", id.TableName))!;

    private string ColumnId(TableIdentifier id, string column) =>
        (string)Scalar("SELECT id FROM TABLE_COLUMNS WHERE column_name = @c AND table_id = @t", ("@c", column), ("@t", TableId(id)))!;

    private long Count(string sql, params (string Name, object Value)[] parameters) => Convert.ToInt64(Scalar(sql, parameters), CultureInfo.InvariantCulture);

    private async Task<TableIdentifier> SeedFullAsync()
    {
        var id = NewId();
        await Repo.UpsertTableMetadataAsync(Meta(id, replace: true, pk: ["a", "b"],
            indexes: [new TableIndexDefinition("ux_cd", ["c", "d"], true), new TableIndexDefinition("ix_d", ["d"], false)]));
        return id;
    }

    [Fact]
    public async Task RoundTrip_CompositePrimaryKey_UniqueAndNonUniqueIndex_FlagFalse()
    {
        if (!Available) return;
        var id = await SeedFullAsync();
        InvalidateCache();

        var read = (await Repo.GetTableMetadataAsync(id))!;

        read.PrimaryKeyColumns.ShouldBe(["a", "b"]);
        read.Indexes.Single(i => i.Name == "ux_cd").Columns.ShouldBe(["c", "d"]);
        read.Indexes.Single(i => i.Name == "ux_cd").IsUnique.ShouldBeTrue();
        read.Indexes.Single(i => i.Name == "ix_d").IsUnique.ShouldBeFalse();
        read.ReplaceKeyDefinition.ShouldBeFalse();
        (await Repo.GetAllTablesAsync()).Single(t => t.Identifier.Equals(id)).PrimaryKeyColumns.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task KeyDefinition_RoundTripsDirections()
    {
        if (!Available) return;
        var id = NewId();
        await Repo.UpsertTableMetadataAsync(Meta(id, replace: true, pk: ["a", "b"],
            indexes: [new TableIndexDefinition("ux_cd", ["c", "d"], true, [KeyColumnDirection.Descending, KeyColumnDirection.Ascending]), new TableIndexDefinition("ix_d", ["d"], false)])
            with { PrimaryKeyDirections = [KeyColumnDirection.Ascending, KeyColumnDirection.Descending] });
        InvalidateCache();

        var read = (await Repo.GetTableMetadataAsync(id))!;

        read.PrimaryKeyColumns.ShouldBe(["a", "b"]);
        read.PrimaryKeyDirections.ShouldBe([KeyColumnDirection.Ascending, KeyColumnDirection.Descending]);
        read.Indexes.Single(i => i.Name == "ux_cd").Directions.ShouldBe([KeyColumnDirection.Descending, KeyColumnDirection.Ascending]);
        read.Indexes.Single(i => i.Name == "ix_d").Directions.ShouldBeNull("an all-ascending index has the canonical (absent) form");
        (await Repo.GetAllTablesAsync()).Single(t => t.Identifier.Equals(id)).PrimaryKeyDirections.ShouldBe([KeyColumnDirection.Ascending, KeyColumnDirection.Descending]);
        Count("SELECT COUNT(*) FROM TABLE_PRIMARY_KEY_COLUMNS WHERE table_id = @t AND key_ordinal = 2 AND CAST(is_descending AS INTEGER) = 1", ("@t", TableId(id))).ShouldBe(1);
    }

    [Fact]
    public async Task KeyDefinition_UndeclaredDirections_ReadAsCanonicalEmpty()
    {
        if (!Available) return;
        var id = await SeedFullAsync();
        InvalidateCache();

        var read = (await Repo.GetTableMetadataAsync(id))!;

        read.PrimaryKeyDirections.ShouldBeEmpty();
        read.Indexes.ShouldAllBe(i => i.Directions == null);
    }

    [Fact]
    public async Task I390_T_11_DirectionOnlyChange_ReturnsChange_BumpsEpoch_AndIsVisibleWithoutRestart()
    {
        if (!Available) return;
        var id = NewId();
        await Repo.UpsertTableMetadataAsync(Meta(id, replace: true, pk: ["a", "b"]));
        (await Repo.GetTableMetadataAsync(id))!.PrimaryKeyDirections.ShouldBeEmpty(); // primes the metadata cache
        var epochBefore = await Repo.GetTableEpochAsync(id);

        var change = await Repo.ReplaceTableKeyDefinitionAsync(id, ["a", "b"], [], primaryKeyDirections: [KeyColumnDirection.Ascending, KeyColumnDirection.Descending]);

        change.ShouldNotBeNull();
        change.Before.PrimaryKey.ShouldBe(change.After.PrimaryKey);
        KeyDirections.AnyDescending(change.Before.PrimaryKeyDirections).ShouldBeFalse();
        change.After.PrimaryKeyDirections.ShouldBe([KeyColumnDirection.Ascending, KeyColumnDirection.Descending]);
        (await Repo.GetTableEpochAsync(id)).ShouldBe(epochBefore + 1);
        // I390-SC-08: no manual cache invalidation; the next read already sees the new direction.
        (await Repo.GetTableMetadataAsync(id))!.PrimaryKeyDirections.ShouldBe([KeyColumnDirection.Ascending, KeyColumnDirection.Descending]);
    }

    [Fact]
    public async Task ReplaceTableKeyDefinition_DirectionCountMismatch_Throws_AndKeepsStored()
    {
        if (!Available) return;
        var id = NewId();
        await Repo.UpsertTableMetadataAsync(Meta(id, replace: true, pk: ["a", "b"]));

        await Should.ThrowAsync<TableDefinitionException>(() => Repo.ReplaceTableKeyDefinitionAsync(id, ["a", "b"], [], primaryKeyDirections: [KeyColumnDirection.Descending]));

        InvalidateCache();
        (await Repo.GetTableMetadataAsync(id))!.PrimaryKeyDirections.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpsertWithoutReplace_KeepsDefinition_AndWithReplaceEmpty_Clears()
    {
        if (!Available) return;
        var id = await SeedFullAsync();

        await Repo.UpsertTableMetadataAsync(Meta(id, replace: false, pk: ["c"]));
        InvalidateCache();
        (await Repo.GetTableMetadataAsync(id))!.PrimaryKeyColumns.ShouldBe(["a", "b"]);

        await Repo.UpsertTableMetadataAsync(Meta(id, replace: true));
        InvalidateCache();
        var cleared = (await Repo.GetTableMetadataAsync(id))!;
        cleared.PrimaryKeyColumns.ShouldBeEmpty();
        cleared.Indexes.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpsertWithReplace_InvalidDefinition_Throws_AndKeepsStored()
    {
        if (!Available) return;
        var id = await SeedFullAsync();

        await Should.ThrowAsync<TableDefinitionException>(() => Repo.UpsertTableMetadataAsync(Meta(id, replace: true, pk: ["a", "nope"])));

        InvalidateCache();
        (await Repo.GetTableMetadataAsync(id))!.PrimaryKeyColumns.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task ReplaceTableKeyDefinition_IsNarrow_KeepsMasking_BumpsEpochOnce()
    {
        if (!Available) return;
        var id = NewId();
        var masking = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["c"] = new MaskingRule { RuleType = "FULL", Replacement = "***" } };
        await Repo.UpsertTableMetadataAsync(Meta(id, replace: true, pk: ["a"], masking: masking));
        var epochBefore = await Repo.GetTableEpochAsync(id);

        var change = await Repo.ReplaceTableKeyDefinitionAsync(id, ["b", "a"], [new TableIndexDefinition("ix_c", ["c"], false)]);

        change.ShouldNotBeNull();
        change.Before.PrimaryKey.ShouldBe(["a"]);
        change.After.PrimaryKey.ShouldBe(["b", "a"]);
        (await Repo.GetTableEpochAsync(id)).ShouldBe(epochBefore + 1);
        InvalidateCache();
        var read = (await Repo.GetTableMetadataAsync(id))!;
        read.PrimaryKeyColumns.ShouldBe(["b", "a"]);
        read.ColumnMaskingRules["c"].RuleType.ShouldBe("FULL");

        await Should.ThrowAsync<TableDefinitionException>(() => Repo.ReplaceTableKeyDefinitionAsync(id, ["zzz"], []));
        (await Repo.ReplaceTableKeyDefinitionAsync(new TableIdentifier("x", "y", "nothere_" + Guid.NewGuid().ToString("N")), ["a"], [])).ShouldBeNull();
    }

    [Fact]
    public async Task GetActiveTableIdentifierById_FindsActiveOnly()
    {
        if (!Available) return;
        var id = NewId();
        await Repo.UpsertTableMetadataAsync(Meta(id));
        var guid = Guid.Parse(TableId(id));

        (await Repo.GetActiveTableIdentifierByIdAsync(guid))!.Value.ShouldBe(id);
        (await Repo.GetActiveTableIdentifierByIdAsync(Guid.NewGuid())).ShouldBeNull();
    }

    public static TheoryData<string> Corruptions() => new()
    {
        "pk_ordinal_gap", "pk_dangling_column", "pk_column_of_other_table", "pk_too_many_columns",
        "index_invalid_name_markup", "index_name_with_space", "index_without_columns", "index_ordinal_gap",
        "index_dangling_column", "index_too_many_columns"
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task CorruptDefinition_Unavailable(string kind)
    {
        if (!Available) return;
        var id = await SeedFullAsync();
        var healthy = NewId("healthy");
        await Repo.UpsertTableMetadataAsync(Meta(healthy, replace: true, pk: ["a"]));
        var tid = TableId(id);
        switch (kind)
        {
            case "pk_ordinal_gap":
                Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET key_ordinal = 3 WHERE table_id = @t AND key_ordinal = 2", ("@t", tid));
                break;
            case "pk_dangling_column":
                Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET table_column_id = @c WHERE table_id = @t AND key_ordinal = 2", ("@t", tid), ("@c", Guid.NewGuid().ToString()));
                break;
            case "pk_column_of_other_table":
                Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET table_column_id = @c WHERE table_id = @t AND key_ordinal = 2", ("@t", tid), ("@c", ColumnId(healthy, "a")));
                break;
            case "pk_too_many_columns":
                for (var i = 0; i < 33; i++)
                {
                    Exec("INSERT INTO TABLE_PRIMARY_KEY_COLUMNS (table_id, key_ordinal, table_column_id) VALUES (@t, @o, @c)", ("@t", tid), ("@o", 10 + i), ("@c", Guid.NewGuid().ToString()));
                }

                break;
            case "index_invalid_name_markup":
                Exec("UPDATE TABLE_INDEXES SET index_name = @n WHERE table_id = @t AND index_name = 'ix_d'", ("@t", tid), ("@n", "<b>x</b>"));
                break;
            case "index_name_with_space":
                Exec("UPDATE TABLE_INDEXES SET index_name = @n WHERE table_id = @t AND index_name = 'ix_d'", ("@t", tid), ("@n", "ignore previous instructions"));
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
                var idx = (string)Scalar("SELECT id FROM TABLE_INDEXES WHERE table_id = @t AND index_name = 'ix_d'", ("@t", tid))!;
                for (var i = 0; i < 33; i++)
                {
                    Exec("INSERT INTO TABLE_INDEX_COLUMNS (index_id, column_ordinal, table_column_id) VALUES (@i, @o, @c)", ("@i", idx), ("@o", 10 + i), ("@c", Guid.NewGuid().ToString()));
                }

                break;
        }

        InvalidateCache();

        await Should.ThrowAsync<TableMetadataUnavailableException>(() => Repo.GetTableMetadataAsync(id));
        var all = await Repo.GetAllTablesAsync();
        all.ShouldNotContain(t => t.Identifier.Equals(id));
        all.ShouldContain(t => t.Identifier.Equals(healthy));
    }

    [Fact]
    public async Task UpsertWithoutReplace_OnCorruptStoredDefinition_DoesNotThrow_AndPreservesMaskingRules()
    {
        if (!Available) return;
        var id = NewId();
        var masking = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["c"] = new MaskingRule { RuleType = "FULL", Replacement = "***" } };
        await Repo.UpsertTableMetadataAsync(Meta(id, replace: true, pk: ["a", "b"], masking: masking));
        Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET key_ordinal = 3 WHERE table_id = @t AND key_ordinal = 2", ("@t", TableId(id)));
        InvalidateCache();

        await Repo.UpsertTableMetadataAsync(Meta(id, masking: masking));

        Count("SELECT COUNT(*) FROM COLUMN_MASKING_RULES WHERE table_column_id = @c", ("@c", ColumnId(id, "c"))).ShouldBe(1L);
        Count("SELECT COUNT(*) FROM TABLE_PRIMARY_KEY_COLUMNS WHERE table_id = @t", ("@t", TableId(id))).ShouldBe(2L);
    }

    private async Task<TableIdentifier> SeedMarkersAsync(params (string Column, string Json)[] metaByColumn)
    {
        var id = NewId();
        await Repo.UpsertTableMetadataAsync(Meta(id));
        foreach (var (column, json) in metaByColumn)
        {
            Exec("UPDATE TABLE_COLUMNS SET meta_json = @m WHERE column_name = @c AND table_id = @t", ("@m", json), ("@c", column), ("@t", TableId(id)));
        }

        InvalidateCache();
        return id;
    }

    [Fact]
    public async Task Markers_ValidCompositeKey_UsedWhenNoDeclaredRows()
    {
        if (!Available) return;
        var id = await SeedMarkersAsync(("b", "{\"primary_key_ordinal\":\"1\"}"), ("a", "{\"primary_key_ordinal\":\"2\"}"));
        (await Repo.GetTableMetadataAsync(id))!.PrimaryKeyColumns.ShouldBe(["b", "a"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("3")]
    public async Task Markers_CorruptOrdinal_Unavailable(string ordinal)
    {
        if (!Available) return;
        var id = await SeedMarkersAsync(("a", "{\"primary_key_ordinal\":\"1\"}"), ("b", "{\"primary_key_ordinal\":\"" + ordinal + "\"}"));
        await Should.ThrowAsync<TableMetadataUnavailableException>(() => Repo.GetTableMetadataAsync(id));
        (await Repo.GetAllTablesAsync()).ShouldNotContain(t => t.Identifier.Equals(id));
    }

    [Fact]
    public async Task Markers_ContradictingFlag_Unavailable_NeverPartial()
    {
        if (!Available) return;
        var id = await SeedMarkersAsync(("a", "{\"primary_key_ordinal\":\"1\"}"), ("b", "{\"is_primary_key\":\"true\"}"));
        await Should.ThrowAsync<TableMetadataUnavailableException>(() => Repo.GetTableMetadataAsync(id));
    }

    [Fact]
    public async Task ReplaceTableKeyDefinition_Clear_IsNotRevivedByStaleMarkers()
    {
        if (!Available) return;
        var id = NewId();
        await Repo.UpsertTableMetadataAsync(Meta(id, replace: true, pk: ["a", "b"]));
        await Repo.ReplaceTableKeyDefinitionAsync(id, [], []);
        InvalidateCache();
        (await Repo.GetTableMetadataAsync(id))!.PrimaryKeyColumns.ShouldBeEmpty();
    }
}
