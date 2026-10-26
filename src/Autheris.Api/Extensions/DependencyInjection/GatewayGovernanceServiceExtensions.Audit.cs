namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.Policy.Services;
using Autheris.Application.Security;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Application.Webhooks;
using Autheris.Application.Workflows;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Itsm;
using Autheris.Infrastructure.Lineage;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Security;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.DependencyInjection;

public static partial class GatewayGovernanceServiceExtensions
{
    private static void AddAuditRetention(IServiceCollection services)
    {
        services.AddSingleton<Autheris.Application.Audit.IAuditWriter, Autheris.Application.Audit.AuditWriter>();

        services.AddOptions<AuditRetentionOptions>()
            .BindConfiguration(GatewaySections.AuditRetention)
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<AuditRetentionOptions>, AuditRetentionOptionsValidator>();
        services.AddHostedService<AuditRetentionPurgeWorker>();
    }

    private static void AddLineageGdprAndConsumerUsage(IServiceCollection services)
    {
        // Lineage Graph Store, Impact Analyzer & GDPR Exporter
        services.AddSingleton<ILineageGraphStore, LineageGraphStore>();
        services.AddOptions<Autheris.Domain.Options.GdprReportOptions>() // MISC-11
            .BindConfiguration(Autheris.Domain.Options.GdprReportOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<ILineageImpactAnalyzerService, LineageImpactAnalyzerService>();
        services.AddSingleton<IGdprAuditReportExporter, GdprAuditReportPdfExporter>();
        services.AddSingleton<Autheris.Infrastructure.Observability.ConsumerUsageRecorder>(sp =>
        {
            var env = sp.GetService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            var isDev = env?.EnvironmentName == Microsoft.Extensions.Hosting.Environments.Development;
            var gatewayOptions = sp.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>()?.Value;
            var entraIdOptions = gatewayOptions?.Authentication?.EntraId;
            var config = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();

            string? secret = config?["Gateway:Audit:ActorPseudonymizationKey"]
                ?? config?["Gateway:GovernanceDb:AuditHmacKey"]
                ?? config?["Gateway:Audit:AuditHmacKey"]
                ?? config?["Gateway:Cluster:Secret"];

            if (string.IsNullOrWhiteSpace(secret))
            {
                var secretProvider = sp.GetService<Autheris.Application.Interfaces.IKeyVaultSecretProvider>();
                var auditRef = gatewayOptions?.GovernanceDb?.AuditHmacKeyVaultRef ?? gatewayOptions?.DataMasking?.HmacSecretKeyVaultRef;
                if (secretProvider != null && !string.IsNullOrWhiteSpace(auditRef))
                {
                    try
                    {
                        var bytes = secretProvider.GetSecretBytes(auditRef);
                        if (bytes != null && bytes.Length > 0)
                        {
                            secret = Convert.ToHexString(bytes);
                        }
                    }
                    catch { }
                }
            }

            if (string.IsNullOrWhiteSpace(secret))
            {
                secret = isDev
                    ? "autheris-default-cluster-telemetry-secret-key-2026"
                    : Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            }
            var callerProvider = sp.GetService<ICallerContextProvider>();
            return new Autheris.Infrastructure.Observability.ConsumerUsageRecorder(secret, callerProvider);
        });
        services.AddSingleton<IConsumerUsageRecorder>(sp => sp.GetRequiredService<Autheris.Infrastructure.Observability.ConsumerUsageRecorder>());
        services.AddHostedService<Autheris.Infrastructure.Observability.ConsumerUsageFlushWorker>();
    }
}
