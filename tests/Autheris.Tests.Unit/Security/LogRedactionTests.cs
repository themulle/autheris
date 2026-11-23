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

    [GeneratedRegex(@"\b_?logger\??\.Log(?:Trace|Debug|Information|Warning|Error|Critical)\((?<args>.*?)\);", RegexOptions.Singleline)]
    private static partial Regex LogCall();

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
            foreach (Match call in LogCall().Matches(File.ReadAllText(file)))
            {
                callsSeen++;
                // The message template is free text; only the arguments are inspected.
                var arguments = StringLiteral().Replace(call.Groups["args"].Value, "\"\"");
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
