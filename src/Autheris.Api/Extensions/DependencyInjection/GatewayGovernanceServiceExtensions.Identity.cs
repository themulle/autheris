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
using Autheris.Infrastructure.Lineage;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Security;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static partial class GatewayGovernanceServiceExtensions
{
    private static void AddIdentityAndAccessGovernance(IServiceCollection services)
    {
        // Wave 2: Identity & Access Governance
        // The registry is filled from Authentication:Realms once, on first use (DT-A-11); without Initialize no realm is registered
        // and realm-bound principals resolve Invalid.
        services.AddSingleton<Autheris.Application.Identity.IIdentityRealmRegistry>(sp =>
        {
            var registry = new Autheris.Application.Identity.IdentityRealmRegistry();
            registry.Initialize(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.Security.IdentityRealmsOptions>>().Value);
            return registry;
        });
        // The node-local cache plus its broadcasting decorator: invalidations also go out on IEventBus (best effort; the TTL is the backstop).
        services.AddSingleton<Autheris.Application.Identity.Caching.RoleResolverCache>();
        services.AddSingleton<Autheris.Application.Identity.Caching.IRoleResolverCache, Autheris.Application.Identity.Caching.BroadcastingRoleResolverCache>();
        services.AddHostedService<Autheris.Application.Identity.Caching.IdentityGroupsChangedSubscriber>();
        // The provider chain the group resolution relies on. No production Graph or LDAP adapter ships yet: without one the
        // Graph and LDAPS providers answer "failed" and the resolution is degraded (never an empty group set).
        services.AddSingleton<Autheris.Application.Identity.IRoleProvider, Autheris.Application.Identity.Providers.ScimStoreRoleProvider>();
        services.AddSingleton<Autheris.Application.Identity.IRoleProvider, Autheris.Application.Identity.Providers.StaticMappingRoleProvider>();
        services.AddSingleton<Autheris.Application.Identity.IRoleProvider, Autheris.Application.Identity.Providers.EntraAppRoleProvider>();
        services.TryAddSingleton<Autheris.Application.Identity.Providers.IGraphClientAdapter, Autheris.Application.Identity.Providers.UnconfiguredGraphClientAdapter>();
        services.AddSingleton<Autheris.Application.Identity.IRoleProvider>(sp => new Autheris.Application.Identity.Providers.GraphRoleProvider(
            sp.GetRequiredService<Autheris.Application.Identity.Providers.IGraphClientAdapter>(),
            maxGroups: sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.Authentication.MaxGroupSidsPerCaller));
        services.AddSingleton<Autheris.Application.Identity.IRoleProvider>(sp => new Autheris.Application.Identity.Providers.LdapsRoleProvider(
            sp.GetService<Autheris.Application.Identity.Providers.ILdapDirectoryAdapter>(),
            maxGroups: sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.Authentication.MaxGroupSidsPerCaller));
        services.AddSingleton<Autheris.Application.Identity.IGroupSidResolver, Autheris.Application.Identity.GroupSidResolver>();
        services.AddSingleton<Autheris.Application.Identity.IRoleResolver, Autheris.Application.Identity.RoleResolver>();
        services.AddSingleton<Autheris.Application.Identity.Services.IRedisLockPubSubChannel, Autheris.Infrastructure.Identity.Locks.RedisLockPubSubChannel>();
        services.AddHostedService<Autheris.Application.Identity.Services.LockRoleCacheInvalidationHostedService>();
        services.AddSingleton<Autheris.Application.Identity.Services.IIdentityDeprovisioningService, Autheris.Application.Identity.Services.IdentityDeprovisioningService>();
        services.AddSingleton<Autheris.Application.Identity.Services.ScimMassChangeGuard>();
        services.AddSingleton<Autheris.Application.Identity.Services.IScimService, Autheris.Application.Identity.Services.ScimService>();
        services.AddSingleton<Autheris.Application.Governance.Rights.IFunctionalRightMapper, Autheris.Application.Governance.Rights.FunctionalRightMapper>();
        services.AddHostedService<Autheris.Api.Hosting.GatewayRoleRightAssignmentReport>();
        services.AddSingleton<Autheris.Application.Governance.Policies.IPolicyEpochService, Autheris.Application.Governance.Policies.PolicyEpochService>();
        services.AddSingleton<Autheris.Application.Governance.Recertification.IRecertificationService, Autheris.Application.Governance.Recertification.RecertificationService>();
        services.AddHostedService<Autheris.Application.Governance.Recertification.RecertificationGovernorHostedService>();
    }
}
