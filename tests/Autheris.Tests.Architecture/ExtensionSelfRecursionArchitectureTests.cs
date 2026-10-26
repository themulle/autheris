using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT WP4.3 regression: a wrapper extension method that calls an extension of the same name on its own
/// <c>this</c> parameter binds to itself (after a rename or split) and overflows the stack at startup.
/// </summary>
public sealed partial class ExtensionSelfRecursionArchitectureTests
{
    [GeneratedRegex(@"static\s+[\w<>\[\],.?\s]+?\s(?<name>\w+)\s*(?:<[^>()]*>)?\s*\(\s*this\s+[\w<>\[\],.?]+\s+(?<param>\w+)\s*[,)]", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionDeclaration();

    /// <summary>Finds extension methods whose body invokes the same name on their own this-parameter. Fail-closed on unbalanced braces.</summary>
    internal static IReadOnlyList<string> FindSelfRecursiveExtensions(string text)
    {
        var findings = new List<string>();
        foreach (Match m in ExtensionDeclaration().Matches(text))
        {
            var name = m.Groups["name"].Value;
            var param = m.Groups["param"].Value;
            var parenClose = FindMatching(text, text.IndexOf('(', m.Index + m.Groups["name"].Index - m.Index), '(', ')');
            var i = parenClose + 1;
            while (i < text.Length && text[i] != '{' && text[i] != ';' && !(text[i] == '=' && i + 1 < text.Length && text[i + 1] == '>'))
            {
                i++;
            }

            if (i >= text.Length || text[i] == ';')
            {
                continue;
            }

            var body = text[i] == '{'
                ? text[i..(FindMatching(text, i, '{', '}') + 1)]
                : text[i..(text.IndexOf(';', i) < 0 ? text.Length : text.IndexOf(';', i))];
            if (Regex.IsMatch(body, @"\b" + Regex.Escape(param) + @"\s*\.\s*" + Regex.Escape(name) + @"\s*[(<]", RegexOptions.CultureInvariant))
            {
                findings.Add(name);
            }
        }

        return findings;
    }

    private static int FindMatching(string text, int open, char openChar, char closeChar)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == openChar)
            {
                depth++;
            }
            else if (text[i] == closeChar && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Unbalanced " + openChar + closeChar + " while scanning an extension method.");
    }

    [Fact]
    public void SelfTest_PlantedSelfRecursiveExtension_IsDetected()
    {
        const string planted = "public static class X { public static IFoo Map(this IFoo app) { app.Map(); return app; } }";
        FindSelfRecursiveExtensions(planted).ShouldBe(["Map"]);
        FindSelfRecursiveExtensions("public static class X { public static IFoo Map(this IFoo app) => app.Map(1); }").ShouldBe(["Map"]);
    }

    [Fact]
    public void SelfTest_DelegationToDifferentNameOrQualifiedCall_IsNotFlagged()
    {
        FindSelfRecursiveExtensions("static class X { public static IFoo Map(this IFoo app) { app.MapOther(); return app; } }").ShouldBeEmpty();
        FindSelfRecursiveExtensions("static class X { public static IFoo Map(this IFoo app) { Other.Map(app); return app; } }").ShouldBeEmpty();
    }

    [Fact]
    public void NoProductionExtensionMethod_CallsItsOwnNameOnItsThisParameter()
    {
        RepositoryScan.ProductionFiles.Count.ShouldBeGreaterThan(100);
        var offenders = RepositoryScan.ProductionFiles
            .SelectMany(f => FindSelfRecursiveExtensions(f.Text).Select(n => f.RelativePath + ": " + n))
            .ToList();
        offenders.ShouldBeEmpty();
    }
}
