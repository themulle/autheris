namespace Autheris.Domain.Common;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Virtual filters: pattern over catalog objects, <c>source.schema.object[.column]</c>. A segment is either a name with
/// wildcards (<c>*</c> any number of characters, <c>?</c> one character, everything else literal) or a regular
/// expression in parentheses that must match the whole segment. Only dots outside parentheses separate segments.
/// Matching ignores case like <see cref="TableIdentifier"/>. Expressions run with
/// <see cref="RegexOptions.NonBacktracking"/> (linear time); constructs that need backtracking are rejected.
/// </summary>
public sealed class ObjectPattern
{
    public const int MaxPatternLength = 1024;
    public const int MaxSegmentLength = 256;

    // Defense in depth on top of the linear engine; a timeout surfaces as RegexMatchTimeoutException (callers fail closed).
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Segment[] _segments;

    private ObjectPattern(string text, Segment[] segments)
    {
        Text = text;
        _segments = segments;
    }

    /// <summary>The pattern as text (segments joined with dots).</summary>
    public string Text { get; }

    /// <summary>True for four segments; a three-segment pattern addresses whole objects.</summary>
    public bool HasColumnSegment => _segments.Length == 4;

    public static ObjectPattern Parse(string pattern) =>
        TryParse(pattern, out var parsed, out var error) ? parsed! : throw new ArgumentException(error, nameof(pattern));

    public static bool TryParse(string? pattern, out ObjectPattern? parsed, out string error)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "The pattern is empty.";
            return false;
        }

        if (pattern.Length > MaxPatternLength)
        {
            error = $"The pattern exceeds {MaxPatternLength} characters.";
            return false;
        }

        if (!TrySplit(pattern, out var parts, out error))
        {
            return false;
        }

        return TryBuild(parts, out parsed, out error);
    }

    /// <summary>The four-field form (no separator question); <paramref name="column"/> null addresses whole objects.</summary>
    public static ObjectPattern FromParts(string source, string schema, string objectName, string? column)
    {
        var parts = column == null ? new[] { source, schema, objectName } : new[] { source, schema, objectName, column };
        return TryBuild(parts, out var parsed, out var error) ? parsed! : throw new ArgumentException(error);
    }

    public bool MatchesObject(TableIdentifier table) =>
        _segments[0].IsMatch(table.Domain) && _segments[1].IsMatch(table.Schema) && _segments[2].IsMatch(table.TableName);

    /// <summary>True when the column segment matches <paramref name="column"/>, or when there is no column segment.</summary>
    public bool MatchesColumn(string column) => !HasColumnSegment || _segments[3].IsMatch(column);

    public override string ToString() => Text;

    private static bool TrySplit(string pattern, out List<string> parts, out string error)
    {
        parts = [];
        var current = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                if (--depth < 0)
                {
                    error = $"Unbalanced ')' at position {i}.";
                    return false;
                }
            }
            else if (c == '.' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (depth != 0)
        {
            error = "Unbalanced '(' in the pattern.";
            return false;
        }

        parts.Add(current.ToString());
        error = string.Empty;
        return true;
    }

    private static bool TryBuild(IReadOnlyList<string> parts, out ObjectPattern? parsed, out string error)
    {
        parsed = null;
        if (parts.Count is < 3 or > 4)
        {
            error = $"A pattern has 3 or 4 segments (source.schema.object[.column]), found {parts.Count}.";
            return false;
        }

        var segments = new Segment[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            if (!Segment.TryCreate(parts[i], i, out var segment, out error))
            {
                return false;
            }

            segments[i] = segment!;
        }

        var text = string.Join('.', parts);
        if (text.Length > MaxPatternLength)
        {
            error = $"The pattern exceeds {MaxPatternLength} characters.";
            return false;
        }

        parsed = new ObjectPattern(text, segments);
        error = string.Empty;
        return true;
    }

    private sealed class Segment
    {
        private readonly string? _literal;
        private readonly Regex? _regex;

        private Segment(string? literal, Regex? regex)
        {
            _literal = literal;
            _regex = regex;
        }

        public bool IsMatch(string value) =>
            _literal != null ? string.Equals(_literal, value, StringComparison.OrdinalIgnoreCase) : _regex!.IsMatch(value);

        public static bool TryCreate(string text, int index, out Segment? segment, out string error)
        {
            segment = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = $"Segment {index + 1} is empty.";
                return false;
            }

            if (text.Length > MaxSegmentLength)
            {
                error = $"Segment {index + 1} exceeds {MaxSegmentLength} characters.";
                return false;
            }

            bool isExpression = text[0] == '(' && text[^1] == ')' && IsSingleGroup(text);
            if (!isExpression && (text.Contains('(') || text.Contains(')')))
            {
                error = $"Segment {index + 1} ('{text}'): parentheses must enclose the whole segment.";
                return false;
            }

            string regexText;
            if (isExpression)
            {
                var inner = text[1..^1];
                if (inner.Length == 0)
                {
                    error = $"Segment {index + 1}: the expression in parentheses is empty.";
                    return false;
                }

                regexText = "^(?:" + inner + ")$";
            }
            else if (text.IndexOfAny(['*', '?']) < 0)
            {
                segment = new Segment(text, null);
                error = string.Empty;
                return true;
            }
            else
            {
                var sb = new StringBuilder("^");
                foreach (char c in text)
                {
                    sb.Append(c switch
                    {
                        '*' => ".*",
                        '?' => ".",
                        _ => Regex.Escape(c.ToString())
                    });
                }

                regexText = sb.Append('$').ToString();
            }

            try
            {
                var regex = new Regex(
                    regexText,
                    RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                    MatchTimeout);
                segment = new Segment(null, regex);
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                error = $"Segment {index + 1} ('{text}') is not a supported expression: {ex.Message}";
                return false;
            }
        }

        // "(a)|(b)" starts with '(' and ends with ')' but is two groups: the first '(' must close at the very end.
        private static bool IsSingleGroup(string text)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0 && i != text.Length - 1) return false;
            }

            return depth == 0;
        }
    }
}
