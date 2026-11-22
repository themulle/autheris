namespace Autheris.Tests.Common.Fakes;

using System;
using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.OData;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class NullClientIpResolver : IClientIpResolver
{
    public static NullClientIpResolver Instance { get; } = new();

    public IPAddress ResolveClientIp() => IPAddress.None;
}

public sealed class DisabledCallerAttributeResolver : ICallerAttributeResolver
{
    public static DisabledCallerAttributeResolver Instance { get; } = new();

    public bool IsEnabled => false;

    public CallerAttributePadding Padding { get; } = new(false, 0);

    public int BindReserve => 0;

    public CallerSubject CreateSubject(ClaimsPrincipal? principal, TenantId tenant, bool longLived = false) =>
        CallerSubject.NoSubject(tenant);

    public ValueTask<CallerAttributeSet> ResolveAsync(CallerSubject subject, IReadOnlyCollection<string> names, CancellationToken ct) =>
        ValueTask.FromResult(CallerAttributeSet.Empty);
}

public sealed class StaticCatalogVersionProvider : ICatalogVersionProvider
{
    public static StaticCatalogVersionProvider Instance { get; } = new();

    public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) =>
        new(new CatalogVersion(1, 0));

    public Task<string> GetVersionAsync(CancellationToken ct = default) =>
        Task.FromResult("1:0");
}

public sealed class PermissiveDataSourceOptionsResolver : IDataSourceOptionsResolver
{
    public static PermissiveDataSourceOptionsResolver Instance { get; } = new();

    private static readonly EffectiveDataSourceProfile DefaultProfile = new(
        DataSourceName: "default",
        QueryCostMultiplier: 1.0,
        MaxAllowedComplexity: 1000,
        MaxAggregateRowBudget: 1_000_000,
        QueryTimeout: TimeSpan.FromSeconds(30),
        MinimumLogLevel: "Information",
        LogGeneratedSql: false,
        SamplingRatio: 1.0,
        OutboundPropagation: OutboundPropagationMode.Inherit,
        MaxResponseRows: 100_000,
        MaxResponseBytes: 10_000_000,
        CommandTimeout: TimeSpan.FromSeconds(30),
        MinPoolSize: null,
        MaxPoolSize: null,
        DefaultIsolationLevel: IsolationLevel.ReadCommitted,
        AllowedTransports: DataSourceTransportFlags.All,
        CachingEnabled: true,
        StagingTtl: TimeSpan.FromMinutes(5),
        MaxKeySetImportLimit: 1000,
        CeilingRelaxed: false,
        IsUnresolved: false);

    public EffectiveDataSourceProfile Resolve(string? dataSourceName) => DefaultProfile;

    public bool TryResolve(string? dataSourceName, out EffectiveDataSourceProfile profile)
    {
        profile = DefaultProfile;
        return false;
    }

    public string CanonicalName(string? dataSourceName) => dataSourceName?.Trim() ?? string.Empty;
}

public sealed class DenyAllODataQueryPolicyEnforcer : IODataQueryPolicyEnforcer
{
    public static DenyAllODataQueryPolicyEnforcer Instance { get; } = new();

    public ValueTask<TableAccessDecision> EnforceAsync(
        ClaimsPrincipal principal,
        TableMetadata metadata,
        IReadOnlyList<string>? requestedColumns = null,
        CancellationToken ct = default) =>
        ValueTask.FromResult(TableAccessDecision.Denied(metadata.Identifier, "Access denied."));
}

public sealed class DelegatingODataQueryPolicyEnforcer(
    Func<ClaimsPrincipal, TableMetadata, IReadOnlyList<string>?, CancellationToken, ValueTask<TableAccessDecision>> handler) : IODataQueryPolicyEnforcer
{
    public ValueTask<TableAccessDecision> EnforceAsync(
        ClaimsPrincipal principal,
        TableMetadata metadata,
        IReadOnlyList<string>? requestedColumns = null,
        CancellationToken ct = default) =>
        handler(principal, metadata, requestedColumns, ct);
}

public static class ODataHandlerTestFactory
{
    public static ODataHandler Create(
        ITableMetadataRepository metadataRepo,
        IGatewayExecutionService executionService,
        ILogger<ODataHandler>? logger = null,
        IHostEnvironment? environment = null,
        IMemoryCache? memoryCache = null,
        ICatalogVersionProvider? versionProvider = null,
        ITenantResolver? tenantResolver = null,
        IDataSourceOptionsResolver? transportResolver = null,
        IIngressTransportContext? ingress = null,
        IODataQueryPolicyEnforcer? policyEnforcer = null) =>
        new(
            metadataRepo,
            executionService,
            logger ?? NullLogger<ODataHandler>.Instance,
            environment ?? new TestHostEnvironment(Environments.Production),
            memoryCache ?? new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 * 1024 * 1024 }),
            versionProvider ?? StaticCatalogVersionProvider.Instance,
            tenantResolver ?? new TenantResolver(() => new TenancyOptions(), null, NullLogger<TenantResolver>.Instance),
            transportResolver ?? PermissiveDataSourceOptionsResolver.Instance,
            ingress ?? new IngressTransportContext(),
            policyEnforcer ?? DenyAllODataQueryPolicyEnforcer.Instance);
}
