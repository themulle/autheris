namespace Autheris.Application.Extensibility;

public sealed class IngressResult
{
    public IngressDecision Decision { get; init; } = IngressDecision.Continue;
    public int StatusCode { get; init; } = 200;
    public string? Reason { get; init; }
    public IDictionary<string, string> ResponseHeaders { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public object? ShortCircuitPayload { get; init; }

    public static IngressResult Continue() => new() { Decision = IngressDecision.Continue };

    public static IngressResult Challenge(string reason, int statusCode = 412, IDictionary<string, string>? headers = null)
    {
        var result = new IngressResult
        {
            Decision = IngressDecision.Challenge,
            StatusCode = statusCode,
            Reason = reason
        };
        if (headers != null)
        {
            foreach (var (k, v) in headers)
            {
                result.ResponseHeaders[k] = v;
            }
        }
        return result;
    }

    public static IngressResult Deny(string reason, int statusCode = 403, IDictionary<string, string>? headers = null)
    {
        var result = new IngressResult
        {
            Decision = IngressDecision.Deny,
            StatusCode = statusCode,
            Reason = reason
        };
        if (headers != null)
        {
            foreach (var (k, v) in headers)
            {
                result.ResponseHeaders[k] = v;
            }
        }
        return result;
    }

    public static IngressResult ShortCircuit(object payload, int statusCode = 200, IDictionary<string, string>? headers = null)
    {
        var result = new IngressResult
        {
            Decision = IngressDecision.ShortCircuit,
            StatusCode = statusCode,
            ShortCircuitPayload = payload
        };
        if (headers != null)
        {
            foreach (var (k, v) in headers)
            {
                result.ResponseHeaders[k] = v;
            }
        }
        return result;
    }
}
