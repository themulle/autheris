namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Security;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Identity;
using Autheris.Application.Tenancy;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Threading;

/// <summary>Builds the production identity pipeline (and the transformation adapter) with a real group resolver for unit tests.</summary>
internal static class TestIdentityPipeline
{
    public static IdentityPipeline Create(
        IIdentityRealmRegistry? registry = null,
        IRoleResolver? roleResolver = null,
        IFunctionalRightMapper? mapper = null,
        ILogger<IdentityPipeline>? logger = null,
        ITenantResolver? tenantResolver = null,
        IGroupSidResolver? groupResolver = null,
        GatewayOptions? options = null)
    {
        var gatewayOptions = Options.Create(options ?? new GatewayOptions());
        groupResolver ??= new GroupSidResolver(roleResolver, Enumerable.Empty<IRoleProvider>(), gatewayOptions);
        return new IdentityPipeline(registry, groupResolver, roleResolver, mapper, tenantResolver, gatewayOptions, logger);
    }

    /// <summary>A registry whose default realm takes groups from the token and binds the fixed tenant <c>tenant-a</c>.</summary>
    public static IIdentityRealmRegistry TokenRealm(IdentityRealmKind kind = IdentityRealmKind.EntraWorkforce)
    {
        var registry = Substitute.For<IIdentityRealmRegistry>();
        var realm = new IdentityRealm(
            "token-realm",
            kind,
            new TenantBindingSpecification(TenantBindingKind.Fixed, FixedTenantId: new TenantId("tenant-a")),
            groupSource: RealmGroupSource.Token);
        registry.GetDefaultRealm().Returns(realm);
        registry.GetRealm("token-realm").Returns(realm);
        registry.ResolveRealmForScheme(Arg.Any<string?>(), Arg.Any<string?>()).Returns(realm);
        return registry;
    }

    public static EnterpriseClaimsTransformation Transformation(
        IIdentityRealmRegistry? registry = null,
        IRoleResolver? roleResolver = null,
        IFunctionalRightMapper? mapper = null,
        ILogger<IdentityPipeline>? logger = null,
        ITenantResolver? tenantResolver = null,
        IGroupSidResolver? groupResolver = null,
        GatewayOptions? options = null) =>
        new(Create(registry, roleResolver, mapper, logger, tenantResolver, groupResolver, options));

    /// <summary>Stubs both resolver entry points: the group resolver uses the chain, the pipeline asks for roles by identity.</summary>
    public static void StubResolution(IRoleResolver resolver, Autheris.Domain.Identity.RoleResolution resolution)
    {
        resolver.ResolveRolesAsync(Arg.Any<ResolvedIdentity>(), Arg.Any<CancellationToken>()).Returns(resolution);
        resolver.ResolveAsync(Arg.Any<ResolvedIdentity>(), Arg.Any<IdentityRealm>(), Arg.Any<CancellationToken>()).Returns(resolution);
    }
}
