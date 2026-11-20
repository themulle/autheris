using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Structural enforcement for grant ledger integrity: all CONSENTS row insertions flow exclusively through
/// <c>GrantRecordStore</c> / <c>GrantWriter</c>, and in-place expiry extensions are forbidden.
/// </summary>
public sealed class GrantWriterArchitectureTests
{
    private static readonly Regex ForbiddenInsertRegex = new(
        @"\bINSERT\s+INTO\s+CONSENTS\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private static readonly Regex ForbiddenExtendUpdateRegex = new(
        @"\bUPDATE\s+CONSENTS\s+SET\s+valid_to\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private static readonly HashSet<string> AllowedInsertFiles = new(StringComparer.Ordinal)
    {
        "Autheris.Infrastructure/Persistence/GovernanceRepository.GrantRecords.cs",
        "Autheris.Application/Governance/Grants/Records/GrantWriter.cs"
    };

    [Trait("Category", "Security")]
    [Fact]
    public void Only_GrantWriter_Inserts_Into_CONSENTS()
    {
        var srcDir = Path.Combine(FindRepositoryRoot(), "src");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
            if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            var literals = root.DescendantNodes()
                .OfType<LiteralExpressionSyntax>()
                .Where(n => n.IsKind(SyntaxKind.StringLiteralExpression) || n.IsKind(SyntaxKind.Utf8StringLiteralExpression))
                .Select(n => (Value: n.Token.ValueText, Line: n.GetLocation().GetLineSpan().StartLinePosition.Line + 1))
                .Concat(root.DescendantNodes()
                    .OfType<InterpolatedStringTextSyntax>()
                    .Select(n => (Value: n.TextToken.ValueText, Line: n.GetLocation().GetLineSpan().StartLinePosition.Line + 1)));

            foreach (var (value, line) in literals)
            {
                if (ForbiddenExtendUpdateRegex.IsMatch(value))
                {
                    violations.Add($"{rel}:{line} contains forbidden extend SQL literal ('UPDATE CONSENTS SET valid_to').");
                }

                if (!AllowedInsertFiles.Contains(rel) && ForbiddenInsertRegex.IsMatch(value))
                {
                    violations.Add($"{rel}:{line} inserts into CONSENTS outside GrantRecordStore/GrantWriter.");
                }
            }
        }

        violations.ShouldBeEmpty("All CONSENTS inserts must go through IGrantWriter/GrantRecordStore and in-place valid_to updates are forbidden.");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }
}
