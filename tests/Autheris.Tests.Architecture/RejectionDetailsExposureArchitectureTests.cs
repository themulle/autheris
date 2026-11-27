using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// A rejection may name the rejected function or construct only while the builder parses client statement text. A builder that parses policy
/// text (row policies, masks, required filters, metric views, endpoint definitions) must keep the unnamed rejection, so policy facts never reach a caller.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class RejectionDetailsExposureArchitectureTests
{
    private static readonly string[] AllowedFiles =
    [
        "src/TrinoSqlEngine/GovernedSqlCompiler.cs",
        "src/Autheris.Application/Sql/Security/TableQualificationValidator.cs"
    ];

    [GeneratedRegex(@"\bExposeRejectionDetails\s*=(?!=)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SetterPattern();

    private const string DefinitionFile = "src/TrinoSqlEngine/Ast/Builder/AstBuilderOptions.cs";

    [Fact]
    public void ExposeRejectionDetails_OnlySetByClientTextParsers()
    {
        var users = RepositoryScan.ProductionFiles
            .Where(f => f.RelativePath != DefinitionFile)
            .Where(f => RepositoryScan.CodeLines(f.Text).Any(l =>
                l.Contains("WithRejectionDetailsExposed(", StringComparison.Ordinal)
                || SetterPattern().IsMatch(l)))
            .Select(f => f.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToList();

        users.Where(p => !AllowedFiles.Contains(p)).ShouldBeEmpty("Only the parsers of client statement text may expose rejection details.");
        AllowedFiles.Where(p => !users.Contains(p)).ShouldBeEmpty("A stale entry: the allowed parser no longer exposes rejection details.");
    }
}
