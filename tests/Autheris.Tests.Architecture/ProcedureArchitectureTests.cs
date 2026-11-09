namespace Autheris.Tests.Architecture;

using System;
using System.IO;
using System.Linq;
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

    [Fact]
    [Trait("Category", "Security")]
    public void OnlyProcedureCatalog_EnumeratesProcedureRegistry()
    {
        // SEC-PROC-09: a single visibility path; nothing outside Application/Procedures lists the registry.
        string src = Path.Combine(RepositoryScan.RepoRoot, "src");
        string procedures = Path.Combine(src, "Autheris.Application", "Procedures") + Path.DirectorySeparatorChar;

        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(procedures, StringComparison.Ordinal))
            .Where(f =>
            {
                string text = File.ReadAllText(f);
                return text.Contains("IProcedureRegistry", StringComparison.Ordinal) && text.Contains(".GetAll()", StringComparison.Ordinal);
            })
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();

        offenders.ShouldBeEmpty();
    }
}
