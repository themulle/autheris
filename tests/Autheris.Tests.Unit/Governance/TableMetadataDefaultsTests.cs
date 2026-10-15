using System.Text.RegularExpressions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #94: a table has no key unless one was declared; no code path may invent an "id" key.</summary>
public sealed class TableMetadataDefaultsTests
{
    [Fact]
    public void NewTableMetadata_HasNoDeclaredKeyDefinition()
    {
        var metadata = new TableMetadata();

        metadata.PrimaryKeyColumns.ShouldBeEmpty();
        metadata.Indexes.ShouldBeEmpty();
        metadata.ReplaceKeyDefinition.ShouldBeFalse();
        metadata.IsCompositePrimaryKey.ShouldBeFalse();
    }

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Autheris.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repo root not found");
    }


    private static readonly Regex IdDefaultLiteral = new(
        @"\[\s*""id""\s*\]|\{\s*""id""\s*\}",
        RegexOptions.CultureInvariant);

    [Fact]
    public void PrimaryKeyColumns_NeverDefaultsToId_InDomainAndCatalogRepositories()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        // Domain: only the PrimaryKeyColumns declaration is checked (relation join keys are a different concept).
        var domain = Path.Combine(root, "src", "Autheris.Domain", "Model", "TableModels.cs");
        foreach (var (line, index) in File.ReadAllLines(domain).Select((l, i) => (l, i + 1)))
        {
            if (line.Contains("PrimaryKeyColumns", StringComparison.Ordinal) && IdDefaultLiteral.IsMatch(line))
                violations.Add($"{Path.GetFileName(domain)}:{index}");
        }

        var persistence = Path.Combine(root, "src", "Autheris.Infrastructure", "Persistence");
        foreach (var file in Directory.GetFiles(persistence, "*GovernanceRepository.Catalog.cs"))
        {
            foreach (var (line, index) in File.ReadAllLines(file).Select((l, i) => (l, i + 1)))
            {
                if (IdDefaultLiteral.IsMatch(line) || Regex.IsMatch(line, @"new\s+(List<string>|string\[\])\s*(\(\))?\s*\{\s*""id""\s*\}"))
                    violations.Add($"{Path.GetFileName(file)}:{index}");
            }
        }

        violations.ShouldBeEmpty("an \"id\" key default must not be hard-coded");
    }
}
