using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Snapshot of the security-relevant defaults of <see cref="GatewayOptions"/> (every bool or enum option whose path names an
/// enforcement, authentication, TLS, masking, PII, redaction or insecure-bypass control). A change that weakens a default fails here
/// until the snapshot file is updated in a reviewed change. Regenerate with <c>AUTHERIS_UPDATE_SECURE_DEFAULTS=1</c>.
/// </summary>
[Trait("Category", "Security")]
public sealed class SecureDefaultsSnapshotTests
{
    private const string SnapshotFile = "secure-defaults.txt";
    private const int MaxDepth = 8;

    private static readonly Regex SecurityPath = new(
        @"Enforce|Require|Allow|Deny|Mask|Pii|Tls|Auth|Redact|Insecure|danger_|Anonymous|Cors|RateLimit|Encrypt|Hitl|StepUp|RestrictedScope|MassChange|TenantRegistry|VerifyHashChain",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    [Fact]
    public void Security_defaults_unchanged()
    {
        var current = Snapshot();
        current.ShouldNotBeEmpty();

        var path = Path.Combine(RepositoryScan.RepoRoot, "tests", "Autheris.Tests.Architecture", "Baselines", SnapshotFile);
        var update = Environment.GetEnvironmentVariable("AUTHERIS_UPDATE_SECURE_DEFAULTS");
        if (string.Equals(update, "1", StringComparison.Ordinal) || string.Equals(update, "true", StringComparison.OrdinalIgnoreCase))
        {
            // Regeneration never passes: the rewritten snapshot must be reviewed and committed, and CI must not regenerate it.
            Environment.GetEnvironmentVariable("CI").ShouldBeNull("The secure-defaults snapshot cannot be regenerated in CI.");
            File.WriteAllText(path, Render(current));
            throw new InvalidOperationException($"{SnapshotFile} regenerated; review the diff, commit it and re-run without AUTHERIS_UPDATE_SECURE_DEFAULTS.");
        }

        var expected = RepositoryScan.ReadBaseline(SnapshotFile);
        current.ShouldBe(
            expected,
            "Security-relevant option defaults changed. Weakening a default needs a reviewed snapshot update; set AUTHERIS_UPDATE_SECURE_DEFAULTS=1 to regenerate.");
    }

    private static string Render(List<string> lines)
    {
        var sb = new StringBuilder();
        sb.Append("# Security-relevant option defaults of GatewayOptions. Format: Path=Default. Regenerate with AUTHERIS_UPDATE_SECURE_DEFAULTS=1.\n");
        foreach (var line in lines)
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static List<string> Snapshot()
    {
        var lines = new List<string>();
        Walk(new GatewayOptions(), "Gateway", 0, lines);
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static void Walk(object instance, string path, int depth, List<string> lines)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
            {
                continue;
            }

            var childPath = path + ":" + property.Name;
            var type = property.PropertyType;
            if (type == typeof(bool) || type.IsEnum)
            {
                if (SecurityPath.IsMatch(childPath))
                {
                    var value = property.GetValue(instance);
                    lines.Add($"{childPath}={value}");
                }

                continue;
            }

            if (type.IsPrimitive || type == typeof(string) || type.IsValueType || typeof(IEnumerable).IsAssignableFrom(type)
                || type.Namespace is null || !type.Namespace.StartsWith("Autheris.Domain.Options", StringComparison.Ordinal))
            {
                continue;
            }

            object? child;
            try
            {
                child = property.GetValue(instance);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            if (child is not null)
            {
                Walk(child, childPath, depth + 1, lines);
            }
        }
    }
}
