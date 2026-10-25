using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// REQ-SIMTEST WP0 and SEC-SIM-03/05..09/13..17/21/22: the simulation suites (Autheris.Tests.Simulation, Autheris.Tests.Fuzz, Autheris.Sim.ApiHost)
/// must be reachable only from <c>simulation-nightly.yml</c>, and that workflow must stay least-privilege, SHA-pinned, secret-free and input-safe.
/// The scanners are pure (testable on samples), fail closed on a missing repo root or workflow, and each guard test ends with a negative self-check
/// on an in-memory sample.
/// </summary>
public sealed partial class SimulationIsolationArchitectureTests
{
    private const string Workflow = "simulation-nightly.yml";
    private const string SimProject = "Autheris.Tests.Simulation";
    private const string FuzzProject = "Autheris.Tests.Fuzz";
    private const string ApiHostProject = "Autheris.Sim.ApiHost";
    private static readonly string[] SimProjects = [SimProject, FuzzProject, ApiHostProject];

    private static readonly string[] AllowedTriggers = ["schedule", "workflow_dispatch"];
    private static readonly string[] AllowedActions = ["actions/checkout", "actions/setup-dotnet", "actions/upload-artifact"];

    // sha256 of the five sqllogictest files in the plan manifest (section 1 item 7); content hashes, so the manifest itself is not matched.
    private static readonly string[] SltFileHashes =
    [
        "e93b83d64d06f78aee0e690455b6c604e86ad9a339f77d927a782cefb6b0e1d5",
        "a8ecc3d206c4d4b2cd6a154c18999e558ec97168cd7e327a4369e23aaf31be64",
        "d5c321683bfe903c9be95ebe80a8d23da4d8f4a39193b2087dbc34cb4e137623",
        "155ff6bb9bbf7c2dcf1e5659bb1688dec5dab58126f8dc66d23dcae6df43f59e",
        "049a5d0bf90999c56db2d5880ef28febdc88906526f87069cc96af9f84c99869",
    ];

    [GeneratedRegex(@"(?m)^permissions:\s*\{\}\s*$")]
    private static partial Regex TopLevelEmptyPermissions();

    [GeneratedRegex(@"\buses:\s*(?<action>[^@\s]+)@(?<ref>[^\s#]+)(?<comment>\s+#\s*v\S+)?")]
    private static partial Regex UsesPattern();

    [GeneratedRegex(@"(?m)^  ([a-z][a-z0-9\-]*):\s*$")]
    private static partial Regex JobHeader();

    [GeneratedRegex(@"schemathesis/schemathesis:\d+\.\d+\.\d+@sha256:[0-9a-f]{64}")]
    private static partial Regex PinnedSchemathesis();

    [GeneratedRegex(@"(?<![\w./$-])(?<cmd>""?\S*sharpfuzz""?)\s+(?<arg>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SharpFuzzCall();

    [GeneratedRegex(@"^""?\$\{?RUNNER_TEMP\}?/fuzz-bin/TrinoSqlEngine\.dll""?$")]
    private static partial Regex InstrumentedTarget();

    [GeneratedRegex(@"^\s+[A-Z][A-Z0-9_]*:\s*\$\{\{\s*inputs\.\w+\s*\}\}\s*$")]
    private static partial Regex EnvInputAssignment();

    [GeneratedRegex(@"(?m)^query [IRT]+ (nosort|rowsort|valuesort)")]
    private static partial Regex SltQueryRecord();

    [GeneratedRegex(@"\bSIM-GAP-\d{3}\b")]
    private static partial Regex GapId();

    [GeneratedRegex(@"\bclass\s+(?<name>\w+)(?:\([^)]*\))?\s*(?::\s*(?<bases>[^\n{]+))?")]
    private static partial Regex ClassDecl();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Commit40();

    // ---------- file helpers ----------

    private static bool IsSkippedDir(string name) => name is "bin" or "obj" or "node_modules" or ".git";

    internal static IEnumerable<string> EnumerateFiles(string dir, Func<string, bool> match)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir).Where(match))
        {
            yield return file;
        }

        foreach (var sub in Directory.EnumerateDirectories(dir).Where(d => !IsSkippedDir(Path.GetFileName(d))))
        {
            foreach (var file in EnumerateFiles(sub, match))
            {
                yield return file;
            }
        }
    }

    private static string Root => ParityIsolationArchitectureTests.FindRepoRoot();

    private static string SimWorkflowText() =>
        ParityIsolationArchitectureTests.ReadWorkflows(Root).TryGetValue(Workflow, out var text)
            ? text
            : throw new InvalidOperationException($"{Workflow} not found: the simulation guard fails closed.");

    /// <summary>Workflow plus the simulation scripts (scripts/sim/*.sh, .github/scripts/sim-*.py) that exist; used by the tool and container scanners.</summary>
    private static IReadOnlyList<string> WorkflowAndScriptTexts()
    {
        var root = Root;
        var texts = new List<string> { SimWorkflowText() };
        texts.AddRange(EnumerateFiles(Path.Combine(root, "scripts", "sim"), f => f.EndsWith(".sh", StringComparison.Ordinal)).Select(File.ReadAllText));
        texts.AddRange(EnumerateFiles(Path.Combine(root, ".github", "scripts"), f => Path.GetFileName(f).StartsWith("sim-", StringComparison.Ordinal)).Select(File.ReadAllText));
        return texts;
    }

    private static IReadOnlyList<string> JoinedNonCommentLines(IEnumerable<string> texts) =>
        texts.SelectMany(ParityIsolationArchitectureTests.JoinedLines).Where(l => !l.TrimStart().StartsWith('#')).ToList();

    // ---------- workflow scanners ----------

    internal static IReadOnlyDictionary<string, string> Jobs(string workflow)
    {
        var padded = ("\n" + workflow).IndexOf("\njobs:\n", StringComparison.Ordinal);
        var start = padded - 1;
        if (padded < 0)
        {
            throw new InvalidOperationException("No jobs: block found in the workflow.");
        }

        var body = workflow[(start + 7)..];
        var headers = JobHeader().Matches(body);
        if (headers.Count == 0)
        {
            throw new InvalidOperationException("No jobs parsed from the workflow.");
        }

        var jobs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < headers.Count; i++)
        {
            var end = i + 1 < headers.Count ? headers[i + 1].Index : body.Length;
            jobs[headers[i].Groups[1].Value] = body[headers[i].Index..end];
        }

        return jobs;
    }

    internal static IReadOnlyList<string> Steps(string jobBlock) => Regex.Split(jobBlock, @"\n(?=      - )").Skip(1).ToList();

    internal static IReadOnlyList<string> ForbiddenTriggers(string workflow) =>
        ParityIsolationArchitectureTests.TriggerKeys(workflow).Except(AllowedTriggers, StringComparer.Ordinal).ToList();

    internal static IReadOnlyList<string> PermissionViolations(string workflow)
    {
        var bad = new List<string>();
        if (!TopLevelEmptyPermissions().IsMatch(workflow))
        {
            bad.Add("top-level permissions: {} missing");
        }

        foreach (var (id, block) in Jobs(workflow))
        {
            var lines = block.Split('\n');
            var idx = Array.FindIndex(lines, l => l.TrimEnd('\r') == "    permissions:");
            if (idx < 0)
            {
                bad.Add($"{id}: no job permissions");
                continue;
            }

            var children = lines.Skip(idx + 1).TakeWhile(l => l.StartsWith("      ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
            if (children.Count != 1 || children[0] != "contents: read")
            {
                bad.Add($"{id}: permissions must be exactly 'contents: read'");
            }
        }

        return bad;
    }

    internal static IReadOnlyList<string> UsesViolations(string workflow)
    {
        var all = UsesPattern().Matches(workflow);
        if (all.Count == 0)
        {
            throw new InvalidOperationException("No uses: lines matched: vacuous guard.");
        }

        var bad = new List<string>();
        foreach (Match m in all)
        {
            var action = m.Groups["action"].Value;
            if (!AllowedActions.Contains(action, StringComparer.Ordinal))
            {
                bad.Add($"{action}: action not allowed");
            }
            else if (!Regex.IsMatch(m.Groups["ref"].Value, "^[0-9a-f]{40}$"))
            {
                bad.Add($"{action}@{m.Groups["ref"].Value}: not pinned to a 40-hex SHA");
            }
            else if (!m.Groups["comment"].Success)
            {
                bad.Add($"{action}: missing '# vX.Y.Z' version comment");
            }
        }

        return bad;
    }

    internal static IReadOnlyList<string> CheckoutViolations(string workflow)
    {
        var checkouts = Jobs(workflow).Values.SelectMany(Steps).Where(s => s.Contains("actions/checkout@", StringComparison.Ordinal)).ToList();
        if (checkouts.Count == 0)
        {
            throw new InvalidOperationException("No checkout step found: vacuous guard.");
        }

        return checkouts.Where(s => !Regex.IsMatch(s, @"persist-credentials:\s*false\b")).Select(s => s.Split('\n')[0].Trim()).ToList();
    }

    internal static IReadOnlyList<string> SecretViolations(string workflow) =>
        Regex.Matches(workflow, @"secrets\.|GITHUB_TOKEN|github\.token|id-token", RegexOptions.IgnoreCase).Select(m => m.Value).ToList();

    internal static IReadOnlyList<string> InputViolations(string workflow)
    {
        var bad = new List<string>();
        foreach (var raw in workflow.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.Contains("${{", StringComparison.Ordinal) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var usesInputs = Regex.IsMatch(line, @"\$\{\{[^}]*\b(inputs\.|github\.event\.inputs)");
            var isEnvAssignment = EnvInputAssignment().IsMatch(line);
            var isIfCondition = line.TrimStart().StartsWith("if:", StringComparison.Ordinal);
            if (usesInputs && !isEnvAssignment && !isIfCondition)
            {
                bad.Add(line.Trim());
            }
        }

        foreach (var required in new[] { "^(all|corpus|api|parser|weekly-corpus|weekly-parser)$", "^[0-9]{1,10}$", "^[0-9]{1,5}$|^all$" })
        {
            if (!workflow.Contains(required, StringComparison.Ordinal))
            {
                bad.Add($"validation regex missing: {required}");
            }
        }

        if (Regex.IsMatch(workflow, @"\b(continue-on-error:\s*true|\|\|\s*true)\b"))
        {
            bad.Add("continue-on-error or '|| true' present (SEC-SIM-19)");
        }

        return bad;
    }

    internal static IReadOnlyList<string> RunEnvViolations(string workflow) =>
        Jobs(workflow)
            .Where(j => !j.Value.Contains("AUTHERIS_SIM_RUN: '1'", StringComparison.Ordinal) || !j.Value.Contains("AUTHERIS_SIM_REPORT_DIR:", StringComparison.Ordinal))
            .Select(j => j.Key).ToList();

    internal static IReadOnlyList<string> ForkGuardViolations(string workflow) =>
        Jobs(workflow).Where(j => !Regex.IsMatch(j.Value, @"(?m)^    if:.*github\.event\.repository\.fork == false")).Select(j => j.Key).ToList();

    internal static IReadOnlyList<string> TimeoutViolations(string workflow)
    {
        var bad = new List<string>();
        foreach (var (id, block) in Jobs(workflow))
        {
            var m = Regex.Match(block, @"(?m)^    timeout-minutes:\s*(\d+)\s*$");
            var limit = id.EndsWith("-weekly", StringComparison.Ordinal) ? 120 : 60;
            if (!m.Success || int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > limit)
            {
                bad.Add($"{id}: timeout-minutes missing or above {limit}");
            }
        }

        return bad;
    }

    internal static IReadOnlyList<string> ArtifactRetentionViolations(string workflow)
    {
        var uploads = Jobs(workflow).Values.SelectMany(Steps).Where(s => s.Contains("actions/upload-artifact@", StringComparison.Ordinal)).ToList();
        if (uploads.Count == 0)
        {
            throw new InvalidOperationException("No upload-artifact step found: vacuous guard.");
        }

        return uploads.Where(s =>
        {
            var m = Regex.Match(s, @"retention-days:\s*(\d+)");
            return !m.Success || int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is < 1 or > 7;
        }).Select(s => s.Split('\n')[0].Trim()).ToList();
    }

    internal static IReadOnlyList<string> UploadPathViolations(string workflow)
    {
        var uploads = Jobs(workflow).Values.SelectMany(Steps).Where(s => s.Contains("actions/upload-artifact@", StringComparison.Ordinal)).ToList();
        if (uploads.Count == 0)
        {
            throw new InvalidOperationException("No upload-artifact step found: vacuous guard.");
        }

        var bad = new List<string>();
        foreach (var step in uploads)
        {
            var lines = step.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var paths = new List<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                var m = Regex.Match(lines[i], @"^\s{10}path:\s*(.*)$");
                if (!m.Success)
                {
                    continue;
                }

                var value = m.Groups[1].Value.Trim();
                if (value.Length > 0 && value != "|")
                {
                    paths.Add(value);
                }
                else
                {
                    paths.AddRange(lines.Skip(i + 1).TakeWhile(l => l.StartsWith("            ", StringComparison.Ordinal)).Select(l => l.Trim()));
                }
            }

            if (paths.Count == 0)
            {
                bad.Add("upload step without path");
            }

            bad.AddRange(paths.Where(p => !Regex.IsMatch(p, @"^\$\{\{ github\.workspace \}\}/sim-report[\w\-]*$")
                                          && !Regex.IsMatch(p, @"^\$\{\{ runner\.temp \}\}/afl/default/(crashes|hangs)$")));
        }

        return bad;
    }

    internal static IReadOnlyList<string> SchemathesisPinViolations(IEnumerable<string> texts)
    {
        var bad = new List<string>();
        var joined = string.Join('\n', texts);
        if (!PinnedSchemathesis().IsMatch(joined))
        {
            bad.Add("no schemathesis image pinned by version and digest");
        }

        foreach (Match m in Regex.Matches(joined, @"schemathesis/schemathesis\S*"))
        {
            if (!PinnedSchemathesis().IsMatch(m.Value))
            {
                bad.Add($"unpinned image reference: {m.Value}");
            }
        }

        return bad;
    }

    internal static IReadOnlyList<string> DockerRunViolations(IEnumerable<string> texts)
    {
        var bad = new List<string>();
        var runLines = JoinedNonCommentLines(texts).Where(l => Regex.IsMatch(l, @"\bdocker\s+run\b")).ToList();
        if (runLines.Count == 0)
        {
            // Vacuity check: scripts/sim/run-api-fuzz.sh must contain the Schemathesis docker run this guard is about.
            bad.Add("no docker run found in the workflow or the sim scripts (vacuous guard)");
        }

        foreach (var line in runLines)
        {
            foreach (var required in new[] { "--read-only", "--cap-drop ALL", "--security-opt no-new-privileges" })
            {
                if (!line.Contains(required, StringComparison.Ordinal))
                {
                    bad.Add($"docker run without {required}");
                }
            }

            if (line.Contains("docker.sock", StringComparison.Ordinal) || line.Contains("--privileged", StringComparison.Ordinal))
            {
                bad.Add("docker run with docker socket or --privileged");
            }

            foreach (Match mount in Regex.Matches(line, @"(?:-v|--volume)\s+(\S+)"))
            {
                if (!Regex.IsMatch(mount.Groups[1].Value, @"^""?\$\{?RUNNER_TEMP\}?/st:"))
                {
                    bad.Add($"docker run mounts {mount.Groups[1].Value}");
                }
            }

            if (!line.Contains("$SCHEMATHESIS_IMAGE", StringComparison.Ordinal) && !line.Contains("${SCHEMATHESIS_IMAGE}", StringComparison.Ordinal))
            {
                bad.Add("docker run image must come from SCHEMATHESIS_IMAGE");
            }
        }

        return bad;
    }

    internal static IReadOnlyList<string> ToolInstallViolations(IEnumerable<string> texts)
    {
        var bad = new List<string>();
        foreach (var line in JoinedNonCommentLines(texts))
        {
            if (Regex.IsMatch(line, @"\bdotnet\s+tool\s+install\b") && !Regex.IsMatch(line, @"--version\s+\d+\.\d+\.\d+"))
            {
                bad.Add($"unpinned dotnet tool install: {line.Trim()}");
            }

            if (Regex.IsMatch(line, @"\bpip3?\s+install\b"))
            {
                bad.Add($"pip install: {line.Trim()}");
            }

            if (Regex.IsMatch(line, @"\b(curl|wget)\b[^\n]*\|\s*(sudo\s+)?(sh|bash)\b"))
            {
                bad.Add($"pipe to shell: {line.Trim()}");
            }

            if (Regex.IsMatch(line, @"\bapt(-get)?\s+install\b") && !line.Contains("--no-install-recommends", StringComparison.Ordinal))
            {
                bad.Add($"apt install without --no-install-recommends: {line.Trim()}");
            }

            if (Regex.IsMatch(line, @":latest\b|@latest\b|--version\s+latest\b"))
            {
                bad.Add($"floating version: {line.Trim()}");
            }

            if (Regex.IsMatch(line, @"\bafl-fuzz\b[^\n]*\s-m\s+none\b"))
            {
                bad.Add($"afl-fuzz with unbounded memory (-m none): {line.Trim()}");
            }

            if (Regex.IsMatch(line, @"\bafl-fuzz\b") && !Regex.IsMatch(line, @"\s-m\s+(\d+|""\$\{?SIM_AFL_MEM_MB\}?"")(\s|$)") && !Regex.IsMatch(line, @"\s-m\s+none\b") && Regex.IsMatch(line, @"\s-i\s"))
            {
                bad.Add($"afl-fuzz without a finite -m limit: {line.Trim()}");
            }

            if (Regex.IsMatch(line, @"\bdotnet\s+restore\b") && !line.Contains("--locked-mode", StringComparison.Ordinal))
            {
                bad.Add($"restore without --locked-mode: {line.Trim()}");
            }
        }

        return bad;
    }

    /// <summary>Arguments of every <c>sharpfuzz</c> command invocation (the tool install line and comments are excluded).</summary>
    internal static IReadOnlyList<string> SharpFuzzCalls(IEnumerable<string> texts) =>
        JoinedNonCommentLines(texts)
            .Where(l => !l.Contains("dotnet tool install", StringComparison.Ordinal) && !l.TrimStart().StartsWith("- name:", StringComparison.Ordinal) && !l.TrimStart().StartsWith("name:", StringComparison.Ordinal))
            .SelectMany(l => SharpFuzzCall().Matches(l).Select(m => m.Groups["arg"].Value))
            .ToList();

    internal static IReadOnlyList<string> SharpFuzzViolations(IReadOnlyList<string> args) =>
        args.Where(a => !InstrumentedTarget().IsMatch(a)).ToList();

    internal static IReadOnlyList<string> TestAuthLineViolations(string workflow) =>
        ParityIsolationArchitectureTests.JoinedLines(workflow)
            .Where(l => l.Contains("AutherisIncludeTestAuth", StringComparison.Ordinal) && !l.Contains(ApiHostProject, StringComparison.Ordinal))
            .Select(l => l.Trim()).ToList();

    // ---------- repository scanners ----------

    internal static IReadOnlyList<string> SolutionsMentioningSimProjects(string root)
    {
        var solutions = Directory.GetFiles(root, "*.sln").Concat(Directory.GetFiles(root, "*.slnf")).Concat(Directory.GetFiles(root, "*.slnx")).ToList();
        if (solutions.Count == 0)
        {
            throw new InvalidOperationException("No solution file found: vacuous guard.");
        }

        return solutions.Where(f => SimProjects.Any(p => File.ReadAllText(f).Contains(p, StringComparison.Ordinal))).Select(Path.GetFileName).ToList()!;
    }

    private static bool InsideSimProject(string path) =>
        SimProjects.Any(p => path.Contains(Path.DirectorySeparatorChar + p + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    /// <summary>Project, props, targets and src sources outside the simulation projects that reference or grant InternalsVisibleTo to them (SEC-SIM-21).</summary>
    internal static IReadOnlyList<string> ReferencesToSimProjects(string root)
    {
        var buildFiles = EnumerateFiles(root, f => !InsideSimProject(f)
                                                   && (f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal) || f.EndsWith(".targets", StringComparison.Ordinal)
                                                       || Path.GetFileName(f).StartsWith("Dockerfile", StringComparison.Ordinal)));
        var srcCode = EnumerateFiles(Path.Combine(root, "src"), f => f.EndsWith(".cs", StringComparison.Ordinal));
        return buildFiles.Concat(srcCode)
            .Where(f => SimProjects.Any(p => File.ReadAllText(f).Contains(p, StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root, f)).ToList();
    }

    internal static IReadOnlyList<string> WorkflowsMentioningSimProjects(IReadOnlyDictionary<string, string> workflows) =>
        workflows.Where(w => w.Key != Workflow && SimProjects.Any(p => w.Value.Contains(p, StringComparison.Ordinal))).Select(w => w.Key).ToList();

    internal static IReadOnlyList<string> TestAuthInSimProjects(string root) =>
        SimProjects.SelectMany(p => EnumerateFiles(Path.Combine(root, "tests", p),
                f => f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal) || f.EndsWith(".targets", StringComparison.Ordinal)))
            .Where(f => File.ReadAllText(f).Contains("AutherisIncludeTestAuth", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f)).ToList();

    internal static IReadOnlyList<string> AflLoggingViolations(string text)
    {
        var bad = new List<string>();
        if (text.Contains("apt-get install", StringComparison.Ordinal) && text.Contains("afl++", StringComparison.Ordinal))
        {
            if (!text.Contains("dpkg-query", StringComparison.Ordinal))
            {
                bad.Add("afl++ install without dpkg-query version log");
            }

            if (!text.Contains("apt-cache policy afl++", StringComparison.Ordinal))
            {
                bad.Add("afl++ install without apt-cache policy log");
            }
        }

        return bad;
    }

    internal static IReadOnlyList<string> MissingNotices(string text) =>
        new[] { "sqllogictest", "Schemathesis", "SharpFuzz", "AFL++", "MIT", "Apache-2.0", "4.09c-1ubuntu3", "noble at time of writing", "apt-cache policy afl++", "c93ac416f05635ff5cc490009add936d9753d01d" }
            .Where(s => !text.Contains(s, StringComparison.Ordinal)).ToList();

    /// <summary>Files whose content hash is a known sqllogictest file, or that look like sqllogictest records outside <c>fixtures</c> (SEC-SIM-13).</summary>
    internal static IReadOnlyList<string> CommittedThirdPartyCorpus(string root, IReadOnlyCollection<string> knownHashes)
    {
        var hits = new List<string>();
        foreach (var dirName in new[] { "tests", "scripts", ".github", "docs" })
        {
            foreach (var file in EnumerateFiles(Path.Combine(root, dirName), _ => true))
            {
                var info = new FileInfo(file);
                if (info.Length == 0 || info.Length > 20 * 1024 * 1024)
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(file);
                if (knownHashes.Contains(Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparer.Ordinal))
                {
                    hits.Add(Path.GetRelativePath(root, file));
                    continue;
                }

                var inSimTree = file.Contains(Path.DirectorySeparatorChar + SimProject + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                || file.Contains(Path.DirectorySeparatorChar + FuzzProject + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                var isFixture = file.Contains(Path.DirectorySeparatorChar + "fixtures" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (inSimTree && !isFixture && file.EndsWith(".test", StringComparison.Ordinal) && SltQueryRecord().IsMatch(System.Text.Encoding.UTF8.GetString(bytes)))
                {
                    hits.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        return hits;
    }

    /// <summary>Manifest shape: <c>{"commit":"&lt;40 hex&gt;","files":[{"path","sha256","queries"}]}</c>; sum of queries must be at least 5000.</summary>
    internal static IReadOnlyList<string> ManifestViolations(string json)
    {
        var bad = new List<string>();
        using var doc = JsonDocument.Parse(json);
        var rootEl = doc.RootElement;
        if (rootEl.ValueKind != JsonValueKind.Object)
        {
            return ["manifest must be an object"];
        }

        if (!rootEl.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.String || !Commit40().IsMatch(commit.GetString()!))
        {
            bad.Add("commit must be 40 lowercase hex");
        }

        var total = 0;
        if (!rootEl.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
        {
            bad.Add("files must be a non-empty array");
        }
        else
        {
            foreach (var f in files.EnumerateArray())
            {
                if (!f.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String || !Sha256().IsMatch(sha.GetString()!))
                {
                    bad.Add("every file needs a 64-hex sha256");
                }

                if (f.TryGetProperty("queries", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out var n))
                {
                    total += n;
                }
                else
                {
                    bad.Add("every file needs an integer queries count");
                }
            }
        }

        if (total < 5000)
        {
            bad.Add($"sum of queries is {total}, need at least 5000");
        }

        return bad;
    }

    /// <summary>KnownGap ids referenced in simulation sources and data files (outside Meta, Infrastructure and fixtures) that have no <c>KnownGaps/drafts/&lt;id&gt;.md</c>.</summary>
    internal static IReadOnlyList<string> GapIdsWithoutDraft(string simRoot)
    {
        var drafts = Path.Combine(simRoot, "KnownGaps", "drafts");
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateFiles(simRoot, f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal)))
        {
            var rel = Path.GetRelativePath(simRoot, file).Replace('\\', '/');
            if (rel.StartsWith("Meta/", StringComparison.Ordinal) || rel.StartsWith("Infrastructure/", StringComparison.Ordinal) || rel.Contains("/fixtures/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match m in GapId().Matches(File.ReadAllText(file)))
            {
                if (!File.Exists(Path.Combine(drafts, m.Value + ".md")))
                {
                    missing.Add(m.Value);
                }
            }
        }

        return missing.ToList();
    }

    /// <summary>Test classes in a source file that are not named <c>*Tests</c> or do not derive from <c>SimulationTestBase</c>; empty when the file holds no tests.</summary>
    internal static IReadOnlyList<string> NonBaseTestClasses(string source)
    {
        if (!Regex.IsMatch(source, @"\[(Fact|Theory)\b"))
        {
            return [];
        }

        var decls = ClassDecl().Matches(source).Where(m => !source[..m.Index].EndsWith("typeof(", StringComparison.Ordinal)).ToList();
        if (decls.Count == 0)
        {
            return ["test file without a class declaration"];
        }

        return decls
            .Where(m => !m.Groups["name"].Value.EndsWith("Tests", StringComparison.Ordinal) || !m.Groups["bases"].Value.Contains("SimulationTestBase", StringComparison.Ordinal))
            .Select(m => m.Groups["name"].Value).ToList();
    }

    internal static IReadOnlyList<string> QuarantineViolations(string json, DateOnly today)
    {
        var bad = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return ["quarantine.json must be an array"];
        }

        var i = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            i++;
            if (e.ValueKind != JsonValueKind.Object)
            {
                bad.Add($"#{i}: not an object");
                continue;
            }

            var unknown = e.EnumerateObject().Select(p => p.Name).Except(["sha256", "issue", "reason", "approvedOn"], StringComparer.Ordinal).ToList();
            bad.AddRange(unknown.Select(u => $"#{i}: unknown key {u}"));
            if (!e.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String || !Sha256().IsMatch(sha.GetString()!))
            {
                bad.Add($"#{i}: sha256 must be 64 lowercase hex");
            }

            if (!e.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Number || !issue.TryGetInt32(out var n) || n <= 0)
            {
                bad.Add($"#{i}: issue must be a positive integer");
            }

            if (!e.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reason.GetString()))
            {
                bad.Add($"#{i}: reason required");
            }

            if (!e.TryGetProperty("approvedOn", out var approved) || approved.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(approved.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                bad.Add($"#{i}: approvedOn must be yyyy-MM-dd");
            }
            else if (date > today || today.DayNumber - date.DayNumber > 90)
            {
                bad.Add($"#{i}: approvedOn is in the future or older than 90 days (expired)");
            }
        }

        return bad;
    }

    // ---------- guard tests ----------

    [Fact]
    [Trait("Category", "Security")]
    public void SimIsolation_ProjectsAreNotInAnySolution()
    {
        SolutionsMentioningSimProjects(Root).ShouldBeEmpty();

        // negative self-check
        var dir = Path.Combine(Path.GetTempPath(), "sim-sln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "X.sln"), "Project(\"{1}\") = \"Autheris.Sim.ApiHost\", \"tests\\x.csproj\"");
            SolutionsMentioningSimProjects(dir).ShouldBe(["X.sln"]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimIsolation_NoProductionOrCiProjectReferencesSimProjects()
    {
        ReferencesToSimProjects(Root).ShouldBeEmpty();

        // negative self-check: a csproj outside the simulation projects and an InternalsVisibleTo in src
        var dir = Path.Combine(Path.GetTempPath(), "sim-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "A"));
        Directory.CreateDirectory(Path.Combine(dir, "tests", FuzzProject));
        try
        {
            File.WriteAllText(Path.Combine(dir, "src", "A", "A.csproj"), "<ProjectReference Include=\"..\\..\\tests\\Autheris.Tests.Fuzz\\Autheris.Tests.Fuzz.csproj\" />");
            File.WriteAllText(Path.Combine(dir, "src", "A", "B.cs"), "[assembly: InternalsVisibleTo(\"Autheris.Tests.Simulation\")]");
            File.WriteAllText(Path.Combine(dir, "tests", FuzzProject, "Autheris.Tests.Fuzz.csproj"), "<Project />");
            ReferencesToSimProjects(dir).Count.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimIsolation_OnlySimulationWorkflowTargetsSimProjects()
    {
        var workflows = ParityIsolationArchitectureTests.ReadWorkflows(Root);
        workflows.ShouldContainKey(Workflow);
        WorkflowsMentioningSimProjects(workflows).ShouldBeEmpty();
        workflows[Workflow].ShouldContain(SimProject);

        // ci.yml must not mention any simulation project (SEC-SIM-21)
        workflows.ShouldContainKey("ci.yml");

        // negative self-check
        WorkflowsMentioningSimProjects(new Dictionary<string, string> { ["ci.yml"] = "dotnet test tests/Autheris.Tests.Fuzz", [Workflow] = SimProject }).ShouldBe(["ci.yml"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_HasOnlyScheduleAndDispatchTriggers()
    {
        ForbiddenTriggers(SimWorkflowText()).ShouldBeEmpty();
        ParityIsolationArchitectureTests.TriggerKeys(SimWorkflowText()).ShouldBe(AllowedTriggers, ignoreOrder: true);

        // negative self-check
        ForbiddenTriggers("on:\n  push:\n    branches: [main]\n  schedule:\n    - cron: '0 3 * * *'\n").ShouldBe(["push"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_NoPullRequestPushOrWorkflowRunTrigger()
    {
        var keys = ParityIsolationArchitectureTests.TriggerKeys(SimWorkflowText());
        keys.Intersect(["pull_request", "pull_request_target", "push", "workflow_run", "issue_comment", "workflow_call"], StringComparer.Ordinal).ShouldBeEmpty();

        // negative self-check
        ParityIsolationArchitectureTests.TriggerKeys("on:\n  pull_request_target:\n    types: [opened]\n").Intersect(["pull_request_target"]).ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_DefinesPlannedJobsWithTimeouts()
    {
        var jobs = Jobs(SimWorkflowText());
        jobs.Keys.ShouldBe(["sim-corpus", "sim-api-fuzz", "sim-parser-fuzz", "sim-corpus-weekly", "sim-parser-fuzz-weekly"], ignoreOrder: true);
        TimeoutViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check: nightly above 60 and weekly above 120 fail, weekly at 90 passes
        const string Sample = "jobs:\n  sim-a:\n    timeout-minutes: 61\n    steps: []\n  sim-b-weekly:\n    timeout-minutes: 121\n    steps: []\n  sim-c-weekly:\n    timeout-minutes: 90\n    steps: []\n  sim-d:\n    runs-on: x\n";
        TimeoutViolations(Sample).Count.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_PermissionsAreLeastPrivilege()
    {
        PermissionViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check
        PermissionViolations("permissions: write-all\njobs:\n  a:\n    permissions:\n      contents: write\n    steps: []\n").Count.ShouldBe(2);
        PermissionViolations("permissions: {}\njobs:\n  a:\n    runs-on: x\n").ShouldBe(["a: no job permissions"]);
        PermissionViolations("permissions: {}\njobs:\n  a:\n    permissions:\n      contents: read\n      id-token: write\n").Count.ShouldBe(1);
        PermissionViolations("permissions: {}\njobs:\n  a:\n    permissions:\n      contents: read\n").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_ActionsPinnedBySha()
    {
        UsesViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check: tag pin, unknown action, missing version comment
        UsesViolations("uses: actions/checkout@v4\n").Count.ShouldBe(1);
        UsesViolations("uses: some/other@3d3c42e5aac5ba805825da76410c181273ba90b1 # v1.0.0\n").Count.ShouldBe(1);
        UsesViolations("uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1\n").Count.ShouldBe(1);
        UsesViolations("uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_CheckoutDoesNotPersistCredentials()
    {
        CheckoutViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check
        const string Bad = "jobs:\n  a:\n    steps:\n      - name: Checkout\n        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n      - name: Other\n        run: echo ok\n";
        CheckoutViolations(Bad).Count.ShouldBe(1);
        CheckoutViolations(Bad.Replace("# v7.0.1\n", "# v7.0.1\n        with:\n          persist-credentials: false\n", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_NoSecretsReferenced()
    {
        SecretViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check
        SecretViolations("env:\n  T: ${{ secrets.FOO }}\n  G: ${{ github.token }}\n").Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_InputsOnlyViaEnvAndValidated()
    {
        InputViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check: input expanded in run, and validation regexes missing
        var bad = InputViolations("steps:\n  - run: echo ${{ inputs.seed }}\n  - run: echo ${{ github.event.inputs.job }}\n    continue-on-error: true\n");
        bad.Count.ShouldBeGreaterThanOrEqualTo(5);
        InputViolations("  X: ${{ inputs.seed }}\n  if: inputs.job == 'all'\n").ShouldNotContain(s => s.Contains("inputs.seed", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_SetsSimRunEnv()
    {
        RunEnvViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check
        RunEnvViolations("jobs:\n  a:\n    env:\n      AUTHERIS_SIM_RUN: '1'\n  b:\n    env:\n      AUTHERIS_SIM_REPORT_DIR: x\n").ShouldBe(["a", "b"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_ForkGuardPresent()
    {
        ForkGuardViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check
        ForkGuardViolations("jobs:\n  a:\n    if: github.event_name == 'schedule'\n  b:\n    if: github.event.repository.fork == false && true\n").ShouldBe(["a"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_LogsAflPackageVersionAndPolicy()
    {
        // SEC-SIM-13/09: every job that installs afl++ logs the installed version and the apt policy.
        AflLoggingViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check
        AflLoggingViolations("sudo apt-get install -y afl++\n").Count.ShouldBe(2);
        AflLoggingViolations("sudo apt-get install -y afl++\ndpkg-query -W afl++\napt-cache policy afl++\n").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_ArtifactRetentionAtMost7Days()
    {
        ArtifactRetentionViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check
        const string Sample = "jobs:\n  a:\n    steps:\n      - name: Up\n        uses: actions/upload-artifact@x\n        with:\n          retention-days: 14\n      - name: Up2\n        uses: actions/upload-artifact@x\n        with:\n          path: p\n";
        ArtifactRetentionViolations(Sample).Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_UploadPathsExcludeDataDirAndCanaries()
    {
        UploadPathViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check: data dir, canaries, temp root and a glob outside the report dir
        const string Sample = "jobs:\n  a:\n    steps:\n      - name: Up\n        uses: actions/upload-artifact@x\n        with:\n          path: |\n            ${{ runner.temp }}\n            ${{ runner.temp }}/sim-data/canaries.txt\n            ${{ github.workspace }}/sim-report-api\n            ~/.nuget\n          retention-days: 7\n";
        UploadPathViolations(Sample).Count.ShouldBe(3);
        UploadPathViolations(Sample.Replace("            ${{ runner.temp }}\n", string.Empty, StringComparison.Ordinal)).Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_SchemathesisImagePinnedByVersionAndDigest()
    {
        SchemathesisPinViolations(WorkflowAndScriptTexts()).ShouldBeEmpty();

        // negative self-check
        SchemathesisPinViolations(["IMAGE: schemathesis/schemathesis:latest"]).Count.ShouldBe(2);
        SchemathesisPinViolations(["IMAGE: schemathesis/schemathesis:4.32.4"]).Count.ShouldBe(2);
        SchemathesisPinViolations([$"IMAGE: schemathesis/schemathesis:4.32.4@sha256:{new string('a', 64)}"]).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_SchemathesisRunsHardenedAndPinned()
    {
        DockerRunViolations(WorkflowAndScriptTexts()).ShouldBeEmpty();

        // negative self-check
        DockerRunViolations(["echo no container here"]).ShouldBe(["no docker run found in the workflow or the sim scripts (vacuous guard)"]);
        DockerRunViolations(["docker run --rm -v /var/run/docker.sock:/s -v $HOME:/h img run"]).Count.ShouldBeGreaterThanOrEqualTo(6);
        DockerRunViolations(["docker run --rm --read-only --cap-drop ALL --security-opt no-new-privileges -v \"$RUNNER_TEMP/st:/work\" \"$SCHEMATHESIS_IMAGE\" run"]).ShouldBeEmpty();
        DockerRunViolations(["docker run --rm --read-only --cap-drop ALL \\\n  --security-opt no-new-privileges -v $RUNNER_TEMP/st:/work $SCHEMATHESIS_IMAGE run"]).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_NoUnpinnedToolInstalls()
    {
        var texts = WorkflowAndScriptTexts();
        ToolInstallViolations(texts).ShouldBeEmpty();

        // vacuity: the guard must really see the pinned SharpFuzz.CommandLine install and the finite afl-fuzz memory limit (REQ-SIMTEST WP3)
        var lines = JoinedNonCommentLines(texts);
        lines.Count(l => Regex.IsMatch(l, @"\bdotnet\s+tool\s+install\s+SharpFuzz\.CommandLine\s+--version\s+\d+\.\d+\.\d+\b")).ShouldBe(1, "run-parser-fuzz.sh must install the pinned tool");
        lines.Count(l => Regex.IsMatch(l, @"^\s*afl-fuzz\s") && l.Contains("-m \"$SIM_AFL_MEM_MB\"", StringComparison.Ordinal)).ShouldBe(1, "afl-fuzz must pass a finite -m limit");

        // negative self-check
        ToolInstallViolations(["afl-fuzz -i a -o b -- x"]).Count.ShouldBe(1);
        ToolInstallViolations(["afl-fuzz -i a -o b -m \"$SIM_AFL_MEM_MB\" -- x", "afl-fuzz -i a -o b -m 2048 -- x"]).ShouldBeEmpty();
        ToolInstallViolations(["dotnet tool install SharpFuzz.CommandLine --tool-path x"]).Count.ShouldBe(1);
        ToolInstallViolations(["dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path x"]).ShouldBeEmpty();
        ToolInstallViolations(["pip install schemathesis", "curl https://x | sh", "sudo apt-get install -y afl++", "image: foo:latest", "afl-fuzz -i a -o b -m none -- x", "dotnet restore x"]).Count.ShouldBe(6);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_SharpFuzzInstrumentsOnlyTrinoSqlEngine()
    {
        var texts = WorkflowAndScriptTexts();
        var calls = SharpFuzzCalls(texts);
        SharpFuzzViolations(calls).ShouldBeEmpty();
        calls.Count.ShouldBeLessThanOrEqualTo(1, "exactly one sharpfuzz call is allowed (SEC-SIM-16)");

        var script = Path.Combine(Root, "scripts", "sim", "run-parser-fuzz.sh");
        File.Exists(script).ShouldBeTrue("run-parser-fuzz.sh must exist (vacuous guard otherwise)");
        SharpFuzzCalls([File.ReadAllText(script)]).Count.ShouldBe(1, "run-parser-fuzz.sh must instrument exactly one assembly");
        calls.Count.ShouldBe(1, "exactly one sharpfuzz call must be found (vacuity check)");

        // negative self-check
        SharpFuzzViolations(SharpFuzzCalls(["\"$RUNNER_TEMP/sf/sharpfuzz\" \"$RUNNER_TEMP/fuzz-bin/TrinoSqlEngine.dll\""])).ShouldBeEmpty();
        SharpFuzzViolations(SharpFuzzCalls(["$RUNNER_TEMP/sf/sharpfuzz src/Autheris.Api/bin/Autheris.Api.dll"])).Count.ShouldBe(1);
        SharpFuzzCalls(["\"$TOOL_DIR/sharpfuzz\" \"$RUNNER_TEMP/fuzz-bin/TrinoSqlEngine.dll\""]).Count.ShouldBe(1);
        SharpFuzzViolations(SharpFuzzCalls(["\"$TOOL_DIR/sharpfuzz\" \"src/Autheris.Api/bin/Autheris.Api.dll\""])).Count.ShouldBe(1);
        SharpFuzzViolations(SharpFuzzCalls(["\"$TOOL_DIR/sharpfuzz\" \"/tmp/fuzz-bin/TrinoSqlEngine.dll\""])).Count.ShouldBe(1);
        SharpFuzzViolations(SharpFuzzCalls(["sharpfuzz /tmp/fuzz-bin/TrinoSqlEngine.dll"])).Count.ShouldBe(1);
        SharpFuzzCalls(["dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path x", "# sharpfuzz a.dll"]).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimWorkflow_TestAuthOnlyOnApiHostBuild()
    {
        TestAuthLineViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check
        TestAuthLineViolations("run: dotnet test tests/Autheris.Tests.Simulation -p:AutherisIncludeTestAuth=true").Count.ShouldBe(1);
        TestAuthLineViolations("run: dotnet build tests/Autheris.Sim.ApiHost -p:AutherisIncludeTestAuth=true").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimProjects_DoNotSetAutherisIncludeTestAuth()
    {
        TestAuthInSimProjects(Root).ShouldBeEmpty();

        // negative self-check
        var dir = Path.Combine(Path.GetTempPath(), "sim-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "tests", ApiHostProject));
        try
        {
            File.WriteAllText(Path.Combine(dir, "tests", ApiHostProject, "Host.csproj"), "<AutherisIncludeTestAuth>true</AutherisIncludeTestAuth>");
            TestAuthInSimProjects(dir).Count.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimApiHost_IsNotPublishedByReleaseOrDockerWorkflows()
    {
        var root = Root;
        var workflows = ParityIsolationArchitectureTests.ReadWorkflows(root);
        foreach (var name in new[] { "release.yml", "docker-publish.yml" })
        {
            workflows.ShouldContainKey(name);
            workflows[name].ShouldNotContain("Sim.ApiHost");
            workflows[name].ShouldNotContain("Autheris.Tests.Simulation");
            workflows[name].ShouldNotContain("Autheris.Tests.Fuzz");
        }

        EnumerateFiles(root, f => Path.GetFileName(f).StartsWith("Dockerfile", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("Sim.ApiHost", StringComparison.Ordinal)).ShouldBeEmpty();

        // negative self-check
        WorkflowsMentioningSimProjects(new Dictionary<string, string> { ["release.yml"] = "dotnet publish tests/Autheris.Sim.ApiHost" }).ShouldBe(["release.yml"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimNotices_ListEveryThirdPartySource()
    {
        var path = Path.Combine(Root, "THIRD-PARTY-NOTICES.md");
        File.Exists(path).ShouldBeTrue();
        MissingNotices(File.ReadAllText(path)).ShouldBeEmpty();

        // negative self-check
        MissingNotices("sqllogictest Schemathesis SharpFuzz").ShouldContain("AFL++");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimCorpus_NoThirdPartyCorpusCommitted()
    {
        CommittedThirdPartyCorpus(Root, SltFileHashes).ShouldBeEmpty();

        // negative self-check: a file with a known content hash, and a .test file with SLT records inside a simulation project
        var dir = Path.Combine(Path.GetTempPath(), "sim-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "tests", SimProject, "Corpus", "fixtures"));
        try
        {
            var content = "pretend slt content"u8.ToArray();
            File.WriteAllBytes(Path.Combine(dir, "tests", "copy.dat"), content);
            File.WriteAllText(Path.Combine(dir, "tests", SimProject, "Corpus", "select1.test"), "statement ok\nCREATE TABLE t1(a INTEGER)\n\nquery I rowsort\nSELECT a FROM t1\n----\n");
            File.WriteAllText(Path.Combine(dir, "tests", SimProject, "Corpus", "fixtures", "mini.test"), "query I rowsort\nSELECT 1\n----\n1\n");
            var hits = CommittedThirdPartyCorpus(dir, [Convert.ToHexStringLower(SHA256.HashData(content))]);
            hits.Count.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimCorpus_ManifestPinsCommitAndSha256()
    {
        // The manifest is delivered by WP1; until it exists only the scanner self-check runs (the guard becomes binding when the file appears).
        var manifest = Path.Combine(Root, "tests", SimProject, "Corpus", "sqllogictest.manifest.json");
        if (File.Exists(manifest))
        {
            ManifestViolations(File.ReadAllText(manifest)).ShouldBeEmpty();
        }

        // negative self-check
        var good = $$"""{"commit":"{{new string('a', 40)}}","files":[{"path":"a","sha256":"{{new string('b', 64)}}","queries":5000}]}""";
        ManifestViolations(good).ShouldBeEmpty();
        ManifestViolations(good.Replace(new string('a', 40), "abc", StringComparison.Ordinal)).ShouldContain("commit must be 40 lowercase hex");
        ManifestViolations(good.Replace(new string('b', 64), "abc", StringComparison.Ordinal)).ShouldContain("every file needs a 64-hex sha256");
        ManifestViolations(good.Replace("5000", "4999", StringComparison.Ordinal)).ShouldContain("sum of queries is 4999, need at least 5000");
        ManifestViolations("""{"commit":"x"}""").Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimKnownGaps_EveryIdHasDraft()
    {
        var simRoot = Path.Combine(Root, "tests", SimProject);
        Directory.Exists(simRoot).ShouldBeTrue();
        File.Exists(Path.Combine(simRoot, "KnownGaps", "drafts", ".gitkeep")).ShouldBeTrue();
        GapIdsWithoutDraft(simRoot).ShouldBeEmpty();

        // negative self-check
        var dir = Path.Combine(Path.GetTempPath(), "sim-gaps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "KnownGaps", "drafts"));
        Directory.CreateDirectory(Path.Combine(dir, "Corpus"));
        Directory.CreateDirectory(Path.Combine(dir, "Meta"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "Corpus", "Gaps.cs"), "gaps.Register(\"SIM-GAP-001\", k, c); gaps.Register(\"SIM-GAP-002\", k, c);");
            File.WriteAllText(Path.Combine(dir, "Meta", "T.cs"), "\"SIM-GAP-009\"");
            File.WriteAllText(Path.Combine(dir, "KnownGaps", "drafts", "SIM-GAP-001.md"), "x");
            GapIdsWithoutDraft(dir).ShouldBe(["SIM-GAP-002"]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimTests_NonMetaClassesDeriveFromBase()
    {
        var simRoot = Path.Combine(Root, "tests", SimProject);
        var violations = EnumerateFiles(simRoot, f => f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !Path.GetRelativePath(simRoot, f).Replace('\\', '/').StartsWith("Meta/", StringComparison.Ordinal))
            .SelectMany(f => NonBaseTestClasses(File.ReadAllText(f)).Select(c => $"{Path.GetFileName(f)}: {c}"))
            .ToList();
        violations.ShouldBeEmpty();

        // negative self-check
        NonBaseTestClasses("public sealed class FooTests { [Fact] public void A() {} }").ShouldBe(["FooTests"]);
        NonBaseTestClasses("public sealed class FooTests : SimulationTestBase { [Fact] public void A() {} }").ShouldBeEmpty();
        NonBaseTestClasses("public sealed class Helper { [Fact] public void A() {} }").ShouldBe(["Helper"]);
        NonBaseTestClasses("public sealed class FooTests(ITestOutputHelper o) : SimulationTestBase { [Theory] public void A() {} }").ShouldBeEmpty();
        NonBaseTestClasses("public static class Helpers { }").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SimFuzz_QuarantineEntriesHaveIssueNumber()
    {
        var path = Path.Combine(Root, "tests", FuzzProject, "findings", "quarantine.json");
        File.Exists(path).ShouldBeTrue("quarantine.json is owner-controlled and must exist (fail-closed)");
        QuarantineViolations(File.ReadAllText(path), DateOnly.FromDateTime(DateTime.UtcNow)).ShouldBeEmpty();

        // negative self-check
        var sha = new string('c', 64);
        var today = new DateOnly(2026, 10, 24);
        QuarantineViolations("[]", today).ShouldBeEmpty();
        QuarantineViolations($$"""[{"sha256":"{{sha}}","issue":7,"reason":"r","approvedOn":"2026-10-01"}]""", today).ShouldBeEmpty();
        QuarantineViolations($$"""[{"sha256":"{{sha}}","reason":"r","approvedOn":"2026-10-01"}]""", today).Count.ShouldBe(1);
        QuarantineViolations($$"""[{"sha256":"{{sha}}","issue":0,"reason":"r","approvedOn":"2026-10-01"}]""", today).Count.ShouldBe(1);
        QuarantineViolations($$"""[{"sha256":"{{sha}}","issue":7,"reason":"r","approvedOn":"2026-07-01"}]""", today).Count.ShouldBe(1);
        QuarantineViolations($$"""[{"sha256":"{{sha}}","issue":7,"reason":"r"}]""", today).Count.ShouldBe(1);
        QuarantineViolations($$"""[{"sha256":"{{sha}}","issue":7,"reason":"r","approvedOn":"2026-10-01","x":1}]""", today).Count.ShouldBe(1);
        QuarantineViolations("{}", today).Count.ShouldBe(1);
    }
}
