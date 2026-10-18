using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Governance;

/// <summary>
/// Issue #151 (PLAN-REL-151 section 5/6, SEC-REL151-03/04/06/07/08): persistence contract of composite-key relations; run on SQLite (unit) and on
/// PostgreSQL and SQL Server (integration, linked). Every test seeds its own tables and one database serves one test.
/// </summary>
public abstract class RelationRepositoryContractTestsBase : IAsyncLifetime
{
    protected IGovernanceRepository Repo { get; private set; } = null!;
    protected bool Available { get; private set; }

    protected abstract Task<IGovernanceRepository?> CreateRepositoryAsync();

    protected abstract Task DisposeRepositoryAsync();

    protected abstract DbConnection OpenRaw();

    /// <summary>DDL that makes every insert of key ordinal 2 into TABLE_RELATION_KEYS fail (simulates a failure after the relation upsert).</summary>
    protected abstract string InstallKeyInsertFailureSql { get; }

    protected abstract string RemoveKeyInsertFailureSql { get; }

    /// <summary>False when the engine's collation plus the unique natural index of TABLES already make two case-only twin tables impossible (SQL Server).</summary>
    protected virtual bool AllowsCaseOnlyDuplicateTables => true;

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

    protected static TableIdentifier NewTable(string prefix) => new("sales", "dbo", prefix + "_" + Guid.NewGuid().ToString("N")[..10]);

    protected async Task SeedTableAsync(TableIdentifier id, params string[] columns)
    {
        var cols = columns.Length == 0 ? ["a", "b", "c", "d"] : columns;
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceType = "sql", SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName,
            DisplayName = id.TableName, Sensitivity = "LOW", RequiresFourEyes = false, IsActive = true
        };
        await Repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            Table = table,
            Columns = [.. cols.Select(c => new TableColumn { TableId = table.Id, ColumnName = c, DataType = "INT" })]
        });
    }

    protected static TableRelation Rel(TableIdentifier parent, TableIdentifier child, string[] parentKeys, string[] childKeys, string name = "items") => new()
    {
        Id = Guid.NewGuid(),
        // Deliberately bogus: the repository resolves table ids from the identifiers (AC-151-4).
        ParentTableId = Guid.NewGuid(),
        ChildTableId = Guid.NewGuid(),
        ParentTableIdentifier = parent,
        ChildTableIdentifier = child,
        RelationName = name,
        JoinKeysParent = parentKeys,
        JoinKeysChild = childKeys,
        Cardinality = RelationCardinality.OneToMany
    };

    protected object? Scalar(string sql, params (string Name, object Value)[] parameters)
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

    protected void Exec(string sql, params (string Name, object Value)[] parameters) => _ = ExecCount(sql, parameters);

    protected int ExecCount(string sql, params (string Name, object Value)[] parameters)
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

        return cmd.ExecuteNonQuery();
    }

    protected long Count(string sql, params (string Name, object Value)[] parameters) => Convert.ToInt64(Scalar(sql, parameters), CultureInfo.InvariantCulture);

    protected string TableId(TableIdentifier id) => (string)Scalar("SELECT id FROM TABLES WHERE table_name = @n", ("@n", id.TableName))!;

    private long RelationCount() => Count("SELECT COUNT(*) FROM TABLE_RELATIONS");

    private long KeyCount() => Count("SELECT COUNT(*) FROM TABLE_RELATION_KEYS");

    private List<(string Parent, string Child)> StoredPairs(Guid relationId)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT parent_column, child_column FROM TABLE_RELATION_KEYS WHERE relation_id = @id ORDER BY key_ordinal";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = relationId.ToString();
        cmd.Parameters.Add(p);
        using var reader = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (reader.Read())
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

    [Fact]
    public async Task CreateRelation_CompositeKeys_RoundTripsInOrder()
    {
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();

        var result = await Repo.CreateRelationAsync(Rel(parent, child, ["a", "b", "c"], ["d", "c", "b"]));

        result.Created.ShouldBeTrue();
        result.KeysChanged.ShouldBeFalse();
        var read = (await Repo.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem();
        read.Id.ShouldBe(result.StoredId);
        read.JoinKeysParent.ShouldBe(["a", "b", "c"]);
        read.JoinKeysChild.ShouldBe(["d", "c", "b"]);
        read.ParentTableId.ToString().ShouldBe(TableId(parent), StringCompareShould.IgnoreCase);
        read.ChildTableId.ToString().ShouldBe(TableId(child), StringCompareShould.IgnoreCase);
        read.AllowCrossDataSource.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateRelation_StoresCanonicalColumnNames()
    {
        if (!Available) return;
        var parent = NewTable("parent");
        var child = NewTable("child");
        await SeedTableAsync(parent, "Order_Id", "Line_No");
        await SeedTableAsync(child, "order_id", "line_no");

        await Repo.CreateRelationAsync(Rel(parent, child, ["ORDER_ID", "line_no"], ["Order_ID", "LINE_NO"]));

        var read = (await Repo.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem();
        read.JoinKeysParent.ShouldBe(["Order_Id", "Line_No"]);
        read.JoinKeysChild.ShouldBe(["order_id", "line_no"]);
    }

    [Fact]
    public async Task CreateRelation_Twice_KeepsOneRowAndStoredId()
    {
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        var first = await Repo.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "d"]));

        var second = await Repo.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "d"]));

        second.StoredId.ShouldBe(first.StoredId);
        second.Created.ShouldBeFalse();
        second.KeysChanged.ShouldBeFalse();
        RelationCount().ShouldBe(1);
        KeyCount().ShouldBe(2);
        StoredPairs(first.StoredId).ShouldBe([("a", "c"), ("b", "d")]);
    }

    [Fact]
    public async Task CreateRelation_ChangedKeys_ReplacesKeyRows()
    {
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        var first = await Repo.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "d"]));

        var grown = await Repo.CreateRelationAsync(Rel(parent, child, ["a", "b", "c"], ["a", "b", "c"]));
        grown.StoredId.ShouldBe(first.StoredId);
        StoredPairs(first.StoredId).ShouldBe([("a", "a"), ("b", "b"), ("c", "c")]);

        var reordered = await Repo.CreateRelationAsync(Rel(parent, child, ["c", "b", "a"], ["c", "b", "a"]));
        reordered.StoredId.ShouldBe(first.StoredId);
        StoredPairs(first.StoredId).ShouldBe([("c", "c"), ("b", "b"), ("a", "a")]);
        RelationCount().ShouldBe(1);
        KeyCount().ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CreateRelation_KeyChange_FlagsChangeAndKeepsApprovalFlagUntouched()
    {
        // SEC-REL151-04: KeysChanged is the signal I-18 uses to reset a cross-source approval. The upsert SET list holds cardinality only.
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        var created = await Repo.CreateRelationAsync(Rel(parent, child, ["a"], ["a"]));
        created.KeysChanged.ShouldBeFalse();

        (await Repo.CreateRelationAsync(Rel(parent, child, ["a"], ["a"]))).KeysChanged.ShouldBeFalse();
        (await Repo.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["a", "b"]))).KeysChanged.ShouldBeTrue();
        (await Repo.CreateRelationAsync(Rel(parent, child, ["b", "a"], ["b", "a"]))).KeysChanged.ShouldBeTrue();
        (await Repo.CreateRelationAsync(Rel(parent, child, ["b", "a"], ["b", "a"]))).KeysChanged.ShouldBeFalse();
        (await Repo.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem().AllowCrossDataSource.ShouldBeFalse();
    }

    [Theory]
    [InlineData("count")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("wide")]
    [InlineData("blank")]
    public async Task CreateRelation_InvalidKeys_WritesNothing(string kind)
    {
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        var (p, c) = kind switch
        {
            "count" => (new[] { "a", "b" }, new[] { "a" }),
            "empty" => (Array.Empty<string>(), Array.Empty<string>()),
            "duplicate" => (new[] { "a", "A" }, new[] { "a", "b" }),
            "wide" => (Enumerable.Range(0, 17).Select(i => "a" + i).ToArray(), Enumerable.Range(0, 17).Select(i => "a" + i).ToArray()),
            _ => (new[] { " " }, new[] { "a" })
        };

        await Should.ThrowAsync<InvalidRelationException>(() => Repo.CreateRelationAsync(Rel(parent, child, p, c)));

        RelationCount().ShouldBe(0);
        KeyCount().ShouldBe(0);
    }

    [Fact]
    public async Task CreateRelation_UnknownColumn_WritesNothingAndKeepsPrevious()
    {
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        var first = await Repo.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "d"]));

        await Should.ThrowAsync<InvalidRelationException>(() => Repo.CreateRelationAsync(Rel(parent, child, ["a", "nope"], ["c", "d"])));
        await Should.ThrowAsync<InvalidRelationException>(() => Repo.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["c", "x'; DROP TABLE TABLES;--"])));

        RelationCount().ShouldBe(1);
        StoredPairs(first.StoredId).ShouldBe([("a", "c"), ("b", "d")]);
        Count("SELECT COUNT(*) FROM TABLES").ShouldBe(2);
    }

    [Fact]
    public async Task CreateRelation_UnknownTable_Throws()
    {
        if (!Available) return;
        var (parent, _) = await SeedPairAsync();

        await Should.ThrowAsync<InvalidRelationException>(() => Repo.CreateRelationAsync(Rel(parent, NewTable("ghost"), ["a"], ["a"])));

        RelationCount().ShouldBe(0);
    }

    [Fact]
    public async Task CreateRelation_InactiveTable_Throws()
    {
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        Exec("UPDATE TABLES SET is_active = 0 WHERE table_name = @n", ("@n", child.TableName));

        await Should.ThrowAsync<InvalidRelationException>(() => Repo.CreateRelationAsync(Rel(parent, child, ["a"], ["a"])));

        RelationCount().ShouldBe(0);
        KeyCount().ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CreateRelation_AmbiguousCaseOnlyTables_Throws()
    {
        // T-REL151-11 (SEC-REL151-07): two active tables that differ only by case must not resolve to an arbitrary one.
        if (!Available || !AllowsCaseOnlyDuplicateTables) return;
        var (parent, child) = await SeedPairAsync();
        var twin = NewTable("twin");
        await SeedTableAsync(twin);
        Exec("UPDATE TABLES SET table_name = @upper WHERE table_name = @n", ("@upper", child.TableName.ToUpperInvariant()), ("@n", twin.TableName));

        await Should.ThrowAsync<InvalidRelationException>(() => Repo.CreateRelationAsync(Rel(parent, child, ["a"], ["a"])));

        RelationCount().ShouldBe(0);
        KeyCount().ShouldBe(0);
    }

    [Fact]
    public async Task GetRelations_RelationWithoutKeyRows_IsSkipped()
    {
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        var broken = await Repo.CreateRelationAsync(Rel(parent, child, ["a"], ["a"], "broken"));
        var good = await Repo.CreateRelationAsync(Rel(parent, child, ["b"], ["b"], "good"));
        Exec("DELETE FROM TABLE_RELATION_KEYS WHERE relation_id = @id", ("@id", broken.StoredId.ToString()));

        var read = await Repo.GetRelationsForTableAsync(parent);

        read.ShouldHaveSingleItem().Id.ShouldBe(good.StoredId);
    }

    [Fact]
    public async Task GetRelations_OrdinalGap_IsSkipped()
    {
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        var gap = await Repo.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["a", "b"], "gap"));
        var good = await Repo.CreateRelationAsync(Rel(parent, child, ["c"], ["c"], "good"));
        Exec("UPDATE TABLE_RELATION_KEYS SET key_ordinal = 3 WHERE relation_id = @id AND key_ordinal = 2", ("@id", gap.StoredId.ToString()));

        var read = await Repo.GetRelationsForTableAsync(parent);

        read.ShouldHaveSingleItem().Id.ShouldBe(good.StoredId);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GetRelations_OrphanKeyRows_AreNotSurfaced()
    {
        // T-REL151-12 (SEC-REL151-08): key rows are only read through the join from TABLE_RELATIONS.
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        var good = await Repo.CreateRelationAsync(Rel(parent, child, ["a"], ["a"]));
        Exec("INSERT INTO TABLE_RELATION_KEYS (relation_id, key_ordinal, parent_column, child_column) VALUES (@id, 1, 'a', 'a')", ("@id", Guid.NewGuid().ToString()));

        var read = (await Repo.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem();

        read.Id.ShouldBe(good.StoredId);
        read.JoinKeysParent.ShouldBe(["a"]);
        (await Repo.GetRelationsForTableAsync(child)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CreateRelation_Cancelled_OrFailed_LeavesPreviousRelationAndKeys()
    {
        // T-REL151-09 (SEC-REL151-06): a failure after the upsert and a cancelled call roll back the relation row and all key rows.
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        var first = await Repo.CreateRelationAsync(Rel(parent, child, ["a"], ["a"]));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Repo.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["a", "b"]), cts.Token));
        StoredPairs(first.StoredId).ShouldBe([("a", "a")]);

        Exec(InstallKeyInsertFailureSql);
        try
        {
            await Should.ThrowAsync<Exception>(() => Repo.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["a", "b"])));
            await Should.ThrowAsync<Exception>(() => Repo.CreateRelationAsync(Rel(parent, child, ["a", "b"], ["a", "b"], "fresh")));
        }
        finally
        {
            Exec(RemoveKeyInsertFailureSql);
        }

        RelationCount().ShouldBe(1);
        StoredPairs(first.StoredId).ShouldBe([("a", "a")]);
        KeyCount().ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CreateRelation_Concurrent_SingleRelationConsistentKeys()
    {
        // T-REL151-10: concurrent syncs of one relation end with exactly one relation row and one consistent key set.
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();
        string[][] variants = [["a"], ["a", "b"], ["a", "b", "c"], ["d", "c"]];

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Task.Run(() => Repo.CreateRelationAsync(Rel(parent, child, variants[i % variants.Length], variants[i % variants.Length])))));

        RelationCount().ShouldBe(1);
        results.Select(r => r.StoredId).Distinct().ShouldHaveSingleItem();
        results.Count(r => r.Created).ShouldBe(1);
        var stored = StoredPairs(results[0].StoredId);
        variants.Select(v => v.Select(x => (x, x)).ToList()).ShouldContain(v => v.SequenceEqual(stored));
        var read = (await Repo.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem();
        read.JoinKeysParent.ShouldBe(stored.Select(p => p.Parent).ToList());
    }

    private DbtMetadataIngestionService NewDbtService() => new(
        Substitute.For<IDbtProposalRepository>(), (ITableMetadataRepository)Repo, Substitute.For<ILineageGraphStore>(), (ITableRelationRepository)Repo, NullLogger<DbtMetadataIngestionService>.Instance);

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
    [Fact]
    public async Task DbtManifest_CompositeForeignKey_ImportsBothPairs_AndSurvivesRestart()
    {
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();

        using var manifest = DbtManifest(parent, child, ["d", "c"], ["a", "b"]);
        var result = await NewDbtService().IngestManifestStreamAsync(manifest);

        result.Success.ShouldBeTrue();
        result.ImportedRelationsCount.ShouldBe(1);
        var read = (await Repo.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem();
        read.JoinKeysParent.ShouldBe(["a", "b"]);
        read.JoinKeysChild.ShouldBe(["d", "c"]);
        read.AllowCrossDataSource.ShouldBeFalse();
        StoredPairs(read.Id).ShouldBe([("a", "d"), ("b", "c")]);
        KeyCount().ShouldBe(2);
    }

    /// <summary>AC-151-2: importing the same manifest twice yields one relation with unchanged id and key rows.</summary>
    [Fact]
    public async Task DbtManifest_ImportedTwice_YieldsOneRelation()
    {
        if (!Available) return;
        var (parent, child) = await SeedPairAsync();

        using var first = DbtManifest(parent, child, ["d", "c"], ["a", "b"]);
        await NewDbtService().IngestManifestStreamAsync(first);
        var id = (await Repo.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem().Id;

        using var second = DbtManifest(parent, child, ["d", "c"], ["a", "b"]);
        var result = await NewDbtService().IngestManifestStreamAsync(second);

        result.ImportedRelationsCount.ShouldBe(1);
        RelationCount().ShouldBe(1);
        KeyCount().ShouldBe(2);
        (await Repo.GetRelationsForTableAsync(parent)).ShouldHaveSingleItem().Id.ShouldBe(id);
        StoredPairs(id).ShouldBe([("a", "d"), ("b", "c")]);
    }
}
