using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>WP-ARC-A-06: Api code writes audit records only through <c>IAuditWriter</c>.</summary>
public class AuditUsageRulesTests
{
    private static readonly Regex ServiceLocatorForAuditRepository =
        new(@"GetService<[\w\.]*IAuditLogRepository>", RegexOptions.Compiled);

    private static readonly Regex NullGuardedAuditRepository =
        new(@"if\s*\(\s*audit\w*\s*(!=\s*null|is\s+not\s+null)\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void Api_DoesNotResolveIAuditLogRepositoryViaServiceLocator_NorNullGuardsIt()
    {
        var apiDir = Path.Combine(FindRepositoryRoot(), "src", "Autheris.Api");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(apiDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (ServiceLocatorForAuditRepository.IsMatch(text) || NullGuardedAuditRepository.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(apiDir, file));
            }
        }

        offenders.ShouldBeEmpty("Use IAuditWriter (RecordRequiredAsync / RecordBestEffortAsync) instead of GetService<IAuditLogRepository>() or null-guarded audit repositories.");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Autheris.sln) not found.");
    }
}
