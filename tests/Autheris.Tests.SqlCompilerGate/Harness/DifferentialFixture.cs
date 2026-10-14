namespace Autheris.Tests.SqlCompilerGate.Harness;

using System.Text.Json;
using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Databases;
using Autheris.Tests.SqlCompilerGate.Model;

/// <summary>The classified result of one statement in one scenario.</summary>
public sealed record StatementResult(
    CorpusStatement Statement,
    PolicyScenario Scenario,
    EngineOutcome Legacy,
    EngineOutcome Ast,
    Classification Classification,
    IReadOnlyList<OracleViolation> LegacyViolations);

/// <summary>
/// One database plus the lazily computed differential over the whole corpus. Computed once per dialect and shared by the tests of
/// the class, so the corpus runs exactly once.
/// </summary>
public sealed class DifferentialFixture<TDatabase> : IAsyncLifetime
    where TDatabase : GateDatabase, new()
{
    private readonly Lazy<Task<IReadOnlyList<StatementResult>>> _results;

    public DifferentialFixture()
    {
        _results = new Lazy<Task<IReadOnlyList<StatementResult>>>(ComputeAsync);
    }

    public TDatabase Database { get; } = new();

    /// <summary>One entry per scenario whose AST leg is unavailable (empty when every scenario ran).</summary>
    public IReadOnlyList<string> AstUnavailable { get; private set; } = [];

    /// <summary>Corpus statements (per scenario) that have no frozen legacy result; a non-empty list fails the gate.</summary>
    public IReadOnlyList<string> MissingFrozen { get; private set; } = [];

    /// <summary>The recorded legacy results of the database's dialect (see <see cref="FrozenLegacy"/>).</summary>
    public FrozenLegacy Frozen => _frozen ??= FrozenLegacy.Load(Database.Dialect);

    private FrozenLegacy? _frozen;

    public Task InitializeAsync() => GateProfile.DifferentialEnabled ? Database.InitializeAsync() : Task.CompletedTask;

    public Task DisposeAsync() => GateProfile.DifferentialEnabled ? Database.DisposeAsync() : Task.CompletedTask;

    public Task<IReadOnlyList<StatementResult>> GetResultsAsync() => _results.Value;

    private async Task<IReadOnlyList<StatementResult>> ComputeAsync()
    {
        {
            var results = new List<StatementResult>();
            var unavailable = new List<string>();
            var missing = new List<string>();
            foreach (var scenario in Enum.GetValues<PolicyScenario>())
            {
                var runner = new GateRunner(Database, scenario);
                if (runner.AstUnavailableReason() is { } reason)
                {
                    unavailable.Add($"{scenario}: {reason}");
                    continue;
                }

                var oracle = new PolicyOracle(scenario);
                foreach (var statement in GateProfile.Corpus())
                {
                    if (!Frozen.TryGet(statement.Id, scenario, out var legacy))
                    {
                        missing.Add($"{statement.Id}/{scenario}");
                        continue;
                    }

                    var ast = await runner.RunAsync(EngineKind.Ast, statement);
                    var legacyViolations = legacy switch
                    {
                        EngineOutcome.Rows r => oracle.CheckRows(r.Data),
                        EngineOutcome.Written w => oracle.CheckWrite(w.Changes),
                        _ => []
                    };
                    results.Add(new StatementResult(statement, scenario, legacy, ast, Classifier.Classify(legacy, ast, oracle), legacyViolations));
                }
            }

            AstUnavailable = unavailable;
            MissingFrozen = missing;
            WriteReport(results);
            if (Environment.GetEnvironmentVariable("SQLGATE_UPDATE_ACCEPTED") is { Length: > 0 } acceptedPath)
            {
                AcceptedDifferencesWriter.Merge(acceptedPath, Database.Dialect, results);
            }

            return results;
        }
    }

    private void WriteReport(IReadOnlyList<StatementResult> results)
    {
        string? directory = Environment.GetEnvironmentVariable("SQLGATE_REPORT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var rows = results.Select(r => new
        {
            id = r.Statement.Id,
            dialect = Database.Dialect.ToString(),
            scenario = r.Scenario.ToString(),
            @class = r.Classification.Class.ToWireName(),
            reason = r.Classification.Reason,
            statementClass = r.Statement.Class,
            expected = r.Statement.Expected,
            source = r.Statement.Source,
            sql = r.Statement.Sql,
            legacy = GateProfile.Describe(r.Legacy),
            ast = GateProfile.Describe(r.Ast),
            astViolations = r.Classification.AstViolations.Select(v => v.Kind.ToString() + ":" + v.Table).Distinct().ToArray(),
            legacyViolations = r.LegacyViolations.Select(v => v.Kind.ToString() + ":" + v.Table).Distinct().ToArray()
        });
        File.WriteAllText(Path.Combine(directory, $"gate-report-{Database.Dialect}.json"),
            JsonSerializer.Serialize(rows, GateProfile.ReportOptions));
    }
}

/// <summary>Run profile and report helpers shared by the differential tests.</summary>
public static class GateProfile
{
    public static JsonSerializerOptions ReportOptions { get; } = new() { WriteIndented = true };

    /// <summary>
    /// <c>SQLGATE_PROFILE</c>: <c>pr</c> (the full corpus), <c>nightly</c>, <c>cutover</c> and <c>smoke</c> (a sample) run the database
    /// differential; unset (a plain <c>dotnet test Autheris.sln</c>) runs only the database-free checks, because the differential needs
    /// containers and takes minutes. The <c>sql-compiler-gate</c> job sets <c>pr</c> (WP-X0w).
    /// </summary>
    public static string Name => Environment.GetEnvironmentVariable("SQLGATE_PROFILE") ?? string.Empty;

    public static bool DifferentialEnabled => Name.Length > 0;

    /// <summary>The corpus statements the profile runs (<c>SQLGATE_PROFILE=smoke</c> runs a sample; every other profile runs all).</summary>
    public static IReadOnlyList<CorpusStatement> Corpus()
    {
        var all = CorpusLoader.LoadAll();
        return string.Equals(Name, "smoke", StringComparison.OrdinalIgnoreCase) ? [.. all.Where((_, i) => i % 25 == 0)] : all;
    }

    /// <summary>A missing database fails on CI (and when <c>SQLGATE_REQUIRE_DATABASES=1</c>) and is an early return locally.</summary>
    public static bool DatabasesRequired =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable("SQLGATE_REQUIRE_DATABASES") == "1";

    public static string Describe(EngineOutcome outcome) => outcome switch
    {
        EngineOutcome.Rejected r => $"rejected:{r.Stage}:{r.Reason}",
        EngineOutcome.Rows r => $"rows:{r.Data.Count}",
        EngineOutcome.Written w => $"written:{w.Changes.Count}",
        _ => "unknown"
    };
}

/// <summary>The database-free rules the differential tests apply to the fixture result.</summary>
public static class DifferentialRules
{
    /// <summary>
    /// The AST leg is the subject of the gate, and every dialect has a binder: when the database is available and the AST compiler
    /// cannot run a scenario, the gate fails instead of reporting an empty (green) differential.
    /// </summary>
    public static void RequireAstLeg(string dialect, IReadOnlyList<string> unavailableScenarios)
    {
        if (unavailableScenarios.Count > 0)
        {
            Assert.Fail($"The AST leg is unavailable on {dialect}: {string.Join("; ", unavailableScenarios)}");
        }
    }

    /// <summary>A corpus statement without a frozen legacy result cannot be classified: the gate fails instead of comparing with nothing.</summary>
    public static void RequireFrozenLegacy(string dialect, IReadOnlyList<string> missing)
    {
        if (missing.Count == 0) return;
        Assert.Fail($"{missing.Count} corpus statements on {dialect} have no frozen legacy result (golden-results/legacy-{dialect}.json is not regenerated; " +
                    $"a new statement needs a documented decision). First: {string.Join(", ", missing.Take(10))}");
    }

    /// <summary>A confirmed legacy defect is skipped only while the AST path has zero oracle violations; otherwise it stays red.</summary>
    public static bool IsSkippedLegacyDefect(bool isLegacyDefect, int astViolationCount) => isLegacyDefect && astViolationCount == 0;
}
