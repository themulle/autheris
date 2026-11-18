using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Column sensitivity lives in <c>ColumnClassifications</c>. Re-creating a loaded table with an object initializer silently drops
/// every field that is not copied by hand, so a copy of a loaded table must use <c>with</c> (or at least carry the classifications).
/// </summary>
[Trait("Category", "Security")]
public sealed class TableMetadataCopyArchitectureTests
{
    private static readonly Regex NewTable = new(@"new\s+TableMetadata\s*\r?\n?\s*\{", RegexOptions.CultureInvariant);
    private static readonly Regex ReadsLoadedTable = new(@"\b(existing\w*|current\w*|loaded\w*|persisted\w*)\.(Identifier|Columns|Table)\b", RegexOptions.CultureInvariant);

    [Fact]
    public void Copies_of_a_loaded_table_keep_column_classifications()
    {
        var root = Root();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match m in NewTable.Matches(text))
            {
                var body = Block(text, m.Index + m.Length - 1);
                if (ReadsLoadedTable.IsMatch(body) && !body.Contains("ColumnClassifications", StringComparison.Ordinal))
                {
                    var line = text.AsSpan(0, m.Index).Count('\n') + 1;
                    offenders.Add($"{rel}:{line}");
                }
            }
        }

        offenders.ShouldBeEmpty("copy a loaded TableMetadata with `with { ... }` so classifications and other persisted governance survive");
    }

    private static string Block(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return text[open..(i + 1)];
            }
        }

        return text[open..];
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }
}
