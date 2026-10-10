namespace Autheris.Application.Governance;

using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Microsoft.Extensions.DependencyInjection;

public static class ClassificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data classification engine, workflow state machine and WORM configuration audit services.
    /// </summary>
    public static IServiceCollection AddAutherisDataClassification(this IServiceCollection services)
    {
        services.AddSingleton<IClassificationEngine, ClassificationEngine>();
        services.AddSingleton<IClassificationWorkflowStateMachine, ClassificationWorkflowStateMachine>();
        services.AddSingleton<IWormConfigurationAuditService, WormConfigurationAuditService>();
        return services;
    }
}
