namespace Autheris.Application.Extensibility;

public sealed class EgressResult
{
    public bool Handled { get; init; }
    public string? MutatedResponseText { get; init; }
    public ReadOnlyMemory<byte>? MutatedResponseBytes { get; init; }
    public IDictionary<string, string> AdditionalHeaders { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static EgressResult Unmodified() => new();

    public static EgressResult Modify(string newBody, IDictionary<string, string>? headers = null)
    {
        var res = new EgressResult
        {
            Handled = true,
            MutatedResponseText = newBody
        };
        if (headers != null)
        {
            foreach (var (k, v) in headers)
            {
                res.AdditionalHeaders[k] = v;
            }
        }
        return res;
    }
}
