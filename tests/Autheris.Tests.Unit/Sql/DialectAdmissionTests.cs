namespace Autheris.Tests.Unit.Sql;

using System;
using System.IO;
using System.Linq;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using Xunit;

/// <summary>
/// WP-X1.3 (SEC-X1-03, SEC-X1-14): WebSQL admission is derived from the capability tier and the connection support, the admitted set is
/// pinned by a test, and a tier flip cannot admit a dialect without a security review.
/// </summary>
public sealed class DialectAdmissionTests
{
    [Fact]
    public void Admission_ExactSet()
    {
        // The admitted set is exactly these five. DuckDB and Ansi are not admitted: a change of this list is a
        // security decision (CODEOWNERS on DialectCapabilityTable.cs) and must change this test in the same commit.
        Enum.GetValues<DatabaseDialect>().Where(SqlDialectMapper.IsExecutable)
            .ShouldBe([DatabaseDialect.PostgreSql, DatabaseDialect.SqlServer, DatabaseDialect.Sqlite, DatabaseDialect.Oracle, DatabaseDialect.Databricks], ignoreOrder: true);
        SqlDialectMapper.AdmittedDialects.ShouldBe(
            [DatabaseDialect.Oracle, DatabaseDialect.PostgreSql, DatabaseDialect.SqlServer, DatabaseDialect.Sqlite, DatabaseDialect.Databricks], ignoreOrder: true);

        // The target dialects that no database dialect maps to are not admitted by construction.
        foreach (var target in new[] { TargetSqlDialect.DuckDb, TargetSqlDialect.Ansi })
        {
            Enum.GetValues<DatabaseDialect>()
                .Where(d => SqlDialectMapper.TryToTargetDialect(d, out var t) && t == target)
                .ShouldAllBe(d => !SqlDialectMapper.IsExecutable(d));
        }
    }

    [Fact]
    public void Admission_IsDerivedFromTheCapabilityTier()
    {
        // Databricks maps and connects: its Production tier admits it. A flip to Experimental keeps it out, and nothing else changes.
        var table = DialectCapabilityTable.Default;
        SqlDialectMapper.IsExecutable(DatabaseDialect.Databricks, table).ShouldBeTrue();
        SqlDialectMapper.IsExecutable(DatabaseDialect.Databricks, new TierOverride(table, TargetSqlDialect.Databricks, DialectSupportTier.Experimental)).ShouldBeFalse();

        // ... and an admitted dialect that loses its tier is no longer admitted.
        SqlDialectMapper.IsExecutable(DatabaseDialect.Oracle, new TierOverride(table, TargetSqlDialect.Oracle, DialectSupportTier.Experimental)).ShouldBeFalse();
        SqlDialectMapper.IsExecutable(DatabaseDialect.Sqlite, new TierOverride(table, TargetSqlDialect.Sqlite, DialectSupportTier.Internal)).ShouldBeFalse();
    }

    [Fact]
    public void Admission_NoCapabilityEntry_IsNeverAdmitted()
    {
        SqlDialectMapper.IsExecutable(DatabaseDialect.PostgreSql, new TierOverride(DialectCapabilityTable.Default, TargetSqlDialect.PostgreSql, null)).ShouldBeFalse();
    }

    [Fact]
    public void Admission_OracleOnlyWithF5Evidence()
    {
        // SEC-X1-03: Oracle is admitted only because WP-F5 / M-10 (Oracle on the real gateway path) exists and covers tenant isolation,
        // pool reuse across tenants, the hostile session and the masks. If the evidence is removed, Oracle must be excluded again.
        SqlDialectMapper.IsExecutable(DatabaseDialect.Oracle).ShouldBeTrue();

        string evidence = Path.Combine(RepoRoot(), "tests", "Autheris.Tests.Integration", "OracleGatewayPathTests.cs");
        File.Exists(evidence).ShouldBeTrue("Oracle is admitted to WebSQL, but its WP-F5 gateway-path suite is missing.");
        string text = File.ReadAllText(evidence);
        foreach (var required in new[]
                 {
                     "Tenants_AreIsolated_UnderAHostileDatabaseDefault", "PoolReuse_AcrossTenants_NeverLeaksSessionStateOrRows",
                     "PooledSession_AfterADirtyRental_IsPinnedAgain_BeforeAnyStatement", "Masks_AreAppliedInTheDatabase_AndTheRawValueNeverLeavesIt",
                     "HostServices_AreTheRealGatewayImplementations_AndCommandsBindByName"
                 })
        {
            text.ShouldContain(required, customMessage: $"WP-F5 evidence test '{required}' is missing.");
        }

        // The Oracle capability entry carries the tier the admission derives from.
        DialectCapabilityTable.Default.Get(TargetSqlDialect.Oracle).Tier.ShouldBe(DialectSupportTier.Production);
    }

    [Fact]
    public void Admission_DatabricksOnlyWithParityEvidence()
    {
        // WP-SIMPL-37 / OD-36 (SEC-X1-14): Databricks is admitted only because DatabricksTransportParityTests exists and covers
        // parity across WebSQL, Trino, and MCP transports. If the evidence is removed, Databricks must be excluded again.
        SqlDialectMapper.IsExecutable(DatabaseDialect.Databricks).ShouldBeTrue();

        string evidence = Path.Combine(RepoRoot(), "tests", "Autheris.Tests.Integration", "DatabricksTransportParityTests.cs");
        File.Exists(evidence).ShouldBeTrue("Databricks is admitted to WebSQL, Trino, and MCP, but its parity suite is missing.");
        string text = File.ReadAllText(evidence);
        foreach (var required in new[]
                 {
                     "SameCaller_SameRowsMasksAndColumnDenials_OnAllTransports",
                     "Tenant_isolation_holds_under_case_insensitive_collation",
                     "ConsentDenied_SameDenialShape_OnAllTransports",
                     "RowLimit_AppliedPerTransportChannel",
                     "Write_statement_rejected_on_Trino_and_Mcp"
                 })
        {
            text.ShouldContain(required, customMessage: $"Databricks parity evidence test '{required}' is missing.");
        }

        // The Databricks capability entry carries the tier the admission derives from.
        DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks).Tier.ShouldBe(DialectSupportTier.Production);
    }

    [Fact]
    public void ToTargetDialect_NeverDefaults_ForAnUnmappedDialect()
    {
        Should.Throw<NotSupportedException>(() => SqlDialectMapper.ToTargetDialect((DatabaseDialect)99));
        SqlDialectMapper.TryToTargetDialect((DatabaseDialect)99, out _).ShouldBeFalse();
        SqlDialectMapper.IsExecutable((DatabaseDialect)99).ShouldBeFalse();
    }

    [Fact]
    public void RejectionMessages_ListTheAdmittedDialects_FromTheTable()
    {
        foreach (var message in new[] { SqlDialectMapper.UnsupportedDialectMessage, SqlDialectMapper.UnsupportedProviderMessage })
        {
            foreach (var dialect in SqlDialectMapper.AdmittedDialects)
            {
                message.ShouldContain(SqlDialectMapper.DisplayName(dialect));
            }
        }
    }

    [Fact]
    public void ConnectionFactorySupport_ListsEveryDialectThatTheFactoryOpens()
    {
        // SqlConnectionFactory throws for every other value (see its switch); the list is the Application-side mirror of it.
        Enum.GetValues<DatabaseDialect>().ShouldAllBe(d => SqlDialectMapper.ConnectionFactorySupports(d));
        SqlDialectMapper.ConnectionFactorySupports((DatabaseDialect)99).ShouldBeFalse();
    }

    [Fact]
    public void CapabilityTable_IsUnderSecurityCodeOwners()
    {
        // A tier flip admits a dialect (SEC-X1-14), so a change of the table needs the security review.
        var owners = File.ReadAllLines(Path.Combine(RepoRoot(), ".github", "CODEOWNERS"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        owners.ShouldContain(l => l.StartsWith("src/TrinoSqlEngine/Ast/Capabilities/DialectCapabilityTable.cs", StringComparison.Ordinal) && l.Contains('@'));
    }

    private sealed class TierOverride(DialectCapabilityTable inner, TargetSqlDialect dialect, DialectSupportTier? tier) : IDialectCapabilityProvider
    {
        public DialectCapabilities Get(TargetSqlDialect requested) =>
            requested != dialect
                ? inner.Get(requested)
                : tier is { } t ? inner.Get(requested) with { Tier = t } : throw new ArgumentOutOfRangeException(nameof(requested));
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Autheris.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return Directory.GetCurrentDirectory();
    }
}
