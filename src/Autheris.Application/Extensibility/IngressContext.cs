namespace Autheris.Application.Extensibility;

using System.Security.Claims;

public sealed class IngressContext
{
    public ClaimsPrincipal? User { get; init; }
    public string Path { get; init; } = "/graphql";
    public string Method { get; init; } = "POST";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string? Query { get; set; }
    public string? OperationName { get; set; }
    public IDictionary<string, object?> Items { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public string? GetHeader(string name)
    {
        return Headers.TryGetValue(name, out var val) ? val : null;
    }
}
