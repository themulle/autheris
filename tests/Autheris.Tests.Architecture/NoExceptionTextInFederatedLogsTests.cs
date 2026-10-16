using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SEC-FED-DELTA-01 guard: on the federated, OLAP and WebSQL paths a log call at Information or above must not receive an
/// exception object or <c>Exception.Message</c>; both can quote staged data values. Use <c>SafeLog.Warning/Error</c>
/// (type name at Warning and Error, full exception at Debug).
/// </summary>
[Trait("Category", "Security")]
public sealed partial class NoExceptionTextInFederatedLogsTests
{
    private static readonly string[] ScopedFiles =
    [
        "WebSqlEndpoints*.cs", "DuckDbOlapEndpoints.cs", "SqlErrorResponse.cs", "OlapStagingService.cs",
        "FederatedStagingService.cs", "FederatedDuckDbExecutionService.cs", "GovernedSqlRewriter.cs", "WebSqlStatementManager.cs"
    ];

    [GeneratedRegex(@"\.Log(Information|Warning|Error|Critical)\(")]
    private static partial Regex HighLevelLogCall();

    [GeneratedRegex(@"\.Log(Information|Warning|Error|Critical)\(\s*\w*([eE]x|[eE]xception)\w*\s*,|\b\w*([eE]x|[eE]xception)\w*\.(Message|ToString\(\))")]
    private static partial Regex ExceptionTextInCall();

    [Fact]
    public void ScopedFiles_DoNotLogExceptionObjectsOrMessagesAboveDebug()
    {
        var root = FindSrcDirectory();
        var files = ScopedFiles.SelectMany(p => Directory.EnumerateFiles(root, p, SearchOption.AllDirectories)).Distinct().ToList();
        files.Count.ShouldBeGreaterThanOrEqualTo(ScopedFiles.Length);

        var violations = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!HighLevelLogCall().IsMatch(lines[i]) || lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                // A call may span lines: join up to the terminating semicolon.
                var statement = lines[i];
                for (var j = i + 1; j < lines.Length && !statement.Contains(';'); j++)
                {
                    statement += " " + lines[j].Trim();
                }

                if (ExceptionTextInCall().IsMatch(statement))
                {
                    violations.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        violations.ShouldBeEmpty("Use SafeLog.Warning/Error so only the exception type is logged above Debug:\n" + string.Join("\n", violations));
    }

    private static string FindSrcDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "Autheris.Api")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src from the test output directory.");
    }
}
