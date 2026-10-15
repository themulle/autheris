namespace Autheris.Api.Extensions;

using Autheris.Api.Endpoints;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Application.Jobs.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public static class Plan9ServiceExtensions
{
    public static IServiceCollection AddPlan9Services(this IServiceCollection services, IConfiguration? configuration = null)
    {
        // AP-9.1: Datasource Connection Testing
        services.AddScoped<IDatasourceTestingService, DatasourceTestingService>();

        // AP-9.4: Async Long-Running Query Job Engine
        services.AddOptions<AsyncJobOptions>().BindConfiguration(AsyncJobOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AsyncJobOptions>, AsyncJobOptionsValidator>();
        services.AddSingleton<AsyncJobNodeIdentity>();
        services.AddSingleton<AsyncQueryJobManager>();
        services.AddSingleton<IAsyncQueryJobManager>(sp => sp.GetRequiredService<AsyncQueryJobManager>());
        services.AddSingleton<IAsyncJobQueue>(sp => sp.GetRequiredService<AsyncQueryJobManager>());
        services.AddHostedService<AsyncQueryJobBackgroundWorker>();

        return services;
    }

    public static IEndpointRouteBuilder MapPlan9Endpoints(this IEndpointRouteBuilder app)
    {
        app.MapAsyncJobEndpoints();
        return app;
    }
}
