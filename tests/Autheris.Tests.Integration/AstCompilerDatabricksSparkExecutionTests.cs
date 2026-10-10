namespace Autheris.Tests.Integration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Spark SQL stand-in for Databricks SQL (WP-C4): one long-lived pyspark process in the pinned <c>apache/spark</c> container,
/// driven over stdin/stdout. It proves the generated Databricks syntax, named parameter markers, ANSI semantics and the exact
/// tenant comparison. It does NOT prove Unity Catalog, Photon, runtime-specific functions or the Statement Execution API
/// (risk R-7); only the secret-gated live job (G9) can. Tests return early when Docker or the image is unavailable.
/// </summary>
public sealed class SparkProxyFixture : IAsyncLifetime, IDisposable
{
    // Pinned by digest (SEC-ADG-24). Update together with the digest recorded in docs/plans (implementation log).
    public const string Image = "apache/spark:4.0.0-python3@sha256:9e2f63442ba1a672ea70d780d56da28f42ce515d0be18840569a3054cc6d2314";

    private Process? _process;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsAvailable { get; private set; }
    public bool CollationAvailable { get; private set; }
    public bool ImagePresent { get; private set; }
    public string StartupError { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            ImagePresent = await ImageExistsAsync();
            if (!ImagePresent) return;
            string runner = Path.Combine(AppContext.BaseDirectory, "Spark");
            if (!File.Exists(Path.Combine(runner, "runner.py")))
            {
                StartupError = "runner.py was not copied to the output directory";
                return;
            }

            var info = new ProcessStartInfo("docker")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            // The Docker daemon may not share the test file system (bind mounts), so the runner is passed as the -c argument.
            foreach (var arg in new[] { "run", "-i", "--rm", "--user", "root", Image, "python3", "-c", await File.ReadAllTextAsync(Path.Combine(runner, "runner.py")) })
            {
                info.ArgumentList.Add(arg);
            }

            _process = Process.Start(info);
            if (_process is null)
            {
                StartupError = "docker could not be started";
                return;
            }

            var ddl = await SendAsync(new JsonObject { ["op"] = "ddl", ["sql"] = "SELECT 1" }, TimeSpan.FromMinutes(4));
            IsAvailable = ddl?["ok"]?.GetValue<bool>() == true;
            if (!IsAvailable)
            {
                StartupError = ddl?.ToJsonString() ?? "no response: " + (_process.HasExited ? await _process.StandardError.ReadToEndAsync() : "process still running");
                return;
            }

            await Ddl("""
                CREATE TABLE default.orders (
                    id INT, tenantid STRING, region STRING, status STRING, amount DECIMAL(18,2), email STRING) USING parquet
                """);
            // Spark 4 collations: a case-insensitive tenant column. Without them the collision test returns early.
            var collated = await SendAsync(new JsonObject { ["op"] = "ddl", ["sql"] = "CREATE TABLE default.orders_ci (id INT, tenantid STRING COLLATE UTF8_LCASE) USING parquet" });
            CollationAvailable = collated?["ok"]?.GetValue<bool>() == true;
            await Ddl("CREATE TABLE default.entitlements (id INT, tenantid STRING, orderid INT) USING parquet");
            await Ddl("""
                INSERT INTO default.orders VALUES
                    (1, 'acme',  'EU', 'open',   10.50, 'alice.smith@acme.example'),
                    (2, 'acme',  'US', 'open',   20.00, 'bob.jones@acme.example'),
                    (3, 'ACME',  'EU', 'open',   30.00, 'upper.case@ACME.example'),
                    (4, 'ACME',  'US', 'closed', 40.00, 'upper.us@ACME.example'),
                    (5, 'other', 'EU', 'open',   50.00, 'carol@other.example'),
                    (6, 'acme',  'EU', 'closed', 60.00, NULL)
                """);
            await Ddl("INSERT INTO default.entitlements VALUES (1, 'acme', 1), (2, 'other', 2), (3, 'acme', 6), (4, 'ACME', 3)");
            if (CollationAvailable)
            {
                await Ddl("INSERT INTO default.orders_ci VALUES (1, 'acme'), (2, 'ACME'), (3, 'other')");
            }
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            StartupError = ex.Message;
        }
    }

    private static async Task<bool> ImageExistsAsync()
    {
        try
        {
            var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "image", "inspect", Image }) info.ArgumentList.Add(arg);
            using var probe = Process.Start(info);
            if (probe is null) return false;
            await probe.WaitForExitAsync();
            return probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task Ddl(string sql)
    {
        var response = await SendAsync(new JsonObject { ["op"] = "ddl", ["sql"] = sql });
        if (response?["ok"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException("Spark fixture statement failed: " + response?["error"]);
        }
    }

    public async Task<JsonNode?> SendAsync(JsonObject request, TimeSpan? timeout = null)
    {
        await _gate.WaitAsync();
        try
        {
            await _process!.StandardInput.WriteLineAsync(request.ToJsonString());
            await _process.StandardInput.FlushAsync();
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(2));
            string? line = await _process.StandardOutput.ReadLineAsync(cts.Token);
            return line is null ? null : JsonNode.Parse(line);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    public Task DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(15000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // best effort
        }

        return Task.CompletedTask;
    }
}

public sealed class AstCompilerDatabricksSparkExecutionTests : IClassFixture<SparkProxyFixture>
{
    private const string Catalog_ = "spark_catalog";
    private static readonly TableIdentity Orders = new("default", "orders", Catalog_);
    private static readonly TableIdentity OrdersCi = new("default", "orders_ci", Catalog_);
    private static readonly TableIdentity Entitlements = new("default", "entitlements", Catalog_);

    private readonly SparkProxyFixture _spark;
    private readonly FastSqlEngine _engine = new();
    private readonly Policies _policies = new();

    public AstCompilerDatabricksSparkExecutionTests(SparkProxyFixture spark)
    {
        _spark = spark;
    }

    private static InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("id", "INT"), new CatalogColumn("tenantid", "STRING"), new CatalogColumn("region", "STRING"),
            new CatalogColumn("status", "STRING"), new CatalogColumn("amount", "DECIMAL(18,2)"), new CatalogColumn("email", "STRING")),
            "tenantid", 1),
        new TableCatalogEntry(Entitlements, ImmutableArray.Create(
            new CatalogColumn("id", "INT"), new CatalogColumn("tenantid", "STRING"), new CatalogColumn("orderid", "INT")),
            "tenantid", 1),
        new TableCatalogEntry(OrdersCi, ImmutableArray.Create(new CatalogColumn("id", "INT"), new CatalogColumn("tenantid", "STRING")), "tenantid", 1)
    }, "default");

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
        TargetDialect = TargetSqlDialect.Databricks,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = _policies,
            Masks = _policies,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    };

    private static JsonArray Parameters(CompiledSql compiled, IReadOnlyDictionary<string, object?> client)
    {
        var array = new JsonArray();
        foreach (var p in compiled.Parameters)
        {
            object? value = p.Origin == ParameterOrigin.ClientNamed ? client[p.SourceName!] : p.Value;
            var type = p.Origin == ParameterOrigin.ClientNamed
                ? value switch { string => "string", int or long => "int64", decimal => "decimal", bool => "boolean", _ => "string" }
                : p.Type switch
                {
                    SqlParameterType.String => "string",
                    SqlParameterType.Int32 => "int32",
                    SqlParameterType.Int64 => "int64",
                    SqlParameterType.Decimal => "decimal",
                    SqlParameterType.Double => "double",
                    SqlParameterType.Boolean => "boolean",
                    SqlParameterType.Date => "date",
                    SqlParameterType.Timestamp => "timestamp",
                    SqlParameterType.Binary => "binary",
                    _ => throw new NotSupportedException(p.Type.ToString())
                };
            JsonNode? json = value switch
            {
                null => null,
                DateTime dt when type == "date" => JsonValue.Create(dt.ToString("yyyy-MM-dd")),
                DateTime dt => JsonValue.Create(dt.ToString("yyyy-MM-ddTHH:mm:ss.ffffff")),
                byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
                decimal d => JsonValue.Create(d.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                bool b => JsonValue.Create(b),
                _ => JsonValue.Create(value.ToString())
            };
            array.Add(new JsonObject { ["name"] = p.Name, ["type"] = type, ["value"] = json });
        }

        return array;
    }

    private async Task<List<JsonNode?[]>> RunAsync(string sql, string tenant, IReadOnlyDictionary<string, object?>? client = null)
    {
        var compiled = _engine.Compile(sql.AsMemory(), Request(tenant), CancellationToken.None);
        var response = await _spark.SendAsync(new JsonObject
        {
            ["op"] = "query", ["sql"] = compiled.Sql, ["params"] = Parameters(compiled, client ?? new Dictionary<string, object?>())
        });
        if (response?["ok"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException("Spark rejected the statement: " + response?["error"] + " | SQL: " + compiled.Sql);
        }

        return response["rows"]!.AsArray().Select(r => r!.AsArray().ToArray()).ToList();
    }

    private static List<int> Ids(List<JsonNode?[]> rows) => rows.Select(r => r[0]!.GetValue<int>()).OrderBy(x => x).ToList();

    [Fact]
    public void SparkProxy_Starts_WheneverTheImageIsPresent()
    {
        // Without the image the suite returns early (no Docker); with it, a failing proxy must be visible.
        if (_spark.ImagePresent)
        {
            _spark.IsAvailable.ShouldBeTrue(_spark.StartupError);
        }
    }

    [Theory]
    [InlineData("SELECT reflect('java.lang.System', 'getProperty', 'java.version') FROM orders")]
    [InlineData("SELECT java_method('java.lang.System', 'getProperty', 'java.version') FROM orders")]
    [InlineData("SELECT secret('scope', 'key') FROM orders")]
    public async Task Reflect_JavaMethod_Secret_AreRejected_BeforeReachingSpark(string sql)
    {
        // CR-ADG-02: the review executed reflect(...) on the Spark proxy (it returned the JVM version). The compiler now rejects
        // every function without a rule, so nothing is sent to Spark. Runs without the proxy: the failure is at compile time.
        Should.Throw<System.Security.SecurityException>(() => _engine.Compile(sql.AsMemory(), Request("acme"), CancellationToken.None));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Tenant_SeesOnlyItsOwnRows()
    {
        if (!_spark.IsAvailable) return;
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(await RunAsync("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        Ids(await RunAsync("SELECT id FROM orders", "ACME")).ShouldBe(new List<int> { 3, 4 });
    }

    [Fact]
    public async Task TenantCaseCollision_IsIsolated_OnAnLcaseCollatedColumn()
    {
        if (!_spark.IsAvailable || !_spark.CollationAvailable) return;
        var plain = await _spark.SendAsync(new JsonObject { ["op"] = "query", ["sql"] = "SELECT count(*) FROM default.orders_ci WHERE tenantid = 'acme'" });
        plain!["rows"]![0]![0]!.GetValue<long>().ShouldBe(2);   // the collation makes a plain equality case-insensitive

        Ids(await RunAsync("SELECT id FROM orders_ci", "acme")).ShouldBe(new List<int> { 1 });
        Ids(await RunAsync("SELECT id FROM orders_ci", "ACME")).ShouldBe(new List<int> { 2 });
    }

    [Fact]
    public async Task ConsentFilter_And_DenyAll()
    {
        if (!_spark.IsAvailable) return;
        _policies.Predicates[Orders] = RegionPolicy("EU");
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 6 });
        _policies.Predicates[Orders] = PolicyPredicate.DenyAll;
        (await RunAsync("SELECT id FROM orders WHERE 1 = 1 OR status = 'open'", "acme")).ShouldBeEmpty();
    }

    [Fact]
    public async Task PolicySubquery_ForeignTenantRowsNeverDecideVisibility()
    {
        if (!_spark.IsAvailable) return;
        var correlated = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(Col("id", "e"), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier(Catalog_, true), new SqlIdentifier("default", true), new SqlIdentifier("entitlements", true) }), new SqlIdentifier("e", true)),
                new BinaryExpression(Col("orderid", "e"), BinaryOperator.Equal, new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("autheris_target"), new SqlIdentifier("id", true) }))),
                null, null), null, null));
        _policies.Predicates[Orders] = PolicyPredicate.Create(correlated, new Dictionary<string, PolicyValue>());
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 6 });
    }

    [Fact]
    public async Task CteNamedLikeTheTable_UnionJoinSubquery_AreSecured()
    {
        if (!_spark.IsAvailable) return;
        Ids(await RunAsync("WITH orders AS (SELECT id FROM orders) SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        var rows = await RunAsync(
            "WITH o AS (SELECT id, status FROM orders) SELECT o.id FROM o JOIN orders p ON p.id = o.id WHERE o.id IN (SELECT id FROM orders) " +
            "UNION ALL SELECT id FROM orders WHERE id = (SELECT max(id) FROM orders)", "acme");
        rows.Select(r => r[0]!.GetValue<int>()).ShouldAllBe(id => id == 1 || id == 2 || id == 6);
    }

    [Theory]
    [InlineData("SELECT status, count(*) AS n, sum(amount) AS total FROM orders GROUP BY status ORDER BY status")]
    [InlineData("SELECT id FROM orders WHERE status = 'open' AND amount > 10.5 ORDER BY id OFFSET 1 LIMIT 5")]
    [InlineData("SELECT id, sum(amount) OVER (PARTITION BY status ORDER BY amount ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS rn FROM orders")]
    [InlineData("SELECT id, CASE WHEN amount > 15 THEN 'big' ELSE 'small' END AS size FROM orders")]
    [InlineData("SELECT id FROM orders WHERE email LIKE '%@acme.example' OR email IS NULL")]
    [InlineData("SELECT id, cast(amount AS integer) AS a, try_cast(status AS integer) AS t FROM orders")]
    [InlineData("SELECT id, amount > 15 AS flag FROM orders")]
    [InlineData("SELECT id FROM orders WHERE id BETWEEN 1 AND 6 AND status IN ('open', 'closed') AND NOT (amount < 0)")]
    [InlineData("SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v (x, y)")]
    [InlineData("SELECT date '2024-01-15' AS d, timestamp '2024-01-15 10:30:00' AS ts FROM orders")]
    [InlineData("SELECT substring(email FROM 1 FOR 3) AS s, upper(status) AS u, length(status) AS l, strpos(email, '@') AS p FROM orders")]
    [InlineData("SELECT DISTINCT region FROM orders ORDER BY region")]
    [InlineData("SELECT id FROM orders GROUP BY id, status HAVING count(*) > 0")]
    [InlineData("SELECT id FROM orders ORDER BY amount DESC NULLS LAST, id")]
    [InlineData("SELECT count(*) FILTER (WHERE amount > 15) AS n FROM orders")]
    [InlineData("SELECT region, GROUPING(region) AS g, count(*) AS n FROM orders GROUP BY ROLLUP (region)")]
    [InlineData("SELECT extract(year FROM date '2024-05-06') AS y, extract(dow FROM date '2024-05-06') AS w FROM orders")]
    [InlineData("SELECT a.id FROM orders a WHERE a.amount IS DISTINCT FROM 20.00")]
    [InlineData("SELECT approx_distinct(region) AS d, arbitrary(status) AS a FROM orders")]
    public async Task UserQueryShapes_CompileAndExecute(string sql)
    {
        if (!_spark.IsAvailable) return;
        (await RunAsync(sql, "acme")).ShouldNotBeNull();
    }

    [Fact]
    public async Task ClientNamedParameter_And_PlanCacheRebinding()
    {
        if (!_spark.IsAvailable) return;
        Ids(await RunAsync("SELECT id FROM orders WHERE status = __param_st", "acme", new Dictionary<string, object?> { ["st"] = "closed" })).ShouldBe(new List<int> { 6 });
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(await RunAsync("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        _engine.CompileCache.Stats.Hits.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Theory]
    [InlineData("'; DROP TABLE default.orders; --")]
    [InlineData("acme' OR '1'='1")]
    [InlineData("${spark.sql.shuffle.partitions}")]
    [InlineData("acme\\")]
    [InlineData("`; DROP TABLE default.orders; --")]
    public async Task HostileValues_AreJustValues_VariableSubstitutionNeverApplies(string hostile)
    {
        if (!_spark.IsAvailable) return;
        (await RunAsync("SELECT id FROM orders", hostile)).ShouldBeEmpty();
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        (await RunAsync("SELECT id FROM orders WHERE status = __param_s", "acme", new Dictionary<string, object?> { ["s"] = hostile })).ShouldBeEmpty();
    }

    [Fact]
    public async Task VariableSubstitutionInInput_IsRejectedByTheTokenGuard()
    {
        if (!_spark.IsAvailable) return;
        await Should.ThrowAsync<Exception>(() => RunAsync("SELECT id FROM orders WHERE status = '${x}'", "acme"));
    }

    // ---- masks ----

    private static PolicyParameterExpression MaskParam(string name, SqlParameterType type) => new(name, type, ParameterOrigin.Mask);

    [Fact]
    public async Task Redact_Partial_GeoJitter_Nullify_AndHmacDegradation()
    {
        if (!_spark.IsAvailable) return;
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Redact,
            new MaskArguments(Constant: MaskParam("__mask_email", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__mask_email"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());
        (await RunAsync("SELECT id, email FROM orders", "acme")).Select(r => r[1]!.GetValue<string>()).ShouldAllBe(e => e == "[REDACTED]");
        (await RunAsync("SELECT * FROM orders", "acme")).SelectMany(r => r).Select(v => v?.ToString()).ShouldNotContain(v => v != null && v.Contains("@acme.example"));

        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.PartialMask,
            new MaskArguments(KeepPrefix: MaskParam("m_p", SqlParameterType.Int32), KeepSuffix: MaskParam("m_s", SqlParameterType.Int32), MaskChar: MaskParam("m_c", SqlParameterType.String)),
            new Dictionary<string, PolicyValue>
            {
                ["m_p"] = new(2, SqlParameterType.Int32), ["m_s"] = new(4, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            }.ToFrozenDictionary());
        var masked = (await RunAsync("SELECT id, email FROM orders WHERE id = 1", "acme")).Single()[1]!.GetValue<string>();
        masked.ShouldStartWith("al");
        masked.ShouldEndWith("mple");
        masked.ShouldContain("*");

        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.PartialMask,
            new MaskArguments(KeepPrefix: MaskParam("m_p", SqlParameterType.Int32), KeepSuffix: MaskParam("m_s", SqlParameterType.Int32), MaskChar: MaskParam("m_c", SqlParameterType.String)),
            new Dictionary<string, PolicyValue>
            {
                ["m_p"] = new(-3, SqlParameterType.Int32), ["m_s"] = new(-3, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            }.ToFrozenDictionary());
        (await RunAsync("SELECT id, email FROM orders WHERE id = 1", "acme")).Single()[1]!.GetValue<string>().ShouldNotContain("alice");

        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Hmac,
            new MaskArguments(HmacKey: MaskParam("k_i", SqlParameterType.Binary), HmacKeyOuter: MaskParam("k_o", SqlParameterType.Binary)),
            new Dictionary<string, PolicyValue>
            {
                ["k_i"] = new(new byte[] { 1 }, SqlParameterType.Binary), ["k_o"] = new(new byte[] { 2 }, SqlParameterType.Binary)
            }.ToFrozenDictionary());
        (await RunAsync("SELECT id, email FROM orders", "acme")).Select(r => r[1]!.GetValue<string>()).ShouldAllBe(e => e == "[REDACTED]");

        _policies.Masks.Clear();
        _policies.Masks[(Orders, "amount")] = new MaskSpec(MaskKind.GeoJitter, new MaskArguments(Decimals: 0), FrozenDictionary<string, PolicyValue>.Empty);
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        var row = (await RunAsync("SELECT id, amount, email FROM orders WHERE id = 1", "acme")).Single();
        decimal.Parse(row[1]!.ToString()!, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(11m);
        row[2].ShouldBeNull();
    }

    // ---- items marked (verify) in the plan, resolved against the Spark proxy ----

    [Fact]
    public async Task Verify_OffsetLimit_TimestampNtz_AndLateral_OnSpark()
    {
        if (!_spark.IsAvailable) return;
        var offset = await _spark.SendAsync(new JsonObject { ["op"] = "query", ["sql"] = "SELECT id FROM default.orders ORDER BY id LIMIT 2 OFFSET 1" });
        offset!["ok"]!.GetValue<bool>().ShouldBeTrue();                              // OFFSET is supported
        var ntz = await _spark.SendAsync(new JsonObject { ["op"] = "query", ["sql"] = "SELECT CAST('2024-01-15 10:00:00' AS TIMESTAMP_NTZ) AS t" });
        ntz!["ok"]!.GetValue<bool>().ShouldBeTrue();                                 // TIMESTAMP_NTZ is supported
        var lateral = await _spark.SendAsync(new JsonObject
        {
            ["op"] = "query",
            ["sql"] = "SELECT o.id FROM default.orders o, LATERAL (SELECT e.id FROM default.entitlements e WHERE e.orderid = o.id) l"
        });
        // Informational: LATERAL stays rejected by the compiler (SupportsLateral = false) until Databricks SQL itself is probed (G9).
        lateral.ShouldNotBeNull();
    }
}
