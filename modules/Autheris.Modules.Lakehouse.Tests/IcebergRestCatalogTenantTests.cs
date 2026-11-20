namespace Autheris.Tests.Unit.Api;

using Autheris.Api.Endpoints;
using Autheris.Api.Hosting;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
using System.Collections.Generic;
using Xunit;

/// <summary>DT-A-9 / DT-SC-12: the Iceberg REST catalog has no literal "default" tenant fallback; an unresolved tenant fails closed.</summary>
[Trait("Category", "Security")]
public sealed class IcebergRestCatalogTenantTests
{
    [Fact]
    public void RequireTenant_UnresolvedTenant_ThrowsTenantUnresolved_NeverDefault()
    {
        var context = new DefaultHttpContext();
        context.Items[SecurityPrincipalContext.TenantIdItemKey] = default(TenantId);

        Should.Throw<TenantUnresolvedException>(() => IcebergRestCatalogEndpoints.RequireTenant(new ModuleRequestContext(), context));
    }

    [Fact]
    public void RequireTenant_ResolvedTenant_IsReturnedUnchanged()
    {
        var context = new DefaultHttpContext();
        context.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId("tenant-a");

        IcebergRestCatalogEndpoints.RequireTenant(new ModuleRequestContext(), context).ShouldBe("tenant-a");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Blank_principal_tenant_fails_closed()
    {
        var context = new DefaultHttpContext();
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext { UserSid = new Sid("S-1-5-21-TEST"), GroupSids = new HashSet<Sid>(), TenantRoles = new HashSet<string>(), ClusterRoles = new HashSet<string>(), AuthenticationScheme = "Bearer" };
        context.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId("tenant-from-items");

        Should.Throw<TenantUnresolvedException>(() => IcebergRestCatalogEndpoints.RequireTenant(new ModuleRequestContext(), context));
    }
}
