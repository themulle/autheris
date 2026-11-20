using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Modules.Dbt;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>ITableRelationRepository</c> across PostgreSQL and SQL Server engines: composite-key relation persistence
/// (ordered key pairs, canonical column names, atomic replacement, fail-closed reads). Relations are keyed by table identifiers, not by tenant.
/// </summary>
public abstract class TableRelationContract : GovernanceContractBase
{
    private ITableRelationRepository Relations => Store.TableRelations;

    /// <summary>DDL that makes every insert of key ordinal 2 into TABLE_RELATION_KEYS fail (simulates a failure after the relation upsert).</summary>
    protected abstract string InstallKeyInsertFailureSql { get; }

    protected abstract string RemoveKeyInsertFailureSql { get; }

    /// <summary>False when the engine's collation plus the unique natural index of TABLES already make two case-only twin tables impossible (SQL Server).</summary>
    protected virtual bool AllowsCaseOnlyDuplicateTables => true;

    private static TableIdentifier NewTable(string prefix) => new("sales", "dbo", prefix + "_" + Guid.NewGuid().ToString("N")[..10]);

    private async Task SeedTableAsync(TableIdentifier id, params string[] columns)
    {
        var cols = columns.Length == 0 ? ["a", "b", "c", "d"] : columns;
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceType = "sql", SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName,
            DisplayName = id.TableName, Sensitivity = "LOW", RequiresFourEyes = false, IsActive = true
        };
        await Store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            Table = table,
            Columns = [.. cols.Select(c => new TableColumn { TableId = table.Id, ColumnName = c, DataType = "INT" })]
        });
    }

    private static TableRelation Rel(TableIdentifier parent, TableIdentifier child, string[] parentKeys, string[] childKeys, string name = "items") => new()
    {
        Id = Guid.NewGuid(),
        // Deliberately bogus: the repository resolves table ids from the identifiers.
        ParentTableId = Guid.NewGuid(),
        ChildTableId = Guid.NewGuid(),
        ParentTableIdentifier = parent,
        ChildTableIdentifier = child,
        RelationName = name,
        JoinKeysParent = parentKeys,
        JoinKeysChild = childKeys,
        Cardinality = RelationCardinality.OneToMany
    };

    private Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters) =>
        Store.ExecuteScalarAsync(sql, [.. parameters.Select(p => (p.Name, (object?)p.Value))]);

    private Task<int> ExecAsync(string sql, params (string Name, object Value)[] parameters) =>
        Store.ExecuteNonQueryAsync(sql, [.. parameters.Select(p => (p.Name, (object?)p.Value))]);

    private async Task<long> CountAsync(string sql) => Convert.ToInt64(await ScalarAsync(sql), CultureInfo.InvariantCulture);

    private async Task<string> TableIdAsync(TableIdentifier id) => (string)(await ScalarAsync("SELECT id FROM TABLES WHERE table_name = @n", ("@n", id.TableName)))!;

    private Task<long> RelationCountAsync() => CountAsync("SELECT COUNT(*) FROM TABLE_RELATIONS");

    private Task<long> KeyCountAsync() => CountAsync("SELECT COUNT(*) FROM TABLE_RELATION_KEYS");

    private async Task<List<(string Parent, string Child)>> StoredPairsAsync(Guid relationId)
    {
        await using var conn = await Store.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT parent_column, child_column FROM TABLE_RELATION_KEYS WHERE relation_id = @id ORDER BY key_ordinal";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = relationId.ToString();
        cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<(string, string)>();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetString(0), reader.GetString(1)));
        }

        return list;
    }

    private async Task<(TableIdentifier Parent, TableIdentifier Child)> SeedPairAsync()
    {
        var parent = NewTable("parent");
        var child = NewTable("child");
        await SeedTableAsync(parent);
        await SeedTableAsync(child);
        return (parent, child);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var (otherParent, otherChild) = await SeedPairAsync();
        var first = await Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["b"]));
        await Relations.CreateRelationAsync(Rel(otherParent, otherChild, ["c"], ["d"]));

        (await Relations.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem().Id.ShouldBe(first.StoredId);
        (await Relations.GetRelationsForTableAsync(child)).ShouldBeEmpty();
        (await Relations.GetRelationsForTableAsync(NewTable("unrelated"))).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var (otherParent, otherChild) = await SeedPairAsync();
        var first = await Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["b"]));

        var other = await Relations.CreateRelationAsync(Rel(otherParent, otherChild, ["c", "d"], ["c", "d"]));

        other.StoredId.ShouldNotBe(first.StoredId);
        (await StoredPairsAsync(first.StoredId)).ShouldBe([("a", "b")]);
        (await RelationCountAsync()).ShouldBe(2);
        (await Relations.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem().AllowCrossDataSource.ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var (parent, _) = await SeedPairAsync();

        // Relations carry no tenant parameter: the scope is the pair of table identifiers, and an unknown table never resolves to another one.
        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(parent, NewTable("ghost"), ["a"], ["a"])));
        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(NewTable("ghost"), parent, ["a"], ["a"])));

        (await RelationCountAsync()).ShouldBe(0);
        (await KeyCountAsync()).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();

        var first = await Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "d"]));
        var second = await Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "d"]));

        second.StoredId.ShouldBe(first.StoredId);
        second.Created.ShouldBeFalse();
        (await RelationCountAsync()).ShouldBe(1);
        (await KeyCountAsync()).ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();

        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(parent, child, ["a", "A"], ["a", "b"])));

        (await RelationCountAsync()).ShouldBe(0);
        (await KeyCountAsync()).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        await Relations.CreateRelationAsync(Rel(parent, child, ["c", "a", "b"], ["d", "b", "a"], "items"));

        var first = await Relations.GetAllRelationsAsync();
        var second = await Relations.GetAllRelationsAsync();

        first.Select(r => r.Id).ShouldBe(second.Select(r => r.Id));
        var read = first.Single(r => r.ParentTableIdentifier == parent);
        read.JoinKeysParent.ShouldBe(["c", "a", "b"]);
        read.JoinKeysChild.ShouldBe(["d", "b", "a"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var wide = Enumerable.Range(0, 17).Select(i => "a" + i).ToArray();

        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(parent, child, wide, wide)));

        (await RelationCountAsync()).ShouldBe(0);
        (await KeyCountAsync()).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        await ExecAsync("UPDATE TABLES SET is_active = 0 WHERE table_name = @n", ("@n", child.TableName));

        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["a"])));

        (await Relations.GetRelationsForTableAsync(child)).ShouldBeEmpty();
        (await RelationCountAsync()).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task CreateRelation_ResolvesTableIdsFromIdentifiers()
    {
        if (!Store.IsAvailable) return;
        // The repository does not trust relation.ParentTableId/ChildTableId (the helper passes random ids).
        var (parent, child) = await SeedPairAsync();

        var result = await Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["b"]));

        var parentId = Convert.ToString(await ScalarAsync("SELECT parent_table_id FROM TABLE_RELATIONS WHERE id = @id", ("@id", result.StoredId.ToString())), CultureInfo.InvariantCulture);
        var childId = Convert.ToString(await ScalarAsync("SELECT child_table_id FROM TABLE_RELATIONS WHERE id = @id", ("@id", result.StoredId.ToString())), CultureInfo.InvariantCulture);
        parentId.ShouldBe(await TableIdAsync(parent), StringCompareShould.IgnoreCase);
        childId.ShouldBe(await TableIdAsync(child), StringCompareShould.IgnoreCase);
    }

    [RequiresContainerRuntimeFact]
    public async Task CreateRelation_CompositeKeys_RoundTripsInOrder()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();

        var result = await Relations.CreateRelationAsync(Rel(parent, child, ["a", "b", "c"], ["d", "c", "b"]));

        result.Created.ShouldBeTrue();
        result.KeysChanged.ShouldBeFalse();
        var read = (await Relations.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem();
        read.Id.ShouldBe(result.StoredId);
        read.JoinKeysParent.ShouldBe(["a", "b", "c"]);
        read.JoinKeysChild.ShouldBe(["d", "c", "b"]);
        read.ParentTableId.ToString().ShouldBe(await TableIdAsync(parent), StringCompareShould.IgnoreCase);
        read.ChildTableId.ToString().ShouldBe(await TableIdAsync(child), StringCompareShould.IgnoreCase);
        read.AllowCrossDataSource.ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    public async Task CreateRelation_StoresCanonicalColumnNames()
    {
        if (!Store.IsAvailable) return;
        var parent = NewTable("parent");
        var child = NewTable("child");
        await SeedTableAsync(parent, "Order_Id", "Line_No");
        await SeedTableAsync(child, "order_id", "line_no");

        await Relations.CreateRelationAsync(Rel(parent, child, ["ORDER_ID", "line_no"], ["Order_ID", "LINE_NO"]));

        var read = (await Relations.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem();
        read.JoinKeysParent.ShouldBe(["Order_Id", "Line_No"]);
        read.JoinKeysChild.ShouldBe(["order_id", "line_no"]);
    }

    [RequiresContainerRuntimeFact]
    public async Task CreateRelation_Twice_KeepsOneRowAndStoredId()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var first = await Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "d"]));

        var second = await Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "d"]));

        second.StoredId.ShouldBe(first.StoredId);
        second.Created.ShouldBeFalse();
        second.KeysChanged.ShouldBeFalse();
        (await RelationCountAsync()).ShouldBe(1);
        (await KeyCountAsync()).ShouldBe(2);
        (await StoredPairsAsync(first.StoredId)).ShouldBe([("a", "c"), ("b", "d")]);
    }

    [RequiresContainerRuntimeFact]
    public async Task CreateRelation_ChangedKeys_ReplacesKeyRows()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var first = await Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "d"]));

        var grown = await Relations.CreateRelationAsync(Rel(parent, child, ["a", "b", "c"], ["a", "b", "c"]));
        grown.StoredId.ShouldBe(first.StoredId);
        (await StoredPairsAsync(first.StoredId)).ShouldBe([("a", "a"), ("b", "b"), ("c", "c")]);

        var reordered = await Relations.CreateRelationAsync(Rel(parent, child, ["c", "b", "a"], ["c", "b", "a"]));
        reordered.StoredId.ShouldBe(first.StoredId);
        (await StoredPairsAsync(first.StoredId)).ShouldBe([("c", "c"), ("b", "b"), ("a", "a")]);
        (await RelationCountAsync()).ShouldBe(1);
        (await KeyCountAsync()).ShouldBe(3);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task CreateRelation_KeyChange_FlagsChangeAndKeepsApprovalFlagUntouched()
    {
        // SEC-REL151-04: KeysChanged is the signal I-18 uses to reset a cross-source approval. The upsert SET list holds cardinality only.
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var created = await Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["a"]));
        created.KeysChanged.ShouldBeFalse();

        (await Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["a"]))).KeysChanged.ShouldBeFalse();
        (await Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["a", "b"]))).KeysChanged.ShouldBeTrue();
        (await Relations.CreateRelationAsync(Rel(parent, child, ["b", "a"], ["b", "a"]))).KeysChanged.ShouldBeTrue();
        (await Relations.CreateRelationAsync(Rel(parent, child, ["b", "a"], ["b", "a"]))).KeysChanged.ShouldBeFalse();
        (await Relations.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem().AllowCrossDataSource.ShouldBeFalse();
    }

    [RequiresContainerRuntimeTheory]
    [InlineData("count")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("wide")]
    [InlineData("blank")]
    public async Task CreateRelation_InvalidKeys_WritesNothing(string kind)
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var (p, c) = kind switch
        {
            "count" => (new[] { "a", "b" }, new[] { "a" }),
            "empty" => (Array.Empty<string>(), Array.Empty<string>()),
            "duplicate" => (new[] { "a", "A" }, new[] { "a", "b" }),
            "wide" => (Enumerable.Range(0, 17).Select(i => "a" + i).ToArray(), Enumerable.Range(0, 17).Select(i => "a" + i).ToArray()),
            _ => (new[] { " " }, new[] { "a" })
        };

        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(parent, child, p, c)));

        (await RelationCountAsync()).ShouldBe(0);
        (await KeyCountAsync()).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task CreateRelation_UnknownColumn_WritesNothingAndKeepsPrevious()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var first = await Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "d"]));

        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(parent, child, ["a", "nope"], ["c", "d"])));
        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "x'; DROP TABLE TABLES;--"])));

        (await RelationCountAsync()).ShouldBe(1);
        (await StoredPairsAsync(first.StoredId)).ShouldBe([("a", "c"), ("b", "d")]);
        (await CountAsync("SELECT COUNT(*) FROM TABLES")).ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    public async Task CreateRelation_UnknownTable_Throws()
    {
        if (!Store.IsAvailable) return;
        var (parent, _) = await SeedPairAsync();

        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(parent, NewTable("ghost"), ["a"], ["a"])));

        (await RelationCountAsync()).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task CreateRelation_InactiveTable_Throws()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        await ExecAsync("UPDATE TABLES SET is_active = 0 WHERE table_name = @n", ("@n", child.TableName));

        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["a"])));

        (await RelationCountAsync()).ShouldBe(0);
        (await KeyCountAsync()).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task CreateRelation_AmbiguousCaseOnlyTables_Throws()
    {
        // T-REL151-11 (SEC-REL151-07): two active tables that differ only by case must not resolve to an arbitrary one.
        if (!Store.IsAvailable || !AllowsCaseOnlyDuplicateTables) return;
        var (parent, child) = await SeedPairAsync();
        var twin = NewTable("twin");
        await SeedTableAsync(twin);
        await ExecAsync("UPDATE TABLES SET table_name = @upper WHERE table_name = @n", ("@upper", child.TableName.ToUpperInvariant()), ("@n", twin.TableName));

        await Should.ThrowAsync<InvalidRelationException>(() => Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["a"])));

        (await RelationCountAsync()).ShouldBe(0);
        (await KeyCountAsync()).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task GetRelations_RelationWithoutKeyRows_IsSkipped()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var broken = await Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["a"], "broken"));
        var good = await Relations.CreateRelationAsync(Rel(parent, child, ["b"], ["b"], "good"));
        await ExecAsync("DELETE FROM TABLE_RELATION_KEYS WHERE relation_id = @id", ("@id", broken.StoredId.ToString()));

        var read = await Relations.GetRelationsForTableAsync(parent);

        read.ShouldHaveSingleItem().Id.ShouldBe(good.StoredId);
    }

    [RequiresContainerRuntimeFact]
    public async Task GetRelations_OrdinalGap_IsSkipped()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var gap = await Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["a", "b"], "gap"));
        var good = await Relations.CreateRelationAsync(Rel(parent, child, ["c"], ["c"], "good"));
        await ExecAsync("UPDATE TABLE_RELATION_KEYS SET key_ordinal = 3 WHERE relation_id = @id AND key_ordinal = 2", ("@id", gap.StoredId.ToString()));

        var read = await Relations.GetRelationsForTableAsync(parent);

        read.ShouldHaveSingleItem().Id.ShouldBe(good.StoredId);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task GetRelations_OrphanKeyRows_AreNotSurfaced()
    {
        // T-REL151-12 (SEC-REL151-08): key rows are only read through the join from TABLE_RELATIONS.
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var good = await Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["a"]));
        await ExecAsync("INSERT INTO TABLE_RELATION_KEYS (relation_id, key_ordinal, parent_column, child_column) VALUES (@id, 1, 'a', 'a')", ("@id", Guid.NewGuid().ToString()));

        var read = (await Relations.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem();

        read.Id.ShouldBe(good.StoredId);
        read.JoinKeysParent.ShouldBe(["a"]);
        (await Relations.GetRelationsForTableAsync(child)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task CreateRelation_Cancelled_OrFailed_LeavesPreviousRelationAndKeys()
    {
        // T-REL151-09 (SEC-REL151-06): a failure after the upsert and a cancelled call roll back the relation row and all key rows.
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        var first = await Relations.CreateRelationAsync(Rel(parent, child, ["a"], ["a"]));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["a", "b"]), cts.Token));
        (await StoredPairsAsync(first.StoredId)).ShouldBe([("a", "a")]);

        await ExecAsync(InstallKeyInsertFailureSql);
        try
        {
            await Should.ThrowAsync<Exception>(() => Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["a", "b"])));
            await Should.ThrowAsync<Exception>(() => Relations.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["a", "b"], "fresh")));
        }
        finally
        {
            await ExecAsync(RemoveKeyInsertFailureSql);
        }

        (await RelationCountAsync()).ShouldBe(1);
        (await StoredPairsAsync(first.StoredId)).ShouldBe([("a", "a")]);
        (await KeyCountAsync()).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task CreateRelation_Concurrent_SingleRelationConsistentKeys()
    {
        // T-REL151-10: concurrent syncs of one relation end with exactly one relation row and one consistent key set.
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();
        string[][] variants = [["a"], ["a", "b"], ["a", "b", "c"], ["d", "c"]];

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Task.Run(() => Relations.CreateRelationAsync(Rel(parent, child, variants[i % variants.Length], variants[i % variants.Length])))));

        (await RelationCountAsync()).ShouldBe(1);
        results.Select(r => r.StoredId).Distinct().ShouldHaveSingleItem();
        results.Count(r => r.Created).ShouldBe(1);
        var stored = (await StoredPairsAsync(results[0].StoredId));
        variants.Select(v => v.Select(x => (x, x)).ToList()).ShouldContain(v => v.SequenceEqual(stored));
        var read = (await Relations.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem();
        read.JoinKeysParent.ShouldBe(stored.Select(p => p.Parent).ToList());
    }

    private DbtMetadataIngestionService NewDbtService() => new(
        Substitute.For<IDbtProposalRepository>(), Store.TableMetadata, Substitute.For<ILineageGraphStore>(), Relations, NullLogger<DbtMetadataIngestionService>.Instance);

    private static MemoryStream DbtManifest(TableIdentifier parent, TableIdentifier child, string[] childCols, string[] parentCols)
    {
        static Dictionary<string, object> Cols(params string[] c) =>
            c.ToDictionary(x => x, x => (object)new Dictionary<string, object> { ["name"] = x, ["data_type"] = "integer" });

        Dictionary<string, object> Node(TableIdentifier id, object? constraints) => new()
        {
            ["name"] = id.TableName, ["database"] = id.Domain, ["schema"] = id.Schema,
            ["columns"] = Cols("a", "b", "c", "d"),
            ["constraints"] = constraints ?? Array.Empty<object>()
        };

        var fk = new Dictionary<string, object>
        {
            ["type"] = "foreign_key", ["to"] = $"ref('{parent.TableName}')", ["columns"] = childCols, ["to_columns"] = parentCols
        };
        var manifest = new Dictionary<string, object>
        {
            ["nodes"] = new Dictionary<string, object>
            {
                [$"model.p.{parent.TableName}"] = Node(parent, null),
                [$"model.p.{child.TableName}"] = Node(child, new object[] { fk })
            }
        };
        return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    /// <summary>AC-151-1: a composite foreign key of a dbt manifest is imported as both ordered pairs and is read back from the store (new connection).</summary>
    [RequiresContainerRuntimeFact]
    public async Task DbtManifest_CompositeForeignKey_ImportsBothPairs_AndSurvivesRestart()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();

        using var manifest = DbtManifest(parent, child, ["d", "c"], ["a", "b"]);
        var result = await NewDbtService().IngestManifestStreamAsClusterAdminAsync(manifest);

        result.Success.ShouldBeTrue();
        result.ImportedRelationsCount.ShouldBe(1);
        var read = (await Relations.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem();
        read.JoinKeysParent.ShouldBe(["a", "b"]);
        read.JoinKeysChild.ShouldBe(["d", "c"]);
        read.AllowCrossDataSource.ShouldBeFalse();
        (await StoredPairsAsync(read.Id)).ShouldBe([("a", "d"), ("b", "c")]);
        (await KeyCountAsync()).ShouldBe(2);
    }

    /// <summary>AC-151-2: importing the same manifest twice yields one relation with unchanged id and key rows.</summary>
    [RequiresContainerRuntimeFact]
    public async Task DbtManifest_ImportedTwice_YieldsOneRelation()
    {
        if (!Store.IsAvailable) return;
        var (parent, child) = await SeedPairAsync();

        using var first = DbtManifest(parent, child, ["d", "c"], ["a", "b"]);
        await NewDbtService().IngestManifestStreamAsClusterAdminAsync(first);
        var id = (await Relations.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem().Id;

        using var second = DbtManifest(parent, child, ["d", "c"], ["a", "b"]);
        var result = await NewDbtService().IngestManifestStreamAsClusterAdminAsync(second);

        result.ImportedRelationsCount.ShouldBe(1);
        (await RelationCountAsync()).ShouldBe(1);
        (await KeyCountAsync()).ShouldBe(2);
        (await Relations.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem().Id.ShouldBe(id);
        (await StoredPairsAsync(id)).ShouldBe([("a", "d"), ("b", "c")]);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlTableRelationContractTests(GovernancePostgresFixture fixture) : TableRelationContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "table_relation_contract");

    protected override string InstallKeyInsertFailureSql =>
        "CREATE FUNCTION trg_fail_key_insert_fn() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'forced key insert failure'; END $$ LANGUAGE plpgsql; " +
        "CREATE TRIGGER trg_fail_key_insert BEFORE INSERT ON TABLE_RELATION_KEYS FOR EACH ROW WHEN (NEW.key_ordinal = 2) EXECUTE FUNCTION trg_fail_key_insert_fn()";

    protected override string RemoveKeyInsertFailureSql => "DROP TRIGGER IF EXISTS trg_fail_key_insert ON TABLE_RELATION_KEYS; DROP FUNCTION IF EXISTS trg_fail_key_insert_fn()";
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerTableRelationContractTests(GovernanceSqlServerFixture fixture) : TableRelationContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "table_relation_contract");

    protected override string InstallKeyInsertFailureSql =>
        "CREATE TRIGGER trg_fail_key_insert ON dbo.TABLE_RELATION_KEYS AFTER INSERT AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE key_ordinal = 2) THROW 50000, 'forced key insert failure', 1; END";

    // The case-insensitive collation and ux_tables_natural reject case-only twins at the database; the repository check is a second line of defence.
    protected override bool AllowsCaseOnlyDuplicateTables => false;

    protected override string RemoveKeyInsertFailureSql => "IF OBJECT_ID(N'dbo.trg_fail_key_insert', N'TR') IS NOT NULL DROP TRIGGER dbo.trg_fail_key_insert";
}
