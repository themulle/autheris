namespace Autheris.Tests.Architecture;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-SIMPL-32, SEC-SIMPL-62: verifies that no enforcement, security, or redaction options were deleted while still wired or referenced.
/// </summary>
public sealed class OptionsAuditTests
{
    private static readonly Regex SecurityKeyword = new(
        @"Enforce|Require|Allowed|Allow|Deny|Mask|Pii|Tls|Auth|Redaction",
        RegexOptions.CultureInvariant);

    [Fact]
    [Trait("Category", "Security")]
    public void No_enforcement_option_deleted_while_wired()
    {
        var removed = RepositoryScan.ReadBaseline("removed-options.txt");
        removed.Count.ShouldBe(75, "The removed-options baseline must contain all 75 deleted options.");

        var assemblies = new[]
        {
            typeof(Autheris.Domain.Options.GatewayOptions).Assembly,
            typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
            typeof(Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository).Assembly,
            typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly,
            typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly,
            typeof(Program).Assembly
        };

        var typesByName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var a in assemblies)
        {
            foreach (var t in a.GetTypes())
            {
                if (t.FullName is not null)
                {
                    typesByName.TryAdd(t.FullName, t);
                }
            }
        }

        var productionFiles = RepositoryScan.ProductionFiles;

        foreach (var entry in removed)
        {
            var relative = entry.StartsWith("Autheris.", StringComparison.Ordinal) ? entry["Autheris.".Length..] : entry;
            if (!SecurityKeyword.IsMatch(relative))
            {
                continue;
            }

            var dot = entry.LastIndexOf('.');
            var typeName = entry[..dot];
            var propertyName = entry[(dot + 1)..];

            // 1. Assert property no longer exists on the type (if type still exists)
            if (typesByName.TryGetValue(typeName, out var type))
            {
                var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                prop.ShouldBeNull($"Enforcement/security option {entry} still exists on {typeName}.");
            }

            // 2. Assert no src/**/*.cs file references .PropertyName
            var memberAccessRegex = new Regex(@"\.\s*" + Regex.Escape(propertyName) + @"\b", RegexOptions.CultureInvariant);
            foreach (var file in productionFiles)
            {
                var text = Regex.Replace(file.Text, @"//[^\n]*|/\*.*?\*/|@""(?:[^""]|"""")*""|""(?:[^""\\\n]|\\.)*""", " ", RegexOptions.Singleline);
                memberAccessRegex.IsMatch(text).ShouldBeFalse(
                    $"Enforcement/security option {entry} is still referenced as .{propertyName} in {file.RelativePath}.");
            }
        }
    }
}
