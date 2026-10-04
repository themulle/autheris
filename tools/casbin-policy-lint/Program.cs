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

        var confFiles = Directory.GetFiles(searchDir, "*.conf", SearchOption.AllDirectories);
        var csvFiles = Directory.GetFiles(searchDir, "*.csv", SearchOption.AllDirectories);

        int errors = 0;

        foreach (var conf in confFiles)
        {
            Console.WriteLine($"Linting model config: {conf}");
            var content = File.ReadAllText(conf);
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
            Console.WriteLine($"Linting policy CSV: {csv}");
            var lines = File.ReadAllLines(csv);
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
}
