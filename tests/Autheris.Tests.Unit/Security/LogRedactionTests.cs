namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

/// <summary>
/// Logs of the permission request services carry request ids and codes only: never justification text, group lists, token or
/// signature material, webhook URLs or sample values. The scan covers every logger call of the services of the request lifecycle.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class LogRedactionTests
{
    private static readonly string[] ScannedPaths =
    [
        "src/Autheris.Application/Governance/Requests",
        "src/Autheris.Application/Governance/Notifications",
        "src/Autheris.Application/Governance/Analysis/BundledConsentRequestService.cs",
        "src/Autheris.Application/ConsentRequests/ExternalDecisionGuard.cs",
        "src/Autheris.Application/ConsentRequests/ExternalDecisionService.cs",
        "src/Autheris.Application/ConsentRequests/RealmExternalApproverResolver.cs",
        "src/Autheris.Application/Audit/AuditReadService.cs"
    ];

    // Identifiers that must never appear as a log argument.
    private static readonly string[] Forbidden =
    [
        "Justification", "Reason", "Group", "Url", "Uri", "Webhook", "Secret", "Token", "Signature", "Nonce", "Email", ".Message"
    ];

    // Every ILogger / ILogger<T> field, parameter or local of a file: the receiver names are resolved per file.
    [GeneratedRegex(@"\bILogger(?:<[^>]+>)?\??\s+(?<name>@?[A-Za-z_]\w*)")]
    private static partial Regex LoggerDeclaration();

    // Source-generated logging methods: the parameter list is the argument surface.
    [GeneratedRegex(@"\[LoggerMessage\b[^\]]*\][^;{]*?\((?<args>[^)]*)\)\s*;", RegexOptions.Singleline)]
    private static partial Regex LoggerMessageMethod();

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [Fact]
    public void No_justification_group_or_url_in_logs()
    {
        var root = FindRepoRoot();
        var files = ScannedPaths
            .Select(p => Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar)))
            .SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.cs", SearchOption.AllDirectories) : [p])
            .Where(File.Exists)
            .ToList();
        files.ShouldNotBeEmpty();

        var violations = new List<string>();
        var callsSeen = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var arguments in LogArguments(text))
            {
                callsSeen++;
                foreach (var word in Forbidden)
                {
                    if (arguments.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"{Path.GetFileName(file)}: log argument mentions '{word}'");
                    }
                }
            }
        }

        callsSeen.ShouldBeGreaterThan(0, "The scan must see the logger calls of the scanned services.");
        violations.ShouldBeEmpty();
    }

    /// <summary>The argument text (string literals blanked: the message template is free text) of every logger call and generated logging method of a file.</summary>
    private static IEnumerable<string> LogArguments(string text)
    {
        var names = LoggerDeclaration().Matches(text).Select(m => m.Groups["name"].Value.TrimStart('@')).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length > 0)
        {
            var callPattern = new Regex(
                @"\b(?:" + string.Join('|', names.Select(Regex.Escape)) + @")\??\.Log\w*\(",
                RegexOptions.CultureInvariant);
            foreach (Match call in callPattern.Matches(text))
            {
                yield return StringLiteral().Replace(BalancedArguments(text, call.Index + call.Length), "\"\"");
            }
        }

        foreach (Match method in LoggerMessageMethod().Matches(text))
        {
            yield return StringLiteral().Replace(method.Groups["args"].Value, "\"\"");
        }
    }

    // text from just after an opening parenthesis up to its matching closing one (string literals skipped)
    private static string BalancedArguments(string text, int start)
    {
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"':
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        i += text[i] == '\\' ? 2 : 1;
                    }

                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return text[start..i];
                    }

                    break;
            }
        }

        return text[start..];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
