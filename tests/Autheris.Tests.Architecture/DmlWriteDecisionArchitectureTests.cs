using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>Structural guards of the interim DML write decision (deny-all) and the order of the DML checks in the rewriter.</summary>
[Trait("Category", "Security")]
public sealed class DmlWriteDecisionArchitectureTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Autheris.Application")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    [Fact]
    public void SEC_36_SP3a_CanWriteTableAsync_references_no_casbin_or_role_or_policy_evaluation()
    {
        var text = Source("src/Autheris.Application/Policy/TableAccessPolicy.cs");
        var body = text[text.IndexOf("CanWriteTableAsync(", StringComparison.Ordinal)..];

        foreach (var forbidden in new[] { "Casbin", "DecideAsync", "DmlWriterRoles", "IsInRole", "_policyEnforcementService", "IsConsentBypassed" })
        {
            body.ShouldNotContain(forbidden, Case.Sensitive);
        }
    }

    [Fact]
    public void SEC_36_ORDER_01_Write_decision_is_the_last_dml_check_in_the_rewriter()
    {
        var text = Source("src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs");
        var tenantColumn = text.IndexOf("RequireTenantColumnOrThrow(", StringComparison.Ordinal);
        var dependency = text.IndexOf("dependencyScan.IsReadAsync(", StringComparison.Ordinal);
        var write = text.IndexOf("CanWriteTableAsync(", StringComparison.Ordinal);

        tenantColumn.ShouldBeGreaterThan(0);
        dependency.ShouldBeGreaterThan(tenantColumn);
        write.ShouldBeGreaterThan(dependency);
    }

    [Fact]
    public void SEC_36_ORDER_02_Permissive_write_seam_is_internal_and_used_only_by_the_write_decision()
    {
        Source("src/Autheris.Application/Policy/DmlWriteDecisionTestSeam.cs").ShouldContain("internal static class DmlWriteDecisionTestSeam");

        var users = new[] { "src", "benchmarks", Path.Combine("tests", "Autheris.Tests.SqlCompilerGate") }
            .Where(d => Directory.Exists(Path.Combine(Root(), d)))
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(Root(), d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && File.ReadAllText(f).Contains("DmlWriteDecisionTestSeam", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        users.ShouldBe(["DmlWriteDecisionTestSeam.cs", "TableAccessPolicy.cs"]);
    }
}
