using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Roslyn syntax scanner for SEC-SIMPL-26: governance SQL is built only from constants and bound parameters.
/// Flags interpolated SQL, string.Format, non-constant '+' concatenation and StringBuilder.Append(non-constant)
/// at statement level (multi-line aware). Exceptions are per file + member and need a reason.
/// </summary>
internal static class GovernanceSqlConstructionScanner
{
    private static readonly Regex SqlWord = new(@"\b(SELECT|INSERT|UPDATE|DELETE|MERGE|FROM|WHERE|ORDER BY|JOIN)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal sealed record Hit(string File, string Member, int Line, string Rule);

    public static List<Hit> Scan(string fileName, string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var hits = new List<Hit>();

        foreach (var node in root.DescendantNodes())
        {
            string? rule = node switch
            {
                InterpolatedStringExpressionSyntax i when SqlWord.IsMatch(i.ToString()) => "interpolated-sql",
                InvocationExpressionSyntax inv when IsStringFormat(inv) => "string.Format",
                InvocationExpressionSyntax inv when IsAppendOfNonConstant(inv) => "StringBuilder.Append(non-constant)",
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression)
                    && b.Parent is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression }
                    && !IsConstantConcat(b) && TouchesSql(b) => "non-constant-concatenation",
                _ => null,
            };

            if (rule is not null)
            {
                hits.Add(new Hit(fileName, MemberOf(node), node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, rule));
            }
        }

        return hits;
    }

    // A '+' chain is SQL-relevant when any literal operand carries SQL text or it feeds a CommandText/SQL-named target.
    private static bool TouchesSql(BinaryExpressionSyntax b) =>
        b.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().Any(l => l.IsKind(SyntaxKind.StringLiteralExpression) && SqlWord.IsMatch(l.Token.ValueText))
        || b.Ancestors().OfType<StatementSyntax>().FirstOrDefault()?.ToString().Contains("CommandText", StringComparison.Ordinal) == true;

    private static bool IsConstantConcat(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax => true,
        ParenthesizedExpressionSyntax p => IsConstantConcat(p.Expression),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) => IsConstantConcat(b.Left) && IsConstantConcat(b.Right),
        _ => false,
    };

    private static bool IsStringFormat(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Format", Expression: PredefinedTypeSyntax or IdentifierNameSyntax { Identifier.ValueText: "String" } };

    private static bool IsAppendOfNonConstant(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Append" or "AppendLine" }
        && inv.ArgumentList.Arguments.Count > 0
        && !IsConstantConcat(inv.ArgumentList.Arguments[0].Expression)
        && !IsChainBuilderOfLiteral(inv);

    private static bool IsChainBuilderOfLiteral(InvocationExpressionSyntax inv) =>
        inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax;

    public static string MemberOf(SyntaxNode node) =>
        node.Ancestors().Select(a => a switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            ConstructorDeclarationSyntax c => c.Identifier.ValueText,
            FieldDeclarationSyntax f => f.Declaration.Variables[0].Identifier.ValueText,
            _ => null,
        }).FirstOrDefault(n => n is not null) ?? "<type>";

    internal sealed record LoggerCall(string File, string Member, IReadOnlyList<string> Arguments);

    internal sealed record ConsentCall(string File, string Method);

    /// <summary>Invocations on a receiver named <c>_logger</c>/<c>logger</c>, including conditional access (<c>_logger?.LogX</c>).</summary>
    public static List<LoggerCall> LoggerCalls(string fileName, string source)
    {
        var result = new List<LoggerCall>();
        foreach (var inv in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var receiver = inv.Expression is MemberAccessExpressionSyntax m ? m.Expression
                : inv.Parent is ConditionalAccessExpressionSyntax c ? c.Expression : null;
            if (receiver is IdentifierNameSyntax { Identifier.ValueText: "_logger" or "logger" })
            {
                result.Add(new LoggerCall(fileName, MemberOf(inv), inv.ArgumentList.Arguments.Select(a => a.Expression.ToString()).ToList()));
            }
        }

        return result;
    }

    /// <summary>Calls of the tenant-less consent overloads (no tenant argument): 3 positional args, or 4 with a cancellation token last.</summary>
    public static List<ConsentCall> TenantlessConsentCalls(string file, string source)
    {
        var result = new List<ConsentCall>();
        foreach (var inv in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = inv.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
                IdentifierNameSyntax i => i.Identifier.ValueText,
                _ => null,
            };
            if (name is not ("GetActiveConsentsForSubjectsAsync" or "GetAllActiveConsentsForSubjectsAsync"))
            {
                continue;
            }

            var args = inv.ArgumentList.Arguments;
            if (args.Any(a => a.NameColon?.Name.Identifier.ValueText == "tenantId"))
            {
                continue;
            }

            var last = args.Count == 4 ? args[3].Expression.ToString() : "";
            var tenantless = args.Count <= 3 || (args.Count == 4 && Regex.IsMatch(last, @"^(ct|token|cancellationToken|cancel\w*|CancellationToken\.\w+)$", RegexOptions.IgnoreCase));
            if (tenantless)
            {
                result.Add(new ConsentCall(file, name));
            }
        }

        return result;
    }
}
