namespace Autheris.Tests.Unit;

using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.SchemaRegistry;
using Autheris.Application.SchemaRegistry.Validation;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public class SrM11SchemaRegistryTenantScopeTests
{
    private const string Sdl = "type Query { a: Int }";

    private static SchemaRegistryService CreateService() => new(
        new InMemorySchemaRegistryRepository(),
        new SchemaLinter(),
        new SchemaRegistrationRequestValidator(),
        NullLogger<SchemaRegistryService>.Instance);

    [Fact]
    public void SR_M_11_ScopedKeys_DifferPerTenant_AndStayValidServiceNames()
    {
        var a = SchemaRegistryEndpoints.ScopeServiceName(new TenantId("tenant-a"), "accounts");
        var b = SchemaRegistryEndpoints.ScopeServiceName(new TenantId("tenant-b"), "accounts");
        var aAgain = SchemaRegistryEndpoints.ScopeServiceName(new TenantId("TENANT-A"), "accounts");

        a.ShouldNotBe(b);
        a.ShouldBe(aAgain);
        System.Text.RegularExpressions.Regex.IsMatch(a, "^[a-zA-Z0-9_\\-]+$").ShouldBeTrue();
    }

    [Fact]
    public async Task SR_M_11_OtherTenant_CannotReadOrSquatService()
    {
        var service = CreateService();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        var reg = await service.RegisterSchemaAsync(new SchemaRegistrationRequest
        {
            ServiceName = SchemaRegistryEndpoints.ScopeServiceName(tenantA, "accounts"),
            Sdl = Sdl,
            RegisteredBy = "owner-a"
        });
        reg.Success.ShouldBeTrue();

        // Tenant B neither sees tenant A's schema nor its history ...
        (await service.GetLatestSchemaAsync(SchemaRegistryEndpoints.ScopeServiceName(tenantB, "accounts"))).ShouldBeNull();
        (await service.GetSchemaHistoryAsync(SchemaRegistryEndpoints.ScopeServiceName(tenantB, "accounts"))).ShouldBeEmpty();

        // ... and the name is still free for tenant B (no squatting across tenants): first registrant owns it there.
        var historyB = await service.GetSchemaHistoryAsync(SchemaRegistryEndpoints.ScopeServiceName(tenantB, "accounts"));
        SchemaRegistryEndpoints.IsServiceOwner(historyB, "owner-b").ShouldBeTrue();

        var all = await service.GetAllServicesAsync();
        var prefixB = SchemaRegistryEndpoints.ScopePrefix(tenantB);
        all.Where(n => n.StartsWith(prefixB, System.StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public void SR_M_11_UnscopeSchema_StripsOnlyOwnTenantPrefix()
    {
        var tenantA = new TenantId("tenant-a");
        var scoped = new RegisteredSchema
        {
            ServiceName = SchemaRegistryEndpoints.ScopeServiceName(tenantA, "accounts"),
            Version = "v1.0.0",
            Sdl = Sdl,
            RegisteredBy = "owner-a"
        };

        SchemaRegistryEndpoints.UnscopeSchema(scoped, tenantA).ServiceName.ShouldBe("accounts");
        SchemaRegistryEndpoints.UnscopeSchema(scoped, new TenantId("tenant-b")).ServiceName.ShouldBe(scoped.ServiceName);
    }
}
