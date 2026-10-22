namespace Autheris.Tests.Integration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-X0h DQL execution evidence on in-process SQLite, through the production <see cref="SqlConnectionFactory"/> (gateway HMAC UDF,
/// <c>trusted_schema = OFF</c>) and the closed binder. The tenant columns are <c>COLLATE NOCASE</c> and <c>COLLATE RTRIM</c>, so a
/// plain equality would leak a case or trailing-space variant (SEC-X1-10, decision B-1).
/// </summary>
public sealed class AstCompilerSqliteExecutionTests : IAsyncLifetime
{
    private static readonly TableIdentity Orders = new("main", "Orders");
    private static readonly TableIdentity Notes = new("main", "Notes");
    private static readonly TableIdentity Big = new("main", "Big");
    private static readonly TableIdentity Ledger = new("main", "Ledger");
    private static readonly TableIdentity Org = new("main", "Org");

    private readonly FastSqlEngine _engine = new();
    private readonly Policies _policies = new();
    private readonly string _connectionString = $"Data Source=file:ast_sqlite_dql_{Guid.NewGuid():N}?mode=memory&cache=shared";
    private DbConnection _conn = null!;

    public async Task InitializeAsync()
    {
        _conn = await new SqlConnectionFactory().CreateOpenConnectionAsync(
            new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = _connectionString });
        await Raw("""
            CREATE TABLE "Orders" ("Id" INTEGER PRIMARY KEY, "TenantId" TEXT COLLATE NOCASE NOT NULL, "Region" TEXT NOT NULL,
                "Status" TEXT NOT NULL, "Amount" NUMERIC NOT NULL, "Email" TEXT);
            INSERT INTO "Orders" VALUES (1, 'acme', 'EU', 'open', 10.5, 'alice.smith@acme.example'), (2, 'acme', 'US', 'open', 20, 'bob.jones@acme.example'),
                (3, 'ACME', 'EU', 'open', 30, 'upper.case@ACME.example'), (4, 'ACME', 'US', 'closed', 40, 'upper.us@ACME.example'),
                (5, 'other', 'EU', 'open', 50, 'carol@other.example'), (6, 'acme', 'EU', 'closed', 60, NULL);
            CREATE TABLE "Notes" ("Id" INTEGER PRIMARY KEY, "TenantId" TEXT COLLATE RTRIM NOT NULL, "Body" TEXT NOT NULL);
            INSERT INTO "Notes" VALUES (1, 'acme', 'exact'), (2, 'acme ', 'trailing space'), (3, 'acme  ', 'two spaces'), (4, 'other', 'foreign');
            CREATE TABLE "Ledger" ("Id" INTEGER PRIMARY KEY, "TenantId" NUMERIC NOT NULL, "Memo" TEXT);
            INSERT INTO "Ledger" VALUES (1, '7', 'seven'), (2, '007', 'stored as 7');
            CREATE TABLE "Org" ("Id" INTEGER PRIMARY KEY, "TenantId" TEXT NOT NULL, "ParentId" INTEGER);
            INSERT INTO "Org" VALUES (1, 'acme', NULL), (2, 'acme', 1), (3, 'acme', 1), (4, 'acme', 2), (5, 'other', NULL), (6, 'other', 5);
            CREATE TABLE "Big" ("Id" INTEGER PRIMARY KEY, "TenantId" TEXT NOT NULL);
            WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < 3000) INSERT INTO "Big" SELECT i, 'acme' FROM s;
            """);
    }

    public async Task DisposeAsync() => await _conn.DisposeAsync();

    private async Task Raw(string sql)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "TEXT"), new CatalogColumn("Region", "TEXT"),
            new CatalogColumn("Status", "TEXT"), new CatalogColumn("Amount", "NUMERIC"), new CatalogColumn("Email", "TEXT")), "TenantId", 1),
        new TableCatalogEntry(Notes, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "TEXT"), new CatalogColumn("Body", "TEXT")), "TenantId", 1),
        new TableCatalogEntry(Big, ImmutableArray.Create(new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "TEXT")), "TenantId", 1),
        new TableCatalogEntry(Org, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "TEXT"), new CatalogColumn("ParentId", "INTEGER")), "TenantId", 1),
        new TableCatalogEntry(Ledger, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "NUMERIC"), new CatalogColumn("Memo", "TEXT")), "TenantId", 1)
    }, "main");

    private sealed class Policies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public Dictionary<TableIdentity, PolicyPredicate> Predicates { get; } = new();
        public Dictionary<(TableIdentity, string), MaskSpec> Masks { get; } = new();
        public bool ShouldApplyPolicy(TableIdentity table) => Predicates.ContainsKey(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => Predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => Masks.ContainsKey((table, column.ToLowerInvariant()));
        public MaskSpec GetMask(TableIdentity table, string column) => Masks[(table, column.ToLowerInvariant())];
    }

    private CompiledSql Compile(string sql, string tenant = "acme") => _engine.Compile(sql.AsMemory(), new CompileRequest
    {
        TargetDialect = TargetSqlDialect.Sqlite,
        AllowRecursiveCte = true,   // issue #277: the recursive CTE tests opt in; the default request rejects WITH RECURSIVE
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = _policies,
            Masks = _policies,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    }, CancellationToken.None);

    private async Task<List<object?[]>> RunAsync(CompiledSql compiled)
    {
        await using var cmd = _conn.CreateCommand();
        CompiledSqlBinders.For(TargetSqlDialect.Sqlite).Bind(cmd, compiled, new Dictionary<string, object?>());
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    private async Task<List<long>> IdsAsync(string sql, string tenant = "acme") =>
        (await RunAsync(Compile(sql, tenant))).Select(r => Convert.ToInt64(r[0])).OrderBy(i => i).ToList();

    private static PolicyPredicate Policy(Expression expression, params (string Name, PolicyValue Value)[] values) =>
        PolicyPredicate.Create(expression, values.ToDictionary(v => v.Name, v => v.Value));

    private static ColumnReference Col(string name) => new(new SqlQualifiedName(new[] { new SqlIdentifier(name, true) }));

    private void NoRowPolicy(params TableIdentity[] tables)
    {
        foreach (var table in tables) _policies.Predicates[table] = Policy(new LiteralExpression(true, LiteralType.Boolean));
    }

    // ---- tenant comparison is byte-exact (decision B-1) ----

    [Fact]
    public async Task Sqlite_TenantCompare_Binary_ANocaseColumnDoesNotMatchTheCaseVariant()
    {
        NoRowPolicy(Orders);
        await using (var probe = _conn.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM \"Orders\" WHERE \"TenantId\" = 'acme'";
            Convert.ToInt32(await probe.ExecuteScalarAsync()).ShouldBe(5);   // the plain equality really leaks the variant
        }

        (await IdsAsync("SELECT id FROM orders")).ShouldBe(new long[] { 1, 2, 6 });
        (await IdsAsync("SELECT id FROM orders", tenant: "ACME")).ShouldBe(new long[] { 3, 4 });
    }

    [Fact]
    public async Task Sqlite_TenantCompare_Binary_AnRtrimColumnDoesNotMatchTrailingSpaces()
    {
        NoRowPolicy(Notes);
        await using (var probe = _conn.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM \"Notes\" WHERE \"TenantId\" = 'acme'";
            Convert.ToInt32(await probe.ExecuteScalarAsync()).ShouldBe(3);
        }

        (await IdsAsync("SELECT id FROM notes")).ShouldBe(new long[] { 1 });
        (await IdsAsync("SELECT id FROM notes", tenant: "acme ")).ShouldBe(new long[] { 2 });
    }

    [Fact]
    public async Task TenantColumnWithNumericAffinity_IsRejected_BeforeAnyStatementReachesTheDatabase()
    {
        NoRowPolicy(Ledger);
        Should.Throw<SecurityException>(() => Compile("SELECT id FROM ledger"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnOrTautologyAndAUnion_StayInsideTheCallersTenant()
    {
        NoRowPolicy(Orders);
        (await IdsAsync("SELECT id FROM orders WHERE 1 = 1 OR status = 'x'")).ShouldBe(new long[] { 1, 2, 6 });
        (await IdsAsync("SELECT id FROM orders UNION SELECT id FROM orders WHERE region = 'EU'")).ShouldBe(new long[] { 1, 2, 6 });
        (await IdsAsync("SELECT id FROM orders WHERE id IN (SELECT id FROM orders WHERE tenantid = 'other')")).ShouldBeEmpty();
    }

    [Fact]
    public async Task RowPolicy_IsAndedOntoTheTenant()
    {
        _policies.Predicates[Orders] = Policy(new BinaryExpression(Col("Region"), BinaryOperator.Equal,
            new PolicyParameterExpression("__pol_region", SqlParameterType.String)), ("__pol_region", new("EU", SqlParameterType.String)));

        (await IdsAsync("SELECT id FROM orders")).ShouldBe(new long[] { 1, 6 });
        (await IdsAsync("SELECT id FROM orders", tenant: "other")).ShouldBe(new long[] { 5 });
    }

    [Fact]
    public async Task DenyAllPolicy_ReturnsNothing_AndATableWithoutAPolicyIsTenantScopedOnly()
    {
        _policies.Predicates[Orders] = PolicyPredicate.DenyAll;
        (await IdsAsync("SELECT id FROM orders")).ShouldBeEmpty();

        _policies.Predicates.Clear();
        (await IdsAsync("SELECT id FROM orders")).ShouldBe(new long[] { 1, 2, 6 });
    }

    [Fact]
    public async Task HostileLiteralsAndTenantValues_AreBound_NeverInterpreted()
    {
        NoRowPolicy(Orders);
        (await IdsAsync("SELECT id FROM orders WHERE status = 'open'' OR 1=1'")).ShouldBeEmpty();
        (await IdsAsync("SELECT id FROM orders", tenant: "acme' OR '1'='1")).ShouldBeEmpty();
        (await IdsAsync("SELECT id FROM orders", tenant: "acme\0")).ShouldBeEmpty();
    }

    // ---- masks (SEC-ADG-15) ----

    [Fact]
    public async Task MaskedColumn_NeverReturnsTheClearValue_PartialMask()
    {
        NoRowPolicy(Orders);
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.PartialMask, new MaskArguments(
            KeepPrefix: new("m_p", SqlParameterType.Int32, ParameterOrigin.Mask), KeepSuffix: new("m_s", SqlParameterType.Int32, ParameterOrigin.Mask),
            MaskChar: new("m_c", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue>
            {
                ["m_p"] = new(2, SqlParameterType.Int32), ["m_s"] = new(3, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            }.ToFrozenDictionary());

        var rows = await RunAsync(Compile("SELECT email FROM orders WHERE id = 1"));

        rows.Single()[0].ShouldBe("al" + new string('*', "alice.smith@acme.example".Length - 5) + "ple");
        // a masked column cannot be used to probe the clear value through a predicate
        Should.Throw<SecurityException>(() => Compile("SELECT id FROM orders WHERE email = 'alice.smith@acme.example'"));
    }

    [Fact]
    public async Task Sqlite_HmacMask_UsesGatewayUdf()
    {
        NoRowPolicy(Orders);
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Hmac,
            new MaskArguments(HmacKey: new("m_k", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue> { ["m_k"] = new("00ff", SqlParameterType.String) }.ToFrozenDictionary());

        var compiled = Compile("SELECT email FROM orders WHERE id = 1");
        compiled.Sql.ShouldContain("gateway_hmac_sha256(");
        compiled.Sql.ShouldNotContain("00ff");

        string expected = "hmac_" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("00ff"), System.Text.Encoding.UTF8.GetBytes("alice.smith@acme.example")));
        (await RunAsync(compiled)).Single()[0].ShouldBe(expected);
    }

    // ---- closed function allowlist ----

    [Theory]
    [InlineData("SELECT load_extension('x') FROM orders")]
    [InlineData("SELECT randomblob(1000000000) FROM orders")]
    [InlineData("SELECT zeroblob(1000000000) FROM orders")]
    [InlineData("SELECT printf('%1000000000d', id) FROM orders")]
    [InlineData("SELECT gateway_hmac_sha256(email, 'k') FROM orders")]
    [InlineData("SELECT sqlite_version() FROM orders")]
    [InlineData("SELECT value FROM json_each('[1,2,3]')")]
    [InlineData("SELECT name FROM pragma_table_info('Orders')")]
    [InlineData("SELECT name FROM pragma_database_list")]
    public async Task FunctionsOutsideTheClosedAllowlist_AreRejected_BeforeTheDatabase(string sql)
    {
        NoRowPolicy(Orders);
        Should.Throw<Exception>(() => Compile(sql));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Sqlite_LikeIsCaseInsensitive_SoAPolicyLikeIsRejected()
    {
        NoRowPolicy(Orders);
        (await IdsAsync("SELECT id FROM orders WHERE email LIKE 'ALICE%'")).ShouldBe(new long[] { 1 });   // the user's own LIKE only narrows

        _policies.Predicates[Orders] = Policy(new LikeExpression(Col("Region"), new PolicyParameterExpression("__pol_like", SqlParameterType.String)),
            ("__pol_like", new("eu%", SqlParameterType.String)));
        Should.Throw<SqlCompileNotSupportedException>(() => Compile("SELECT id FROM orders")).Reason.ShouldBe(SqlCompileNotSupportedReason.Construct);
    }

    // ---- limits ----

    [Fact]
    public async Task DeclaredBindLimit_IsTheLimitOfTheBundledLibrary()
    {
        int max = DialectCapabilityTable.Default.Get(TargetSqlDialect.Sqlite).MaxBindParameters;

        async Task<long> BindAsync(int count)
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM \"Orders\" WHERE \"Id\" IN (" + string.Join(",", Enumerable.Range(1, count).Select(i => "?" + i)) + ")";
            for (int i = 1; i <= count; i++)
            {
                var parameter = cmd.CreateParameter();
                parameter.ParameterName = "?" + i;
                parameter.Value = i;
                cmd.Parameters.Add(parameter);
            }

            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        (await BindAsync(max)).ShouldBe(6);
        await Should.ThrowAsync<SqliteException>(() => BindAsync(max + 1));
    }

    // ---- execution timeout (SEC-X1-10) ----

    [Fact]
    public async Task ACrossJoinOfTheCallersRows_IsInterruptedWithinTheTimeoutPlusOneSecond()
    {
        NoRowPolicy(Big);
        var compiled = Compile("SELECT count(*) FROM big a, big b, big c");   // 2.7e10 rows without the interrupt
        var clock = Stopwatch.StartNew();

        await Should.ThrowAsync<TimeoutException>(() => SqliteExecutionGuard.RunAsync(
            _conn, TimeSpan.FromMilliseconds(400), () => RunAsync(compiled), CancellationToken.None));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARecursiveCte_IsInterruptedWithinTheTimeoutPlusOneSecond()
    {
        var compiled = Compile("WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c");
        var clock = Stopwatch.StartNew();
        await Should.ThrowAsync<TimeoutException>(() => SqliteExecutionGuard.RunAsync(
            _conn, TimeSpan.FromMilliseconds(400), () => RunAsync(compiled), CancellationToken.None));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    // ---- issue #277: recursive CTE hierarchy walk and bounds ----

    [Fact]
    public async Task RecursiveCte_HierarchyWalk_ReturnsSameRowsAsUnsecuredQuery()
    {
        NoRowPolicy(Org);
        var secured = await IdsAsync("WITH RECURSIVE w(id) AS (SELECT id FROM org WHERE parentid IS NULL UNION ALL SELECT o.id FROM org o JOIN w ON o.parentid = w.id) SELECT id FROM w");

        await using var raw = _conn.CreateCommand();
        raw.CommandText = """
            WITH RECURSIVE w(id) AS (SELECT "Id" FROM "Org" WHERE "ParentId" IS NULL AND "TenantId" = 'acme'
                UNION ALL SELECT o."Id" FROM "Org" o JOIN w ON o."ParentId" = w.id WHERE o."TenantId" = 'acme')
            SELECT id FROM w ORDER BY id
            """;
        var unsecured = new List<long>();
        await using (var reader = await raw.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) unsecured.Add(reader.GetInt64(0));
        }

        secured.ShouldBe(unsecured);
        secured.ShouldBe(new long[] { 1, 2, 3, 4 });
        (await IdsAsync("WITH RECURSIVE w(id) AS (SELECT id FROM org WHERE parentid IS NULL UNION ALL SELECT o.id FROM org o JOIN w ON o.parentid = w.id) SELECT id FROM w", tenant: "other")).ShouldBe(new long[] { 5, 6 });
    }

    [Fact]
    public async Task RecursiveCte_CounterToThree_ReturnsThreeRows() =>
        (await IdsAsync("WITH RECURSIVE t(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM t WHERE n < 3) SELECT n FROM t")).ShouldBe(new long[] { 1, 2, 3 });

    [Fact]
    public async Task RecursiveCte_OuterAggregateOverRunaway_IsBounded()
    {
        var compiled = Compile("WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c");
        var clock = Stopwatch.StartNew();

        var ex = await Should.ThrowAsync<TimeoutException>(() => SqliteExecutionGuard.RunAsync(
            _conn, TimeSpan.FromMilliseconds(500), () => RunAsync(compiled), CancellationToken.None));

        Autheris.Application.Common.DataAccessErrorClassifier.Classify(ex).ShouldBe(Autheris.Application.Common.DataAccessErrorKind.Timeout);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ACancelledRequest_InterruptsTheStatement()
    {
        NoRowPolicy(Big);
        var compiled = Compile("SELECT count(*) FROM big a, big b, big c");
        using var cts = new CancellationTokenSource();
        var cancelThread = new Thread(() =>
        {
            Thread.Sleep(200);
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        })
        {
            IsBackground = true
        };
        cancelThread.Start();
        var clock = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() => SqliteExecutionGuard.RunAsync(
            _conn, TimeSpan.FromSeconds(30), () => RunAsync(compiled), cts.Token));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }
}
