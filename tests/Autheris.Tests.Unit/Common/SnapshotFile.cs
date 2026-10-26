namespace Autheris.Tests.Unit.Common;

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;

/// <summary>
/// Text snapshot support for refactoring guards (ARCH-MNT-14-SC-05). A snapshot is rewritten only when
/// <c>AUTHERIS_UPDATE_SNAPSHOTS=1</c> is set; the flag is rejected when the run looks like CI, so a pipeline can never
/// silently accept a changed snapshot.
/// </summary>
internal static class SnapshotFile
{
    public const string UpdateFlag = "AUTHERIS_UPDATE_SNAPSHOTS";

    /// <summary>Directory of the calling source file; snapshots live next to the test in a <c>Snapshots</c> folder.</summary>
    public static string SnapshotPath(string fileName, [CallerFilePath] string? callerFile = null) =>
        Path.Combine(Path.GetDirectoryName(callerFile)!, "Snapshots", fileName);

    public static bool IsUpdateRequested(Func<string, string?> getEnv) =>
        string.Equals(getEnv(UpdateFlag), "1", StringComparison.Ordinal);

    public static bool IsCi(Func<string, string?> getEnv) =>
        string.Equals(getEnv("CI"), "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(getEnv("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Throws when the update flag is set in a CI run.</summary>
    public static void EnsureUpdateAllowed(Func<string, string?> getEnv)
    {
        if (IsUpdateRequested(getEnv) && IsCi(getEnv))
        {
            throw new InvalidOperationException($"{UpdateFlag} must not be set in CI; snapshots are updated locally and reviewed in the diff.");
        }
    }

    public static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

    public static void AssertMatches(string actual, string snapshotPath)
    {
        Func<string, string?> getEnv = Environment.GetEnvironmentVariable;
        var normalized = Normalize(actual);
        if (IsUpdateRequested(getEnv))
        {
            EnsureUpdateAllowed(getEnv);
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, normalized);
            return;
        }

        File.Exists(snapshotPath).ShouldBeTrue($"snapshot '{snapshotPath}' is missing; generate it with {UpdateFlag}=1");
        var expected = Normalize(File.ReadAllText(snapshotPath));
        if (!string.Equals(expected, normalized, StringComparison.Ordinal))
        {
            // The actual text goes to a temp file so a failure names the first differing line instead of dumping both texts.
            var actualPath = Path.Combine(Path.GetTempPath(), "autheris-snapshot-actual-" + Path.GetFileName(snapshotPath));
            File.WriteAllText(actualPath, normalized);
            var expectedLines = expected.Split('\n');
            var actualLines = normalized.Split('\n');
            var line = 0;
            while (line < expectedLines.Length && line < actualLines.Length && expectedLines[line] == actualLines[line])
            {
                line++;
            }

            Assert.Fail(
                $"snapshot '{snapshotPath}' differs at line {line + 1}: expected '{(line < expectedLines.Length ? expectedLines[line] : "<end>")}' " +
                $"but was '{(line < actualLines.Length ? actualLines[line] : "<end>")}'. Actual text: {actualPath}. Update with {UpdateFlag}=1 after review.");
        }
    }
}
