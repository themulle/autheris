namespace Autheris.Api.Extensions;

using Autheris.Api.Endpoints;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Application.Jobs.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public static class AsyncJobServiceExtensions
{
    public static IServiceCollection AddDatasourceTesting(this IServiceCollection services)
    {
        services.AddScoped<IDatasourceTestingService, DatasourceTestingService>();
        return services;
    }

    public static IServiceCollection AddAsyncJobs(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddOptions<AsyncJobOptions>().BindConfiguration(AsyncJobOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AsyncJobOptions>, AsyncJobOptionsValidator>();
        services.AddSingleton<AsyncJobNodeIdentity>();
        services.AddSingleton<AsyncQueryJobManager>();
        services.AddSingleton<IAsyncQueryJobManager>(sp => sp.GetRequiredService<AsyncQueryJobManager>());
        services.AddSingleton<IAsyncJobQueue>(sp => sp.GetRequiredService<AsyncQueryJobManager>());
        services.AddHostedService<AsyncQueryJobBackgroundWorker>();
        return services;
    }

    public static IServiceCollection AddAsyncJobsAndDatasourceTesting(this IServiceCollection services, IConfiguration? configuration = null)
    {
        return services
            .AddDatasourceTesting()
            .AddAsyncJobs(configuration);
    }
}

