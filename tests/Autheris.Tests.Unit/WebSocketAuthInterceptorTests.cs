namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.GraphQL.Subscriptions;
using Shouldly;
using Xunit;

#pragma warning disable CS0618

public sealed class WebSocketAuthInterceptorTests
{
    [Fact]
    public void ExtractToken_WithBearerPrefix_ExtractsCleanToken()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Authorization"] = "Bearer my-secret-jwt-token"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBe("my-secret-jwt-token");
    }

    [Fact]
    public void ExtractToken_WithApiKeyHeader_ExtractsToken()
    {
        var dict = new Dictionary<string, object?>
        {
            ["x-api-key"] = "enterprise-api-key-99"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBe("enterprise-api-key-99");
    }

    [Fact]
    public void ExtractToken_EmptyOrMissing_ReturnsNull()
    {
        var dict = new Dictionary<string, object?>
        {
            ["unrelated"] = "value"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBeNull();
    }

    [Fact]
    public void ExtractToken_WithMixedCaseAuthorization_ExtractsCleanToken()
    {
        var dict = new Dictionary<string, object?>
        {
            ["authorization"] = "Bearer token-abc"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBe("token-abc");
    }
}
