namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.GraphQL.Subscriptions;
using Autheris.Domain.Options;
using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Issue #279: registers the subscription root type behind <c>Gateway:GraphQL:Subscriptions:Enabled</c>.</summary>
internal static class GatewayGraphQlSubscriptionRegistration
{
    internal static IRequestExecutorBuilder AddGatewaySubscriptions(this IRequestExecutorBuilder builder, GraphQLOptions options)
    {
        if (options.Subscriptions.Enabled)
        {
            builder.AddSubscriptionType<Subscription>().AddInMemorySubscriptions();
        }

        // Stays registered in both modes: socket authentication and revocation watch also protect query traffic.
        return builder.AddSocketSessionInterceptor(sp => sp.GetRequiredService<WebSocketAuthInterceptor>());
    }
}
