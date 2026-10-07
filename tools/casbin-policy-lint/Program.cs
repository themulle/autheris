using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Casbin;
using Casbin.Model;
using Autheris.Application.Governance;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace CasbinPolicyLint;

public static class Program
{
    private static readonly string[] DangerousPatterns =
    [
        "System.", "System;", "Process", "File.", "Directory.", "Assembly", "GetType", "Activator",
        "Environment.", "AppDomain", "MethodInfo", "Invoke"
    ];

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== Casbin Policy Linter & CI Gate ===");
        string searchDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        Console.WriteLine($"Scanning directory: {searchDir}");

        var confFiles = EnumerateRepositoryFiles(searchDir, "*.conf");
        var csvFiles = EnumerateRepositoryFiles(searchDir, "*.csv");

        int errors = 0;

        foreach (var conf in confFiles)
        {
            var content = File.ReadAllText(conf);
            // *.conf is also used by nginx and others; only files with a Casbin request definition are models.
            if (!IsCasbinModel(content))
            {
                Console.WriteLine($"Skipping non-Casbin config: {conf}");
                continue;
            }

            Console.WriteLine($"Linting model config: {conf}");
            try
            {
                var model = DefaultModel.CreateFromText(content);
                var enforcer = new Enforcer(model);
                Console.WriteLine($"  [OK] Model parsed successfully.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [ERROR] Failed to parse model '{conf}': {ex.Message}");
                Console.ResetColor();
                errors++;
            }
        }

        foreach (var csv in csvFiles)
        {
            var lines = File.ReadAllLines(csv);
            // Only CSV files with Casbin policy ('p') or grouping ('g') lines are policies (not e.g. benchmark results).
            if (!IsCasbinPolicy(lines))
            {
                Console.WriteLine($"Skipping non-policy CSV: {csv}");
                continue;
            }

            Console.WriteLine($"Linting policy CSV: {csv}");
            int lineNo = 0;
            foreach (var line in lines)
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

                foreach (var danger in DangerousPatterns)
                {
                    if (line.Contains(danger, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  [ERROR] {csv}:{lineNo} contains forbidden keyword '{danger}'");
                        Console.ResetColor();
                        errors++;
                    }
                }
            }
        }

        // Dry-run synthetic test
        Console.WriteLine("Running dry-run synthetic ABAC verification...");
        try
        {
            var service = new CasbinEnforcementService();
            var tenant = new TenantId("lint-tenant");
            service.AddPolicy(tenant, "test-user", "finance.dbo.invoices", "read", "true", "allow");

            var ctx = new SecurityEvaluationContext(
                new Sid("test-user"),
                [],
                tenant,
                new TableIdentifier("finance", "dbo", "invoices"),
                ["id"],
                IPAddress.Loopback,
                DateTimeOffset.UtcNow,
                null);

            var result = await service.EvaluatePolicyAsync(ctx);
            if (!result.IsAllowed)
            {
                Console.WriteLine("  [ERROR] Synthetic allow policy failed evaluation.");
                errors++;
            }
            else
            {
                Console.WriteLine("  [OK] Synthetic dry-run evaluation passed.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [ERROR] Synthetic dry-run threw exception: {ex.Message}");
            errors++;
        }

        if (errors > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Casbin Policy Lint failed with {errors} error(s).");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("All Casbin policies and models passed validation.");
        Console.ResetColor();
        return 0;
    }

    private static readonly string[] ExcludedDirectories = ["bin", "obj", ".git", "node_modules"];

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
        lines.Select(l => l.TrimStart())
            .Any(l => Regex.IsMatch(l, @"^[pg]\d*\s*,", RegexOptions.IgnoreCase));
}
