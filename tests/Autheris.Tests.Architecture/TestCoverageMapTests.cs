namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

internal static partial class TestInventoryScanner
{
    private static readonly CSharpParseOptions ScannerParseOptions =
        CSharpParseOptions.Default.WithPreprocessorSymbols("AUTHERIS_TEST_AUTH");

    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    internal static partial Regex NamespaceRegex();

    [GeneratedRegex(@"^\s*(?:public|internal|private|protected)\s+(?:(?:sealed|abstract|static|partial)\s+)*(?:class|record)\s+([A-Za-z0-9_]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    internal static partial Regex TypeDeclRegex();

    [GeneratedRegex(@"\[\s*(?:Fact|Theory|ParityFact|ParityTheory|SparkFact|SparkTheory|RequiresContainerRuntimeFact|RequiresContainerRuntimeTheory|Property)(?:Attribute)?\b", RegexOptions.CultureInvariant)]
    internal static partial Regex FactAttrRegex();

    [GeneratedRegex(@"\[\s*Trait(?:Attribute)?\s*\(\s*""Category""\s*,\s*""Security""\s*\)\s*\]", RegexOptions.CultureInvariant)]
    internal static partial Regex SecurityTraitRegex();

    [GeneratedRegex(@"^\s*(?:public|internal|private|protected)\s+(?:(?:static|async|virtual|override|sealed)\s+)*[A-Za-z0-9_<>,.?\[\]\s]+\s+([A-Za-z0-9_]+)\s*\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    internal static partial Regex MethodDeclRegex();

    public static (HashSet<string> AllTests, HashSet<string> SecurityTests) ScanRepository()
    {
        var allTests = new HashSet<string>(StringComparer.Ordinal);
        var securityTests = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in RepositoryScan.EnumerateFiles("tests", "*.cs").Concat(RepositoryScan.EnumerateFiles("modules", "*.cs")))
        {
            var source = RepositoryScan.ReadText(file);
            ExtractFromSource(source, allTests, securityTests);
        }

        return (allTests, securityTests);
    }

    public static void ExtractFromSource(string source, HashSet<string> allTests, HashSet<string> securityTests)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(allTests);
        ArgumentNullException.ThrowIfNull(securityTests);

        var masked = MaskLiteralsAndComments(source);

        var nsMatch = NamespaceRegex().Match(masked);
        var ns = nsMatch.Success ? nsMatch.Groups[1].Value : "Global";

        var typeMatches = TypeDeclRegex().Matches(masked)
            .Where(m => !string.Equals(m.Groups[1].Value, "HostFixture", StringComparison.Ordinal))
            .ToList();
        if (typeMatches.Count == 0)
        {
            return;
        }

        int outerClassEnd = -1;
        bool outerClassIsSecurity = false;

        for (int i = 0; i < typeMatches.Count; i++)
        {
            var tMatch = typeMatches[i];
            var className = tMatch.Groups[1].Value;
            int classStart = tMatch.Index;
            int bodyEnd = i + 1 < typeMatches.Count ? typeMatches[i + 1].Index : source.Length;

            int prefixStart = i > 0 ? typeMatches[i - 1].Index + typeMatches[i - 1].Length : 0;
            int lastSep = FindLastSeparator(masked.AsSpan(prefixStart, classStart - prefixStart), includeOpenBrace: true);
            int classAttrStart = lastSep >= 0 ? prefixStart + lastSep + 1 : prefixStart;
            var rawClassAttrWindow = source.AsSpan(classAttrStart, classStart - classAttrStart);
            var maskedClassAttrWindow = masked.AsSpan(classAttrStart, classStart - classAttrStart);

            bool ownClassIsSecurity = HasSecurityTrait(rawClassAttrWindow, maskedClassAttrWindow);
            if (classStart > outerClassEnd)
            {
                outerClassEnd = FindTypeBodyEnd(masked, tMatch.Index + tMatch.Length);
                outerClassIsSecurity = ownClassIsSecurity;
            }

            bool classIsSecurity = ownClassIsSecurity || (classStart < outerClassEnd && outerClassIsSecurity);

            int bodyStart = tMatch.Index + tMatch.Length;
            if (bodyStart >= bodyEnd)
            {
                continue;
            }

            var rawClassBody = source.AsSpan(bodyStart, bodyEnd - bodyStart);
            var maskedClassBody = masked.Substring(bodyStart, bodyEnd - bodyStart);
            var methodMatches = MethodDeclRegex().Matches(maskedClassBody);
            foreach (Match mMatch in methodMatches)
            {
                var methodName = mMatch.Groups[1].Value;
                if (string.Equals(methodName, className, StringComparison.Ordinal))
                {
                    continue;
                }

                int lastMethodSep = FindLastSeparator(maskedClassBody.AsSpan(0, mMatch.Index), includeOpenBrace: true);
                int attrStart = lastMethodSep >= 0 ? lastMethodSep + 1 : 0;
                var rawAttrWindow = rawClassBody.Slice(attrStart, mMatch.Index - attrStart);
                var maskedAttrWindow = maskedClassBody.AsSpan(attrStart, mMatch.Index - attrStart);

                if (!FactAttrRegex().IsMatch(maskedAttrWindow))
                {
                    continue;
                }

                var fqn = $"{ns}.{className}.{methodName}";
                allTests.Add(fqn);
                if (classIsSecurity || HasSecurityTrait(rawAttrWindow, maskedAttrWindow))
                {
                    securityTests.Add(fqn);
                }
            }
        }
    }

    internal static string MaskLiteralsAndComments(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0)
        {
            return source;
        }

        var buffer = source.ToCharArray();
        foreach (var token in SyntaxFactory.ParseTokens(source, options: ScannerParseOptions))
        {
            MaskTriviaList(buffer, token.LeadingTrivia);
            if (IsLiteralToMask(token.Kind()))
            {
                MaskSpan(buffer, token.SpanStart, token.Span.Length);
            }

            MaskTriviaList(buffer, token.TrailingTrivia);
        }

        return new string(buffer);
    }

    private static void MaskTriviaList(char[] buffer, SyntaxTriviaList triviaList)
    {
        foreach (var trivia in triviaList)
        {
            if (IsCommentOrDirectiveMessageTrivia(trivia.Kind()))
            {
                MaskSpan(buffer, trivia.SpanStart, trivia.Span.Length);
                continue;
            }

            if (trivia.HasStructure && !trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            {
                var structure = trivia.GetStructure();
                if (structure is not null)
                {
                    foreach (var nested in structure.DescendantTrivia(descendIntoTrivia: true))
                    {
                        if (IsCommentOrDirectiveMessageTrivia(nested.Kind()))
                        {
                            MaskSpan(buffer, nested.SpanStart, nested.Span.Length);
                        }
                    }
                }
            }
        }
    }

    private static bool IsCommentOrDirectiveMessageTrivia(SyntaxKind kind) => kind is
        SyntaxKind.SingleLineCommentTrivia or
        SyntaxKind.MultiLineCommentTrivia or
        SyntaxKind.SingleLineDocumentationCommentTrivia or
        SyntaxKind.MultiLineDocumentationCommentTrivia or
        SyntaxKind.PreprocessingMessageTrivia;

    private static bool IsLiteralToMask(SyntaxKind kind) => kind is
        SyntaxKind.StringLiteralToken or
        SyntaxKind.Utf8StringLiteralToken or
        SyntaxKind.SingleLineRawStringLiteralToken or
        SyntaxKind.MultiLineRawStringLiteralToken or
        SyntaxKind.Utf8SingleLineRawStringLiteralToken or
        SyntaxKind.Utf8MultiLineRawStringLiteralToken or
        SyntaxKind.CharacterLiteralToken or
        SyntaxKind.InterpolatedStringToken or
        SyntaxKind.InterpolatedStringTextToken;

    private static void MaskSpan(char[] buffer, int start, int length)
    {
        int end = Math.Min(buffer.Length, start + length);
        for (int i = Math.Max(0, start); i < end; i++)
        {
            if (buffer[i] is not ('\r' or '\n'))
            {
                buffer[i] = ' ';
            }
        }
    }

    private static int FindLastSeparator(ReadOnlySpan<char> maskedWindow, bool includeOpenBrace)
    {
        int parenDepth = 0;
        int bracketDepth = 0;
        int lastSep = -1;

        for (int i = 0; i < maskedWindow.Length; i++)
        {
            switch (maskedWindow[i])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                    {
                        parenDepth--;
                    }
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                    {
                        bracketDepth--;
                    }
                    break;
                case '}' when parenDepth == 0 && bracketDepth == 0:
                case ';' when parenDepth == 0 && bracketDepth == 0:
                    lastSep = i;
                    break;
                case '{' when includeOpenBrace && parenDepth == 0 && bracketDepth == 0:
                    lastSep = i;
                    break;
            }
        }

        return lastSep;
    }

    private static bool HasSecurityTrait(ReadOnlySpan<char> rawWindow, ReadOnlySpan<char> maskedWindow)
    {
        foreach (var match in SecurityTraitRegex().EnumerateMatches(rawWindow))
        {
            if ((uint)match.Index < (uint)maskedWindow.Length && maskedWindow[match.Index] == '[')
            {
                return true;
            }
        }

        return false;
    }

    public static Dictionary<string, (string NewIdOrReason, string Category)> LoadCoverageMap(string root)
    {
        var mapPath = Path.Combine(root, "tests", "Autheris.Tests.Architecture", "Baselines", "test-coverage-map.csv");
        var result = new Dictionary<string, (string NewIdOrReason, string Category)>(StringComparer.Ordinal);
        if (!File.Exists(mapPath))
        {
            return result;
        }

        var lines = File.ReadAllLines(mapPath);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(',', 3);
            if (parts.Length >= 2)
            {
                var oldId = parts[0].Trim();
                var newIdOrReason = parts[1].Trim();
                var category = parts.Length >= 3 ? parts[2].Trim() : string.Empty;
                if (oldId.Length > 0)
                {
                    result[oldId] = (newIdOrReason, category);
                }
            }
        }

        return result;
    }

    internal static int FindTypeBodyEnd(string source, int searchStart)
    {
        int openBrace = source.IndexOf('{', searchStart);
        if (openBrace < 0)
        {
            return source.Length;
        }

        int depth = 0;
        for (int i = openBrace; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return source.Length;
    }
}

[Trait("Category", "Security")]
public sealed partial class TestCoverageMapTests
{
    private const string BaselineRelativePath = "tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt";
    private const string CoverageMapRelativePath = "tests/Autheris.Tests.Architecture/Baselines/test-coverage-map.csv";

    [GeneratedRegex(@"WP-SIMPL-\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DeletingWpRegex();

    [Fact]
    [Trait("Category", "Security")]
    public void No_security_test_lost_without_mapping()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var baselinePath = Path.Combine(root, BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(baselinePath).ShouldBeTrue($"Missing baseline file: {BaselineRelativePath}");

        var (_, currentSecurityTests) = TestInventoryScanner.ScanRepository();
        var coverageMap = TestInventoryScanner.LoadCoverageMap(root);

        var baseline = File.ReadAllLines(baselinePath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        var unmappedMissing = baseline
            .Where(testId => !currentSecurityTests.Contains(testId) && !coverageMap.ContainsKey(testId))
            .ToList();

        unmappedMissing.ShouldBeEmpty(
            $"WP-SIMPL-23: Security tests from {BaselineRelativePath} were removed or renamed without an entry in {CoverageMapRelativePath}: {string.Join(", ", unmappedMissing.Take(20))}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Map_rows_reference_existing_tests_or_deleting_wp()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var mapPath = Path.Combine(root, CoverageMapRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(mapPath).ShouldBeTrue($"Missing coverage map file: {CoverageMapRelativePath}");

        var (allTests, _) = TestInventoryScanner.ScanRepository();
        var coverageMap = TestInventoryScanner.LoadCoverageMap(root);

        var invalidRows = new List<string>();
        foreach (var (oldId, (newIdOrReason, _)) in coverageMap)
        {
            if (string.IsNullOrWhiteSpace(newIdOrReason))
            {
                invalidRows.Add($"{oldId} -> <empty>");
                continue;
            }

            var candidateTestId = newIdOrReason.Split(' ', 2)[0].Trim();
            bool referencesExistingTest = allTests.Contains(newIdOrReason) || allTests.Contains(candidateTestId);
            bool referencesDeletingWp = DeletingWpRegex().IsMatch(newIdOrReason);

            if (!referencesExistingTest && !referencesDeletingWp)
            {
                invalidRows.Add($"{oldId} -> {newIdOrReason}");
            }
        }

        invalidRows.ShouldBeEmpty(
            $"WP-SIMPL-23: Every row in {CoverageMapRelativePath} must reference an existing test method or a WP-SIMPL-* deletion work package. Invalid rows: {string.Join("; ", invalidRows.Take(20))}");
    }
}
