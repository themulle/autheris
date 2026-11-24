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
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
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
        services.AddSingleton<Autheris.Application.Governance.Grants.Records.IGrantRecordStore>(sp =>
            new Autheris.Infrastructure.Persistence.GovernanceRepository.GrantRecordStore(sp.GetRequiredService<Autheris.Infrastructure.Persistence.Dialects.IGovernanceSqlDialect>()));
        services.AddSingleton<Autheris.Application.Governance.Grants.Records.IGrantRecordSigner>(sp =>
            GrantRecordKeyStartupCheck.CreateSigner(
                sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
                sp.GetService<IKeyVaultSecretProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>()));
        services.AddSingleton<Autheris.Application.Governance.Grants.Records.IGrantWriter>(sp =>
            new Autheris.Application.Governance.Grants.Records.GrantWriter(
                sp.GetRequiredService<Autheris.Application.Governance.Grants.Records.IGrantRecordStore>(),
                sp.GetRequiredService<Autheris.Application.Governance.Grants.Records.IGrantRecordSigner>(),
                sp.GetService<TimeProvider>()));
        services.AddSingleton<Autheris.Application.Governance.Grants.Records.IGrantChainVerifier, Autheris.Application.Governance.Grants.Records.GrantChainVerifier>();
        services.AddSingleton<Autheris.Application.Governance.Grants.Records.IGrantRecordExportService, Autheris.Application.Governance.Grants.Records.GrantRecordExportService>();
        services.AddHostedService<GrantRecordKeyStartupCheck>();
        services.AddHostedService<Autheris.Infrastructure.Persistence.Migrations.MigrationPurgeAuditHostedService>();
        services.AddOptions<Autheris.Domain.Options.GrantValidityOptions>()
            .BindConfiguration(Autheris.Domain.Options.GrantValidityOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Autheris.Domain.Options.GrantValidityOptions>, Autheris.Application.Governance.Grants.GrantValidityOptionsValidator>();
        services.AddOptions<Autheris.Domain.Options.ConsentEventsOptions>()
            .BindConfiguration(Autheris.Domain.Options.ConsentEventsOptions.SectionName)
            .Validate(
                o => o.MaxAttempts >= 1 && o.RetentionDays >= 1 && o.PollIntervalSeconds >= 1 && o.BatchSize >= 1 && o.LeaseSeconds >= 5,
                "ConsentEventsOptions bounds must be positive (LeaseSeconds >= 5).")
            .ValidateOnStart();
        services.AddSingleton<Autheris.Application.Governance.Grants.IGrantValidityPolicy, Autheris.Application.Governance.Grants.GrantValidityPolicy>();
        services.AddSingleton<Autheris.Application.ConsentRequests.Routing.IConsentWorkflowRouter, Autheris.Application.ConsentRequests.Routing.ConsentWorkflowRouter>();
        services.AddSingleton<Autheris.Application.Governance.ConsentRequests.ISeparationOfDutiesService, Autheris.Application.Governance.ConsentRequests.SeparationOfDutiesService>();
        services.AddSingleton<Autheris.Application.ConsentRequests.ILegacyExternalDecisionGuard, Autheris.Application.ConsentRequests.LegacyExternalDecisionGuard>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ExternalSystemRequirementHandler>();
        services.AddScoped<Autheris.Application.ConsentRequests.IConsentDecisionPipeline, Autheris.Application.ConsentRequests.ConsentDecisionPipeline>();
        services.AddScoped<Autheris.Application.ConsentRequests.IConsentRequestService>(sp =>
            new Autheris.Application.ConsentRequests.ConsentRequestEndpointAdapter(
                sp.GetRequiredService<Autheris.Application.Governance.Requests.IPermissionRequestEngine>(),
                sp.GetService<Autheris.Application.Common.Interfaces.IIdentitySubjectResolver>()));
        services.AddSingleton<Autheris.Application.Policy.Classification.IClassificationAdminService, Autheris.Application.Policy.Classification.ClassificationAdminService>();
        services.AddSingleton<Autheris.Application.Governance.Grants.IOwnerGrantService, Autheris.Application.Governance.Grants.OwnerGrantService>();
        services.AddSingleton<Autheris.Application.ConsentRequests.ProfileAssignments.IProfileGrantMaterializer, Autheris.Application.ConsentRequests.ProfileAssignments.ProfileGrantMaterializer>();
        services.AddScoped<Autheris.Application.ConsentRequests.ProfileAssignments.ProfileAssignmentRequestService>();
        services.AddScoped<Autheris.Application.ConsentRequests.ProfileAssignments.IProfileAssignmentRequestService>(sp => sp.GetRequiredService<Autheris.Application.ConsentRequests.ProfileAssignments.ProfileAssignmentRequestService>());
        services.AddScoped<Autheris.Application.ConsentRequests.ProfileAssignments.IProfileAssignmentDecisionHandler>(sp => sp.GetRequiredService<Autheris.Application.ConsentRequests.ProfileAssignments.ProfileAssignmentRequestService>());
        services.AddSingleton<Autheris.Infrastructure.Persistence.PermissionRequestStore>(sp =>
            new Autheris.Infrastructure.Persistence.PermissionRequestStore(
                sp.GetRequiredService<Autheris.Infrastructure.Persistence.Dialects.IGovernanceSqlDialect>(),
                sp.GetService<TimeProvider>()));
        services.AddSingleton<Autheris.Application.Governance.Requests.IConsentExtrasWriter>(sp =>
            sp.GetRequiredService<Autheris.Infrastructure.Persistence.PermissionRequestStore>());
        services.AddSingleton<Autheris.Application.Governance.Requests.ApproverResolver>();
        services.AddSingleton<Autheris.Application.Governance.Requests.ApproverEligibility>();
        services.AddSingleton<Autheris.Application.Governance.Requests.IGrantEffectApplier, Autheris.Application.Governance.Requests.DataGrantApplier>();
        services.AddSingleton<Autheris.Application.Governance.Requests.IGrantEffectApplier, Autheris.Application.Governance.Requests.UnmaskGrantApplier>();
        services.AddSingleton<Autheris.Application.Governance.Requests.IGrantEffectApplier, Autheris.Application.Governance.Requests.CatalogGrantApplier>();
        services.AddSingleton<Autheris.Application.Governance.Requests.IGrantEffectApplier, Autheris.Application.Governance.Requests.ProfileAssignmentApplier>();
        services.AddScoped<Autheris.Application.Governance.Requests.PermissionRequestEngine>();
        services.AddScoped<Autheris.Application.ConsentRequests.IExternalDecisionGuard>(sp =>
            new Autheris.Application.ConsentRequests.ExternalDecisionGuard(
                sp.GetService<Autheris.Application.ConsentRequests.IExternalApproverResolver>(),
                sp.GetService<Autheris.Application.Webhooks.IWebhookSecretProtector>(),
                sp.GetService<Autheris.Application.Common.Interfaces.IClusterWideIdempotencyStore>(),
                sp.GetService<TimeProvider>(),
                allowPlaintextSecret: sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>() is { } hostEnv && string.Equals(hostEnv.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase)));
        services.AddScoped<Autheris.Application.ConsentRequests.IExternalApproverResolver, Autheris.Application.ConsentRequests.RealmExternalApproverResolver>();
        services.AddScoped<Autheris.Application.ConsentRequests.IExternalDecisionService>(sp =>
            new Autheris.Application.ConsentRequests.ExternalDecisionService(
                sp.GetRequiredService<Autheris.Application.Governance.Requests.IPermissionRequestEngine>(),
                sp.GetRequiredService<Autheris.Application.ConsentRequests.IExternalDecisionGuard>(),
                sp.GetRequiredService<Autheris.Application.Common.Interfaces.IConsentApprovalRepository>(),
                sp.GetRequiredService<Autheris.Application.ConsentRequests.IConsentWorkflowConfigRepository>(),
                sp.GetRequiredService<Autheris.Application.ConsentRequests.Routing.IConsentWorkflowRouter>()));
        services.AddScoped<Autheris.Application.Governance.Requests.IPermissionRequestEngine>(sp => sp.GetRequiredService<Autheris.Application.Governance.Requests.PermissionRequestEngine>());
        services.AddScoped<Autheris.Application.Governance.Requests.SelfGrantService>();
        services.AddSingleton<Autheris.Application.Governance.Grants.IGrantRevocationService>(sp => new Autheris.Application.Governance.Grants.GrantRevocationService(
            sp.GetRequiredService<Autheris.Application.Common.Interfaces.IConsentRepository>(),
            sp.GetRequiredService<Autheris.Application.Common.Interfaces.IConsentApprovalRepository>(),
            sp.GetRequiredService<Autheris.Application.Audit.IAuditWriter>(),
            sp.GetService<Autheris.Application.Common.Interfaces.ICatalogVisibilityGrantRepository>(),
            timeProvider: sp.GetService<TimeProvider>(),
            logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<Autheris.Application.Governance.Grants.GrantRevocationService>>()));
        services.AddHostedService<Autheris.Application.ConsentRequests.Events.ConsentWebhookDispatcherHostedService>();
        services.AddHostedService<Autheris.Application.Governance.Grants.GrantExpiryHostedService>();
        services.AddHostedService<Autheris.Application.Policy.RowScopes.PatternCoverageMonitor>();
    }
}
