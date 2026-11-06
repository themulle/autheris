namespace Autheris.Tests.Unit.GraphQL;

using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// GQL-2: over graphql-ws the HttpContext is the upgrade request whose response has started; the GraphQL request
/// middlewares must not touch its headers or status (Kestrel's headers are read-only then and writing throws).
/// </summary>
public sealed class StartedResponseGql2Tests
{
    public sealed class PingQuery
    {
        public string Ping() => "pong";
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public StartedResponseFeature() => Headers = new HeaderDictionary { IsReadOnly = true };

        public override bool HasStarted => true;
    }

    private static HttpContext StartedHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        return context;
    }

    [Fact]
    public void Guard_SkipsWrites_WhenResponseHasStarted()
    {
        var context = StartedHttpContext();

        Should.NotThrow(() =>
        {
            HttpResponseGuard.SetHeader(context, "X-Query-Cost", "1");
            HttpResponseGuard.SetStatus(context, StatusCodes.Status429TooManyRequests);
            HttpResponseGuard.RemoveHeader(context, "Cache-Tag");
        });
    }
}
