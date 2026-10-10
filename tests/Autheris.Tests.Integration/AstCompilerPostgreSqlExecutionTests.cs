namespace Autheris.Tests.Integration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>One PostgreSQL container with the fixture schema; ICU nondeterministic collations are optional.</summary>
public sealed class AstCompilerPostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("ast_gate")
        .WithUsername("postgres")
        .WithPassword("postgres_password_2026")
        .Build();

    public string ConnectionString => _container.GetConnectionString();
    public bool IcuCollationAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await Exec(conn, """
            CREATE EXTENSION IF NOT EXISTS pgcrypto;
            CREATE EXTENSION IF NOT EXISTS citext;
            CREATE TABLE public.orders (
                id integer PRIMARY KEY,
                tenantid citext NOT NULL,
                region text NOT NULL,
                status text NOT NULL,
                amount numeric(18,2) NOT NULL,
                email text);
            CREATE TABLE public.entitlements (id integer PRIMARY KEY, tenantid citext NOT NULL, orderid integer NOT NULL);
            INSERT INTO public.orders VALUES
                (1, 'acme',  'EU', 'open',   10.50, 'alice.smith@acme.example'),
                (2, 'acme',  'US', 'open',   20.00, 'bob.jones@acme.example'),
                (3, 'ACME',  'EU', 'open',   30.00, 'upper.case@ACME.example'),
                (4, 'ACME',  'US', 'closed', 40.00, 'upper.us@ACME.example'),
                (5, 'other', 'EU', 'open',   50.00, 'carol@other.example'),
                (6, 'acme',  'EU', 'closed', 60.00, NULL);
            INSERT INTO public.entitlements VALUES (1, 'acme', 1), (2, 'other', 2), (3, 'acme', 6), (4, 'ACME', 3);
            CREATE SCHEMA evil;
            CREATE TABLE evil.orders (id integer PRIMARY KEY, tenantid text, region text, status text, amount numeric(18,2), email text);
            INSERT INTO evil.orders VALUES (99, 'acme', 'EU', 'decoy', 1, 'decoy@evil.example');
            """);

        try
        {
            await Exec(conn, """
                CREATE COLLATION public.ci_icu (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
                CREATE TABLE public.orders_icu (id integer PRIMARY KEY, tenantid text COLLATE public.ci_icu NOT NULL);
                INSERT INTO public.orders_icu VALUES (1, 'acme'), (2, 'ACME'), (3, 'other');
                """);
            IcuCollationAvailable = true;
        }
        catch (PostgresException)
        {
            IcuCollationAvailable = false;
        }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// Executes the governed compiler's PostgreSQL output on a real PostgreSQL: RLS visibility, byte-exact tenant comparison
/// (citext and a non-deterministic ICU collation), search_path independence, masks (pgcrypto HMAC) and hostile values.
/// </summary>
public sealed class AstCompilerPostgreSqlExecutionTests : IClassFixture<AstCompilerPostgreSqlFixture>
{
    private static readonly TableIdentity Orders = new("public", "orders");
    private static readonly TableIdentity Entitlements = new("public", "entitlements");
    private static readonly TableIdentity OrdersIcu = new("public", "orders_icu");

    private readonly AstCompilerPostgreSqlFixture _fx;
    private readonly FastSqlEngine _engine = new();
    private readonly PostgreSqlCompiledSqlBinder _binder = new();
    private readonly Policies _policies = new();

    public AstCompilerPostgreSqlExecutionTests(AstCompilerPostgreSqlFixture fixture)
    {
        _fx = fixture;
    }

    private static InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("id", "integer"), new CatalogColumn("tenantid", "citext"), new CatalogColumn("region", "text"),
            new CatalogColumn("status", "text"), new CatalogColumn("amount", "numeric(18,2)"), new CatalogColumn("email", "text")),
            "tenantid", 1),
        new TableCatalogEntry(Entitlements, ImmutableArray.Create(
            new CatalogColumn("id", "integer"), new CatalogColumn("tenantid", "citext"), new CatalogColumn("orderid", "integer")),
            "tenantid", 1),
        new TableCatalogEntry(OrdersIcu, ImmutableArray.Create(new CatalogColumn("id", "integer"), new CatalogColumn("tenantid", "text")), "tenantid", 1)
    }, "public");

    private sealed class Policies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public Dictionary<TableIdentity, PolicyPredicate> Predicates { get; } = new();
        public Dictionary<(TableIdentity, string), MaskSpec> Masks { get; } = new();
        public bool ShouldApplyPolicy(TableIdentity table) => Predicates.ContainsKey(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => Predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => Masks.ContainsKey((table, column.ToLowerInvariant()));
        public MaskSpec GetMask(TableIdentity table, string column) => Masks[(table, column.ToLowerInvariant())];
    }

    private static ColumnReference Col(string name, string? qualifier = null) => new(new SqlQualifiedName(
        qualifier is null ? new[] { new SqlIdentifier(name, true) } : new[] { new SqlIdentifier(qualifier), new SqlIdentifier(name, true) }));

    private static PolicyPredicate RegionPolicy(string region) => PolicyPredicate.Create(
        new BinaryExpression(Col("region"), BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new(region, SqlParameterType.String) });

    private CompileRequest Request(string tenant) => new()
    {
        TargetDialect = TargetSqlDialect.PostgreSql,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = _policies,
            Masks = _policies,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    };

    private async Task<List<object?[]>> RunAsync(string sql, string tenant, IReadOnlyDictionary<string, object?>? client = null,
        Func<NpgsqlConnection, Task>? prepare = null)
    {
        var compiled = _engine.Compile(sql.AsMemory(), Request(tenant), CancellationToken.None);
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        if (prepare != null) await prepare(conn);
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, client ?? new Dictionary<string, object?>());
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < row.Length; i++)
            {
                if (await reader.IsDBNullAsync(i)) continue;
                try
                {
                    row[i] = reader.GetValue(i);
                }
                catch (InvalidCastException)
                {
                    row[i] = reader.GetFieldValue<string>(i);   // extension types such as citext have no Npgsql mapping
                }
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<int> Ids(List<object?[]> rows) => rows.Select(r => Convert.ToInt32(r[0])).OrderBy(x => x).ToList();

    [Fact]
    public async Task Tenant_SeesOnlyItsOwnRows()
    {
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(await RunAsync("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
    }

    [Fact]
    public async Task TenantCaseCollision_IsIsolated_OnACitextColumn()
    {
        // citext compares case-insensitively: a plain equality would also return the ACME rows 3 and 4.
        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var probe = new NpgsqlCommand("SELECT count(*) FROM public.orders WHERE tenantid = 'acme'", conn);
            Convert.ToInt32(await probe.ExecuteScalarAsync()).ShouldBe(5);
        }

        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(await RunAsync("SELECT id FROM orders", "ACME")).ShouldBe(new List<int> { 3, 4 });
    }

    [Fact]
    public async Task TenantCaseCollision_IsIsolated_OnANondeterministicIcuCollation()
    {
        if (!_fx.IcuCollationAvailable) return;   // the image has no ICU collation support
        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var probe = new NpgsqlCommand("SELECT count(*) FROM public.orders_icu WHERE tenantid = 'acme'", conn);
            Convert.ToInt32(await probe.ExecuteScalarAsync()).ShouldBe(2);
        }

        Ids(await RunAsync("SELECT id FROM orders_icu", "acme")).ShouldBe(new List<int> { 1 });
        Ids(await RunAsync("SELECT id FROM orders_icu", "ACME")).ShouldBe(new List<int> { 2 });
    }

    [Fact]
    public async Task ConsentFilter_IsAppliedOnTopOfTheTenant()
    {
        _policies.Predicates[Orders] = RegionPolicy("EU");
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 6 });
    }

    [Fact]
    public async Task DenyAll_ReturnsNoRows_EvenWithATautologyInTheUserQuery()
    {
        _policies.Predicates[Orders] = PolicyPredicate.DenyAll;
        (await RunAsync("SELECT id FROM orders WHERE 1 = 1 OR status = 'open'", "acme")).ShouldBeEmpty();
    }

    [Fact]
    public async Task PolicySubquery_ForeignTenantRowsNeverDecideVisibility()
    {
        var correlated = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(Col("id", "e"), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("public", true), new SqlIdentifier("entitlements", true) }), new SqlIdentifier("e", true)),
                new BinaryExpression(Col("orderid", "e"), BinaryOperator.Equal, new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("autheris_target"), new SqlIdentifier("id", true) }))),
                null, null), null, null));
        _policies.Predicates[Orders] = PolicyPredicate.Create(correlated, new Dictionary<string, PolicyValue>());

        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 6 });
    }

    // ---- SEC-ADG-07: search_path independence ----

    [Fact]
    public async Task SearchPathAndTempTables_CannotRedirectTheSecuredTable()
    {
        Func<NpgsqlConnection, Task> hostile = async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SET search_path TO evil, public;
                CREATE TEMP TABLE orders (id integer, tenantid text, region text, status text, amount numeric, email text);
                INSERT INTO pg_temp.orders VALUES (77, 'acme', 'EU', 'temp-decoy', 1, 'temp@evil.example');
                """;
            await cmd.ExecuteNonQueryAsync();
        };

        // Sanity: an unqualified reference on that connection reads the decoys.
        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await hostile(conn);
            await using var probe = new NpgsqlCommand("SELECT count(*) FROM orders WHERE id = 77", conn);
            Convert.ToInt32(await probe.ExecuteScalarAsync()).ShouldBe(1);
        }

        var rows = await RunAsync("SELECT id FROM orders", "acme", prepare: hostile);
        Ids(rows).ShouldBe(new List<int> { 1, 2, 6 });                  // never 99 or 77
        (await RunAsync("WITH orders AS (SELECT id FROM orders) SELECT id FROM orders", "other", prepare: hostile)).Select(r => Convert.ToInt32(r[0])).ShouldBe(new List<int> { 5 });
    }

    [Fact]
    public async Task UnionCteJoinSubquery_AllScopesAreSecured()
    {
        var rows = await RunAsync(
            "WITH o AS (SELECT id, status FROM orders) " +
            "SELECT o.id FROM o JOIN orders p ON p.id = o.id WHERE o.id IN (SELECT id FROM orders) " +
            "UNION ALL SELECT id FROM orders WHERE id = (SELECT max(id) FROM orders)",
            "acme");
        rows.Select(r => Convert.ToInt32(r[0])).ShouldAllBe(id => id == 1 || id == 2 || id == 6);
    }

    [Fact]
    public async Task RecursiveCte_ExecutesAndOnlyReadsTheTenantsRows()
    {
        // CR-ADG-29: the CTE name resolves to itself; the anchor reads the secured table.
        var rows = await RunAsync(
            "WITH RECURSIVE chain (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT id, n + 1 FROM chain WHERE n < 3) SELECT id, n FROM chain",
            "acme");
        rows.ShouldNotBeEmpty();
        rows.Select(r => Convert.ToInt32(r[0])).ShouldAllBe(id => id == 1 || id == 2 || id == 6);
        rows.Max(r => Convert.ToInt32(r[1])).ShouldBe(3);
        (rows.Count % 3).ShouldBe(0);
        await Should.ThrowAsync<System.Security.SecurityException>(() => RunAsync(
            "WITH RECURSIVE orders (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT id, n + 1 FROM orders WHERE n < 3) SELECT id FROM orders",
            "acme"));
    }

    [Theory]
    [InlineData("SELECT status, count(*) AS n, sum(amount) AS total FROM orders GROUP BY status ORDER BY status")]
    [InlineData("SELECT id FROM orders WHERE status = 'open' AND amount > 10.5 ORDER BY id OFFSET 1 LIMIT 5")]
    [InlineData("SELECT id FROM orders ORDER BY id FETCH FIRST 2 ROWS WITH TIES")]
    [InlineData("SELECT id, sum(amount) OVER (PARTITION BY status ORDER BY amount ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS rn FROM orders")]
    [InlineData("SELECT id, CASE WHEN amount > 15 THEN 'big' ELSE 'small' END AS size FROM orders")]
    [InlineData("SELECT id FROM orders WHERE email LIKE '%@acme.example' OR email IS NULL")]
    [InlineData("SELECT id, cast(amount AS integer) AS a, cast(status AS varchar) AS t FROM orders")]
    [InlineData("SELECT id, amount > 15 AS flag FROM orders")]
    [InlineData("SELECT id FROM orders WHERE id BETWEEN 1 AND 6 AND status IN ('open', 'closed') AND NOT (amount < 0)")]
    [InlineData("SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v (x, y)")]
    [InlineData("SELECT date '2024-01-15' AS d, timestamp '2024-01-15 10:30:00' AS ts, time '10:30:00' AS tm FROM orders")]
    [InlineData("SELECT substring(email FROM 1 FOR 3) AS s, upper(status) AS u, length(status) AS l, strpos(email, '@') AS p FROM orders")]
    [InlineData("SELECT DISTINCT region FROM orders ORDER BY region")]
    [InlineData("SELECT id FROM orders GROUP BY id, status HAVING count(*) > 0")]
    [InlineData("SELECT id FROM orders ORDER BY amount DESC NULLS LAST, id")]
    [InlineData("SELECT count(*) FILTER (WHERE amount > 15) AS n, array_agg(id ORDER BY id) AS ids FROM orders")]
    [InlineData("SELECT region, GROUPING(region) AS g, count(*) AS n FROM orders GROUP BY ROLLUP (region)")]
    [InlineData("SELECT o.id FROM orders o CROSS JOIN LATERAL (SELECT e.id FROM entitlements e WHERE e.orderid = o.id) l")]
    [InlineData("SELECT extract(year FROM date '2024-05-06') AS y, extract(dow FROM date '2024-05-06') AS w FROM orders")]
    [InlineData("SELECT a.id FROM orders a WHERE a.amount IS DISTINCT FROM 20.00")]
    public async Task UserQueryShapes_CompileAndExecute(string sql)
    {
        (await RunAsync(sql, "acme")).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("SELECT CAST(__param_t AS regclass) FROM orders")]
    [InlineData("SELECT CAST(__param_t AS regrole) FROM orders")]
    [InlineData("SELECT __param_t::regclass FROM orders")]
    [InlineData("SELECT CAST(status AS xml) FROM orders")]
    public async Task CastToCatalogProbingType_IsRejected_BeforeExecution(string sql)
    {
        // CR-ADG-25: the compiler refuses it; nothing reaches the database.
        Should.Throw<Exception>(() => _engine.Compile(sql.AsMemory(), Request("acme"), CancellationToken.None));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ClientNamedParameter_IsBoundPositionally()
    {
        Ids(await RunAsync("SELECT id FROM orders WHERE status = __param_st AND amount > __param_min", "acme",
            new Dictionary<string, object?> { ["st"] = "closed", ["min"] = 5m })).ShouldBe(new List<int> { 6 });
    }

    [Fact]
    public async Task PlanCache_RebindsTheTenant_NeverServesTheOtherTenantsRows()
    {
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(await RunAsync("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        Ids(await RunAsync("SELECT id FROM orders", "ACME")).ShouldBe(new List<int> { 3, 4 });
        _engine.CompileCache.Stats.Hits.ShouldBe(2);
    }

    [Theory]
    [InlineData("'; DROP TABLE public.orders; --")]
    [InlineData("acme' OR '1'='1")]
    [InlineData("acme\"; DROP TABLE public.orders; --")]
    [InlineData("$$acme$$")]
    [InlineData("acme\\")]
    public async Task HostileTenantValue_IsJustAValue_NoRows_TableIntact(string hostile)
    {
        (await RunAsync("SELECT id FROM orders", hostile)).ShouldBeEmpty();
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
    }

    [Fact]
    public async Task EmbeddedNul_IsRejectedByTheBinder()
    {
        await Should.ThrowAsync<System.Security.SecurityException>(() => RunAsync("SELECT id FROM orders", "acme\u0000"));
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("'; DELETE FROM public.orders; --")]
    [InlineData("%")]
    public async Task HostileUserLiteral_IsBound(string hostile)
    {
        (await RunAsync("SELECT id FROM orders WHERE status = __param_s", "acme", new Dictionary<string, object?> { ["s"] = hostile })).ShouldBeEmpty();
    }

    // ---- masks ----

    private static PolicyParameterExpression MaskParam(string name, SqlParameterType type) => new(name, type, ParameterOrigin.Mask);

    [Fact]
    public async Task Redact_MaskedEmail_IsNeverInClear()
    {
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Redact,
            new MaskArguments(Constant: MaskParam("__mask_email", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__mask_email"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());

        (await RunAsync("SELECT id, email FROM orders", "acme")).Select(r => (string?)r[1]).ShouldAllBe(e => e == "[REDACTED]");
        // SELECT * (citext has no Npgsql mapping, so the rows are read as JSON text).
        var compiled = _engine.Compile("SELECT * FROM orders".AsMemory(), Request("acme"), CancellationToken.None);
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        cmd.CommandText = "SELECT to_jsonb(q)::text FROM (" + compiled.Sql + ") q";
        var texts = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) texts.Add(reader.GetString(0));
        }

        texts.ShouldNotBeEmpty();
        texts.ShouldAllBe(t => !t.Contains("@acme.example") && t.Contains("[REDACTED]"));
    }

    [Fact]
    public async Task PartialMask_KeepsPrefixAndSuffix_AndNegativeCountsCannotExposeTheValue()
    {
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.PartialMask,
            new MaskArguments(
                KeepPrefix: MaskParam("m_p", SqlParameterType.Int32), KeepSuffix: MaskParam("m_s", SqlParameterType.Int32), MaskChar: MaskParam("m_c", SqlParameterType.String)),
            new Dictionary<string, PolicyValue>
            {
                ["m_p"] = new(2, SqlParameterType.Int32), ["m_s"] = new(4, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            }.ToFrozenDictionary());

        var masked = (string)(await RunAsync("SELECT id, email FROM orders WHERE id = 1", "acme")).Single()[1]!;
        masked.ShouldStartWith("al");
        masked.ShouldEndWith("mple");
        masked.ShouldContain("*");

        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.PartialMask,
            new MaskArguments(
                KeepPrefix: MaskParam("m_p", SqlParameterType.Int32), KeepSuffix: MaskParam("m_s", SqlParameterType.Int32), MaskChar: MaskParam("m_c", SqlParameterType.String)),
            new Dictionary<string, PolicyValue>
            {
                ["m_p"] = new(-3, SqlParameterType.Int32), ["m_s"] = new(-3, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            }.ToFrozenDictionary());
        var negative = (string)(await RunAsync("SELECT id, email FROM orders WHERE id = 1", "acme")).Single()[1]!;
        negative.ShouldNotContain("alice");
    }

    [Fact]
    public async Task Hmac_MatchesTheReferenceHmacSha256()
    {
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Hmac,
            new MaskArguments(HmacKey: MaskParam("k", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["k"] = new(key, SqlParameterType.String) }.ToFrozenDictionary());

        var rows = await RunAsync("SELECT id, email FROM orders WHERE id = 1", "acme");
        string expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes("alice.smith@acme.example"))).ToLowerInvariant();
        ((string)rows.Single()[1]!).ShouldBe(expected);
    }

    [Fact]
    public async Task GeoJitterAndNullify_ExecuteAndNeverShowTheRawValue()
    {
        _policies.Masks[(Orders, "amount")] = new MaskSpec(MaskKind.GeoJitter, new MaskArguments(Decimals: 0), FrozenDictionary<string, PolicyValue>.Empty);
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);

        var row = (await RunAsync("SELECT id, amount, email FROM orders WHERE id = 1", "acme")).Single();
        Convert.ToDecimal(row[1]).ShouldBe(11m);
        row[2].ShouldBeNull();
    }

    [Fact]
    public async Task MaskedColumn_InWhereOrOrderBy_IsRejected()
    {
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        await Should.ThrowAsync<System.Security.SecurityException>(() => RunAsync("SELECT id FROM orders WHERE email = 'x'", "acme"));
        await Should.ThrowAsync<System.Security.SecurityException>(() => RunAsync("SELECT id FROM orders ORDER BY email", "acme"));
    }

    [Fact]
    public async Task BindLimitProbe_PostgreSqlAcceptsTheCapabilityTableValue()
    {
        int max = TrinoSqlEngine.Ast.Capabilities.DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql).MaxBindParameters;
        var sql = new StringBuilder("SELECT count(*) FROM generate_series(1, 10) AS g(x) WHERE x IN (");
        for (int i = 1; i <= max; i++)
        {
            if (i > 1) sql.Append(',');
            sql.Append('$').Append(i);
        }

        sql.Append(')');
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        for (int i = 1; i <= max; i++) cmd.Parameters.Add(new NpgsqlParameter { Value = i });
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(10);
    }
}
