namespace TrinoSqlEngine.Ast.Emit;

/// <summary>
/// The single choke point for a name taken from a client statement that may be echoed in an error response.
/// Accepts 1 to 3 dot-separated ASCII identifier segments (<c>[A-Za-z_][A-Za-z0-9_$]*</c>) of at most 128 characters in total;
/// anything else (quotes, whitespace, control or non-ASCII characters, empty segments) is refused and the caller must omit the name.
/// </summary>
public static class PublicSqlName
{
    public const int MaxLength = 128;
    public const int MaxSegments = 3;

    public static bool TryCreate(string? raw, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength)
        {
            return false;
        }

        int segments = 1;
        bool segmentStart = true;
        foreach (char c in raw)
        {
            if (c == '.')
            {
                if (segmentStart || ++segments > MaxSegments)
                {
                    return false;
                }

                segmentStart = true;
                continue;
            }

            bool letter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
            bool digitOrDollar = (c >= '0' && c <= '9') || c == '$';
            if (segmentStart ? !letter : !(letter || digitOrDollar))
            {
                return false;
            }

            segmentStart = false;
        }

        if (segmentStart)
        {
            return false;
        }

        name = raw;
        return true;
    }
}
