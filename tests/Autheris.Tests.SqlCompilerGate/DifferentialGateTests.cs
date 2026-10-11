namespace Autheris.Tests.SqlCompilerGate;

using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Databases;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;
using Xunit.Abstractions;

/// <summary>
/// G4: every corpus statement is compiled by legacy (<c>RewriteRls</c>, <c>LegacyTokenStream</c>) and by the AST compiler
/// (<c>Compile</c>), executed on the same fixture and classified directionally. The gate asserts 0 <c>ast-looser-*</c>, 0 oracle
/// violations on the AST path (M-2), and that every <c>ast-stricter</c>/<c>ast-error</c> is listed in <c>accepted-differences.json</c>.
/// </summary>
public abstract class DifferentialGateBase<TDatabase>(DifferentialFixture<TDatabase> fixture, ITestOutputHelper output)
    where TDatabase : GateDatabase, new()
{
    protected DifferentialFixture<TDatabase> Fixture { get; } = fixture;

    /// <summary>False when the database is missing: fails on CI, returns early locally.</summary>
    private bool DatabaseReady()
    {
        if (!GateProfile.DifferentialEnabled)
        {
            output.WriteLine("SKIPPED: SQLGATE_PROFILE is not set (the database differential runs in the sql-compiler-gate job).");
            return false;
        }

        if (Fixture.Database.IsAvailable) return true;
        if (GateProfile.DatabasesRequired) Assert.Fail(Fixture.Database.UnavailableReason);
        output.WriteLine("SKIPPED (local run): " + Fixture.Database.UnavailableReason);
        return false;
    }

    /// <summary>The AST leg exists only once the compiler lists the dialect with a binder. Reported, never silent.</summary>
    private async Task<IReadOnlyList<StatementResult>?> ResultsAsync()
    {
        if (!DatabaseReady()) return null;
        var results = await Fixture.GetResultsAsync();
        if (!Fixture.AstSupported)
        {
            output.WriteLine($"AST LEG SKIPPED: the AST compiler does not list {Fixture.Database.Dialect} yet (WP-X0h for SQLite).");
            return null;
        }

        var counts = results.GroupBy(r => r.Classification.Class.ToWireName()).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}");
        output.WriteLine($"{Fixture.Database.Dialect} ({results.Count} results): " + string.Join(", ", counts));
        return results;
    }

    private static string Describe(IEnumerable<StatementResult> results, int max = 15) =>
        string.Join("\n", results.Take(max).Select(r =>
            $"  [{r.Statement.Id} {r.Scenario} {r.Classification.Class.ToWireName()}/{r.Classification.Reason}] {Truncate(r.Statement.Sql)}"));

    private static string Truncate(string sql)
    {
        string flat = sql.ReplaceLineEndings(" ");
        return flat.Length <= 160 ? flat : flat[..160] + "...";
    }

    [Fact]
    public async Task Fixture_HoldsBothTenantsInEveryTable()
    {
        if (!DatabaseReady()) return;

        var snapshot = await Fixture.Database.SnapshotAsync();

        foreach (var table in GateModel.Tables)
        {
            snapshot[table.Name].Count.ShouldBe(GateModel.Rows.Count(r => r.Table == table.Name));
            int tenantIndex = table.IndexOf(GateModel.TenantColumn);
            snapshot[table.Name].Select(r => (string?)r[tenantIndex]).Distinct().Order().ShouldBe([GateModel.TenantA, GateModel.TenantB]);
        }
    }

    [Fact]
    public async Task NegativeControl_AnUnfilteredEngine_IsFlaggedAsLooser_OnRowsMaskAndWrite()
    {
        if (!DatabaseReady()) return;
        var tenant = new GateRunner(Fixture.Database, PolicyScenario.Tenant);
        var masked = new GateRunner(Fixture.Database, PolicyScenario.TenantConsentMask);
        var tenantOracle = new PolicyOracle(PolicyScenario.Tenant);
        var maskOracle = new PolicyOracle(PolicyScenario.TenantConsentMask);

        // (Oracle's legacy path is not executable, so the class there is ast-looser-accepts; every looser class fails the gate.)
        // rows: no tenant filter returns the rows of tenant B
        var select = new CorpusStatement("000000000001", "select", "accept", "negative-control", "SELECT id FROM orders");
        var rowsResult = Classifier.Classify(await tenant.RunAsync(EngineKind.Legacy, select), await tenant.RunAsync(EngineKind.Unfiltered, select), tenantOracle);
        rowsResult.Class.IsLooser().ShouldBeTrue();
        rowsResult.AstViolations.ShouldContain(v => v.Kind == OracleViolationKind.ForeignTenantMarker);

        // mask: no mask returns the clear e-mail where legacy returned the constant
        var email = new CorpusStatement("000000000002", "select", "accept", "negative-control", "SELECT email FROM users WHERE id = 1");
        var maskResult = Classifier.Classify(await masked.RunAsync(EngineKind.Legacy, email), await masked.RunAsync(EngineKind.Unfiltered, email), maskOracle);
        maskResult.Class.IsLooser().ShouldBeTrue();
        maskResult.AstViolations.ShouldContain(v => v.Kind == OracleViolationKind.ClearMaskedValue);

        // write: an unfiltered DELETE also deletes the rows of tenant B
        var delete = new CorpusStatement("000000000003", "delete", "accept", "negative-control", "DELETE FROM users WHERE id > 0");
        var writeResult = Classifier.Classify(await tenant.RunAsync(EngineKind.Legacy, delete), await tenant.RunAsync(EngineKind.Unfiltered, delete), tenantOracle);
        writeResult.Class.IsLooser().ShouldBeTrue();
        writeResult.AstViolations.ShouldContain(v => v.Kind == OracleViolationKind.InvisibleRowChanged);

        // and an engine against itself is identical (the unfiltered engine runs on every dialect, legacy does not on Oracle)
        (await Same(tenant, select, tenantOracle)).ShouldBe(DifferenceClass.Identical);
        (await Same(tenant, delete, tenantOracle)).ShouldBe(DifferenceClass.Identical);
    }

    private static async Task<DifferenceClass> Same(GateRunner runner, CorpusStatement statement, PolicyOracle oracle) =>
        Classifier.Classify(await runner.RunAsync(EngineKind.Unfiltered, statement), await runner.RunAsync(EngineKind.Unfiltered, statement), oracle).Class;

    [Fact]
    public async Task Corpus_HasNoAstLooser()
    {
        var results = await ResultsAsync();
        if (results is null) return;

        var accepted = AcceptedDifferences.Parse(File.ReadAllText(Path.Combine(CorpusLoader.CorpusDirectory, "accepted-differences.json")));

        // A confirmed legacy defect (approved, listed by statement id) is skipped, but only while the AST path has zero oracle violations.
        // Everything else that is looser stays red: ast-looser-* is never acceptable as a class.
        bool IsSkippedLegacyDefect(StatementResult r) =>
            accepted.IsLegacyDefect(r.Statement.Id, Fixture.Database.Dialect) && r.Classification.AstViolations.Count == 0;

        var skipped = results.Where(r => r.Classification.Class.IsLooser() && IsSkippedLegacyDefect(r)).ToList();
        if (skipped.Count > 0) output.WriteLine($"{skipped.Count} ast-looser results skipped as confirmed legacy defects:\n{Describe(skipped)}");
        var looser = results.Where(r => r.Classification.Class.IsLooser() && !IsSkippedLegacyDefect(r)).ToList();

        looser.ShouldBeEmpty($"{looser.Count} ast-looser results on {Fixture.Database.Dialect}:\n{Describe(looser)}");
    }

    [Fact]
    public async Task Ast_Results_HaveNoPolicyOracleViolation()
    {
        var results = await ResultsAsync();
        if (results is null) return;

        var violating = results.Where(r => r.Classification.AstViolations.Count > 0).ToList();

        violating.ShouldBeEmpty($"{violating.Count} AST results violate the independent policy oracle on {Fixture.Database.Dialect}:\n{Describe(violating)}");
    }

    [Fact]
    public async Task StricterAndErrorDifferences_AreAllListedInAcceptedDifferences()
    {
        var results = await ResultsAsync();
        if (results is null) return;
        var accepted = AcceptedDifferences.Parse(File.ReadAllText(Path.Combine(CorpusLoader.CorpusDirectory, "accepted-differences.json")));

        var unlisted = results
            .Where(r => r.Classification.Class is DifferenceClass.AstStricter or DifferenceClass.AstError)
            .Where(r => !accepted.Accepts(r.Statement.Id, Fixture.Database.Dialect, r.Classification.Class, r.Classification.Reason))
            .ToList();

        unlisted.ShouldBeEmpty($"{unlisted.Count} ast-stricter/ast-error differences are not in accepted-differences.json:\n{Describe(unlisted)}");
    }

    [Fact]
    public async Task AstErrors_AreNotUnexpectedExceptionTypes()
    {
        var results = await ResultsAsync();
        if (results is null) return;

        // an AST compile that throws an exception type outside the governed set is a defect even when legacy refused as well
        var unexpected = results.Where(r => r.Ast is EngineOutcome.Rejected { Stage: RejectionStage.Compile, Governed: false }).ToList();

        output.WriteLine($"unexpected AST compile exception types: {unexpected.Count}");
        unexpected.Select(r => ((EngineOutcome.Rejected)r.Ast).Reason).Distinct().Order().ShouldBeEmpty(
            $"unexpected AST exception types:\n{Describe(unexpected)}");
    }
}

public sealed class SqliteDifferentialGateTests(DifferentialFixture<SqliteGateDatabase> fixture, ITestOutputHelper output)
    : DifferentialGateBase<SqliteGateDatabase>(fixture, output), IClassFixture<DifferentialFixture<SqliteGateDatabase>>;

public sealed class PostgreSqlDifferentialGateTests(DifferentialFixture<PostgreSqlGateDatabase> fixture, ITestOutputHelper output)
    : DifferentialGateBase<PostgreSqlGateDatabase>(fixture, output), IClassFixture<DifferentialFixture<PostgreSqlGateDatabase>>;

public sealed class SqlServerDifferentialGateTests(DifferentialFixture<SqlServerGateDatabase> fixture, ITestOutputHelper output)
    : DifferentialGateBase<SqlServerGateDatabase>(fixture, output), IClassFixture<DifferentialFixture<SqlServerGateDatabase>>;

public sealed class OracleDifferentialGateTests(DifferentialFixture<OracleGateDatabase> fixture, ITestOutputHelper output)
    : DifferentialGateBase<OracleGateDatabase>(fixture, output), IClassFixture<DifferentialFixture<OracleGateDatabase>>;
