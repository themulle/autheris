using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CasbinPolicyLint;

/// <summary>
/// CI gate kept for the existing policy-lint workflow. Casbin is no longer part of Autheris (data access is granted only by grant
/// records, column rules and virtual filters), so a Casbin model or policy file in the repository is an error: it would suggest a
/// restriction that nothing enforces.
/// </summary>
public static partial class Program
{
    private static readonly string[] ExcludedDirectories = ["bin", "obj", ".git", "node_modules"];

    public static int Main(string[] args)
    {
        Console.WriteLine("=== Casbin removal gate ===");
        string searchDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        Console.WriteLine($"Scanning directory: {searchDir}");

        var offenders = EnumerateRepositoryFiles(searchDir, "*.conf")
            .Where(path => IsCasbinModel(File.ReadAllText(path)))
            .Concat(EnumerateRepositoryFiles(searchDir, "*.csv").Where(path => IsCasbinPolicy(File.ReadAllLines(path))))
            .ToList();

        if (offenders.Count > 0)
        {
            foreach (var offender in offenders)
            {
                Console.WriteLine($"  [ERROR] Casbin model or policy file found: {Path.GetRelativePath(searchDir, offender)}");
            }

            Console.WriteLine(
                "Casbin was removed; express the rule as grant records (consents), deny consents, column rules or virtual filters instead.");
            return 1;
        }

        Console.WriteLine("No Casbin model or policy files found.");
        return 0;
    }

    private static string[] EnumerateRepositoryFiles(string root, string pattern) =>
        Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static bool IsCasbinModel(string content) =>
        content.Contains("[request_definition]", StringComparison.OrdinalIgnoreCase);

    private static bool IsCasbinPolicy(string[] lines) =>
        lines.Select(l => l.TrimStart()).Any(l => PolicyLine().IsMatch(l));

    [GeneratedRegex(@"^[pg]\d*\s*,", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyLine();
}
