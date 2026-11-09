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

    public static EnterpriseClaimsTransformation Transformation(
        IIdentityRealmRegistry? registry = null,
        IRoleResolver? roleResolver = null,
        IFunctionalRightMapper? mapper = null,
        ILogger<IdentityPipeline>? logger = null,
        ITenantResolver? tenantResolver = null,
        IGroupSidResolver? groupResolver = null,
        GatewayOptions? options = null) =>
        new(Create(registry, roleResolver, mapper, logger, tenantResolver, groupResolver, options));
}
