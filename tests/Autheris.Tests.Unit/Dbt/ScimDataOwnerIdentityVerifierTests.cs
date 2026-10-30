using Autheris.Application.Governance;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Dbt;

/// <summary>I393B-SC-04 / I393B-T-05: the SCIM based identity check of imported data owners.</summary>
public sealed class ScimDataOwnerIdentityVerifierTests
{
    private static readonly TenantId Tenant = new("sales");

    private static IIdentityRealmRegistry Registry(bool implicitLegacy = false)
    {
        var registry = Substitute.For<IIdentityRealmRegistry>();
        registry.GetAllRealms().Returns([new IdentityRealm("realm-1", IdentityRealmKind.EntraWorkforce, TenantBindingSpecification.Fixed(Tenant), isImplicitLegacy: implicitLegacy)]);
        return registry;
    }

    private static ScimUser User(bool active) => new("u1", null, "alice", "Alice", "alice@corp.example", active, null, null, null);

    [Fact]
    public async Task NoRealm_IsNotConfigured()
    {
        (await new ScimDataOwnerIdentityVerifier().VerifyAsync(Tenant, "alice")).ShouldBe(OwnerIdentityStatus.NotConfigured);
        (await new ScimDataOwnerIdentityVerifier(Registry(implicitLegacy: true), Substitute.For<IIdentityStoreRepository>()).VerifyAsync(Tenant, "alice"))
            .ShouldBe(OwnerIdentityStatus.NotConfigured);
    }

    [Fact]
    public async Task ActiveUser_IsVerified_InactiveOrUnknownIsNot()
    {
        var store = Substitute.For<IIdentityStoreRepository>();
        store.GetUserAsync(Tenant, "realm-1", "active", Arg.Any<CancellationToken>()).Returns(User(true));
        store.GetUserAsync(Tenant, "realm-1", "inactive", Arg.Any<CancellationToken>()).Returns(User(false));
        var verifier = new ScimDataOwnerIdentityVerifier(Registry(), store);

        (await verifier.VerifyAsync(Tenant, "active")).ShouldBe(OwnerIdentityStatus.Verified);
        (await verifier.VerifyAsync(Tenant, "inactive")).ShouldBe(OwnerIdentityStatus.NotFoundOrInactive);
        (await verifier.VerifyAsync(Tenant, "unknown")).ShouldBe(OwnerIdentityStatus.NotFoundOrInactive);
    }

    [Fact]
    public async Task StoreFailureOrMissingStore_IsUnavailable()
    {
        var store = Substitute.For<IIdentityStoreRepository>();
        store.GetUserAsync(default, default!, default!, default).ReturnsForAnyArgs<ScimUser?>(_ => throw new InvalidOperationException("down"));

        (await new ScimDataOwnerIdentityVerifier(Registry(), store).VerifyAsync(Tenant, "x")).ShouldBe(OwnerIdentityStatus.Unavailable);
        (await new ScimDataOwnerIdentityVerifier(Registry(), null).VerifyAsync(Tenant, "x")).ShouldBe(OwnerIdentityStatus.Unavailable);
    }
}
