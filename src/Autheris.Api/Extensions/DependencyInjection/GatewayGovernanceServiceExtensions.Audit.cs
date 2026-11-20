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
using Autheris.Application.Compliance;
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
        services.AddScoped<IGdprDisclosureReportService, GdprDisclosureReportService>();
        services.AddSingleton<IGdprAuditReportExporter, GdprAuditReportPdfExporter>();
        services.AddOptions<Autheris.Domain.Options.UsageAccountingOptions>()
            .BindConfiguration(Autheris.Domain.Options.GatewaySections.UsageAccounting)
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Autheris.Domain.Options.UsageAccountingOptions>, Autheris.Application.Usage.UsageAccountingOptionsValidator>();
        services.AddSingleton<Autheris.Application.Usage.IUsageAggregator>(sp =>
            new Autheris.Application.Usage.UsageAggregator(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.UsageAccountingOptions>>(),
                hmacKey: ResolveUsagePseudonymizationKey(sp),
                expectedCostUnitResolver: BuildExpectedCostUnitResolver(sp),
                timeProvider: sp.GetService<TimeProvider>()));
        services.AddSingleton<Autheris.Application.Usage.IUsageRecorder, Autheris.Application.Usage.UsageRecorder>();
        services.AddSingleton<Autheris.Application.Usage.IUsageTariffService>(sp =>
        {
            var gateway = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Autheris.Domain.Options.GatewayOptions>>();
            return new Autheris.Application.Usage.UsageTariffService(
                sp.GetRequiredService<Autheris.Application.Usage.IUsageRepository>(),
                sp.GetRequiredService<Autheris.Application.Audit.IAuditWriter>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.UsageAccountingOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Autheris.Application.Usage.UsageTariffService>>(),
                sp.GetService<TimeProvider>(),
                dataSource => gateway.CurrentValue.DataSources.Connections.ContainsKey(dataSource),
                BuildExpectedCostUnitResolver(sp));
        });
        services.AddSingleton<Autheris.Application.Usage.IUsageGroupNameResolver>(sp =>
            new Autheris.Application.Usage.UsageGroupNameResolver(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.UsageAccountingOptions>>(),
                sp.GetService<Autheris.Domain.Interfaces.IScimGroupStore>(),
                sp.GetService<TimeProvider>()));
        services.AddSingleton<Autheris.Application.Usage.IUsageReportService>(sp =>
            new Autheris.Application.Usage.UsageReportService(
                sp.GetRequiredService<Autheris.Application.Usage.IUsageRepository>(),
                sp.GetRequiredService<Autheris.Application.Usage.IUsageGroupNameResolver>(),
                sp.GetRequiredService<Autheris.Application.Audit.IAuditWriter>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.UsageAccountingOptions>>(),
                sp.GetService<TimeProvider>(),
                hmacKey: ResolveUsagePseudonymizationKey(sp)));
        services.AddSingleton<Autheris.Application.Usage.IUsageQuotaService>(sp =>
            new Autheris.Application.Usage.UsageQuotaService(
                sp.GetRequiredService<Autheris.Application.Usage.IUsageRepository>(),
                sp.GetRequiredService<Autheris.Application.Usage.IUsageAggregator>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.UsageAccountingOptions>>(),
                hmacKey: ResolveUsagePseudonymizationKey(sp),
                timeProvider: sp.GetService<TimeProvider>(),
                logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Autheris.Application.Usage.UsageQuotaService>>()));
        services.AddHostedService<Autheris.Infrastructure.Usage.UsageFlushWorker>();
        services.AddHostedService<Autheris.Infrastructure.Usage.SourceCostCollectorWorker>();
        services.AddHostedService<Autheris.Infrastructure.Usage.UsageInitialTariffSeedService>();
    }

    internal static byte[]? ResolveUsagePseudonymizationKey(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.UsageAccountingOptions>>().Value;
        if (!options.PseudonymizeUsers)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.PseudonymizationKeyVaultRef))
        {
            throw new InvalidOperationException(
                "Gateway:UsageAccounting:PseudonymizationKeyVaultRef is required when PseudonymizeUsers is enabled.");
        }

        var secretProvider = sp.GetService<IKeyVaultSecretProvider>()
            ?? throw new InvalidOperationException(
                "IKeyVaultSecretProvider is required when Gateway:UsageAccounting:PseudonymizeUsers is enabled.");

        var keyBytes = secretProvider.GetSecretBytes(options.PseudonymizationKeyVaultRef);
        var env = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
        bool isDev = env is not null && Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsDevelopment(env);
        SecretKeyRequirements.EnsureMinimumLength(
            keyBytes,
            "The usage pseudonymization HMAC key (Gateway:UsageAccounting:PseudonymizationKeyVaultRef)",
            isDevelopment: isDev);

        if (keyBytes is null || keyBytes.Length == 0)
        {
            throw new InvalidOperationException(
                "The secret referenced by Gateway:UsageAccounting:PseudonymizationKeyVaultRef is missing or empty.");
        }

        return keyBytes;
    }

    private static Func<string, string?> BuildExpectedCostUnitResolver(IServiceProvider sp)
    {
        var gateway = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Autheris.Domain.Options.GatewayOptions>>();
        var collectors = sp.GetServices<Autheris.Application.Usage.ISourceCostCollector>().ToArray();
        return dataSource =>
            Autheris.Application.Usage.SourceCostCollectorResolver
                .FindForDataSource(dataSource, gateway.CurrentValue, collectors)?
                .CostUnit;
    }
}
