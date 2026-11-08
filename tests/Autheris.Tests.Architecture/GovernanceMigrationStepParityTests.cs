using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class GovernanceMigrationStepParityTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Migration_steps_in_postgresql_and_sqlserver_have_exact_parity()
    {
        var repoRoot = RepositoryScan.RepoRoot;
        var pgStepsDir = Path.Combine(repoRoot, "src", "Autheris.Infrastructure", "Persistence", "Migrations", "Steps", "postgresql");
        var ssStepsDir = Path.Combine(repoRoot, "src", "Autheris.Infrastructure", "Persistence", "Migrations", "Steps", "sqlserver");

        Directory.Exists(pgStepsDir).ShouldBeTrue("PostgreSQL migration steps directory must exist.");
        Directory.Exists(ssStepsDir).ShouldBeTrue("SQL Server migration steps directory must exist.");

        var pgFiles = Directory.GetFiles(pgStepsDir).Select(f => Path.GetFileName(f)!).OrderBy(x => x).ToList();
        var ssFiles = Directory.GetFiles(ssStepsDir).Select(f => Path.GetFileName(f)!).OrderBy(x => x).ToList();

        pgFiles.ShouldNotBeEmpty("PostgreSQL steps must not be empty.");
        ssFiles.ShouldNotBeEmpty("SQL Server steps must not be empty.");

        pgFiles.ShouldBe(ssFiles, "Every migration step and shape snapshot in Steps/postgresql must match Steps/sqlserver.");

        var sqlSteps = pgFiles.Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var sqlStep in sqlSteps)
        {
            var shapeFile = Path.ChangeExtension(sqlStep, null) + ".shape.json";
            pgFiles.ShouldContain(shapeFile, $"Missing shape file for step {sqlStep} in postgresql.");
            ssFiles.ShouldContain(shapeFile, $"Missing shape file for step {sqlStep} in sqlserver.");
        }
    }
}
