using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlCorpusExtractor;

// WP-X0s: extracts every SQL string of the existing test projects into the seed corpus of the SQL compiler gate (plan 9.2).
// Usage: SqlCorpusExtractor <repo-root> <output-dir>
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: SqlCorpusExtractor <repo-root> <output-dir>");
    return 2;
}

string root = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
var result = CorpusExtractor.Extract(root);
Directory.CreateDirectory(output);
File.WriteAllText(Path.Combine(output, "dql.sql"), CorpusExtractor.Render(result.Where(e => e.Class == "select")), new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "dml.sql"), CorpusExtractor.Render(result.Where(e => e.Class != "select")), new UTF8Encoding(false));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"dql={result.Count(e => e.Class == "select")} dml={result.Count(e => e.Class != "select")}"));
return 0;

namespace SqlCorpusExtractor
{
    internal sealed record CorpusEntry(string Id, string Class, string Expected, string Source, string Sql);

    internal static partial class CorpusExtractor
    {
        // Source roots scanned for SQL strings. The gate project itself is excluded: its corpus must not feed itself.
        private static readonly string[] SourceRoots = ["tests", "benchmarks"];
        private const string GateProject = "Autheris.Tests.SqlCompilerGate";

        [GeneratedRegex(@"^\s*(SELECT|WITH|INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex StartsWithStatement();

        [GeneratedRegex(@"\)\s*(INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex CteDml();

        [GeneratedRegex(@"Throws|ShouldThrow|Reject|Invalid|Forbidden|Denied|Fail", RegexOptions.CultureInvariant)]
        private static partial Regex RejectContext();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();

        public static List<CorpusEntry> Extract(string root)
        {
            var found = new Dictionary<string, (string Sql, string Source, bool Reject)>(StringComparer.Ordinal);
            foreach (string sourceRoot in SourceRoots)
            {
                string dir = Path.Combine(root, sourceRoot);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                             .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                         && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                         && !f.Contains(GateProject, StringComparison.Ordinal))
                             .Order(StringComparer.Ordinal))
                {
                    string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    foreach (var (text, reject) in LiteralScanner.Scan(File.ReadAllText(file), RejectContext()))
                    {
                        Add(found, text, relative, reject);
                    }
                }
            }

            foreach (var (file, reject) in new[] { ("trino_statements.json", false), ("trino_statements_invalid.json", true) })
            {
                string path = Path.Combine(root, "tests", "TrinoSqlEngine.Tests", "Fixtures", file);
                if (!File.Exists(path)) continue;
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                foreach (var statement in JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [])
                {
                    Add(found, statement, relative, reject);
                }
            }

            foreach (var (template, sql) in GeneratedCorpus.Queries().Concat(GeneratedCorpus.Statements()))
            {
                Add(found, sql, "generated:" + template, GeneratedCorpus.ExpectedRejected.Contains(template));
            }

            return found.Select(p => new CorpusEntry(
                    Id: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Key)))[..12].ToLowerInvariant(),
                    Class: Classify(p.Value.Sql),
                    Expected: p.Value.Reject ? "reject" : "accept",
                    Source: p.Value.Source,
                    Sql: p.Value.Sql))
                .OrderBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
        }

        private static void Add(Dictionary<string, (string, string, bool)> found, string raw, string source, bool reject)
        {
            string sql = raw.Trim();
            if (!StartsWithStatement().IsMatch(sql) || !sql.Contains(' ', StringComparison.Ordinal)) return;
            if (sql.Contains("-- @id", StringComparison.Ordinal)) return;
            string key = Whitespace().Replace(sql, " ");
            if (!found.TryGetValue(key, out var existing))
            {
                found[key] = (sql, source, reject);
            }
            else if (reject != existing.Item3)
            {
                // the same text is expected to be accepted by one test and rejected by another: an accept expectation is not decidable
                found[key] = (existing.Item1, existing.Item2, false);
            }
        }

        private static string Classify(string sql)
        {
            string first = StartsWithStatement().Match(sql).Groups[1].Value.ToUpperInvariant();
            if (first == "WITH")
            {
                var dml = CteDml().Match(sql);
                return dml.Success ? dml.Groups[1].Value.ToLowerInvariant() : "select";
            }

            return first.ToLowerInvariant();
        }

        public static string Render(IEnumerable<CorpusEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                sb.Append("-- @id ").Append(e.Id)
                    .Append(" class=").Append(e.Class)
                    .Append(" expected=").Append(e.Expected)
                    .Append(" source=").Append(e.Source).Append('\n')
                    .Append(e.Sql.Replace("\r\n", "\n", StringComparison.Ordinal)).Append("\n\n");
            }

            return sb.ToString();
        }
    }

    /// <summary>A small scanner for C# string literals (regular, verbatim and raw; interpolated strings are skipped).</summary>
    internal static class LiteralScanner
    {
        public static IEnumerable<(string Text, bool RejectContext)> Scan(string source, Regex rejectContext)
        {
            int i = 0;
            int n = source.Length;
            while (i < n)
            {
                char c = source[i];
                if (c == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    while (i < n && source[i] != '\n') i++;
                    continue;
                }

                if (c == '/' && i + 1 < n && source[i + 1] == '*')
                {
                    int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                    continue;
                }

                if (c == '\'')
                {
                    i = SkipChar(source, i);
                    continue;
                }

                bool interpolated = false;
                bool verbatim = false;
                int j = i;
                while (j < n && (source[j] == '$' || source[j] == '@') && j - i < 3)
                {
                    if (source[j] == '$') interpolated = true;
                    else verbatim = true;
                    j++;
                }

                if (j < n && source[j] == '"' && (j == i || (j > i && !IsIdentifierChar(i > 0 ? source[i - 1] : ' '))))
                {
                    int start = j;
                    (string? text, int next) = ReadLiteral(source, start, verbatim);
                    if (text is not null && !interpolated)
                    {
                        int from = Math.Max(0, i - 200);
                        yield return (text, rejectContext.IsMatch(source.AsSpan(from, Math.Min(n - from, i - from + 200))));
                    }

                    i = next;
                    continue;
                }

                i++;
            }
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static int SkipChar(string s, int i)
        {
            int j = i + 1;
            while (j < s.Length && s[j] != '\'')
            {
                if (s[j] == '\\') j++;
                j++;
            }

            return Math.Min(s.Length, j + 1);
        }

        private static (string? Text, int Next) ReadLiteral(string s, int quoteIndex, bool verbatim)
        {
            int quotes = 0;
            while (quoteIndex + quotes < s.Length && s[quoteIndex + quotes] == '"') quotes++;
            if (quotes >= 3 && !verbatim)
            {
                return ReadRaw(s, quoteIndex, quotes);
            }

            int k = quoteIndex + 1;
            var sb = new StringBuilder();
            while (k < s.Length)
            {
                char ch = s[k];
                if (verbatim)
                {
                    if (ch == '"')
                    {
                        if (k + 1 < s.Length && s[k + 1] == '"') { sb.Append('"'); k += 2; continue; }
                        return (sb.ToString(), k + 1);
                    }

                    sb.Append(ch);
                    k++;
                    continue;
                }

                if (ch == '"') return (sb.ToString(), k + 1);
                if (ch == '\n') return (null, k);
                if (ch == '\\' && k + 1 < s.Length)
                {
                    char e = s[k + 1];
                    k += 2;
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '0': sb.Append('\0'); break;
                        case 'u' when k + 4 <= s.Length && int.TryParse(s.AsSpan(k, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u):
                            sb.Append((char)u);
                            k += 4;
                            break;
                        default: sb.Append(e); break;
                    }

                    continue;
                }

                sb.Append(ch);
                k++;
            }

            return (null, s.Length);
        }

        private static (string? Text, int Next) ReadRaw(string s, int quoteIndex, int quotes)
        {
            string delimiter = new('"', quotes);
            int bodyStart = quoteIndex + quotes;
            int close = s.IndexOf(delimiter, bodyStart, StringComparison.Ordinal);
            if (close < 0) return (null, s.Length);
            string body = s[bodyStart..close];
            // content starts on the line after the opening delimiter; the closing delimiter's indentation is stripped from every line
            int firstNewline = body.IndexOf('\n', StringComparison.Ordinal);
            if (firstNewline < 0) return (body, close + quotes);
            int lastNewline = body.LastIndexOf('\n');
            string indent = body[(lastNewline + 1)..];
            string content = lastNewline > firstNewline ? body[(firstNewline + 1)..lastNewline] : string.Empty;
            var lines = content.Split('\n').Select(l => l.TrimEnd('\r'));
            string text = string.Join('\n', lines.Select(l => l.StartsWith(indent, StringComparison.Ordinal) ? l[indent.Length..] : l.TrimStart()));
            return (text, close + quotes);
        }
    }
}
