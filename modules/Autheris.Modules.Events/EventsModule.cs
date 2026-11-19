namespace Autheris.Modules.Events;

using System;
using System.Collections.Generic;
using Autheris.Api.Endpoints;
using Autheris.Modules.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Event delivery module: CDC stream administration, CDC webhook subscriptions and delivery, CDC event ingestion and the
/// configured event webhooks (dispatcher, bridge and delivery workers). The governed stores, the stream crypto, the row filter and
/// masking enforcer and the webhook outbox stay in the host; the module resolves those ports from DI.
/// </summary>
public sealed class EventsModule : IAutherisModule
{
    public string Id => "events";

    public IReadOnlyList<string> ConfigurationSections => ["Gateway:Events"];

    public void ConfigureServices(
        IServiceCollection services,
        IConfiguration moduleConfiguration,
        IModuleHostContext host)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(moduleConfiguration);
        ArgumentNullException.ThrowIfNull(host);

        services.AddEventsModule(moduleConfiguration);
    }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(EventsRoutes.Prefix);
        StreamingCdcEndpoints.MapEndpoints(group);
        CdcStreamAdminEndpoints.MapEndpoints(group);
    }
}

/// <summary>Route prefix of the events module; every route sits under <c>/modules/events/v1/cdc</c>.</summary>
public static class EventsRoutes
{
    public const string Prefix = "/modules/events";

    public const string CdcPrefix = "/v1/cdc";
}
