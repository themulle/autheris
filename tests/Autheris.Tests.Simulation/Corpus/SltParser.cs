using System.Globalization;
using System.Text.RegularExpressions;

namespace Autheris.Tests.Simulation.Corpus;

/// <summary>
/// Parses sqllogictest files as untrusted text (SEC-SIM-12): bounded file size, line length and record count; no <c>include</c>, no shell,
/// no evaluation; an unknown directive is an error (fail-closed). SQL bodies are carried as plain strings and nothing executes here.
/// </summary>
public static partial class SltParser
{
    public const int MaxFileChars = 20 * 1024 * 1024;
    public const int MaxLineChars = 64 * 1024;
    public const int MaxRecords = 200_000;

    [GeneratedRegex(@"^query (?<types>[TIR]+)(?: (?<sort>nosort|rowsort|valuesort))?(?: (?<label>[A-Za-z0-9_\-]{1,64}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex QueryHeader();

    [GeneratedRegex(@"^(?<count>[0-9]{1,9}) values hashing to (?<md5>[0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex HashLine();

    [GeneratedRegex(@"^(?<kind>skipif|onlyif) (?<engine>[A-Za-z0-9_\-]{1,32})$", RegexOptions.CultureInvariant)]
    private static partial Regex ConditionLine();

    [GeneratedRegex(@"^hash-threshold (?<n>[0-9]{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex ThresholdLine();

    public static IReadOnlyList<SltRecord> Parse(string text, string file)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxFileChars)
        {
            throw new SltParseException($"{file}: file exceeds {MaxFileChars} characters.");
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var l in lines)
        {
            if (l.Length > MaxLineChars)
            {
                throw new SltParseException($"{file}: a line exceeds {MaxLineChars} characters.");
            }
        }

        var records = new List<SltRecord>();
        var pending = new List<SltCondition>();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var lineNo = i + 1;
            if (line.Length == 0 || line.TrimEnd().Length == 0 || line[0] == '#')
            {
                i++;
                continue;
            }

            if (records.Count >= MaxRecords)
            {
                throw new SltParseException($"{file}: more than {MaxRecords} records.");
            }

            var trimmed = line.TrimEnd();
            var cond = ConditionLine().Match(trimmed);
            if (cond.Success)
            {
                pending.Add(new SltCondition(cond.Groups["kind"].Value == "skipif", cond.Groups["engine"].Value));
                i++;
                continue;
            }

            SltRecord record;
            if (trimmed == "halt")
            {
                record = new SltHalt(file, lineNo);
                i++;
            }
            else if (ThresholdLine().Match(trimmed) is { Success: true } th)
            {
                record = new SltHashThreshold(file, lineNo, int.Parse(th.Groups["n"].Value, CultureInfo.InvariantCulture));
                i++;
            }
            else if (trimmed is "statement ok" or "statement error")
            {
                i++;
                var sql = ReadBlock(lines, ref i, stopAtSeparator: false);
                record = new SltStatement(file, lineNo, trimmed == "statement ok", sql);
            }
            else if (QueryHeader().Match(trimmed) is { Success: true } q)
            {
                i++;
                var sql = ReadBlock(lines, ref i, stopAtSeparator: true);
                if (i >= lines.Length || lines[i].TrimEnd() != "----")
                {
                    throw new SltParseException($"{file}:{lineNo}: query record has no '----' separator.");
                }

                i++;
                var expectedLines = new List<string>();
                while (i < lines.Length && lines[i].TrimEnd().Length != 0)
                {
                    expectedLines.Add(lines[i].TrimEnd('\r'));
                    i++;
                }

                SltExpected expected = expectedLines is [var only] && HashLine().Match(only) is { Success: true } h
                    ? new SltHash(int.Parse(h.Groups["count"].Value, CultureInfo.InvariantCulture), h.Groups["md5"].Value)
                    : new SltValues(expectedLines);
                var sort = q.Groups["sort"].Value switch
                {
                    "rowsort" => SltSortMode.RowSort,
                    "valuesort" => SltSortMode.ValueSort,
                    _ => SltSortMode.NoSort,
                };
                var label = q.Groups["label"].Success ? q.Groups["label"].Value : null;
                record = new SltQuery(file, lineNo, q.Groups["types"].Value, sort, label, sql, expected);
            }
            else
            {
                throw new SltParseException($"{file}:{lineNo}: unknown directive.");
            }

            records.Add(pending.Count == 0 ? record : record with { Conditions = [.. pending] });
            pending.Clear();
        }

        if (pending.Count > 0)
        {
            throw new SltParseException($"{file}: dangling skipif/onlyif without a record.");
        }

        return records;
    }

    private static string ReadBlock(string[] lines, ref int i, bool stopAtSeparator)
    {
        var body = new List<string>();
        while (i < lines.Length && lines[i].TrimEnd().Length != 0 && !(stopAtSeparator && lines[i].TrimEnd() == "----"))
        {
            body.Add(lines[i].TrimEnd('\r'));
            i++;
        }

        if (body.Count == 0)
        {
            throw new SltParseException("record without a SQL body.");
        }

        return string.Join('\n', body);
    }
}

/// <summary>The dialect filter: <c>skipif X</c> / <c>onlyif X</c> against the engine under test (here <c>sqlite</c>).</summary>
public static class SltDialectFilter
{
    public const string Engine = "sqlite";

    public static bool Applies(SltRecord record, string engine = Engine)
    {
        foreach (var c in record.Conditions)
        {
            var matches = string.Equals(c.Engine, engine, StringComparison.OrdinalIgnoreCase);
            if (c.Skip ? matches : !matches)
            {
                return false;
            }
        }

        return true;
    }
}
