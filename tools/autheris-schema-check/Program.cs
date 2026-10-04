namespace Autheris.Tools.SchemaCheck;

using System.Net.Http.Json;
using System.Text.Json;
using Autheris.Application.SchemaRegistry;

internal sealed class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        if (command is not ("check" or "publish"))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Unknown command '{command}'. Available commands: check, publish.");
            Console.ResetColor();
            PrintUsage();
            return 1;
        }

        string? baselinePath = GetArgValue(args, "--baseline");
        string? targetPath = GetArgValue(args, "--target");
        string? url = GetArgValue(args, "--url");
        string? service = GetArgValue(args, "--service");
        bool force = args.Contains("--force");
        bool verbose = args.Contains("--verbose") || args.Contains("-v");

        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Target schema file not found or not specified via --target.");
            Console.ResetColor();
            return 1;
        }

        string targetSdl = await File.ReadAllTextAsync(targetPath);

        // Scenario 1: Local Diff between two files
        if (!string.IsNullOrWhiteSpace(baselinePath))
        {
            if (!File.Exists(baselinePath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Baseline schema file '{baselinePath}' not found.");
                Console.ResetColor();
                return 1;
            }

            string baselineSdl = await File.ReadAllTextAsync(baselinePath);
            var linter = new SchemaLinter();

            Console.WriteLine($"[Schema Check] Comparing baseline '{baselinePath}' with target '{targetPath}'...");
            var diff = linter.Compare(baselineSdl, targetSdl);
            PrintDiff(diff, verbose);

            return diff.HasBreakingChanges ? 1 : 0;
        }

        // Scenario 2: Remote Check or Publish against Gateway Schema Registry
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (string.IsNullOrWhiteSpace(service))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: --service <name> is required when checking or publishing against a remote registry.");
                Console.ResetColor();
                return 1;
            }

            using var httpClient = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };

            if (command == "check")
            {
                Console.WriteLine($"[Schema Check] Checking '{targetPath}' against service '{service}' at {url}...");
                var request = new SchemaRegistrationRequest
                {
                    ServiceName = service,
                    Sdl = targetSdl,
                    DryRun = true
                };

                var response = await httpClient.PostAsJsonAsync("api/schema-registry/check", request);
                if (!response.IsSuccessStatusCode)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Server returned {(int)response.StatusCode} {response.ReasonPhrase}");
                    Console.ResetColor();
                    return 1;
                }

                var json = await response.Content.ReadFromJsonAsync<RemoteCheckResult>();
                if (json == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Failed to parse registry response.");
                    Console.ResetColor();
                    return 1;
                }

                PrintRemoteChanges(json.Changes, verbose);
                Console.WriteLine($"Summary: {json.BreakingCount} breaking, {json.DangerousCount} dangerous, {json.SafeCount} safe change(s).");

                if (json.HasBreakingChanges)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("FAIL: Breaking changes detected! Schema check rejected.");
                    Console.ResetColor();
                    return 1;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("SUCCESS: Target schema is backwards-compatible.");
                Console.ResetColor();
                return 0;
            }

            if (command == "publish")
            {
                Console.WriteLine($"[Schema Publish] Publishing '{targetPath}' to service '{service}' at {url} (Force: {force})...");
                var request = new SchemaRegistrationRequest
                {
                    ServiceName = service,
                    Sdl = targetSdl,
                    ForceIfBreaking = force,
                    DryRun = false
                };

                var response = await httpClient.PostAsJsonAsync("api/schema-registry/publish", request);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Publish rejected ({(int)response.StatusCode}): {content}");
                    Console.ResetColor();
                    return 1;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"SUCCESS: Schema registered successfully! Response: {content}");
                Console.ResetColor();
                return 0;
            }
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Error: You must specify either --baseline <file> or --url <url> with --service <name>.");
        Console.ResetColor();
        PrintUsage();
        return 1;
    }

    private static void PrintDiff(SchemaDiffResult diff, bool verbose)
    {
        if (diff.Changes.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("No changes detected. Schemas are identical.");
            Console.ResetColor();
            return;
        }

        foreach (var change in diff.Changes)
        {
            switch (change.ChangeType)
            {
                case SchemaChangeType.Breaking:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[BREAKING] {change.Code}: {change.Path} - {change.Description}");
                    break;

                case SchemaChangeType.Dangerous:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[DANGEROUS] {change.Code}: {change.Path} - {change.Description}");
                    break;

                case SchemaChangeType.Safe:
                    if (verbose)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[SAFE] {change.Code}: {change.Path} - {change.Description}");
                    }
                    break;
            }
        }
        Console.ResetColor();

        Console.WriteLine();
        Console.WriteLine($"Total Changes: {diff.Changes.Count} ({diff.BreakingCount} breaking, {diff.DangerousCount} dangerous, {diff.SafeCount} safe)");

        if (diff.HasBreakingChanges)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAIL: Breaking changes detected! Schema change would break existing clients.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("SUCCESS: Schema change is backwards-compatible.");
            Console.ResetColor();
        }
    }

    private static void PrintRemoteChanges(List<RemoteSchemaChange>? changes, bool verbose)
    {
        if (changes == null || changes.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("No changes detected.");
            Console.ResetColor();
            return;
        }

        foreach (var change in changes)
        {
            if (change.IsBreaking)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[BREAKING] {change.Code}: {change.Path} - {change.Description}");
            }
            else if (change.ChangeType == 1)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[DANGEROUS] {change.Code}: {change.Path} - {change.Description}");
            }
            else if (verbose)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SAFE] {change.Code}: {change.Path} - {change.Description}");
            }
        }
        Console.ResetColor();
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        autheris-schema-check - GraphQL Schema Compatibility & CI/CD Linter

        Usage:
          autheris-schema-check check --baseline <file> --target <file> [--verbose]
          autheris-schema-check check --url <url> --service <name> --target <file> [--verbose]
          autheris-schema-check publish --url <url> --service <name> --target <file> [--force]

        Options:
          --baseline <path>     Path to baseline schema file (.graphql / .gql)
          --target <path>       Path to target/incoming schema file (.graphql / .gql)
          --url <url>           Gateway API URL (e.g. http://localhost:5000)
          --service <name>      Service/subgraph name in the schema registry
          --force               Force registration even if breaking changes exist
          --verbose, -v         Display safe changes as well
          -h, --help            Show this help text
        """);
    }

    private sealed class RemoteCheckResult
    {
        public string? ServiceName { get; set; }
        public bool IsCompatible { get; set; }
        public bool HasBreakingChanges { get; set; }
        public int BreakingCount { get; set; }
        public int DangerousCount { get; set; }
        public int SafeCount { get; set; }
        public List<RemoteSchemaChange>? Changes { get; set; }
    }

    private sealed class RemoteSchemaChange
    {
        public int ChangeType { get; set; }
        public string? Code { get; set; }
        public string? Path { get; set; }
        public string? Description { get; set; }
        public bool IsBreaking { get; set; }
    }
}
