namespace Autheris.Application.Governance;

using System;
using System.Buffers;

/// <summary>
/// Hardware-accelerated (AVX-2 / AVX-512) SIMD token scanner utilizing .NET SearchValues&lt;char&gt;.
/// Achieves multi-gigabyte/sec line-rate scanning of GraphQL delimiters, SQL injection characters,
/// and dangerous token boundaries without regular expression overhead or allocations.
/// </summary>
public static class SimdTokenScanner
{
    // SearchValues for GraphQL special syntax delimiters: { } ( ) : $ @ [ ] ! , =
    private static readonly SearchValues<char> GraphQlDelimiters =
        SearchValues.Create("{}():$@[]!,=");

    // SearchValues for dangerous SQL syntax characters: ; ' " - / * \ | &
    private static readonly SearchValues<char> DangerousSqlChars =
        SearchValues.Create(";'\x22-/*\\|&");

    // SearchValues for whitespace delimiters
    private static readonly SearchValues<char> WhitespaceChars =
        SearchValues.Create(" \t\r\n");

    /// <summary>
    /// Scans span for any GraphQL syntax delimiter using vectorized SIMD instructions.
    /// </summary>
    public static int IndexOfGraphQlDelimiter(ReadOnlySpan<char> span)
    {
        return span.IndexOfAny(GraphQlDelimiters);
    }

    /// <summary>
    /// Checks whether the span contains any GraphQL syntax delimiter characters.
    /// </summary>
    public static bool ContainsGraphQlDelimiter(ReadOnlySpan<char> span)
    {
        return span.ContainsAny(GraphQlDelimiters);
    }

    /// <summary>
    /// Scans span for any dangerous SQL injection token character using vectorized SIMD instructions.
    /// </summary>
    public static bool ContainsDangerousSqlChars(ReadOnlySpan<char> span)
    {
        return span.ContainsAny(DangerousSqlChars);
    }

    /// <summary>
    /// Scans span for dangerous tokens with line-rate throughput.
    /// </summary>
    public static int IndexOfDangerousSqlChar(ReadOnlySpan<char> span)
    {
        return span.IndexOfAny(DangerousSqlChars);
    }

    /// <summary>
    /// Scans span for whitespace characters.
    /// </summary>
    public static int IndexOfWhitespace(ReadOnlySpan<char> span)
    {
        return span.IndexOfAny(WhitespaceChars);
    }
}
