using System.Reflection;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Unit.Support;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// Issue #849: an intermittent SQL Server error 102 in a migration step was reported without a position. The failure report now names
/// the phase, the batch, the server line and the first 200 characters of the failing (static) statement; the splitter invariants are
/// pinned for every embedded SQL Server step, also under concurrent parsing.
/// </summary>
public sealed class MigrationFailureDiagnosticsTests
{
    private const string EmptyDelta = "{\"ops\":[]}";

    [Fact]
    public void DescribeFailure_NamesPhaseBatchLineAndStatementPrefix_ForASqlServerError()
    {
        var sql = "IF COL_LENGTH('dbo.IDENTITY_USERS', 'object_sid') IS NULL\n    ALTER TABLE dbo.IDENTITY_USERS ADD object_sid NVARCHAR(184) NULL;";
        var failure = new InvalidOperationException("Incorrect syntax near 'secret-token'.");
        failure.Data[SqlMigrationStep.FailedPhaseDataKey] = "apply";
        failure.Data[SqlMigrationStep.FailedBatchDataKey] = 6;
        SqlMigrationStep.AnnotateStatement(failure, sql);

        var text = GovernanceMigrationRunnerBase.DescribeFailure(new GovernanceMigrationRunnerBase.SqlFailureInfo(102, 2), failure);

        text.ShouldStartWith("error 102, line 2, phase apply, batch 6, statement \"IF COL_LENGTH(");
        text.ShouldNotContain("\n");
        text.ShouldNotContain("secret-token");
        text.ShouldNotContain(failure.Message, Case.Sensitive, "the server message can carry values and stays out of the report");
    }

    [Fact]
    public void DescribeFailure_WithoutAnnotations_KeepsTheErrorNumberOnly()
    {
        GovernanceMigrationRunnerBase.DescribeFailure(new GovernanceMigrationRunnerBase.SqlFailureInfo(102, 0)).ShouldBe("error 102");
    }

    [RuntimeSkippableFact]
    public void RealSqlException_MapsNumberAndLine()
    {
        var failure = TryCreateSqlException(102, lineNumber: 2)
            ?? throw new RuntimeSkipException($"SqlClient {SqlClientVersion()} changed its internal SqlError API");

        var text = GovernanceMigrationRunnerBase.DescribeFailure(failure);

        text.ShouldBe("error 102, line 2");
        text.ShouldNotContain(failure.Message, Case.Sensitive, "the server message can carry values and stays out of the report");
    }

    [Fact]
    public void AnnotateStatement_KeepsAtMost200Characters_ReplacesControlCharacters_AndTheFirstStatementWins()
    {
        var failure = new InvalidOperationException("x");
        SqlMigrationStep.AnnotateStatement(failure, "\n" + new string('a', 150) + "\r\n\t" + new string('b', 150));
        SqlMigrationStep.AnnotateStatement(failure, "SELECT 1");

        var statement = failure.Data[SqlMigrationStep.FailedStatementDataKey].ShouldBeOfType<string>();
        statement.Length.ShouldBe(SqlMigrationStep.StatementPrefixLength);
        statement.ShouldStartWith(new string('a', 150));
        statement.ShouldNotContain("\n");
        statement.ShouldNotContain("\r");
        statement.ShouldNotContain("\t");
    }

    [Fact]
    public void EmbeddedSqlServerSteps_SplitIntoNonEmptyMarkerFreeBatches_AndStayEqualUnderConcurrentParsing()
    {
        var reference = SqlMigrationStep.LoadEmbedded("sqlserver");
        reference.ShouldNotBeEmpty();
        foreach (var step in reference)
        {
            step.Batches.ShouldNotBeEmpty();
            step.Batches.ShouldAllBe(b => b.Length > 0 && b == b.Trim());
            step.Batches.ShouldAllBe(b => !b.Split('\n').Any(l => l.Trim().Equals("-- batch", StringComparison.OrdinalIgnoreCase)));
            step.Batches.ShouldAllBe(b => !b.Contains('\r'));
        }

        var expected = reference.Select(s => s.Checksum + "|" + string.Join("\u0001", s.Batches)).ToArray();
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var again = SqlMigrationStep.LoadEmbedded("sqlserver");
            var actual = again.Select(s => s.Checksum + "|" + string.Join("\u0001", s.Batches)).ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            {
                mismatches.Add(i);
            }
        });

        mismatches.ShouldBeEmpty();
        SqlMigrationStep.Parse("sqlserver", 2, "x", "A1;\n-- batch\nB1;", EmptyDelta).Batches.Count.ShouldBe(2);
    }

    internal static string SqlClientVersion() =>
        typeof(SqlException).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SqlException).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    internal static SqlException? TryCreateSqlException(int number, int lineNumber)
    {
        try
        {
            var ctor = typeof(SqlError)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .SingleOrDefault(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(
                    [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(int), typeof(Exception)]));
            if (ctor is null)
            {
                return null;
            }

            var error = ctor.Invoke([number, (byte)1, (byte)15, "server", "Incorrect syntax near 'secret-token'.", "proc", lineNumber, 0, null]);
            if (Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true) is not SqlErrorCollection collection)
            {
                return null;
            }

            var add = typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (add is null)
            {
                return null;
            }

            add.Invoke(collection, [error]);
            var create = typeof(SqlException).GetMethod(
                "CreateException", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(SqlErrorCollection), typeof(string)]);
            return create?.Invoke(null, [collection, "16.0"]) as SqlException;
        }
        catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException or MemberAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
