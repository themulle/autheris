namespace Autheris.Tests.Unit.Api;

using Autheris.Api.Endpoints;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
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

        Should.Throw<TenantUnresolvedException>(() => IcebergRestCatalogEndpoints.RequireTenant(context));
    }

    [Fact]
    public void RequireTenant_ResolvedTenant_IsReturnedUnchanged()
    {
        var context = new DefaultHttpContext();
        context.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId("tenant-a");

        IcebergRestCatalogEndpoints.RequireTenant(context).ShouldBe("tenant-a");
    }
}
