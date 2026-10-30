namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #390 (WP-390-4): the default read order, the tie-breaker and the bulk keyset stream follow the declared key and index
/// directions; undeclared tables, filters, masking and an explicit ORDER BY compile unchanged.
/// </summary>
public sealed class Issue390ReadOrderDirectionTests
{
    private const KeyColumnDirection Asc = KeyColumnDirection.Ascending;
    private const KeyColumnDirection Desc = KeyColumnDirection.Descending;
    private static readonly TableIdentifier Readings = new("d", "main", "readings");
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private static TableMetadata Meta(
        IReadOnlyList<string>? pk = null,
        IReadOnlyList<KeyColumnDirection>? directions = null,
        IReadOnlyList<TableIndexDefinition>? indexes = null,
        params string[] cols)
    {
        if (cols.Length == 0)
        {
            cols = ["device_id", "ts", "value"];
        }

        return new TableMetadata
        {
            Identifier = Readings,
            Table = new Table { SourceName = "d", SchemaName = "main", TableName = "readings", SourceType = "SqlServer" },
            PrimaryKeyColumns = pk ?? [],
            PrimaryKeyDirections = directions ?? [],
            Indexes = indexes ?? [],
            Columns = cols.Select(c => new TableColumn { ColumnName = c, DataType = "integer" }).ToList()
        };
    }

    private static TableAccessDecision AllClear(TableMetadata m) => TableAccessDecision.Allowed(
        m.Identifier,
        m.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase),
        hasUnconstrainedColumnAllow: true);

    private static TableAccessDecision Levels(TableMetadata m, params (string Name, ColumnAccessLevel Level)[] levels) => TableAccessDecision.Allowed(
        m.Identifier,
        levels.ToDictionary(l => l.Name, l => l.Level, StringComparer.OrdinalIgnoreCase),
        hasUnconstrainedColumnAllow: true);

    // ---------------------------------------------------------------- DefaultReadOrdering.Resolve

    [Fact]
    public void Resolve_PrimaryKeyWithDesc_ReturnsDirections()
    {
        var meta = Meta(["device_id", "ts"], [Asc, Desc]);

        var ordering = DefaultReadOrdering.Resolve(meta, AllClear(meta));

        ordering.Kind.ShouldBe(DefaultOrderingKind.PrimaryKey);
        ordering.Terms.ShouldBe([new OrderingColumn("device_id", false), new OrderingColumn("ts", true)]);
        ordering.Columns.ShouldBe(["device_id", "ts"]);
    }

    [Fact]
    public void Resolve_UniqueIndexWithDesc_ReturnsIndexDirections()
    {
        var meta = Meta(indexes: [new TableIndexDefinition("ux", ["device_id", "ts"], true, [Desc, Asc])]);

        var ordering = DefaultReadOrdering.Resolve(meta, AllClear(meta));

        ordering.Kind.ShouldBe(DefaultOrderingKind.UniqueIndex);
        ordering.Terms.ShouldBe([new OrderingColumn("device_id", true), new OrderingColumn("ts", false)]);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Resolve_HiddenDescColumn_SkipsCandidateAsToday()
    {
        var meta = Meta(["device_id", "ts"], [Asc, Desc], [new TableIndexDefinition("ux_value", ["value"], true, [Desc])]);
        var hiddenTs = Levels(meta, ("device_id", ColumnAccessLevel.Clear), ("ts", ColumnAccessLevel.Mask), ("value", ColumnAccessLevel.Clear));
        var hiddenTsAscending = Levels(meta, ("device_id", ColumnAccessLevel.Clear), ("ts", ColumnAccessLevel.Mask), ("value", ColumnAccessLevel.Clear));

        var withDirection = DefaultReadOrdering.Resolve(meta, hiddenTs);
        var withoutDirection = DefaultReadOrdering.Resolve(meta with { PrimaryKeyDirections = [] }, hiddenTsAscending);

        withDirection.Kind.ShouldBe(DefaultOrderingKind.UniqueIndex, "the descending key column is not Clear, so the whole key is skipped");
        withDirection.Columns.ShouldBe(withoutDirection.Columns, "candidate choice is independent of the direction");
        withDirection.Terms.ShouldBe([new OrderingColumn("value", true)], "the next candidate keeps its own declared direction");
    }

    [Fact]
    public void Resolve_MarkerAndConventionKeys_StayAscending()
    {
        var marker = Meta(cols: ["a", "b"]) with
        {
            Columns =
            [
                new TableColumn { ColumnName = "a", DataType = "int", Meta = new Dictionary<string, string> { ["primary_key_ordinal"] = "1" } },
                new TableColumn { ColumnName = "b", DataType = "int", Meta = new Dictionary<string, string> { ["primary_key_ordinal"] = "2" } }
            ]
        };
        DefaultReadOrdering.Resolve(marker, AllClear(marker)).Terms.ShouldAllBe(t => !t.Descending);
        DefaultReadOrdering.Resolve(marker, AllClear(marker)).Kind.ShouldBe(DefaultOrderingKind.MetaMarkerKey);

        var convention = Meta(cols: ["client_id", "ts", "v"]);
        var resolved = DefaultReadOrdering.Resolve(convention, AllClear(convention));
        resolved.Kind.ShouldBe(DefaultOrderingKind.ConventionKey);
        resolved.Terms.ShouldAllBe(t => !t.Descending);
    }

    // ---------------------------------------------------------------- TreeSqlCompiler goldens

    private static TreeTableAccess Access(TableMetadata meta) => new(meta, AllClear(meta));

    private static string Compile(TableMetadata meta, DatabaseDialect dialect, TreeQueryNode? node = null) =>
        TreeSqlCompiler.Compile(
            node ?? new TreeQueryNode(Readings, ["device_id", "ts", "value"]),
            new Dictionary<TableIdentifier, TreeTableAccess> { [meta.Identifier] = Access(meta) },
            dialect).Sql;

    [Fact]
    public void DefaultOrder_DescKey_EmitsADescB_SqlServerOffsetFetch()
    {
        var sql = Compile(Meta(["device_id", "ts"], [Asc, Desc]), DatabaseDialect.SqlServer);

        sql.ShouldContain("ORDER BY [t0].[device_id], [t0].[ts] DESC");
        sql.ShouldContain("FETCH NEXT");
    }

    [Fact]
    public void DefaultOrder_DescKey_EmitsADescB_PostgreSqlLimit()
    {
        var sql = Compile(Meta(["device_id", "ts"], [Asc, Desc]), DatabaseDialect.PostgreSql);

        sql.ShouldContain("ORDER BY \"t0\".\"device_id\", \"t0\".\"ts\" DESC");
        sql.ShouldContain("LIMIT");
    }

    [Fact]
    public void DefaultOrder_UniqueIndexDesc_EmitsDesc()
    {
        var sql = Compile(Meta(indexes: [new TableIndexDefinition("ux", ["device_id", "ts"], true, [Desc, Desc])]), DatabaseDialect.PostgreSql);

        sql.ShouldContain("ORDER BY \"t0\".\"device_id\" DESC, \"t0\".\"ts\" DESC");
    }

    [Fact]
    public void NestedRelation_DescKey_EmitsDesc()
    {
        var parent = Meta(["device_id"]);
        var childId = new TableIdentifier("d", "main", "samples");
        var child = Meta(["device_id", "ts"], [Asc, Desc]) with
        {
            Identifier = childId,
            Table = new Table { SourceName = "d", SchemaName = "main", TableName = "samples", SourceType = "SqlServer" }
        };
        var root = new TreeQueryNode(Readings, ["device_id"])
        {
            Relations = [new TreeRelationNode("samples", ["device_id"], ["device_id"], true, new TreeQueryNode(childId, ["device_id", "ts", "value"]))]
        };

        var sql = TreeSqlCompiler.Compile(
            root,
            new Dictionary<TableIdentifier, TreeTableAccess> { [parent.Identifier] = Access(parent), [childId] = Access(child) },
            DatabaseDialect.PostgreSql).Sql;

        sql.ShouldContain("\"ts\" DESC");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void UndeclaredDirection_SqlByteIdentical()
    {
        foreach (var dialect in new[] { DatabaseDialect.SqlServer, DatabaseDialect.PostgreSql, DatabaseDialect.Sqlite })
        {
            var baseline = Compile(Meta(["device_id", "ts"]), dialect);
            Compile(Meta(["device_id", "ts"], [Asc, Asc]), dialect).ShouldBe(baseline);
            Compile(Meta(["device_id", "ts"], [Asc, Desc]), dialect).ShouldNotBe(baseline);
        }

        Compile(Meta(["device_id", "ts"]), DatabaseDialect.PostgreSql).ShouldContain("ORDER BY \"t0\".\"device_id\", \"t0\".\"ts\"");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void I390_T_07_OrderingChange_DoesNotAlterFilterMaskingOrExplicitOrderBySql()
    {
        var plain = Meta(["device_id", "ts"]);
        var descending = Meta(["device_id", "ts"], [Asc, Desc]);
        var explicitOrder = new TreeQueryNode(Readings, ["device_id", "ts", "value"])
        {
            OrderBy = [new TreeOrder("value", false)]
        };

        foreach (var dialect in new[] { DatabaseDialect.SqlServer, DatabaseDialect.PostgreSql })
        {
            // Explicit orderBy: the declared direction only appears in the tie-breaker-free explicit order, so the SQL is identical.
            Compile(descending, dialect, explicitOrder).ShouldBe(Compile(plain, dialect, explicitOrder));
        }

        // Masking and the Clear requirement: a masked descending key column still skips the candidate, exactly like an ascending one.
        var masked = Levels(descending, ("device_id", ColumnAccessLevel.Clear), ("ts", ColumnAccessLevel.Mask), ("value", ColumnAccessLevel.Clear));
        var maskedPlain = Levels(plain, ("device_id", ColumnAccessLevel.Clear), ("ts", ColumnAccessLevel.Mask), ("value", ColumnAccessLevel.Clear));
        var node = new TreeQueryNode(Readings, ["device_id", "value"]);
        TreeSqlCompiler.Compile(node, new Dictionary<TableIdentifier, TreeTableAccess> { [Readings] = new(descending, masked) }, DatabaseDialect.PostgreSql).Sql
            .ShouldBe(TreeSqlCompiler.Compile(node, new Dictionary<TableIdentifier, TreeTableAccess> { [Readings] = new(plain, maskedPlain) }, DatabaseDialect.PostgreSql).Sql);
    }

    // ---------------------------------------------------------------- GovernedTableReadPlanner

    private static TableReadIntent Intent(IReadOnlyList<TableOrderBy>? orderBy = null, int limit = 51, int offset = 0) =>
        new(Readings, ["device_id", "ts", "value"], null, orderBy, limit, offset, "Rest");

    [Fact]
    public void DefaultOrder_DescKey_EmitsDesc()
    {
        var meta = Meta(["device_id", "ts"], [Asc, Desc]);

        var planned = new GovernedTableReadPlanner().Plan(Intent(), meta, AllClear(meta));

        planned.Sql.ShouldContain("ORDER BY \"device_id\", \"ts\" DESC");
    }

    [Fact]
    public void TieBreaker_AppendsKeyWithDeclaredDirection()
    {
        var meta = Meta(["device_id", "ts"], [Asc, Desc]);

        var planned = new GovernedTableReadPlanner().Plan(Intent([new TableOrderBy("value", false)]), meta, AllClear(meta));

        planned.Sql.ShouldContain("ORDER BY \"value\", \"device_id\", \"ts\" DESC");
    }

    [Fact]
    public void TieBreaker_SkipsColumnNamedByCaller_CallerDirectionWins()
    {
        var meta = Meta(["device_id", "ts"], [Asc, Desc]);

        var planned = new GovernedTableReadPlanner().Plan(Intent([new TableOrderBy("ts", false)]), meta, AllClear(meta));

        planned.Sql.ShouldContain("ORDER BY \"ts\", \"device_id\"");
        planned.Sql.ShouldNotContain("\"ts\" DESC");
    }

    // ---------------------------------------------------------------- bulk keyset stream

    private const string Tenant = "tenant-1";
    private const string Caller = "S-1-5-21-caller";

    private static TableMetadata StreamMeta(IReadOnlyList<KeyColumnDirection>? directions, bool nullable = false) => new()
    {
        Identifier = Orders,
        Table = new Table { TableName = "orders", SchemaName = "dbo", SourceName = "sales", SourceType = "PostgreSQL" },
        PrimaryKeyColumns = ["grp", "ts"],
        PrimaryKeyDirections = directions ?? [],
        Columns =
        [
            new TableColumn { ColumnName = "grp", DataType = "integer" },
            new TableColumn { ColumnName = "ts", DataType = "integer" },
            new TableColumn { ColumnName = "payload", DataType = "varchar" }
        ]
    };

    private static string Issue(KeysetCursorEncoder encoder, TableMetadata meta, BulkTableStreamRequest request, IReadOnlyDictionary<string, object?> values, IReadOnlyList<KeyColumnDirection>? directions) =>
        encoder.EncodeCursor(values, KeysetCursorContextFactory.ForBulkStream(Tenant, meta, BulkStreamKey.TryResolve(meta)!.Columns, Caller, request, directions));

    [Fact]
    public void PlanKeysetStream_DescKeyColumn_SeeksLessThanAndOrdersDesc()
    {
        var encoder = new KeysetCursorEncoder("signing-key-for-i390-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var meta = StreamMeta([Asc, Desc]);
        var request = new BulkTableStreamRequest(Limit: 10);
        var cursor = Issue(encoder, meta, request, new Dictionary<string, object?> { ["grp"] = 2, ["ts"] = 7 }, [Asc, Desc]);

        var planned = planner.PlanKeysetStream(request with { Cursor = cursor }, meta, AllClear(meta), Tenant, Caller);

        planned.Sql.ShouldContain("WHERE ((\"grp\" > @cursor_0) OR (\"grp\" = @cursor_0 AND \"ts\" < @cursor_1))");
        planned.Sql.ShouldContain("ORDER BY \"grp\" ASC, \"ts\" DESC");
    }

    [Fact]
    public void PlanKeysetStream_AllDescendingAndSingleColumn_UseLessThan()
    {
        var encoder = new KeysetCursorEncoder("signing-key-for-i390-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var all = StreamMeta([Desc, Desc]);
        var request = new BulkTableStreamRequest(Limit: 10);
        var cursor = Issue(encoder, all, request, new Dictionary<string, object?> { ["grp"] = 2, ["ts"] = 7 }, [Desc, Desc]);

        var planned = planner.PlanKeysetStream(request with { Cursor = cursor }, all, AllClear(all), Tenant, Caller);
        planned.Sql.ShouldContain("WHERE ((\"grp\" < @cursor_0) OR (\"grp\" = @cursor_0 AND \"ts\" < @cursor_1))");
        planned.Sql.ShouldContain("ORDER BY \"grp\" DESC, \"ts\" DESC");

        var single = StreamMeta([Desc]) with { PrimaryKeyColumns = ["ts"], PrimaryKeyDirections = [Desc] };
        var singleCursor = Issue(encoder, single, request, new Dictionary<string, object?> { ["ts"] = 7 }, [Desc]);
        var singlePlanned = planner.PlanKeysetStream(request with { Cursor = singleCursor }, single, AllClear(single), Tenant, Caller);
        singlePlanned.Sql.ShouldContain("WHERE (\"ts\" < @cursor_0)");
        singlePlanned.Sql.ShouldContain("ORDER BY \"ts\" DESC");
    }

    [Fact]
    public void PlanKeysetStream_UniqueIndexFallback_UsesIndexDirections()
    {
        var encoder = new KeysetCursorEncoder("signing-key-for-i390-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var meta = StreamMeta(null) with
        {
            PrimaryKeyColumns = [],
            Indexes = [new TableIndexDefinition("ux", ["grp", "ts"], true, [Desc, Asc])]
        };
        var request = new BulkTableStreamRequest(Limit: 10);
        var cursor = Issue(encoder, meta, request, new Dictionary<string, object?> { ["grp"] = 2, ["ts"] = 7 }, [Desc, Asc]);

        var planned = planner.PlanKeysetStream(request with { Cursor = cursor }, meta, AllClear(meta), Tenant, Caller);

        planned.Sql.ShouldContain("ORDER BY \"grp\" DESC, \"ts\" ASC");
        planned.Sql.ShouldContain("(\"grp\" < @cursor_0)");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void PlanKeysetStream_CursorFromOtherDirectionSignature_FailsClosed()
    {
        var encoder = new KeysetCursorEncoder("signing-key-for-i390-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var request = new BulkTableStreamRequest(Limit: 10);
        var values = new Dictionary<string, object?> { ["grp"] = 2, ["ts"] = 7 };
        var ascendingMeta = StreamMeta(null);
        var descendingMeta = StreamMeta([Asc, Desc]);
        var ascendingCursor = Issue(encoder, ascendingMeta, request, values, null);
        var descendingCursor = Issue(encoder, descendingMeta, request, values, [Asc, Desc]);

        // A cursor issued before the direction change fails (and the reverse): no restart fallback.
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(request with { Cursor = ascendingCursor }, descendingMeta, AllClear(descendingMeta), Tenant, Caller));
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(request with { Cursor = descendingCursor }, ascendingMeta, AllClear(ascendingMeta), Tenant, Caller));
        // A different signature (desc,asc instead of asc,desc) is another context.
        var otherMeta = StreamMeta([Desc, Asc]);
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(request with { Cursor = descendingCursor }, otherMeta, AllClear(otherMeta), Tenant, Caller));
        planner.PlanKeysetStream(request with { Cursor = descendingCursor }, descendingMeta, AllClear(descendingMeta), Tenant, Caller).Parameters["@cursor_1"].ShouldBe(7);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void I390_T_09_PlanKeysetStream_CursorAfterDirectionChange_FailsClosed()
    {
        var encoder = new KeysetCursorEncoder("signing-key-for-i390-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var request = new BulkTableStreamRequest(Limit: 10);
        var before = StreamMeta([Asc, Desc]);
        var cursor = Issue(encoder, before, request, new Dictionary<string, object?> { ["grp"] = 1, ["ts"] = 3 }, [Asc, Desc]);

        // The administrator flips the second column to ascending: the stored cursor is rejected, never silently re-seeked.
        var after = StreamMeta(null);
        var ex = Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(request with { Cursor = cursor }, after, AllClear(after), Tenant, Caller));
        ex.Message.ShouldStartWith("INVALID_CURSOR");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void I390_T_09_PlanKeysetStream_TamperedDirectionSignature_FailsClosed()
    {
        var encoder = new KeysetCursorEncoder("signing-key-for-i390-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var request = new BulkTableStreamRequest(Limit: 10);
        var meta = StreamMeta([Asc, Desc]);
        var cursor = Issue(encoder, meta, request, new Dictionary<string, object?> { ["grp"] = 1, ["ts"] = 3 }, [Asc, Desc]);

        // The direction lives in the HMAC'd context, not in the payload: editing the payload (or the signature) invalidates it.
        var dot = cursor.LastIndexOf('.');
        var tampered = cursor[..dot] + "." + new string(cursor[(dot + 1)..].Reverse().ToArray());
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(request with { Cursor = tampered }, meta, AllClear(meta), Tenant, Caller));
        // A cursor signed for an all-ascending signature cannot be presented as 'asc,desc' (the context is built server-side only).
        KeysetCursorContextFactory.DirectionSignature([Asc, Desc]).ShouldBe("asc,desc");
        KeysetCursorContextFactory.DirectionSignature([Asc, Asc]).ShouldBe("asc");
        KeysetCursorContextFactory.DirectionSignature(null).ShouldBe("asc");
    }

    [Fact]
    public void PlanKeysetStream_AllAscending_ExistingCursorStillValid()
    {
        var encoder = new KeysetCursorEncoder("signing-key-for-i390-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var request = new BulkTableStreamRequest(Limit: 10);
        var meta = StreamMeta(null);
        // Issued exactly as before issue #390: no directions at all.
        var legacy = encoder.EncodeCursor(
            new Dictionary<string, object?> { ["grp"] = 1, ["ts"] = 3 },
            KeysetCursorContextFactory.ForBulkStream(Tenant, meta, meta.PrimaryKeyColumns, Caller, request));

        var planned = planner.PlanKeysetStream(request with { Cursor = legacy }, meta, AllClear(meta), Tenant, Caller);

        planned.Sql.ShouldContain("ORDER BY \"grp\" ASC, \"ts\" ASC");
        planned.Sql.ShouldContain("(\"grp\" > @cursor_0) OR (\"grp\" = @cursor_0 AND \"ts\" > @cursor_1)");
        // An explicit all-ascending list is the same context as no list.
        planner.PlanKeysetStream(request with { Cursor = legacy }, StreamMeta([Asc, Asc]), AllClear(meta), Tenant, Caller).Parameters.ShouldContainKey("@cursor_0");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void I390_T_10_PlanKeysetStream_NullableKeyColumn_BehaviourIsDefinedAndTested()
    {
        var encoder = new KeysetCursorEncoder("signing-key-for-i390-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var request = new BulkTableStreamRequest(Limit: 10);
        var meta = StreamMeta([Asc, Desc]);

        // A NULL key value can never be a cursor position: the cursor of such a row is refused (existing behaviour, direction independent).
        var nullCursor = Issue(encoder, meta, request, new Dictionary<string, object?> { ["grp"] = 1, ["ts"] = null }, [Asc, Desc]);
        var ex = Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(request with { Cursor = nullCursor }, meta, AllClear(meta), Tenant, Caller));
        ex.Message.ShouldContain("cannot be null");

        // No NULLS FIRST/LAST clause is ever emitted: NULL placement is engine-defined (PostgreSQL DESC puts NULLs first, SQL Server and SQLite treat NULL as smallest).
        var first = planner.PlanKeysetStream(request, meta, AllClear(meta), Tenant, Caller);
        first.Sql.ShouldNotContain("NULLS");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public void I390_T_08_PlanKeysetStream_MixedDirections_NoSkipNoDuplicateAcrossPages(int firstDesc, int secondDesc)
    {
        var directions = new[] { firstDesc == 1 ? Desc : Asc, secondDesc == 1 ? Desc : Asc };
        var encoder = new KeysetCursorEncoder("signing-key-for-i390-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var meta = StreamMeta(directions);
        var request = new BulkTableStreamRequest(Select: ["grp", "ts", "payload"], Limit: 3);

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE orders (grp INTEGER NOT NULL, ts INTEGER NOT NULL, payload TEXT, PRIMARY KEY (grp, ts))";
            create.ExecuteNonQuery();
        }

        // Ties on the first column (4 rows per group), inserted in a scrambled order.
        var rows = (from g in Enumerable.Range(1, 3) from t in Enumerable.Range(1, 4) select (g, t)).OrderBy(r => (r.g * 7 + r.t * 13) % 11).ToList();
        foreach (var (g, t) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = $"INSERT INTO orders VALUES ({g}, {t}, 'p')";
            insert.ExecuteNonQuery();
        }

        var expected = rows.Order(Comparer<(int g, int t)>.Create((a, b) =>
        {
            var byGroup = firstDesc == 1 ? b.g.CompareTo(a.g) : a.g.CompareTo(b.g);
            return byGroup != 0 ? byGroup : secondDesc == 1 ? b.t.CompareTo(a.t) : a.t.CompareTo(b.t);
        })).ToList();

        var seen = new List<(int g, int t)>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var planned = planner.PlanKeysetStream(request with { Cursor = cursor }, meta, AllClear(meta), Tenant, Caller);
            using var query = connection.CreateCommand();
            query.CommandText = planned.Sql.Replace("FROM sales.dbo.orders", "FROM orders", StringComparison.Ordinal).Replace(" LIMIT 4", " LIMIT 4", StringComparison.Ordinal);
            foreach (var (name, value) in planned.Parameters)
            {
                query.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            var batch = new List<(int g, int t)>();
            using (var reader = query.ExecuteReader())
            {
                while (reader.Read())
                {
                    batch.Add((reader.GetInt32(0), reader.GetInt32(1)));
                }
            }

            var take = batch.Take(3).ToList();
            seen.AddRange(take);
            if (batch.Count <= 3)
            {
                break;
            }

            var last = take[^1];
            cursor = Issue(encoder, meta, request, new Dictionary<string, object?> { ["grp"] = last.g, ["ts"] = last.t }, directions);
        }

        seen.ShouldBe(expected, "every row exactly once, in the declared order, across all pages");
    }

    // ---------------------------------------------------------------- describe_dataset / catalog detail

    [Trait("Category", "Security")]
    [Fact]
    public void I390_T_12_RestrictedView_HiddenColumn_NoDirectionLeak_ForPrimaryKeyAndIndex()
    {
        var meta = Meta(["device_id", "ts"], [Asc, Desc], [new TableIndexDefinition("ux", ["device_id", "value"], true, [Desc, Asc])]);
        var visibleWithoutTs = meta.Columns.Where(c => c.ColumnName != "ts").ToList();

        var restricted = TableKeyVisibility.Restrict(meta, visibleWithoutTs);

        restricted.PrimaryKeyColumns.ShouldBeEmpty();
        restricted.PrimaryKeyDirections.ShouldBeEmpty();
        restricted.Indexes.Single().Directions.ShouldBe([Desc, Asc], "the index lies wholly within the visible columns");
        TableKeyVisibility.Restrict(meta, meta.Columns.Where(c => c.ColumnName != "value").ToList()).Indexes.ShouldBeEmpty();
    }
}
