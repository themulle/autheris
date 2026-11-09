namespace Autheris.Tests.Architecture;

using System.IO;
using Shouldly;
using Xunit;

/// <summary>WP-SIMPL-39: structural rules of the governed stored procedure feature.</summary>
public sealed class ProcedureArchitectureTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void NoOracleOrDatabricksBranchInProcedureInvoker()
    {
        // SEC-PROC-05: the invoker serves the closed dialect set only; no branch may name another dialect.
        string source = File.ReadAllText(Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Application", "Procedures", "Services", "ProcedureInvoker.cs"));

        source.ShouldNotContain("DatabaseDialect.Oracle");
        source.ShouldNotContain("DatabaseDialect.Databricks");
        source.ShouldNotContain("TABLE(");
    }
}
