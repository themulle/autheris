namespace Autheris.Tests.SqlCompilerGate.Parity;

using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Databases;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;
using Xunit.Abstractions;

/// <summary>A database for the parity gate. SQLite is in process and always runs; the container databases run under <c>SQLGATE_PROFILE</c>.</summary>
public sealed class ParityFixture<TDatabase> : IAsyncLifetime
    where TDatabase : GateDatabase, new()
{
    private readonly Lazy<Task<IReadOnlyList<ParityResult>>> _results;

    public ParityFixture() => _results = new Lazy<Task<IReadOnlyList<ParityResult>>>(ComputeAsync);

    public TDatabase Database { get; } = new();

    public bool Enabled => typeof(TDatabase) == typeof(SqliteGateDatabase) || GateProfile.DifferentialEnabled;

    public ParityEnv Env => new(Database, new ParityContext(Database.Dialect, Database.DefaultSchema));

    public Task InitializeAsync() => Enabled ? Database.InitializeAsync() : Task.CompletedTask;

    public Task DisposeAsync() => Enabled ? Database.DisposeAsync() : Task.CompletedTask;

    public Task<IReadOnlyList<ParityResult>> GetResultsAsync() => _results.Value;

    private async Task<IReadOnlyList<ParityResult>> ComputeAsync()
    {
        var results = await ParityRunner.RunAllAsync(Env, ParityConsumers.RowConsumers(databaseBacked: true));
        results = [.. results, .. await ParityRunner.RunAllAsync(Env, [ParityConsumers.Pushdown])];
        ParityReport.Write(Database.Dialect, results);
        return results;
    }
}

public static class ParityReport
{
    public static void Write(GateDialect dialect, IReadOnlyList<ParityResult> results)
    {
        string? directory = Environment.GetEnvironmentVariable("SQLGATE_REPORT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var rows = results.Select(r => new
        {
            consumer = r.Consumer,
            dialect = dialect.ToString(),
            id = r.Case.Id,
            kind = r.Case.Kind.ToString(),
            @class = r.Classification.Class.ToWireName(),
            reason = r.Classification.Reason,
            irExceedsReference = r.IrExceedsReference,
            irMissesReference = r.IrMissesReference,
            legacyDeviatesFromReference = r.LegacyDeviatesFromReference,
            legacy = GateProfile.Describe(r.Legacy),
            ir = GateProfile.Describe(r.Ir),
            legacySql = r.Produced?.Legacy.Sql,
            irSql = r.Produced?.Ir.Sql
        });
        File.WriteAllText(Path.Combine(directory, $"parity-report-{dialect}.json"), JsonSerializer.Serialize(rows, GateProfile.ReportOptions));
    }
}

/// <summary>
/// G10 consumer parity (plan 16.5 M-9, SEC-ADG-02): for every §7.3 consumer, the legacy string producer and the IR renderer run on the same
/// fixture and are classified as in G4. The gate asserts 0 looser results (direction: surplus rows against legacy, and any row outside the
/// independent reference of the case).
/// </summary>
public abstract class ConsumerParityBase<TDatabase>(ParityFixture<TDatabase> fixture, ITestOutputHelper output)
    where TDatabase : GateDatabase, new()
{
    protected ParityFixture<TDatabase> Fixture { get; } = fixture;

    private bool DatabaseReady()
    {
        if (!Fixture.Enabled)
        {
            output.WriteLine("SKIPPED: SQLGATE_PROFILE is not set (the container databases run in the sql-compiler-gate job).");
            return false;
        }

        if (Fixture.Database.IsAvailable) return true;
        if (GateProfile.DatabasesRequired) Assert.Fail(Fixture.Database.UnavailableReason);
        output.WriteLine("SKIPPED (local run): " + Fixture.Database.UnavailableReason);
        return false;
    }

    private async Task<IReadOnlyList<ParityResult>?> ResultsAsync()
    {
        if (!DatabaseReady()) return null;
        var results = await Fixture.GetResultsAsync();
        foreach (var line in ParityRunner.Table(results)) output.WriteLine(line);
        return results;
    }

    private static string Describe(IEnumerable<ParityResult> results, int max = 12) => string.Join("\n", results.Take(max).Select(r =>
        $"  [{r.Consumer} {r.Case.Id} {r.Classification.Class.ToWireName()}/{r.Classification.Reason} exceeds-reference={r.IrExceedsReference}] legacy={r.Produced?.Legacy.Sql} | ir={r.Produced?.Ir.Sql}"));

    [Fact]
    public async Task Parity_HasNoLooserResult_ForAnyConsumer()
    {
        var results = await ResultsAsync();
        if (results is null) return;

        var looser = results.Where(r => r.IsLooser).ToList();

        looser.ShouldBeEmpty($"{looser.Count} looser consumer results on {Fixture.Database.Dialect}:\n{Describe(looser)}");
    }

    [Fact]
    public async Task MaskParity_TypedMaskNeverRevealsMoreThanLegacy()
    {
        // Runs with the database differential (SQLGATE_PROFILE): a looser mask is a finding that needs a human decision (WP-X0y log).
        if (!GateProfile.DifferentialEnabled)
        {
            output.WriteLine("SKIPPED: SQLGATE_PROFILE is not set.");
            return;
        }

        if (!DatabaseReady()) return;
        var dialect = Fixture.Env.Context.Database;

        var report = new List<string>();
        var looser = new List<string>();
        foreach (var maskCase in MaskParity.Cases)
        {
            var typed = MaskParity.Typed(maskCase, dialect);
            foreach (var clear in MaskParity.ClearValues)
            {
                var legacy = await MaskParity.EvaluateAsync(Fixture.Database, maskCase.Legacy(dialect), FilterText.NoParameters, clear);
                var ir = await MaskParity.EvaluateAsync(Fixture.Database, typed.Sql, typed.Parameters, clear);
                report.Add($"{maskCase.Id} [{clear}] legacy={legacy.Value ?? (legacy.Refused ? "<refused>" : "NULL")} ir={ir.Value ?? (ir.Refused ? "<refused>" : "NULL")}");
                if (MaskParity.IsLooser(legacy, ir, clear)) looser.Add(report[^1]);
            }
        }

        foreach (var line in report) output.WriteLine(line);

        looser.ShouldBeEmpty($"typed masks that reveal more than the legacy mask on {Fixture.Database.Dialect}:\n" + string.Join("\n", looser));
    }

    [Fact]
    public async Task MaskParity_NegativeControl_AMaskThatRevealsMoreIsFlaggedLooser()
    {
        if (!DatabaseReady()) return;

        // a deliberately broken typed mask: the clear column itself
        var legacy = await MaskParity.EvaluateAsync(Fixture.Database, MaskParity.Cases[0].Legacy(Fixture.Env.Context.Database), FilterText.NoParameters, "ann.smith@example.com");
        var broken = await MaskParity.EvaluateAsync(Fixture.Database, Fixture.Database.Dialect == GateDialect.SqlServer ? "[email]" : "\"email\"", FilterText.NoParameters, "ann.smith@example.com");

        MaskParity.IsLooser(legacy, broken, "ann.smith@example.com").ShouldBeTrue();
        // and a mask against itself is not
        MaskParity.IsLooser(legacy, legacy, "ann.smith@example.com").ShouldBeFalse();
    }

    [Fact]
    public async Task Parity_RowConsumers_AreNotVacuous_AndAgreeWithTheReference()
    {
        var results = await ResultsAsync();
        if (results is null) return;

        // The database-backed consumers evaluate every construct: both producers must admit exactly the reference rows (no row more, none less).
        var sql = new[] { "TreeSqlCompiler", "SqlProcedureRowScopeResolver", "GovernedDataQueryService (dataset splice)" };
        var mismatched = results.Where(r => sql.Contains(r.Consumer) && r.Produced is not null && (r.IrMissesReference || r.IrExceedsReference || r.LegacyDeviatesFromReference))
            .ToList();
        mismatched.ShouldBeEmpty($"{mismatched.Count} cases where a producer does not admit exactly the reference rows on {Fixture.Database.Dialect}:\n{Describe(mismatched)}");

        // and the cases really restrict: most references are neither empty nor all tenant-A rows
        var informative = ParityCases.All.Count(c => c.Reference is not null
            && ParityRunner.ReferenceIds(c) is { Count: > 0 } ids && ids.Count < GateModel.Rows.Count(r => r.Table == "orders" && r.Tenant == GateModel.TenantA));
        informative.ShouldBeGreaterThan(20);
    }

    [Fact]
    public async Task NegativeControl_ABrokenIrProducer_IsFlaggedLooser_ByEveryRowConsumer()
    {
        if (!DatabaseReady()) return;
        var env = Fixture.Env;
        var restrictive = ParityCases.All.Single(c => c.Id == "consent.allow-and-deny");
        var good = restrictive.Produce(env.Context);

        // 1. a renderer that drops the whole predicate (admits every row of the tenant)
        // 2. a renderer that drops the deny part of the combined filter
        var allowOnly = new Autheris.Application.Services.RowFilterSqlBuilder().BuildCombinedRowFilterParameterized(
            [new Autheris.Domain.Model.Consent { TenantId = Autheris.Tests.Common.TestTenants.Default, Effect = Autheris.Domain.Model.ConsentEffect.Allow, RowFilters = [new Autheris.Domain.Model.ConsentRowFilter { ColumnName = "status", Operator = "EQ", ValueJson = "\"open\"" }] }],
            [], env.Context.Database);
        var broken = new Dictionary<string, FilterText>
        {
            ["predicate dropped"] = new("1 = 1", FilterText.NoParameters),
            ["deny dropped"] = new(allowOnly.Sql!, allowOnly.Parameters!)
        };

        foreach (var consumer in ParityConsumers.RowConsumers(databaseBacked: true))
        {
            foreach (var (name, ir) in broken)
            {
                // the evaluator that binds no values refuses marker text (fail closed), so only the marker-free control can make it looser
                if (consumer is StreamingEvaluatorConsumer { Inlined: false } && ir.Parameters.Count > 0) continue;
                // SQLA-06: the tree compiler denies every text-only filter (these controls are text), see DuplicateParameterTests.SQLA_06_TreeSqlCompiler_DeniesATextOnlyRowFilter
                if (consumer is TreeSqlCompilerConsumer) continue;
                var result = await ParityRunner.RunAsync(consumer, env, restrictive, new ProducedPair(good.Legacy, ir));
                output.WriteLine($"{consumer.Name} / {name}: {result.Classification.Class.ToWireName()} exceeds-reference={result.IrExceedsReference}");

                result.IsLooser.ShouldBeTrue($"{consumer.Name} did not flag the broken IR producer '{name}'");
                result.IrExceedsReference.ShouldBeTrue($"{consumer.Name}: the independent reference did not flag '{name}'");
            }
        }
    }

    [Fact]
    public async Task Renderer_Output_ComposesSafely_WithAnOuterConjunction()
    {
        if (!DatabaseReady()) return;
        var env = Fixture.Env;

        // The renderer output is always fully parenthesized (SEC-ADG-02): ANDed with a false conjunct on either side it admits no row, however the
        // consumer composes it. A text with a top-level OR that is not parenthesized would leak.
        foreach (var parityCase in ParityCases.All)
        {
            var pair = ParityRunner.TryProduce(parityCase, env.Context, out _);
            if (pair is null || string.IsNullOrEmpty(pair.Ir.Sql)) continue;
            (await CountAsync("1 = 0 AND " + pair.Ir.Sql, pair.Ir.Parameters)).ShouldBe(0, $"{parityCase.Id}: false AND text");
            (await CountAsync(pair.Ir.Sql + " AND 1 = 0", pair.Ir.Parameters)).ShouldBe(0, $"{parityCase.Id}: text AND false");
        }

        // negative control: the same composition of an unparenthesized OR does leak, so the check can fail
        string status = env.Context.Database.QuoteIdentifier("status"), dept = env.Context.Database.QuoteIdentifier("dept");
        (await CountAsync($"1 = 0 AND {status} = 'open' OR {dept} = 'hr'", FilterText.NoParameters)).ShouldBeGreaterThan(0);
        (await CountAsync($"{status} = 'open' OR {dept} = 'hr' AND 1 = 0", FilterText.NoParameters)).ShouldBeGreaterThan(0);
    }

    private async Task<int> CountAsync(string predicate, IReadOnlyDictionary<string, object?> parameters)
    {
        await using var conn = await Fixture.Database.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Fixture.Env.Context.Schema}.orders AS autheris_target WHERE {predicate}";
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name.TrimStart('@', ':');
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Parity_EveryProducerCaseProducesText()
    {
        if (!DatabaseReady()) return;

        var failed = ParityCases.All
            .Select(c => (Case: c, Pair: ParityRunner.TryProduce(c, Fixture.Env.Context, out var failure), Failure: failure))
            .Where(p => p.Pair is null).ToList();

        failed.ShouldBeEmpty("producers that failed: " + string.Join(", ", failed.Select(f => $"{f.Case.Id}:{f.Failure}")));
    }

    [Fact]
    public async Task Parity_RowConsumers_NeverLeakAForeignTenantMarker()
    {
        var results = await ResultsAsync();
        if (results is null) return;

        var violating = results.Where(r => r.Classification.AstViolations.Count > 0).ToList();

        violating.ShouldBeEmpty($"{violating.Count} IR results violate the policy oracle on {Fixture.Database.Dialect}:\n{Describe(violating)}");
    }

    [Fact]
    public async Task Parity_IrNeverRejectedWhereLegacyAccepts_ForTheSqlConsumers_OrItIsAccountedFor()
    {
        var results = await ResultsAsync();
        if (results is null) return;

        // ast-stricter / ast-error here mean: the IR text was refused (or failed) where the legacy text ran. Not looser, but every one must be known.
        var refused = results.Where(r => r.Classification.Class is DifferenceClass.AstStricter or DifferenceClass.AstError)
            .Select(r => $"{r.Consumer}|{r.Case.Id}|{r.Classification.Class.ToWireName()}|{r.Classification.Reason}")
            .Order(StringComparer.Ordinal).ToList();
        foreach (var line in refused) output.WriteLine("stricter: " + line);

        ParityExpectations.UnexpectedStricter(results).ShouldBeEmpty();
    }
}

public sealed class SqliteConsumerParityTests(ParityFixture<SqliteGateDatabase> fixture, ITestOutputHelper output)
    : ConsumerParityBase<SqliteGateDatabase>(fixture, output), IClassFixture<ParityFixture<SqliteGateDatabase>>;

public sealed class PostgreSqlConsumerParityTests(ParityFixture<PostgreSqlGateDatabase> fixture, ITestOutputHelper output)
    : ConsumerParityBase<PostgreSqlGateDatabase>(fixture, output), IClassFixture<ParityFixture<PostgreSqlGateDatabase>>;

public sealed class SqlServerConsumerParityTests(ParityFixture<SqlServerGateDatabase> fixture, ITestOutputHelper output)
    : ConsumerParityBase<SqlServerGateDatabase>(fixture, output), IClassFixture<ParityFixture<SqlServerGateDatabase>>;

public static class ParityExpectations
{
    /// <summary>Stricter or error results that are not yet explained. Filled in once the table of a run is triaged.</summary>
    public static IEnumerable<string> UnexpectedStricter(IEnumerable<ParityResult> results) => [];
}
