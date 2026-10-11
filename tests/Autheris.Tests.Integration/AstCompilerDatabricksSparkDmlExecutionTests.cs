namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// The Spark proxy with Delta Lake for the DML work (WP-A7): UPDATE, DELETE and MERGE need a transactional table format, which the
/// plain pinned Spark image does not have. The Delta jars are baked into an image derived from the pinned image
/// (<c>Spark/Dockerfile</c>, SHA-256-verified at image build time, never resolved at run time, SEC-ADG-24).
/// </summary>
public sealed class SparkDeltaProxyFixture : SparkProxyFixture
{
    public const string DerivedImage = "autheris-spark-delta-proxy:4.0.0-1";

    protected override async Task<string> PrepareImageAsync()
    {
        if (await RunDockerAsync("image", "inspect", DerivedImage) != 0)
        {
            string context = Path.Combine(AppContext.BaseDirectory, "Spark");
            int exit = await RunDockerAsync("build", "-t", DerivedImage, context);
            if (exit != 0)
            {
                throw new InvalidOperationException("The Delta Lake proxy image could not be built (docker build exit code " + exit + ").");
            }
        }

        return DerivedImage;
    }

    protected override IEnumerable<string> ExtraRunArguments => new[] { "-e", "AUTHERIS_SPARK_DELTA=1" };

    // The Delta tests create their own schema and tables per test.
    protected override Task SeedAsync() => Task.CompletedTask;

    private static async Task<int> RunDockerAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }
}

/// <summary>
/// Databricks DML on Delta (the Spark proxy): the shared <see cref="AstCompilerDmlContract"/>. Databricks stays Experimental until
/// the first green G9 run (CR-ADG-03); the proxy proves syntax, named markers and ANSI semantics, not Unity Catalog or Photon (R-7).
/// </summary>
public sealed class AstCompilerDatabricksSparkDmlExecutionTests : AstCompilerDmlContract, IClassFixture<SparkDeltaProxyFixture>, IAsyncLifetime
{
    private const string CatalogName = "spark_catalog";
    private readonly SparkDeltaProxyFixture _spark;
    private readonly string _schema = "dml_" + Guid.NewGuid().ToString("N")[..12];

    public AstCompilerDatabricksSparkDmlExecutionTests(SparkDeltaProxyFixture spark) => _spark = spark;

    protected override TargetSqlDialect Dialect => TargetSqlDialect.Databricks;
    protected override bool AllowExperimental => true;
    protected override bool HasUniqueKey => false;          // Delta has no enforced unique key
    protected override bool ReportsInsertCount => false;    // Delta INSERT returns an empty result
    protected override bool SupportsSubqueryInDmlWhere => false;   // open-source Delta: DELTA_UNSUPPORTED_SUBQUERY
    protected override TableIdentity OrdersId => new(_schema, "orders", CatalogName);
    protected override TableIdentity EntitlementsId => new(_schema, "entitlements", CatalogName);
    protected override string Canon(string logical) => logical.ToLowerInvariant();
    protected override string Quote(string identifier) => "`" + identifier + "`";
    protected override string RawTable(string logical) => $"{_schema}.{logical.ToLowerInvariant()}";
    protected override string UserTable(string logical) => $"{_schema}.{logical.ToLowerInvariant()}";

    protected override bool Available()
    {
        // CR-ADG-11: absence of the pinned base image is a reported skip locally and a failure on CI; a proxy that does not start fails.
        if (SparkAvailability.SkipReason is { } reason) throw new SparkSkipException(reason);
        if (!_spark.IsAvailable) throw new InvalidOperationException("The Spark Delta proxy did not start: " + _spark.StartupError);
        return true;
    }

    public async Task InitializeAsync()
    {
        if (SparkAvailability.SkipReason is not null) return;   // the tests report the skip
        if (!Available()) return;
        await DdlAsync($"CREATE SCHEMA {_schema}");
        await DdlAsync($"CREATE TABLE {_schema}.orders (id INT, tenantid STRING, region STRING, status STRING, amount DECIMAL(18,2), email STRING) USING delta");
        await DdlAsync($"CREATE TABLE {_schema}.entitlements (id INT, tenantid STRING, orderid INT) USING delta");
        string orders = string.Join(", ", OrderRows.Select(r =>
            $"({r.Id}, '{r.Tenant}', '{r.Region}', '{r.Status}', {r.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {(r.Email is null ? "NULL" : $"'{r.Email}'")})"));
        await DdlAsync($"INSERT INTO {_schema}.orders VALUES {orders}");
        string entitlements = string.Join(", ", EntitlementRows.Select(r => $"({r.Id}, '{r.Tenant}', {r.OrderId})"));
        await DdlAsync($"INSERT INTO {_schema}.entitlements VALUES {entitlements}");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task DdlAsync(string sql)
    {
        var response = await _spark.SendAsync(new JsonObject { ["op"] = "ddl", ["sql"] = sql });
        if (response?["ok"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException("Spark fixture statement failed: " + response?["error"] + " | " + sql);
        }
    }

    protected override InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(OrdersId, ImmutableArray.Create(
            new CatalogColumn("id", "INT"), new CatalogColumn("tenantid", "STRING", "UTF8_BINARY"), new CatalogColumn("region", "STRING"),
            new CatalogColumn("status", "STRING"), new CatalogColumn("amount", "DECIMAL(18,2)"), new CatalogColumn("email", "STRING")),
            "tenantid", 1),
        new TableCatalogEntry(EntitlementsId, ImmutableArray.Create(
            new CatalogColumn("id", "INT"), new CatalogColumn("tenantid", "STRING", "UTF8_BINARY"), new CatalogColumn("orderid", "INT")),
            "tenantid", 1),
        new TableCatalogEntry(new TableIdentity(_schema, "ordersci", CatalogName), ImmutableArray.Create(
            new CatalogColumn("id", "INT"), new CatalogColumn("tenantid", "STRING", "UTF8_LCASE"), new CatalogColumn("status", "STRING")),
            "tenantid", 1)
    }, _schema);

    // Delta reports no INSERT row count, so the compiler never produces a check-option statement for it (CR-ADG-35).
    protected override Task<int> ExecuteInTransactionAsync(CompiledSql compiled) =>
        throw new NotSupportedException("Delta has no row-count check option.");

    protected override async Task<int> ExecuteAsync(CompiledSql compiled)
    {
        var response = await _spark.SendAsync(new JsonObject
        {
            ["op"] = "query",
            ["sql"] = compiled.Sql,
            ["params"] = AstCompilerDatabricksSparkExecutionTests.Parameters(compiled, new Dictionary<string, object?>())
        });
        if (response?["ok"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException("Spark rejected the statement: " + response?["error"] + " | SQL: " + compiled.Sql);
        }

        var columns = response["columns"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        int index = columns.IndexOf("num_affected_rows");
        var rows = response["rows"]!.AsArray();
        return index < 0 || rows.Count == 0 ? -1 : rows[0]!.AsArray()[index]!.GetValue<int>();
    }

    protected override async Task<List<object?[]>> QueryAsync(string sql)
    {
        var response = await _spark.SendAsync(new JsonObject { ["op"] = "query", ["sql"] = sql, ["params"] = new JsonArray() });
        if (response?["ok"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException("Spark rejected the query: " + response?["error"] + " | " + sql);
        }

        return response["rows"]!.AsArray()
            .Select(row => row!.AsArray().Select(ToClr).ToArray())
            .ToList();
    }

    private static object? ToClr(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<bool>(out var b)) return b;
        return value.TryGetValue<string>(out var s) ? s : value.ToString();
    }

    // ---- Delta specifics ----

    [Fact]
    public async Task CaseInsensitiveCollatedTenantColumn_DmlStaysExact()
    {
        if (!Available()) return;
        // B-1 on Delta: the catalog says UTF8_LCASE, so the compiler keeps the binary comparison (no plain equality).
        await DdlAsync($"CREATE TABLE {_schema}.ordersci (id INT, tenantid STRING COLLATE UTF8_LCASE, status STRING) USING delta");
        await DdlAsync($"INSERT INTO {_schema}.ordersci VALUES (1, 'acme', 'o'), (2, 'ACME', 'o'), (3, 'other', 'o')");
        var probe = await QueryAsync($"SELECT count(*) FROM {_schema}.ordersci WHERE tenantid = 'acme'");
        Convert.ToInt32(probe[0][0]).ShouldBe(2);   // the plain equality would have matched the case variant
        (await ExecAsync($"UPDATE {_schema}.ordersci SET status = 'x' WHERE id > 0", "acme")).ShouldBe(1);
        (await ExecAsync($"DELETE FROM {_schema}.ordersci WHERE id > 0", "ACME")).ShouldBe(1);
        var rows = await QueryAsync($"SELECT id, status FROM {_schema}.ordersci ORDER BY id");
        rows.Select(r => $"{r[0]}:{r[1]}").ShouldBe(new[] { "1:x", "3:o" });
    }

    [Fact]
    public async Task Merge_WithTwoSourceRowsForOneTargetRow_FailsWithTheTypedMergeError()
    {
        if (!Available()) return;
        // two acme entitlements point at order 1: Delta refuses the ambiguous MERGE
        await DdlAsync($"INSERT INTO {_schema}.entitlements VALUES (9, 'acme', 1)");
        var before = await SnapshotAsync();
        var raw = await Should.ThrowAsync<InvalidOperationException>(() => ExecAsync(
            $"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'dup'", "acme"));
        var mapped = DmlErrorSanitizer.TryMap(TargetSqlDialect.Databricks, raw);
        mapped.ShouldNotBeNull();
        mapped.Kind.ShouldBe(DmlConstraintKind.MergeMultipleMatches);
        mapped.Message.ShouldBe("The statement violated a data constraint.");
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Update_OfANonDeltaTable_IsABackendError_NotAnUnsafeWrite()
    {
        if (!Available()) return;
        // The base proxy tables are parquet; Spark refuses UPDATE on them. The compiler cannot know the format, so the backend error is
        // the only protection and nothing is written.
        await DdlAsync($"CREATE TABLE {_schema}.plain (id INT, tenantid STRING, status STRING) USING parquet");
        await DdlAsync($"INSERT INTO {_schema}.plain VALUES (1, 'acme', 'o')");
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(new TableIdentity(_schema, "plain", CatalogName),
                ImmutableArray.Create(new CatalogColumn("id", "INT"), new CatalogColumn("tenantid", "STRING", "UTF8_BINARY"), new CatalogColumn("status", "STRING")), "tenantid", 1)
        }, _schema);
        var request = Request("acme") with { Policy = Request("acme").Policy with { Catalog = catalog } };
        var compiled = Engine.Compile($"UPDATE {_schema}.plain SET status = 'x' WHERE id > 0".AsMemory(), request, CancellationToken.None);
        await Should.ThrowAsync<InvalidOperationException>(() => ExecuteAsync(compiled));
        var rows = await QueryAsync($"SELECT status FROM {_schema}.plain");
        rows.Single()[0].ShouldBe("o");
    }
}
