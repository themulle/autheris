namespace TrinoSqlEngine;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

public static class SqlKeywords
{
    private static readonly bool[] IsKeywordTable;

    static SqlKeywords()
    {
        var vocab = SqlBaseLexer.DefaultVocabulary;
        const int maxTokenType = 512;
        IsKeywordTable = new bool[maxTokenType];

        var keywordLiteralRegex = new Regex(@"^'[A-Z_]+'$", RegexOptions.Compiled);

        for (int i = 0; i < maxTokenType; i++)
        {
            string? name = vocab.GetLiteralName(i);
            if (name != null && keywordLiteralRegex.IsMatch(name))
            {
                IsKeywordTable[i] = true;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsKeyword(int tokenType)
    {
        return (uint)tokenType < (uint)IsKeywordTable.Length && IsKeywordTable[tokenType];
    }
}
