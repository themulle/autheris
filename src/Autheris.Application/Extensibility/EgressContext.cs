namespace Autheris.Application.Extensibility;

public sealed class EgressContext
{
    public required IngressContext IngressContext { get; init; }
    public int StatusCode { get; set; } = 200;
    public string? ResponseBodyText { get; set; }
    public ReadOnlyMemory<byte>? ResponseBytes { get; set; }
    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public TimeSpan Elapsed { get; init; }
    public IDictionary<string, object?> Items => IngressContext.Items;
}
