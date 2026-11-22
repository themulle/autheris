namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

/// <summary>
/// Enforces consolidation of SQL identifier validation regexes and helper methods onto
/// <c>Autheris.Domain.Common.SqlIdentifierGuard</c> across <c>Autheris.Infrastructure</c> and <c>Autheris.Application</c>.
/// </summary>
public sealed partial class SqlIdentifierConsolidationArchitectureTests
{
    private static readonly string[] ScannedRoots =
    [
        "src/Autheris.Infrastructure/",
        "src/Autheris.Application/"
    ];

    private static readonly HashSet<string> ForbiddenDuplicateHelperNames = new(StringComparer.Ordinal)
    {
        "SafeIdentifierRegex",
        "SafeSimpleIdentifierRegex",
        "SafeQualifiedIdentifierRegex",
        "ValidIdentifierRegex",
        "EchoableColumnNameRegex"
    };

    // Transitional exemptions solely for active parallel branches excluded by conflict-avoidance rules.
    private static readonly HashSet<string> TransitionalAllowlistedFiles = new(StringComparer.Ordinal)
    {
        "src/Autheris.Application/OData/ODataHandler.cs",
        "src/Autheris.Application/Sql/Tree/TreeSqlCompiler.cs",
        "src/Autheris.Application/DataSources/Services/GovernedTableReadPlanner.Keyset.cs"
    };

    private const string IdentifierStartClass = @"\[(?:a-zA-Z|A-Za-z)_\]";
    private const string IdentifierRestClass = @"\[(?:a-zA-Z|A-Za-z)0-9_\]";
    private const string Quantifier = @"(?:\*|\+|\{\d+,\d+\})";
    private static readonly string SegmentPattern =
        $@"(?:{IdentifierStartClass}{IdentifierRestClass}{Quantifier}|\[(?:a-zA-Z|A-Za-z)0-9_\.?\]{Quantifier})";
    private static readonly string WrappedSegmentPattern = $@"(?:\(\?:|\()?{SegmentPattern}\)?";

    private static readonly Regex AnchoredSqlIdentifierRegex = new(
        $@"^(?:\^|\\A){WrappedSegmentPattern}(?:(?:\(\?:|\()?\\\.{WrappedSegmentPattern}(?:\)\*|\))?)*(?:\$|\\z)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Infrastructure_And_Application_Have_No_Unapproved_Duplicate_Sql_Identifier_Regexes()
    {
        var files = RepositoryScan.ProductionFiles
            .Where(f => ScannedRoots.Any(root => f.RelativePath.StartsWith(root, StringComparison.Ordinal)))
            .Where(f => !TransitionalAllowlistedFiles.Contains(f.RelativePath))
            .ToList();

        files.ShouldNotBeEmpty("Scanner must inspect Autheris.Infrastructure and Autheris.Application C# files.");

        var violations = new List<string>();
        foreach (var file in files)
        {
            violations.AddRange(FindViolations(file.RelativePath, file.Text));
        }

        violations.ShouldBeEmpty(
            "Duplicate SQL identifier validation regexes/helpers must delegate to SqlIdentifierGuard in Autheris.Domain.Common. " +
            "Violations:\n" + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Scanner_Detects_Planted_Duplicate_Sql_Identifier_Regexes_And_Helpers()
    {
        const string sample = """
            using System.Text.RegularExpressions;
            namespace Sample;
            public partial class Foo
            {
                [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
                private static partial Regex SafeIdentifierRegex();

                private static readonly Regex CustomColRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

                public bool Check(string s) => Regex.IsMatch(s, "^[A-Za-z0-9_.]+$");
            }
            """;

        var hits = FindViolations("src/Autheris.Application/Sample/Foo.cs", sample);
        hits.Count.ShouldBeGreaterThanOrEqualTo(3);
        hits.ShouldContain(h => h.Contains("SafeIdentifierRegex", StringComparison.Ordinal));
        hits.ShouldContain(h => h.Contains("CustomColRegex", StringComparison.Ordinal) || h.Contains("^[a-zA-Z_][a-zA-Z0-9_]*$", StringComparison.Ordinal));
        hits.ShouldContain(h => h.Contains("^[A-Za-z0-9_.]+$", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_Does_Not_Flag_Non_Sql_Domain_Slug_Or_Sql_Token_Regexes()
    {
        const string sample = """
            using System.Text.RegularExpressions;
            namespace Sample;
            public partial class Bar
            {
                [GeneratedRegex("^[A-Z][A-Z0-9_]{2,63}$")]
                private static partial Regex ErrorCodeRegex();

                [GeneratedRegex(@"^[a-zA-Z0-9_\-\.]+$")]
                private static partial Regex SlugRegex();

                [GeneratedRegex(@"[a-zA-Z_][a-zA-Z0-9_]*")]
                private static partial Regex UnanchoredTokenRegex();
            }
            """;

        var hits = FindViolations("src/Autheris.Application/Sample/Bar.cs", sample);
        hits.ShouldBeEmpty();
    }

    internal static List<string> FindViolations(string relativePath, string source)
    {
        var hits = new List<string>();
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (ForbiddenDuplicateHelperNames.Contains(method.Identifier.ValueText))
            {
                var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                hits.Add($"{relativePath}:{line} forbidden duplicate method '{method.Identifier.ValueText}'");
            }
        }

        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                if (ForbiddenDuplicateHelperNames.Contains(variable.Identifier.ValueText))
                {
                    var line = variable.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    hits.Add($"{relativePath}:{line} forbidden duplicate field '{variable.Identifier.ValueText}'");
                }
            }
        }

        foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var name = attr.Name.ToString();
            if (!name.EndsWith("GeneratedRegex", StringComparison.Ordinal))
            {
                continue;
            }

            var argExpr = attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            if (TryGetStringLiteral(argExpr, out var pattern) && IsDuplicateSqlIdentifierPattern(pattern))
            {
                var line = attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                hits.Add($"{relativePath}:{line} duplicate [GeneratedRegex] pattern '{pattern}'");
            }
        }

        foreach (var obj in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = obj.Type.ToString();
            if (!typeName.EndsWith("Regex", StringComparison.Ordinal))
            {
                continue;
            }

            var argExpr = obj.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            if (TryGetStringLiteral(argExpr, out var pattern) && IsDuplicateSqlIdentifierPattern(pattern))
            {
                var line = obj.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                hits.Add($"{relativePath}:{line} duplicate new Regex pattern '{pattern}'");
            }
        }

        foreach (var implicitObj in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
        {
            var argExpr = implicitObj.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            if (TryGetStringLiteral(argExpr, out var pattern) && IsDuplicateSqlIdentifierPattern(pattern))
            {
                var line = implicitObj.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                hits.Add($"{relativePath}:{line} duplicate implicit new Regex pattern '{pattern}'");
            }
        }

        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "IsMatch" } member &&
                member.Expression.ToString().EndsWith("Regex", StringComparison.Ordinal) &&
                inv.ArgumentList.Arguments.Count >= 2 &&
                TryGetStringLiteral(inv.ArgumentList.Arguments[1].Expression, out var pattern) &&
                IsDuplicateSqlIdentifierPattern(pattern))
            {
                var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                hits.Add($"{relativePath}:{line} duplicate Regex.IsMatch pattern '{pattern}'");
            }

            if (inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Create" } svMember &&
                svMember.Expression.ToString().EndsWith("SearchValues", StringComparison.Ordinal) &&
                inv.ArgumentList.Arguments.Count >= 1 &&
                TryGetStringLiteral(inv.ArgumentList.Arguments[0].Expression, out var alphabet) &&
                alphabet.Contains("ABCDEFGHIJKLMNOPQRSTUVWXYZ", StringComparison.Ordinal) &&
                alphabet.Contains("abcdefghijklmnopqrstuvwxyz", StringComparison.Ordinal) &&
                alphabet.Contains("0123456789_", StringComparison.Ordinal))
            {
                var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                hits.Add($"{relativePath}:{line} duplicate SearchValues identifier charset");
            }
        }

        return hits;
    }

    private static bool TryGetStringLiteral(ExpressionSyntax? expr, out string value)
    {
        if (expr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            value = literal.Token.ValueText;
            return true;
        }

        value = string.Empty;
        return false;
    }

    internal static bool IsDuplicateSqlIdentifierPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        // Only consider mixed-case or case-insensitive SQL identifier patterns (not uppercase-only error codes or lowercase-only slugs).
        bool hasMixedCaseClass =
            pattern.Contains("[A-Za-z", StringComparison.Ordinal) ||
            pattern.Contains("[a-zA-Z", StringComparison.Ordinal);

        return hasMixedCaseClass && AnchoredSqlIdentifierRegex.IsMatch(pattern);
    }
}
