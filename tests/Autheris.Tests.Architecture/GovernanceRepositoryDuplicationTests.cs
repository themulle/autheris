namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

public sealed class GovernanceRepositoryDuplicationTests
{
    private const int ChunkSize = 5;
    private const double MaxAllowedRatio = 0.15; // 15%

    [Fact]
    [Trait("Category", "Architecture")]
    public void Duplicated_code_ratio_below_15_percent()
    {
        var repoDir = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Infrastructure", "Persistence");
        var files = Directory.GetFiles(repoDir, "GovernanceRepository*.cs", SearchOption.TopDirectoryOnly);
        files.ShouldNotBeEmpty();

        var fileLines = new Dictionary<string, List<string>>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("using ", StringComparison.Ordinal) && l != "{" && l != "}")
                .ToList();
            fileLines[file] = lines;
        }

        var totalLines = fileLines.Values.Sum(l => l.Count);
        totalLines.ShouldBeGreaterThan(0);

        var chunkCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (file, lines) in fileLines)
        {
            var seenInFile = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i <= lines.Count - ChunkSize; i++)
            {
                var chunk = string.Join("\n", lines.Skip(i).Take(ChunkSize));
                if (seenInFile.Add(chunk))
                {
                    chunkCounts[chunk] = chunkCounts.GetValueOrDefault(chunk) + 1;
                }
            }
        }

        var duplicatedChunks = chunkCounts.Where(kvp => kvp.Value > 1).Select(kvp => kvp.Key).ToHashSet(StringComparer.Ordinal);
        var duplicatedLineIndices = new HashSet<(string File, int LineIndex)>();

        foreach (var (file, lines) in fileLines)
        {
            for (var i = 0; i <= lines.Count - ChunkSize; i++)
            {
                var chunk = string.Join("\n", lines.Skip(i).Take(ChunkSize));
                if (duplicatedChunks.Contains(chunk))
                {
                    for (var j = i; j < i + ChunkSize; j++)
                    {
                        duplicatedLineIndices.Add((file, j));
                    }
                }
            }
        }

        var duplicateRatio = (double)duplicatedLineIndices.Count / totalLines;
        duplicateRatio.ShouldBeLessThan(
            MaxAllowedRatio,
            $"Duplicated code ratio across GovernanceRepository*.cs was {duplicateRatio:P2} ({duplicatedLineIndices.Count}/{totalLines} lines), which exceeds the {MaxAllowedRatio:P0} threshold.");
    }
}
