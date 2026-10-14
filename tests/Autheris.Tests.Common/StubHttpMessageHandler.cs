namespace Autheris.Tests.Common;

/// <summary>Delegating-free HTTP handler that answers every request through a supplied function and records requests.</summary>
public sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests = [];

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return Task.FromResult(responder(request));
    }
}
