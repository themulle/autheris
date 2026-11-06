namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.Policy.Services;
using Autheris.Application.Security;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Application.Webhooks;
using Autheris.Application.Governance.Workflows;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Governance;
using Autheris.Infrastructure.Lineage;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Security;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.DependencyInjection;

public static partial class GatewayGovernanceServiceExtensions
{
    private static void AddRestrictedScopesAndConsentResolution(IServiceCollection services)
    {
        // 143 (D-1/D-15): restricted scopes. Bound once (IOptions, not reloadable) and validated at startup.
        services.AddOptions<Autheris.Domain.Options.RestrictedScopeOptions>()
            .BindConfiguration(Autheris.Domain.Options.RestrictedScopeOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Autheris.Domain.Options.RestrictedScopeOptions>, Autheris.Application.Policy.RestrictedScopeOptionsValidator>();
        services.AddSingleton<Autheris.Domain.Security.IRestrictedScopePolicy>(sp =>
            Autheris.Domain.Security.RestrictedScopePolicy.FromOptions(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.RestrictedScopeOptions>>().Value));
        services.AddSingleton<IConsentResolutionService, ConsentResolutionService>();
        services.AddSingleton<IIdentitySubjectResolver, IdentitySubjectResolver>();
        // only a person decides consent requests. The resolvers resolve the guard with GetRequiredService (no fallback).
        services.AddSingleton<Autheris.Application.Governance.ConsentRequests.IConsentDecisionGuard, Autheris.Application.Governance.ConsentRequests.ConsentDecisionGuard>();
        services.AddSingleton<Autheris.Application.Governance.ConsentRequests.IConsentDecisionDenialRecorder, Autheris.Application.Governance.ConsentRequests.ConsentDecisionDenialRecorder>();
        services.AddSingleton<IKeyVaultSecretProvider, DefaultEnvironmentSecretProvider>();
        services.AddSingleton<IColumnMaskingProvider, ColumnMaskingProvider>();
    }

    private static void AddConsentRequests(IServiceCollection services)
    {
        services.AddScoped<Autheris.Application.ConsentRequests.IConsentRequestService, Autheris.Application.ConsentRequests.ConsentRequestService>();
        services.AddScoped<IConsentRecertificationService, ConsentRecertificationWorkflowService>();
        services.AddHostedService<ConsentRecertificationHostedService>();
    }
}
