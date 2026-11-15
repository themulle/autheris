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
    public void IWriteGrantEvaluator_only_called_by_decision_point()
    {
        var srcDir = Path.Combine(Root(), "src");
        var allowed = new[]
        {
            "src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs",
            "src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.WriteGrants.cs",
            "src/Autheris.Application/Policy/WriteGrants/IWriteGrantEvaluator.cs",
            "src/Autheris.Application/Policy/WriteGrants/WriteGrantEvaluator.cs",
            "src/Autheris.Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs"
        }.ToHashSet(StringComparer.Ordinal);

        var callers = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f =>
            {
                var content = File.ReadAllText(f);
                return content.Contains("IWriteGrantEvaluator", StringComparison.Ordinal) ||
                       content.Contains("WriteGrantEvaluator", StringComparison.Ordinal);
            })
            .Select(f => Path.GetRelativePath(Root(), f).Replace('\\', '/'))
            .Where(rel => !allowed.Contains(rel))
            .Order(StringComparer.Ordinal)
            .ToList();

        callers.ShouldBeEmpty();
    }

    [Fact]
    public void SEC_36_ORDER_01_Write_decision_is_the_last_dml_check_in_the_rewriter()
    {
        var text = Source("src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs");
        var tenantColumn = text.IndexOf("RequireTenantColumnOrThrow(", StringComparison.Ordinal);
        var dependency = text.IndexOf("dependencyScan.IsReadAsync(", StringComparison.Ordinal);
        var write = text.IndexOf("AuthorizeDmlTargetAsync(", StringComparison.Ordinal);

        tenantColumn.ShouldBeGreaterThan(0);
        dependency.ShouldBeGreaterThan(tenantColumn);
        write.ShouldBeGreaterThan(dependency);
    }

    [Fact]
    public void Legacy_write_seam_and_CanWriteTableAsync_are_deleted()
    {
        File.Exists(Path.Combine(Root(), "src", "Autheris.Application", "Policy", "DmlWriteDecisionTestSeam.cs")).ShouldBeFalse();

        var srcDir = Path.Combine(Root(), "src");
        var matches = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("DmlWriteDecisionTestSeam", StringComparison.Ordinal) ||
                       text.Contains("CanWriteTableAsync", StringComparison.Ordinal);
            })
            .Select(f => Path.GetRelativePath(Root(), f).Replace('\\', '/'))
            .ToList();

        matches.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluator_DoesNotReferenceCasbin()
    {
        var text = Source("src/Autheris.Application/Policy/WriteGrants/WriteGrantEvaluator.cs");
        foreach (var forbidden in new[] { "Casbin", "IPolicyEnforcementService", "_policyEnforcementService" })
        {
            text.ShouldNotContain(forbidden, Case.Insensitive);
        }
    }
}
