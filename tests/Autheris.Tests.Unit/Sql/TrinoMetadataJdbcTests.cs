namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Streaming.Producers.CustomQuery;
using Autheris.Tests.Unit.Catalog;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Trino <c>system.jdbc</c> metadata and the shared listing source (discovery visible set plus one decision call).</summary>
public sealed class TrinoMetadataJdbcTests
{
    private static readonly TenantId Tenant = Wp0.TenantA;
    private static readonly System.Security.Claims.ClaimsPrincipal Caller = Wp0.User("S-1-5-21-USER", Tenant.Value);

    private static TableAccessDecision Decision(TableMetadata table, bool allowed, params string[] deniedColumns) => new(
        table.Identifier,
        allowed,
        table.Columns.ToDictionary(c => c.ColumnName, c => deniedColumns.Contains(c.ColumnName) ? ColumnAccessLevel.Deny : ColumnAccessLevel.Clear),
        null,
        []);

    private static DiscoveryObject Obj(TableMetadata table, bool view = false, params (string Name, DiscoveryColumnState State)[] states) => new(
        view ? DiscoveryObjectKind.View : DiscoveryObjectKind.Table,
        table.Identifier.Domain,
        table.Identifier.Schema,
        table.Identifier.TableName,
        null,
        null,
        "Public",
        [],
        [],
        DiscoveryAccessState.Granted,
        null,
        table.Columns
            .Select(c => new DiscoveryColumn(
                c.ColumnName, null, "varchar", null, false, false,
                states.Any(s => s.Name == c.ColumnName) ? states.First(s => s.Name == c.ColumnName).State : DiscoveryColumnState.Clear, null))
            .ToList(),
        null,
        false,
        false);

    private static string[] Column(GovernedSqlResult result, string name) =>
        [.. result.Rows.Select(r => r[name]?.ToString() ?? "<null>")];

    private static readonly TableMetadata Orders = Wp0.Table("sales", "orders", columns: ["id", "amount", "ssn"]);
    private static readonly TableMetadata Invoices = Wp0.Table("sales", "invoices", columns: ["id"]);
    private static readonly TableMetadata Salaries = Wp0.Table("hr", "salaries", columns: ["salary"]);
    private static readonly TableMetadata Foreign = Wp0.Table("other-tenant", "secrets", columns: ["x"]);

    [Fact]
    [Trait("Category", "Security")]
    public async Task SystemJdbcTables_ReturnsOnlyVisibleTables()
    {
        // Salaries and Foreign exist in the catalog but discovery (the visible set) does not return them.
        var rig = new Rig(
            [Orders, Invoices, Salaries, Foreign],
            [Obj(Orders), Obj(Invoices, view: true)],
            t => Decision(t, allowed: t.Identifier.TableName == "orders"));

        var result = await rig.RunAsync("SELECT TABLE_CAT, TABLE_SCHEM, TABLE_NAME, TABLE_TYPE, REMARKS FROM system.jdbc.tables ORDER BY TABLE_NAME");

        result.ShouldNotBeNull();
        Column(result, "TABLE_NAME").ShouldBe(["invoices", "orders"]);
        Column(result, "TABLE_TYPE").ShouldBe(["VIEW", "TABLE"]);
        Column(result, "REMARKS").ShouldBe(["access: not accessible", "access: granted"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SystemJdbcColumns_AppliesConsentAndHiddenColumns()
    {
        var rig = new Rig(
            [Orders],
            [Obj(Orders, false, ("ssn", DiscoveryColumnState.NoAccess))],
            t => Decision(t, allowed: true, deniedColumns: "amount"));

        var result = await rig.RunAsync("SELECT * FROM system.jdbc.columns WHERE TABLE_NAME = 'orders'");

        result.ShouldNotBeNull();
        result.Columns.Count.ShouldBe(24);
        Column(result, "COLUMN_NAME").ShouldBe(["id"]);
        Column(result, "ORDINAL_POSITION").ShouldBe(["1"]);
    }

    [InlineData("SELECT * FROM system.jdbc.tables WHERE TABLE_NAME = 'a' OR TABLE_NAME = 'b'")]
    [InlineData("SELECT * FROM system.jdbc.tables WHERE TABLE_NAME IN (SELECT name FROM t)")]
    [InlineData("SELECT * FROM system.jdbc.tables t JOIN system.jdbc.columns c ON 1 = 1")]
    [InlineData("SELECT * FROM system.jdbc.tables UNION SELECT * FROM system.jdbc.tables")]
    [InlineData("SELECT COUNT(*) FROM system.jdbc.tables")]
    [InlineData("SELECT * FROM system.jdbc.tables LIMIT 1")]
    [InlineData("SELECT * FROM system.jdbc.tables OFFSET 1")]
    [InlineData("SELECT * FROM system.jdbc.nonexistent")]
    [InlineData("SELECT * FROM system.runtime.nodes")]
    [InlineData("SELECT * FROM system.metadata.catalogs")]
    [InlineData("SELECT * FROM sales.dbo.orders o JOIN system.jdbc.tables t ON 1 = 1")]
    [InlineData("SELECT * FROM system.jdbc.tables WHERE lower(TABLE_NAME) = 'a'")]
    [InlineData("SELECT * FROM system.jdbc.tables WHERE TABLE_NAME = 'a' AND TABLE_NAME = 'a' AND TABLE_NAME = 'a' AND TABLE_NAME = 'a' AND TABLE_NAME = 'a' AND TABLE_NAME = 'a' AND TABLE_NAME = 'a' AND TABLE_NAME = 'a' AND TABLE_NAME = 'a'")]
    [InlineData("SELECT * FROM system.jdbc.tables ORDER BY NOT_A_COLUMN")]
    [InlineData("SELECT * FROM system.jdbc.tables WHERE TABLE_NAME LIKE 'a' ESCAPE 'ab'")]
    [InlineData("SELECT * FROM information_schema.tables JOIN sales.dbo.orders ON 1 = 1")]
    [InlineData("SELECT * FROM information_schema.tables WHERE table_name IN (SELECT 1)")]
    [InlineData("SELECT * FROM \"system\".\"jdbc\".\"tables\"")]
    [InlineData("SELECT * FROM \"SyStem\".\"JDBC\".\"tables\"")]
    [InlineData("SELECT * FROM \"information_schema\".\"tables\"")]
    [InlineData("SELECT * FROM sales.\"Information_Schema\".tables")]
    [InlineData("SELECT * FROM system/**/.runtime.nodes")]
    [InlineData("SELECT * FROM system /* x */ . jdbc.tables")]
    [InlineData("SELECT * FROM `system`.jdbc.tables")]
    [InlineData("SELECT TABLE_NAME FROM system.jdbc.tables ORDER BY TABLE_TYPE")]
    [InlineData("SELECT '--', x FROM system.runtime.nodes")]
    [InlineData("SELECT \"a--b\" FROM system.runtime.nodes")]
    [InlineData("SELECT 1 /* -- */ FROM system.runtime.nodes")]
    [InlineData("SELECT 'it''s --' AS c FROM \"system\".runtime.nodes")]
    [InlineData("SELECT * FROM U&\"\\0073ystem\".\"jdbc\".\"tables\"")]
    [InlineData("SELECT * FROM U&\"inform\\0061tion_schema\".tables")]
    [Theory]
    [Trait("Category", "Security")]
    public async Task SystemJdbc_UnsupportedShape_IsNotSupported_NotForwarded(string sql)
    {
        var rig = new Rig([Orders], [Obj(Orders)]);

        var ex = await Should.ThrowAsync<NotSupportedException>(async () => await rig.RunAsync(sql));

        ex.Message.ShouldBe("The metadata statement shape is not supported.");
        ex.Message.ShouldNotContain("orders");
        rig.DecisionPoint.ManyCalls.ShouldBe(0);
    }

    [Fact]
    public async Task SystemJdbcTables_LikeEscapeFilter()
    {
        var odd = Wp0.Table("sales", "a_b%c", columns: ["id"]);
        var plain = Wp0.Table("sales", "axbyc", columns: ["id"]);
        var rig = new Rig([odd, plain], [Obj(odd), Obj(plain)]);

        var wildcard = await rig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.tables WHERE TABLE_NAME LIKE 'a_b%'");
        var escaped = await rig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.tables WHERE TABLE_NAME LIKE 'a\\_b\\%c' ESCAPE '\\'");
        var isNull = await rig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.tables WHERE TYPE_CAT IS NULL AND TABLE_TYPE = 'TABLE' ORDER BY TABLE_NAME DESC");

        Column(wildcard!, "TABLE_NAME").ShouldBe(["axbyc", "a_b%c"]);
        Column(escaped!, "TABLE_NAME").ShouldBe(["a_b%c"]);
        Column(isNull!, "TABLE_NAME").ShouldBe(["a_b%c", "axbyc"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Security_SystemJdbcTables_OmitsHiddenAndOtherTenantObjects()
    {
        var rig = new Rig([Orders, Salaries, Foreign], [Obj(Orders)]);

        var all = await rig.RunAsync("SELECT TABLE_CAT, TABLE_NAME FROM system.jdbc.tables");
        var otherTenantCatalog = await rig.RunAsync("SELECT * FROM system.jdbc.tables WHERE TABLE_CAT = 'other-tenant'");
        var catalogs = await rig.RunAsync("SELECT * FROM system.jdbc.catalogs");
        var schemas = await rig.RunAsync("SELECT * FROM system.jdbc.schemas");

        Column(all!, "TABLE_NAME").ShouldBe(["orders"]);
        otherTenantCatalog!.Rows.ShouldBeEmpty();
        Column(catalogs!, "TABLE_CAT").ShouldBe(["sales", "system"]);
        Column(schemas!, "TABLE_SCHEM").ShouldBe(["dbo", "information_schema"]);
        (await rig.RunAsync("SELECT * FROM system.jdbc.table_types"))!.Rows.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Security_SystemJdbcColumns_OmitsDeniedAndHiddenColumns()
    {
        var rig = new Rig(
            [Orders, Invoices],
            [Obj(Orders, false, ("ssn", DiscoveryColumnState.NoAccess)), Obj(Invoices)],
            t => t.Identifier.TableName == "orders" ? Decision(t, allowed: true, deniedColumns: "amount") : Decision(t, allowed: false));

        var result = await rig.RunAsync("SELECT TABLE_NAME, COLUMN_NAME, TYPE_NAME FROM system.jdbc.columns");

        // The denied and the no-consent column are absent; the non-readable table lists no column at all.
        result!.Rows.Select(r => $"{r["TABLE_NAME"]}.{r["COLUMN_NAME"]}").ShouldBe(["orders.id"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Security_MetadataListing_DecideManyThrows_MarksNotAccessible()
    {
        var throwing = new Rig([Orders], [Obj(Orders)], many: _ => throw new InvalidOperationException("boom"));

        var tables = await throwing.RunAsync("SELECT TABLE_NAME, REMARKS FROM system.jdbc.tables");
        var columns = await throwing.RunAsync("SELECT * FROM system.jdbc.columns");

        Column(tables!, "REMARKS").ShouldBe(["access: not accessible"]);
        columns!.Rows.ShouldBeEmpty();

        // A partial answer is treated like a failure.
        var partial = new Rig([Orders], [Obj(Orders)], many: _ => []);
        Column((await partial.RunAsync("SELECT TABLE_NAME, REMARKS FROM system.jdbc.tables"))!, "REMARKS").ShouldBe(["access: not accessible"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Security_SystemJdbc_UnsupportedShapes_NotSupportedAndNotForwarded()
    {
        var rig = new Rig([Orders], [Obj(Orders)]);
        string[] shapes =
        [
            "SELECT * FROM system.jdbc.tables WHERE TABLE_NAME = 'a' OR TABLE_NAME = 'b'",
            "SELECT * FROM system.jdbc.tables WHERE TABLE_NAME = (SELECT 'a')",
            "SELECT * FROM system.jdbc.tables a, system.jdbc.columns b",
            "SELECT * FROM system.jdbc.tables UNION ALL SELECT * FROM system.jdbc.tables",
            "SELECT count(*) FROM system.jdbc.tables",
            "SELECT * FROM system.jdbc.tables LIMIT 5",
            "SELECT * FROM system.jdbc.unknown_view",
            "SELECT o.id FROM sales.dbo.orders o, system.jdbc.tables t"
        ];

        foreach (var sql in shapes)
        {
            await Should.ThrowAsync<NotSupportedException>(async () => await rig.RunAsync(sql));
        }

        rig.Discovery.ListCalls.ShouldBe(0);
        rig.DecisionPoint.ManyCalls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Security_SystemJdbc_LikeEscape_NoRegexOrSqlInjection_AndLengthCapped()
    {
        var weird = Wp0.Table("sales", "a+b", columns: ["id"]);
        var other = Wp0.Table("sales", "aab", columns: ["id"]);
        var rig = new Rig([weird, other], [Obj(weird), Obj(other)]);

        // Regex metacharacters are literals; no SQL text is ever built from the pattern.
        Column((await rig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.tables WHERE TABLE_NAME LIKE 'a+b'"))!, "TABLE_NAME").ShouldBe(["a+b"]);
        (await rig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.tables WHERE TABLE_NAME LIKE '(a+)+$'"))!.Rows.ShouldBeEmpty();
        (await rig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.tables WHERE TABLE_NAME LIKE '''; DROP TABLE x'"))!.Rows.ShouldBeEmpty();

        // Many wildcards over a long value stay linear (no catastrophic backtracking).
        var pattern = new string('%', 120) + "z";
        var started = DateTime.UtcNow;
        (await rig.RunAsync($"SELECT TABLE_NAME FROM system.jdbc.tables WHERE TABLE_NAME LIKE '{pattern}'"))!.Rows.ShouldBeEmpty();
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(5));

        // Length cap, single-char ESCAPE and escape misuse are refused.
        var tooLong = new string('a', 257);
        await Should.ThrowAsync<NotSupportedException>(async () => await rig.RunAsync($"SELECT * FROM system.jdbc.tables WHERE TABLE_NAME LIKE '{tooLong}'"));
        await Should.ThrowAsync<NotSupportedException>(async () => await rig.RunAsync("SELECT * FROM system.jdbc.tables WHERE TABLE_NAME LIKE 'a' ESCAPE ''"));
        await Should.ThrowAsync<NotSupportedException>(async () => await rig.RunAsync("SELECT * FROM system.jdbc.tables WHERE TABLE_NAME LIKE 'a\\x' ESCAPE '\\'"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Security_SystemJdbc_HiddenVersusMissing_IdenticalResponse()
    {
        // 'hidden' exists in the catalog but is not in the visible set; 'missing' exists nowhere.
        var hidden = Wp0.Table("sales", "hidden", columns: ["id"]);
        var rig = new Rig([Orders, hidden], [Obj(Orders)]);

        var forHidden = await rig.RunAsync("SELECT * FROM system.jdbc.tables WHERE TABLE_NAME = 'hidden'");
        var forMissing = await rig.RunAsync("SELECT * FROM system.jdbc.tables WHERE TABLE_NAME = 'missing'");
        var columnsHidden = await rig.RunAsync("SELECT * FROM system.jdbc.columns WHERE TABLE_NAME = 'hidden'");
        var columnsMissing = await rig.RunAsync("SELECT * FROM system.jdbc.columns WHERE TABLE_NAME = 'missing'");

        forHidden!.Columns.ShouldBe(forMissing!.Columns);
        forHidden.Rows.ShouldBeEmpty();
        forMissing.Rows.ShouldBeEmpty();
        columnsHidden!.Columns.ShouldBe(columnsMissing!.Columns);
        columnsHidden.Rows.ShouldBeEmpty();
        columnsMissing.Rows.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Security_MetadataHandler_PerformsNoSourceMetadataRead()
    {
        var connections = Substitute.For<IDataSourceConnectionFactory>();
        var rig = new Rig([Orders], [Obj(Orders)]);

        foreach (var sql in new[]
                 {
                     "SHOW TABLES FROM sales.dbo", "SELECT * FROM information_schema.columns", "SELECT * FROM system.jdbc.tables",
                     "SELECT * FROM system.jdbc.columns", "SELECT * FROM system.jdbc.catalogs", "SELECT * FROM system.jdbc.schemas"
                 })
        {
            (await rig.RunAsync(sql)).ShouldNotBeNull();
        }

        connections.ReceivedCalls().ShouldBeEmpty();
        // The only reads are the catalog repository and discovery; no DbConnection is reachable from the handler.
        await rig.Repository.DidNotReceive().GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MetadataListing_AccessStatusEqualsDecisionPoint()
    {
        var rig = new Rig(
            [Orders, Invoices, Salaries],
            [Obj(Orders), Obj(Invoices), Obj(Salaries)],
            t => t.Identifier.TableName switch
            {
                "orders" => Decision(t, allowed: true),
                "invoices" => Decision(t, allowed: false),
                _ => Decision(t, allowed: true, deniedColumns: "salary")
            });

        var tables = await rig.RunAsync("SELECT TABLE_NAME, REMARKS FROM system.jdbc.tables");
        var columns = await rig.RunAsync("SELECT TABLE_NAME, COLUMN_NAME FROM system.jdbc.columns");
        var show = await rig.RunAsync("SHOW TABLES FROM sales.dbo");
        var info = await rig.RunAsync("SELECT table_name FROM information_schema.tables");

        tables!.Rows.Select(r => $"{r["TABLE_NAME"]}={r["REMARKS"]}").Order().ShouldBe(
            ["invoices=access: not accessible", "orders=access: granted", "salaries=access: granted"]);
        columns!.Rows.Select(r => $"{r["TABLE_NAME"]}.{r["COLUMN_NAME"]}").ShouldBe(["orders.id", "orders.amount", "orders.ssn"]);
        Column(show!, "Table").ShouldBe(["invoices", "orders"]);
        Column(info!, "table_name").ShouldBe(["salaries", "invoices", "orders"]);

        // One decision call per statement.
        rig.DecisionPoint.ManyCalls.ShouldBe(2);
    }

    [Fact]
    public async Task SystemJdbc_WithoutVisibleSet_FailsClosed()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => TrinoMetadataQueryHandler.TryExecuteAsync(
            "SELECT * FROM system.jdbc.tables", Caller, Tenant, Wp0.Tables(Orders), Wp0.Consents(), Wp0.Options(), null, null!));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ShowTables_HiddenByDiscovery_IsNotListed()
    {
        // Both tables are active and consented, but discovery hides Salaries from the caller.
        var rig = new Rig([Orders, Salaries], [Obj(Orders)]);

        var allTables = await rig.RunAsync("SHOW TABLES");
        var hrTables = await rig.RunAsync("SHOW TABLES FROM hr.dbo");

        Column(allTables!, "Table").ShouldBe(["orders"]);
        hrTables!.Rows.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task InformationSchemaColumns_DeniedByPdp_IsNotListed()
    {
        var rig = new Rig(
            [Orders, Invoices],
            [Obj(Orders, false, ("ssn", DiscoveryColumnState.NoAccess)), Obj(Invoices)],
            t => t.Identifier.TableName == "orders"
                ? Decision(t, allowed: true, deniedColumns: "amount")
                : Decision(t, allowed: false));

        var result = await rig.RunAsync("SELECT table_name, column_name FROM information_schema.columns");

        result.ShouldNotBeNull();
        result.Rows.Select(r => $"{r["table_name"]}.{r["column_name"]}").ShouldBe(["orders.id"]);

        var throwing = new Rig([Orders], [Obj(Orders)], many: _ => throw new InvalidOperationException("pdp down"));
        (await throwing.RunAsync("SELECT table_name, column_name FROM information_schema.columns"))!.Rows.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CaseVariantTables_AreBothOmitted()
    {
        var lower = Wp0.Table("sales", "orders", columns: ["id"]);
        var upper = Wp0.Table("sales", "Orders", columns: ["secret"]);

        // Collision in both discovery and metadata alongside an unrelated table.
        var bothRig = new Rig([lower, upper, Invoices], [Obj(lower), Obj(upper), Obj(Invoices)]);
        Column((await bothRig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.tables"))!, "TABLE_NAME").ShouldBe(["invoices"]);
        Column((await bothRig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.columns"))!, "TABLE_NAME").ShouldBe(["invoices"]);
        Column((await bothRig.RunAsync("SHOW TABLES FROM sales.dbo"))!, "Table").ShouldBe(["invoices"]);
        Column((await bothRig.RunAsync("SELECT table_name FROM information_schema.tables"))!, "table_name").ShouldBe(["invoices"]);
        Column((await bothRig.RunAsync("SELECT table_name FROM information_schema.columns"))!, "table_name").ShouldBe(["invoices"]);

        // Single entry in discovery colliding with two case-variant active rows in metadata.
        var metaCollisionRig = new Rig([lower, upper, Invoices], [Obj(lower), Obj(Invoices)]);
        Column((await metaCollisionRig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.tables"))!, "TABLE_NAME").ShouldBe(["invoices"]);
        Column((await metaCollisionRig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.columns"))!, "TABLE_NAME").ShouldBe(["invoices"]);
        Column((await metaCollisionRig.RunAsync("SHOW TABLES FROM sales.dbo"))!, "Table").ShouldBe(["invoices"]);
        Column((await metaCollisionRig.RunAsync("SELECT table_name FROM information_schema.tables"))!, "table_name").ShouldBe(["invoices"]);
        Column((await metaCollisionRig.RunAsync("SELECT table_name FROM information_schema.columns"))!, "table_name").ShouldBe(["invoices"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DiscoveryAndMetadataCaseMismatch_IsNotPaired()
    {
        var metaLower = Wp0.Table("sales", "orders", columns: ["id"]);
        var discUpperName = Wp0.Table("sales", "Orders", columns: ["id"]);
        var discUpperSchema = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "Dbo", "orders"),
            Table = new Table { SourceName = "sales", SchemaName = "Dbo", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
        };
        var discUpperDomain = Wp0.Table("Sales", "orders", columns: ["id"]);

        foreach (var mismatched in new[] { discUpperName, discUpperSchema, discUpperDomain })
        {
            var rig = new Rig([metaLower], [Obj(mismatched)]);

            var jdbcTables = await rig.RunAsync("SELECT TABLE_NAME, REMARKS FROM system.jdbc.tables");
            var jdbcColumns = await rig.RunAsync("SELECT * FROM system.jdbc.columns");
            var showTables = await rig.RunAsync("SHOW TABLES");
            var infoTables = await rig.RunAsync("SELECT * FROM information_schema.tables");
            var infoCols = await rig.RunAsync("SELECT * FROM information_schema.columns");

            Column(jdbcTables!, "REMARKS").ShouldBe(["access: not accessible"]);
            jdbcColumns!.Rows.ShouldBeEmpty();
            showTables!.Rows.ShouldBeEmpty();
            infoTables!.Rows.ShouldBeEmpty();
            infoCols!.Rows.ShouldBeEmpty();
            rig.DecisionPoint.ManyCalls.ShouldBe(0);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UndefinedColumnState_IsNotListed()
    {
        var table = Wp0.Table("sales", "orders", columns: ["id", "mystery", "email", "ssn"]);
        var rig = new Rig(
            [table],
            [Obj(table, false, ("mystery", (DiscoveryColumnState)99), ("ssn", DiscoveryColumnState.NoAccess))]);

        var jdbcColumns = await rig.RunAsync("SELECT COLUMN_NAME, ORDINAL_POSITION FROM system.jdbc.columns WHERE TABLE_NAME = 'orders'");
        var infoColumns = await rig.RunAsync("SELECT column_name, ordinal_position FROM information_schema.columns WHERE table_name = 'orders'");

        Column(jdbcColumns!, "COLUMN_NAME").ShouldBe(["id", "email"]);
        Column(jdbcColumns!, "ORDINAL_POSITION").ShouldBe(["1", "2"]);
        Column(infoColumns!, "column_name").ShouldBe(["id", "email"]);
        Column(infoColumns!, "ordinal_position").ShouldBe(["1", "2"]);
    }

    [Fact]
    public async Task GetColumnsShape_DecidesOnlyMatchingTables()
    {
        var rig = new Rig([Orders, Invoices, Salaries], [Obj(Orders), Obj(Invoices), Obj(Salaries)]);

        var single = await rig.RunAsync("SELECT TABLE_NAME, COLUMN_NAME FROM system.jdbc.columns WHERE TABLE_SCHEM = 'dbo' AND TABLE_NAME = 'orders'");
        rig.DecisionPoint.ManyCalls.ShouldBe(1);
        rig.DecisionPoint.DecidedTables.ShouldBe([Orders.Identifier]);
        Column(single!, "TABLE_NAME").ShouldBe(["orders", "orders", "orders"]);

        rig.DecisionPoint.DecidedTables.Clear();
        var pair = await rig.RunAsync("SELECT TABLE_NAME, COLUMN_NAME FROM system.jdbc.columns WHERE TABLE_SCHEM = 'dbo' AND TABLE_NAME IN ('orders', 'invoices')");
        rig.DecisionPoint.ManyCalls.ShouldBe(2);
        rig.DecisionPoint.DecidedTables.ShouldBe([Orders.Identifier, Invoices.Identifier], ignoreOrder: true);
        pair!.Rows.Select(r => r["TABLE_NAME"]?.ToString()).Distinct().ShouldBe(["invoices", "orders"], ignoreOrder: true);

        var none = await rig.RunAsync("SELECT * FROM system.jdbc.columns WHERE TABLE_CAT = 'missing'");
        rig.DecisionPoint.ManyCalls.ShouldBe(2);
        none!.Rows.ShouldBeEmpty();

        var infoSchema = await rig.RunAsync("SELECT TABLE_CATALOG, TABLE_SCHEM FROM system.jdbc.schemas WHERE TABLE_SCHEM = 'information_schema' ORDER BY TABLE_CATALOG");
        Column(infoSchema!, "TABLE_CATALOG").ShouldBe(["hr", "sales"]);
        Column(infoSchema!, "TABLE_SCHEM").ShouldBe(["information_schema", "information_schema"]);
    }

    [Fact]
    public async Task Unordered_StopsAtLimitPlusOne_TruncatedSet()
    {
        TableMetadata[] catalog =
        [
            Wp0.Table("sales", "t1", columns: ["id"]),
            Wp0.Table("sales", "t2", columns: ["id"]),
            Wp0.Table("sales", "t3", columns: ["id"]),
            Wp0.Table("sales", "t4", columns: ["id"]),
            Wp0.Table("sales", "t5", columns: ["id"])
        ];
        var match = TrinoJdbcMetadataShapes.JdbcStatementRegex.Match("SELECT TABLE_NAME FROM system.jdbc.tables");
        var query = TrinoJdbcMetadataShapes.Parse(match);
        var entries = catalog.Select(t => new TrinoJdbcEntry(Obj(t), true, new HashSet<string>(["id"], StringComparer.OrdinalIgnoreCase))).ToList();

        var shaped = TrinoJdbcMetadataShapes.Execute(query, entries, rowLimit: 2);
        shaped.Rows.Count.ShouldBe(3);

        var rig = new Rig(catalog, catalog.Select(t => Obj(t)).ToList());
        var truncated = await rig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.tables", rowLimit: 2);
        truncated!.Rows.Count.ShouldBe(2);
        truncated.RowCount.ShouldBe(2);
        truncated.Truncated.ShouldBeTrue();
        Column(truncated, "TABLE_NAME").ShouldBe(["t1", "t2"]);

        var exact = await rig.RunAsync("SELECT TABLE_NAME FROM system.jdbc.tables", rowLimit: 5);
        exact!.Rows.Count.ShouldBe(5);
        exact.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Ordered_StillSortsFullFilteredSet()
    {
        TableMetadata[] catalog =
        [
            Wp0.Table("sales", "alpha", columns: ["id"]),
            Wp0.Table("sales", "beta", columns: ["id"]),
            Wp0.Table("sales", "delta", columns: ["id"]),
            Wp0.Table("sales", "gamma", columns: ["id"])
        ];
        const string sql = "SELECT TABLE_NAME FROM system.jdbc.tables ORDER BY TABLE_NAME DESC";
        var query = TrinoJdbcMetadataShapes.Parse(TrinoJdbcMetadataShapes.JdbcStatementRegex.Match(sql));
        var entries = catalog.Select(t => new TrinoJdbcEntry(Obj(t), true, new HashSet<string>(["id"], StringComparer.OrdinalIgnoreCase))).ToList();

        var shaped = TrinoJdbcMetadataShapes.Execute(query, entries, rowLimit: 2);
        shaped.Rows.Select(r => r["TABLE_NAME"]?.ToString()).ShouldBe(["gamma", "delta", "beta", "alpha"]);

        var rig = new Rig(catalog, catalog.Select(t => Obj(t)).ToList());
        var limited = await rig.RunAsync(sql, rowLimit: 2);
        Column(limited!, "TABLE_NAME").ShouldBe(["gamma", "delta"]);
        limited!.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task LikePredicate_IsNotPushedDown()
    {
        const string sql = "SELECT TABLE_NAME, COLUMN_NAME FROM system.jdbc.columns WHERE TABLE_NAME LIKE 'ord%'";
        var query = TrinoJdbcMetadataShapes.Parse(TrinoJdbcMetadataShapes.JdbcStatementRegex.Match(sql));
        var pushdown = TrinoJdbcMetadataShapes.PushdownKeys(query);
        pushdown.Catalogs.ShouldBeNull();
        pushdown.Schemas.ShouldBeNull();
        pushdown.Tables.ShouldBeNull();

        var rig = new Rig([Orders, Invoices], [Obj(Orders), Obj(Invoices)]);
        var result = await rig.RunAsync(sql);

        rig.DecisionPoint.DecidedTables.ShouldBe([Orders.Identifier, Invoices.Identifier], ignoreOrder: true);
        result!.Rows.Select(r => r["TABLE_NAME"]?.ToString()).Distinct().ShouldBe(["orders"]);
    }

    private sealed class Rig
    {
        public StubDiscovery Discovery { get; }

        public StubDecisionPoint DecisionPoint { get; }

        public ITableMetadataRepository Repository { get; }

        private readonly IConsentRepository _consents;

        public Rig(IReadOnlyList<TableMetadata> catalog, IReadOnlyList<DiscoveryObject> visible, Func<TableMetadata, TableAccessDecision>? decide = null,
            Func<IReadOnlyList<TableMetadata>, IReadOnlyList<TableAccessDecision>>? many = null)
        {
            Repository = Wp0.Tables([.. catalog]);
            _consents = Wp0.Consents([.. catalog.Select(t => Wp0.Allow(t.Identifier, "S-1-5-21-USER", Tenant, [.. t.Columns.Select(c => c.ColumnName)]))]);
            Discovery = new StubDiscovery(visible);
            decide ??= t => Decision(t, allowed: true);
            DecisionPoint = new StubDecisionPoint(tables => many != null ? many(tables) : tables.Select(decide).ToList());
        }

        public Task<GovernedSqlResult?> RunAsync(string sql, int? rowLimit = null) =>
            TrinoMetadataQueryHandler.TryExecuteAsync(
                sql, Caller, Tenant, Repository, _consents, Wp0.Options(), null, new TrinoMetadataVisibility(Discovery, DecisionPoint), rowLimit);
    }

    private sealed class StubDiscovery(IReadOnlyList<DiscoveryObject> visible) : IDiscoveryService
    {
        public int ListCalls { get; private set; }

        public ValueTask<DiscoveryPage> ListAsync(DiscoveryQuery query, CatalogCaller caller, CancellationToken ct = default)
        {
            ListCalls++;
            return new(new DiscoveryPage(visible, visible.Count, false));
        }

        public ValueTask<DiscoveryKeysetPage> ListKeysetAsync(DiscoveryKeysetQuery query, CatalogCaller caller, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<DiscoveryFacets> FacetsAsync(DiscoveryAccessState? access, CatalogCaller caller, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<DiscoveryObject?> GetAsync(TableIdentifier id, CatalogCaller caller, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<DiscoveryObject>> SearchAsync(string text, DiscoverySearchOptions options, CatalogCaller caller, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubDecisionPoint(Func<IReadOnlyList<TableMetadata>, IReadOnlyList<TableAccessDecision>> many) : IAccessDecisionPoint
    {
        public int ManyCalls { get; private set; }

        public List<TableIdentifier> DecidedTables { get; } = [];

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default)
        {
            ManyCalls++;
            DecidedTables.AddRange(objects.Select(static o => o.Identifier));
            return new(many(objects));
        }

        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
