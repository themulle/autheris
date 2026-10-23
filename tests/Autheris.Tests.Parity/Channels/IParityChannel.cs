using System.Text.Json;

namespace Autheris.Tests.Parity.Channels;

public enum ParityFn
{
    Count,
    Sum,
    Avg,
    Min,
    Max,
    CountDistinct,
}

public sealed record ParityAggregate(ParityFn Fn, string? Column, string Alias);

public sealed record ParityEquals(string Column, string Value);

/// <summary>Logical query (D-8), rendered per channel. WP1 covers aggregates, group by, equality filters and ordering by group column.</summary>
public sealed record ParityQuery(
    string Table,
    IReadOnlyList<ParityAggregate> Aggregates,
    IReadOnlyList<string>? GroupBy = null,
    IReadOnlyList<ParityEquals>? Where = null,
    string? RawSql = null);

public sealed record ChannelResult(
    int Status,
    string? Code,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    string RawBody,
    bool NotExpressible = false,
    bool IsPending = false)
{
    public static readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> NoRows = [];

    public static ChannelResult CreateNotExpressible() => new(0, null, NoRows, string.Empty, NotExpressible: true);

    public static ChannelResult CreatePending() => new(0, null, NoRows, string.Empty, IsPending: true);

    public static object? ToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDecimal(),
        JsonValueKind.String => e.GetString(),
        _ => e.GetRawText(),
    };
}

public interface IParityChannel
{
    string Name { get; }

    Task<ChannelResult> ExecuteAsync(ParityQuery query, CancellationToken ct);
}
