namespace Autheris.Api.Serialization;

using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

/// <summary>
/// F-DATA-01: Central Accept-header negotiation for Parquet output on all data egress channels.
/// Parquet is selected only when <c>application/vnd.apache.parquet</c> (alias <c>application/x-parquet</c>) is
/// listed explicitly with q &gt; 0 and no other listed media type has a higher q value. Wildcards never select Parquet.
/// </summary>
public static class ParquetContentNegotiation
{
    public const string ParquetMediaType = "application/vnd.apache.parquet";
    public const string ParquetMediaTypeAlias = "application/x-parquet";

    /// <summary>Result of evaluating the Accept header.</summary>
    /// <param name="ParquetPreferred">Parquet is explicitly requested and has the highest q value (ties favour Parquet).</param>
    /// <param name="HasJsonAlternative">A JSON media type or a wildcard (*/*, application/*) is acceptable as well (q &gt; 0).</param>
    public readonly record struct AcceptEvaluation(bool ParquetPreferred, bool HasJsonAlternative);

    public static bool IsParquetRequested(HttpRequest request) => Evaluate(request).ParquetPreferred;

    public static AcceptEvaluation Evaluate(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var acceptValues = request.Headers.Accept;
        if (acceptValues.Count == 0)
        {
            return default;
        }

        var inputs = new List<string>(acceptValues.Count);
        foreach (var value in acceptValues)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                inputs.Add(value);
            }
        }

        if (inputs.Count == 0 ||
            !MediaTypeHeaderValue.TryParseList(inputs, out var parsed) ||
            parsed is null)
        {
            return default;
        }

        double bestParquet = 0;
        double bestOther = 0;
        bool hasJsonAlternative = false;

        foreach (var mediaType in parsed)
        {
            var quality = mediaType.Quality ?? 1.0;
            if (quality <= 0)
            {
                continue;
            }

            if (IsParquetMediaType(mediaType))
            {
                bestParquet = Math.Max(bestParquet, quality);
                continue;
            }

            bestOther = Math.Max(bestOther, quality);
            if (IsJsonOrWildcard(mediaType))
            {
                hasJsonAlternative = true;
            }
        }

        return new AcceptEvaluation(
            ParquetPreferred: bestParquet > 0 && bestParquet >= bestOther,
            HasJsonAlternative: hasJsonAlternative);
    }

    internal static bool IsParquetMediaType(MediaTypeHeaderValue mediaType) =>
        mediaType.MediaType.Equals(ParquetMediaType, StringComparison.OrdinalIgnoreCase) ||
        mediaType.MediaType.Equals(ParquetMediaTypeAlias, StringComparison.OrdinalIgnoreCase);

    private static bool IsJsonOrWildcard(MediaTypeHeaderValue mediaType)
    {
        if (mediaType.MatchesAllTypes)
        {
            return true;
        }

        if (!mediaType.Type.Equals("application", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return mediaType.MatchesAllSubTypes ||
               mediaType.SubType.Equals("json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.SubType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
