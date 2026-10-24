using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autheris.Tests.Simulation.Corpus;

public sealed record SltManifestFile(string Path, int Queries, string Sha256);

public sealed record SltManifest(string Commit, string Host, string Repo, IReadOnlyList<SltManifestFile> Files)
{
    public int TotalQueries => Files.Sum(f => f.Queries);
}

/// <summary>
/// Loads <c>Corpus/sqllogictest.manifest.json</c> and the fetched files. The corpus directory comes from <c>AUTHERIS_SIM_CORPUS_DIR</c>; there is
/// no network access from C#. A missing directory, missing file or hash mismatch fails with the fetch script command (SEC-SIM-10/11).
/// </summary>
public static partial class SltCorpus
{
    public const string DirEnvVar = "AUTHERIS_SIM_CORPUS_DIR";
    public const string ManifestRelative = "Corpus/sqllogictest.manifest.json";
    public const string FetchCommand = "bash scripts/sim/fetch-sqllogictest.sh tests/Autheris.Tests.Simulation/Corpus/sqllogictest.manifest.json <dir> (then set AUTHERIS_SIM_CORPUS_DIR=<dir>)";

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Commit40();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha64();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_./\-]{0,127}$")]
    private static partial Regex SafePath();

    public static string ManifestPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Corpus", "sqllogictest.manifest.json");

    public static SltManifest LoadManifest(string path) => ParseManifest(File.ReadAllText(path));

    public static SltManifest ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var commit = Str(root, "commit");
        if (!Commit40().IsMatch(commit))
        {
            throw new InvalidOperationException("manifest commit must be 40 lowercase hex.");
        }

        var files = new List<SltManifestFile>();
        foreach (var f in root.GetProperty("files").EnumerateArray())
        {
            var path = Str(f, "path");
            var sha = Str(f, "sha256");
            if (!SafePath().IsMatch(path) || path.Contains("..", StringComparison.Ordinal) || !Sha64().IsMatch(sha))
            {
                throw new InvalidOperationException("manifest file entry needs a safe relative path and a 64-hex sha256.");
            }

            files.Add(new SltManifestFile(path, f.GetProperty("queries").GetInt32(), sha));
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("manifest has no files.");
        }

        return new SltManifest(commit, Str(root, "host"), Str(root, "repo"), files);
    }

    /// <summary>Verifies every file of the manifest in <paramref name="dir"/> by sha256 and returns the parsed records per file.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<SltRecord>> Load(SltManifest manifest, string? dir = null)
    {
        dir ??= Environment.GetEnvironmentVariable(DirEnvVar);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            throw new InvalidOperationException($"{DirEnvVar} is not set or the directory is missing. Fetch the corpus first: {FetchCommand}");
        }

        var result = new Dictionary<string, IReadOnlyList<SltRecord>>(StringComparer.Ordinal);
        foreach (var f in manifest.Files)
        {
            var path = System.IO.Path.Combine(dir, f.Path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > SltParser.MaxFileChars)
            {
                throw new InvalidOperationException($"corpus file {f.Path} is missing, empty or over 20 MB. Fetch the corpus first: {FetchCommand}");
            }

            var bytes = File.ReadAllBytes(path);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), f.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"corpus file {f.Path} does not match the manifest sha256. Fetch the corpus again: {FetchCommand}");
            }

            result[f.Path] = SltParser.Parse(Encoding.UTF8.GetString(bytes), f.Path);
        }

        return result;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException($"manifest requires string '{name}'.");
}
