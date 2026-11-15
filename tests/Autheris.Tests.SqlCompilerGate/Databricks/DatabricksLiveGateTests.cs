namespace Autheris.Tests.SqlCompilerGate.Databricks;

using System.Globalization;
using System.Security;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

/// <summary>
/// G9: the execution suites against a live Databricks SQL warehouse (nightly, main only, environment <c>databricks-g9</c>).
/// Every test is deterministic without credentials: a recording transport stands in for the warehouse and the compiled statements
/// are asserted. When the five <c>DATABRICKS_*</c> secrets are present the same tests additionally run against the warehouse, inside
/// an ephemeral scratch schema that is dropped even when a test fails.
/// </summary>
[Trait("Category", "DatabricksLive")]
public sealed class DatabricksLiveGateTests
{
    private static readonly FastSqlEngine Engine = new();

    private static CompileRequest Request(string schema, PolicyScenario scenario, StatementPermissions permissions)
    {
        // Same catalog, tenant binding, consent filter and masks as the other gate legs; only the target dialect is Databricks.
        var request = new GatePolicies(GateDialect.Sqlite, schema, scenario).AstRequest(permissions);
        return request with
        {
            Policy = request.Policy with { Catalog = Catalog(schema) },
            TargetDialect = TargetSqlDialect.Databricks,
            TokenGuards = SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.Databricks),
            AllowedFunctions = SqlFunctionAllowlists.Build(TargetSqlDialect.Databricks, null),
            AllowExperimentalDialect = true
        };
    }

    /// <summary>The gate catalog with Databricks column types (the masks cast to the catalog type of the column).</summary>
    private static InMemoryTableCatalog Catalog(string schema) => new(
        GateModel.Tables.Select(t => new TableCatalogEntry(
            new TableIdentity(schema, GateModel.Canon(GateDialect.Sqlite, t.Name)),
            [.. t.Columns.Select(c => new CatalogColumn(GateModel.Canon(GateDialect.Sqlite, c.Name), c.Kind switch
            {
                ColumnKind.Whole => "bigint",
                ColumnKind.Fixed => "decimal(18,2)",
                ColumnKind.Timestamp => "timestamp",
                _ => "string"
            }))],
            GateModel.Canon(GateDialect.Sqlite, GateModel.TenantColumn),
            1)),
        schema);

    /// <summary>Compiles under the governed policy first; only a statement that compiled is handed to the transport.</summary>
    private static async Task<(CompiledSql Compiled, IReadOnlyList<string?[]> Rows)> GovernedAsync(
        IDatabricksGateTransport transport, string schema, string sql, PolicyScenario scenario)
    {
        var compiled = Engine.Compile(sql.AsMemory(), Request(schema, scenario, StatementPermissions.ReadOnly), CancellationToken.None);
        var rows = await transport.ExecuteAsync(compiled.Sql, DatabricksFixture.ParametersOf(compiled.Parameters), CancellationToken.None);
        return (compiled, rows);
    }

    /// <summary>The warehouse when configured, otherwise the recording fake.</summary>
    private static IDatabricksGateTransport Transport(out LiveDatabricksGateTransport? live)
    {
        live = LiveDatabricksGateTransport.TryCreateFromEnvironment();
        return live ?? (IDatabricksGateTransport)new RecordingGateTransport();
    }

    private sealed class GateRun : IAsyncDisposable
    {
        private readonly IDatabricksGateTransport _transport;

        private GateRun(IDatabricksGateTransport transport, string schema)
        {
            _transport = transport;
            Schema = schema;
        }

        public string Schema { get; }

        public static async Task<GateRun> StartAsync(IDatabricksGateTransport transport)
        {
            var run = new GateRun(transport, ScratchSchema.Prefix + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            try
            {
                await ScratchSchema.CreateAsync(transport, run.Schema, CancellationToken.None);
                await transport.ExecuteAsync(DatabricksFixture.DefinitionOf(run.Schema), [], CancellationToken.None);
                foreach (var insert in DatabricksFixture.InsertsOf(run.Schema))
                {
                    await transport.ExecuteAsync(insert, [], CancellationToken.None);
                }
            }
            catch
            {
                await run.DisposeAsync();
                throw;
            }

            return run;
        }

        public ValueTask DisposeAsync() => new(ScratchSchema.DropAsync(_transport, Schema, CancellationToken.None));
    }

    private static IReadOnlyList<long> VisibleOrderIds(PolicyScenario scenario) =>
        [.. GateModel.Rows.Where(r => r.Table == "orders" && r.VisibleTo(scenario)).Select(r => r.Id).Order()];

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("main")]
    [InlineData("autheris_g9_")]
    [InlineData("autheris_g9_x")]
    [InlineData("AUTHERIS_G9_1")]
    [InlineData("autheris_g9_1\n")]
    [InlineData("autheris_g9_1; DROP CATALOG main")]
    [InlineData("autheris_g9_1.other")]
    [InlineData("other.autheris_g9_1")]
    [InlineData("autheris_g9_123456789012345678901")]
    public async Task Schema_name_is_validated_before_every_ddl(string name)
    {
        // SEC-OI-609-4: a name outside ^autheris_g9_[0-9]{1,20}$ never produces a statement.
        var transport = new RecordingGateTransport();

        Should.Throw<ArgumentException>(() => ScratchSchema.ValidateScratchSchemaName(name));
        Should.Throw<ArgumentException>(() => ScratchSchema.BuildCreateSchemaSql(name));
        Should.Throw<ArgumentException>(() => ScratchSchema.BuildDropSchemaSql(name));
        await Should.ThrowAsync<ArgumentException>(() => ScratchSchema.CreateAsync(transport, name, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => ScratchSchema.DropAsync(transport, name, CancellationToken.None));
        Should.Throw<ArgumentException>(() => DatabricksFixture.DefinitionOf(name));

        transport.Statements.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("autheris_g9_1")]
    [InlineData("autheris_g9_12345678901234567890")]
    public void Valid_scratch_names_build_only_scoped_ddl(string name)
    {
        ScratchSchema.BuildCreateSchemaSql(name).ShouldBe($"CREATE SCHEMA IF NOT EXISTS {name}");
        string drop = ScratchSchema.BuildDropSchemaSql(name);
        drop.ShouldBe($"DROP SCHEMA IF EXISTS {name} CASCADE");
        drop.ShouldNotContain("CATALOG", Case.Insensitive);
        DatabricksFixture.InsertsOf(name).ShouldAllBe(sql => sql.StartsWith($"INSERT INTO {name}.orders ", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sweep_drops_only_prefixed_schemas_older_than_24h()
    {
        // SEC-OI-609-4, Q-SEC8-3: only autheris_g9_<digits> schemas strictly older than 24 h are dropped.
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var schemas = new (string Name, DateTimeOffset CreatedAtUtc)[]
        {
            ("autheris_g9_1", now.AddHours(-48)),
            ("autheris_g9_2", now.AddHours(-24).AddSeconds(-1)),
            ("autheris_g9_3", now.AddHours(-24)),
            ("autheris_g9_4", now.AddHours(-1)),
            ("main", now.AddDays(-400)),
            ("default", now.AddDays(-400)),
            ("autheris_g9_x", now.AddDays(-400)),
            ("my_autheris_g9_5", now.AddDays(-400)),
            ("autheris_g9_6\n", now.AddDays(-400))
        };

        ScratchSchema.SelectStaleScratchSchemasToDrop(schemas, now).ShouldBe(["autheris_g9_1", "autheris_g9_2"]);

        var transport = new RecordingGateTransport(sql => sql.StartsWith("SELECT schema_name", StringComparison.Ordinal)
            ? [.. schemas.Select(s => new string?[] { s.Name, s.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture) })]
            : []);
        var dropped = await ScratchSchema.SweepAsync(transport, now, CancellationToken.None);

        dropped.ShouldBe(["autheris_g9_1", "autheris_g9_2"]);
        transport.Statements.Skip(1).ShouldBe(
        [
            "DROP SCHEMA IF EXISTS autheris_g9_1 CASCADE",
            "DROP SCHEMA IF EXISTS autheris_g9_2 CASCADE"
        ]);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("DROP TABLE orders")]
    [InlineData("DROP SCHEMA main CASCADE")]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("ALTER TABLE orders ADD COLUMN x INT")]
    [InlineData("TRUNCATE TABLE orders")]
    [InlineData("GRANT SELECT ON orders TO PUBLIC")]
    [InlineData("INSERT INTO orders (id, tenant_id) VALUES (9, 'A')")]
    [InlineData("UPDATE orders SET status = 'x'")]
    [InlineData("DELETE FROM orders")]
    [InlineData("MERGE INTO orders USING customers ON 1 = 1 WHEN MATCHED THEN DELETE")]
    public async Task Ddl_and_dml_are_denied_before_reaching_the_warehouse(string statement)
    {
        // SEC-OI-609-6: the read-only governed request refuses the statement at compile time, so nothing is sent to the warehouse.
        var transport = new RecordingGateTransport();

        var ex = await Should.ThrowAsync<Exception>(() => GovernedAsync(transport, "autheris_g9_1", statement, PolicyScenario.TenantConsentMask));

        ex.ShouldBeAssignableTo<SecurityException>();
        transport.Statements.ShouldBeEmpty("a denied statement must send zero HTTP statements to the warehouse");

        // Positive control: the same path does send a permitted SELECT, so the assertion above is not vacuous.
        await GovernedAsync(transport, "autheris_g9_1", "SELECT id FROM orders", PolicyScenario.TenantConsentMask);
        transport.Statements.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Row_filter_and_mask_are_applied_on_warehouse()
    {
        // SEC-OI-609-6: tenant filter, consent filter and mask are part of every statement the warehouse receives.
        var transport = Transport(out var live);
        using var _ = live;
        await using var run = await GateRun.StartAsync(transport);
        const string sql = "SELECT id, email FROM orders ORDER BY id";

        var (governed, governedRows) = await GovernedAsync(transport, run.Schema, sql, PolicyScenario.TenantConsentMask);
        var (tenantOnly, tenantOnlyRows) = await GovernedAsync(transport, run.Schema, sql, PolicyScenario.Tenant);

        governed.Parameters.ShouldContain(p => p.Origin == ParameterOrigin.Mask && Equals(p.Value, GateModel.MaskValue));
        governed.Parameters.ShouldContain(p => Equals(p.Value, GateModel.TenantA));
        governed.Parameters.ShouldContain(p => Equals(p.Value, GateModel.ConsentValue));

        // Negative control: without the consent and mask policy the compiled statement is different, so the checks above can fail.
        tenantOnly.Parameters.ShouldNotContain(p => p.Origin == ParameterOrigin.Mask);
        tenantOnly.Parameters.ShouldNotContain(p => Equals(p.Value, GateModel.ConsentValue));
        tenantOnly.Sql.ShouldNotBe(governed.Sql);
        tenantOnly.AppliedPredicates.Length.ShouldBeLessThan(governed.AppliedPredicates.Length);

        if (live is null)
        {
            return;
        }

        governedRows.Select(r => long.Parse(r[0]!, CultureInfo.InvariantCulture)).ShouldBe(VisibleOrderIds(PolicyScenario.TenantConsentMask));
        governedRows.ShouldAllBe(r => r[1] == GateModel.MaskValue);
        governedRows.ShouldNotContain(r => r[1]!.Contains("@clear.example", StringComparison.Ordinal));
        tenantOnlyRows.Select(r => long.Parse(r[0]!, CultureInfo.InvariantCulture)).ShouldBe(VisibleOrderIds(PolicyScenario.Tenant));
        tenantOnlyRows.Count.ShouldBeGreaterThan(governedRows.Count, "negative control: the consent filter must remove rows");

        // Negative control: the warehouse itself holds the rows the policy hides.
        var unfiltered = await transport.ExecuteAsync($"SELECT id FROM {run.Schema}.orders", [], CancellationToken.None);
        unfiltered.Count.ShouldBe(GateModel.Rows.Count(r => r.Table == "orders"));
    }

    [Fact]
    public async Task Ephemeral_schema_is_dropped_even_on_failure()
    {
        // SEC-OI-609-4: the run drops its scratch schema when a statement fails mid-run.
        var transport = new RecordingGateTransport(failOn: sql => sql.StartsWith("SELECT", StringComparison.Ordinal));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await using var run = await GateRun.StartAsync(transport);
            await GovernedAsync(transport, run.Schema, "SELECT id FROM orders", PolicyScenario.Tenant);
        });

        ex.Message.ShouldBe("Injected warehouse failure.");
        string created = transport.Statements.First(s => s.StartsWith("CREATE SCHEMA", StringComparison.Ordinal));
        string schema = created["CREATE SCHEMA IF NOT EXISTS ".Length..];
        transport.Statements.Last().ShouldBe($"DROP SCHEMA IF EXISTS {schema} CASCADE");
    }

    [Fact]
    public async Task Ephemeral_schema_is_dropped_when_fixture_setup_fails()
    {
        var transport = new RecordingGateTransport(failOn: sql => sql.StartsWith("INSERT INTO", StringComparison.Ordinal));

        await Should.ThrowAsync<InvalidOperationException>(() => GateRun.StartAsync(transport));

        transport.Statements.Last().ShouldStartWith("DROP SCHEMA IF EXISTS autheris_g9_");
    }

    [Theory]
    [InlineData(PolicyScenario.Tenant)]
    [InlineData(PolicyScenario.TenantConsentMask)]
    public async Task Differential_cases_match_reference(PolicyScenario scenario)
    {
        // The reference is the gate model itself (RowDef.VisibleTo), the same oracle the other dialects are compared with.
        var transport = Transport(out var live);
        using var _ = live;
        await using var run = await GateRun.StartAsync(transport);
        var orders = GateModel.Table("orders");
        int status = orders.IndexOf("status");
        var visible = GateModel.Rows.Where(r => r.Table == "orders" && r.VisibleTo(scenario)).ToList();

        var cases = new (string Sql, IReadOnlyList<string> Expected)[]
        {
            ("SELECT COUNT(*) FROM orders", [visible.Count.ToString(CultureInfo.InvariantCulture)]),
            ("SELECT id FROM orders WHERE status = 'open' ORDER BY id",
                [.. visible.Where(r => (string?)r.Values[status] == "open").Select(r => r.Id).Order().Select(i => i.ToString(CultureInfo.InvariantCulture))]),
            ("SELECT id FROM orders ORDER BY id DESC LIMIT 3",
                [.. visible.Select(r => r.Id).OrderDescending().Take(3).Select(i => i.ToString(CultureInfo.InvariantCulture))])
        };

        foreach (var (sql, expected) in cases)
        {
            var (compiled, rows) = await GovernedAsync(transport, run.Schema, sql, scenario);

            compiled.Dialect.ShouldBe(TargetSqlDialect.Databricks);
            compiled.StatementClass.ShouldBe(SqlStatementClass.Select);
            if (live is not null)
            {
                rows.Select(r => r[0]!).ShouldBe(expected, sql);
            }
        }
    }
}
