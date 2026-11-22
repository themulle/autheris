namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class LegacyRoleStringRatchetTests
{
    [GeneratedRegex(@"""(DataOwner|DataSteward|Auditor|Analyst|Operator|SchemaAdmin|SecurityAdmin|GatewayAdmin|PlatformAdmin|PrivacyAdmin|DataProtectionOfficer|AiAgent|McpCaller|Developer)""", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyRoleLiteralRegex();

    [Fact]
    public void No_legacy_role_literals_in_src()
    {
        var offenders = new List<string>();

        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var lines = file.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (LegacyRoleLiteralRegex().IsMatch(line))
                {
                    offenders.Add($"{file.RelativePath}:{i + 1}: {line.Trim()}");
                }
            }
        }

        foreach (var relPath in RepositoryScan.EnumerateFiles("src", "appsettings*.json"))
        {
            var text = RepositoryScan.ReadText(relPath);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (LegacyRoleLiteralRegex().IsMatch(line))
                {
                    offenders.Add($"{relPath}:{i + 1}: {line.Trim()}");
                }
            }
        }

        foreach (var relPath in RepositoryScan.EnumerateFiles("modules", "*.cs"))
        {
            if (relPath.Contains(".Tests/", StringComparison.Ordinal))
            {
                continue;
            }

            var text = RepositoryScan.ReadText(relPath);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (LegacyRoleLiteralRegex().IsMatch(line))
                {
                    offenders.Add($"{relPath}:{i + 1}: {line.Trim()}");
                }
            }
        }

        offenders.ShouldBeEmpty("Legacy role string literals must not appear in src/, appsettings*.json, or module production code.");
    }
}
