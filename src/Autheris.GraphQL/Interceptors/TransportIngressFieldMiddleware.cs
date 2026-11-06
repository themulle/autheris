namespace Autheris.GraphQL.Interceptors;

using System;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Domain.Options.DataSources;
using HotChocolate.Resolvers;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Issue #447: the service scope a resolver receives can differ from the request scope that carries the stamped ingress.
/// Before every field the ingress of the request (put into the request's context data by <see cref="TransportGateMiddleware"/>) is copied
/// into the resolver scope, so the choke points behind the resolver (aggregation, metrics, table reads) see the same transport. The copy
/// is set-once: a scope that already carries the same value is untouched, and a scope that carries another value fails the request.
/// Nothing is taken from client input.
/// </summary>
public sealed class TransportIngressFieldMiddleware
{
    private readonly FieldDelegate _next;

    public TransportIngressFieldMiddleware(FieldDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(IMiddlewareContext context)
    {
        if (context.ContextData.TryGetValue(TransportGateMiddleware.IngressContextKey, out var value) &&
            value is DataSourceTransportFlags ingress &&
            context.Services.GetService<IIngressTransportContext>() is { Current: DataSourceTransportFlags.None } resolverIngress)
        {
            resolverIngress.Set(ingress);
        }

        await _next(context).ConfigureAwait(false);
    }
}
