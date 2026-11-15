namespace Autheris.Api.Endpoints;

using Microsoft.AspNetCore.Routing;

/// <summary>Usage accounting and tariff administration HTTP endpoints.</summary>
public static partial class UsageEndpoints
{
    public static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder app)
    {
        MapTariffEndpoints(app);
        MapReportEndpoints(app);
        return app;
    }
}
