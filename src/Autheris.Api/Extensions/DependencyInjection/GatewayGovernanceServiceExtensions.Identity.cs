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
    private static void AddIdentityAndAccessGovernance(IServiceCollection services)
    {
        // Wave 2: Identity & Access Governance
        services.AddSingleton<Autheris.Application.Identity.IIdentityRealmRegistry, Autheris.Application.Identity.IdentityRealmRegistry>();
        services.AddSingleton<Autheris.Application.Identity.Caching.IRoleResolverCache, Autheris.Application.Identity.Caching.RoleResolverCache>();
        services.AddSingleton<Autheris.Application.Identity.IRoleResolver, Autheris.Application.Identity.RoleResolver>();
        services.AddSingleton<Autheris.Application.Identity.Services.IRedisLockPubSubChannel, Autheris.Infrastructure.Identity.Locks.RedisLockPubSubChannel>();
        services.AddHostedService<Autheris.Application.Identity.Services.LockRoleCacheInvalidationHostedService>();
        services.AddSingleton<Autheris.Application.Identity.Services.IIdentityDeprovisioningService, Autheris.Application.Identity.Services.IdentityDeprovisioningService>();
        services.AddSingleton<Autheris.Application.Identity.Services.IScimService, Autheris.Application.Identity.Services.ScimService>();
        services.AddSingleton<Autheris.Application.Governance.Rights.IFunctionalRightMapper, Autheris.Application.Governance.Rights.FunctionalRightMapper>();
        services.AddSingleton<Autheris.Application.Governance.Policies.IPolicyEpochService, Autheris.Application.Governance.Policies.PolicyEpochService>();
        services.AddSingleton<Autheris.Application.Governance.Recertification.IRecertificationService, Autheris.Application.Governance.Recertification.RecertificationService>();
        services.AddHostedService<Autheris.Application.Governance.Recertification.RecertificationGovernorHostedService>();
    }
}
