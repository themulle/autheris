namespace Autheris.Tests.Unit.Tenancy;

using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Identity;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>DT-A-2 / DT-SC-01 / DT-SC-02 / DT-SC-07: the resolver is the only default-assignment decision (semantics table S1-S8).</summary>
[Trait("Category", "Security")]
public sealed class TenantResolverTests
{
    private const string Default = "dflt";

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
    {
        var list = new List<Claim>();
        foreach (var (type, value) in claims)
        {
            list.Add(new Claim(type, value));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(list, "Test"));
    }

    private static TenantResolver Sut(string? defaultTenant, IdentityRealm? realm = null, string? mode = null)
    {
        var tenancy = new TenancyOptions { DefaultTenantId = defaultTenant, Mode = mode };
        var realms = Substitute.For<IIdentityRealmRegistry>();
        if (realm is not null)
        {
            realms.GetRealm(realm.Id).Returns(realm);
        }

        return new TenantResolver(() => tenancy, realms, NullLogger<TenantResolver>.Instance);
    }

    private static IdentityRealm Realm(TenantBindingSpecification binding) =>
        new("r1", IdentityRealmKind.GenericOidc, binding);

    [Theory]
    [InlineData("valid", "dflt", TenantResolutionOutcome.FromClaim, "valid")]            // S1
    [InlineData("invalid", "dflt", TenantResolutionOutcome.Invalid, null)]                // S2
    [InlineData("absent", "dflt", TenantResolutionOutcome.DefaultAssigned, "dflt")]       // S3
    [InlineData("valid", null, TenantResolutionOutcome.FromClaim, "valid")]               // S4
    [InlineData("invalid", null, TenantResolutionOutcome.Invalid, null)]                  // S5
    [InlineData("absent", null, TenantResolutionOutcome.Unresolved, null)]                // S6
    public void TenantResolver_Decide_Matrix(string claimKind, string? defaultTenant, TenantResolutionOutcome outcome, string? tenant)
    {
        var claim = claimKind switch
        {
            "valid" => ClaimTenant.Valid(new TenantId("valid")),
            "invalid" => ClaimTenant.Invalid,
            _ => ClaimTenant.Absent
        };
        TenantId? d = null;
        if (defaultTenant is not null)
        {
            d = new TenantId(defaultTenant);
        }


        var result = TenantResolver.Decide(claim, d);

        result.Outcome.ShouldBe(outcome);
        result.Tenant?.Value.ShouldBe(tenant);
        (result.Tenant is null).ShouldBe(tenant is null);
    }

    [Fact]
    public void Unauthenticated_Principal_Is_Unauthenticated_Never_Default()
    {
        var sut = Sut(Default);

        sut.Resolve(null).Outcome.ShouldBe(TenantResolutionOutcome.Unauthenticated);
        sut.Resolve(new ClaimsPrincipal(new ClaimsIdentity())).Outcome.ShouldBe(TenantResolutionOutcome.Unauthenticated);
        sut.Resolve(new ClaimsPrincipal(new ClaimsIdentity())).Tenant.ShouldBeNull();
    }

    [Fact]
    public void Claim_Wins_Over_Default()
    {
        var result = Sut(Default).Resolve(Principal(("tenant_id", "acme")));

        result.Outcome.ShouldBe(TenantResolutionOutcome.FromClaim);
        result.Tenant!.Value.Value.ShouldBe("acme");
    }

    [Theory]
    [InlineData("bad value!")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData("a b")]
    public void Invalid_Claim_Never_Falls_Back_To_Default(string claim)
    {
        var result = Sut(Default).Resolve(Principal(("tenant_id", claim)));

        result.Outcome.ShouldBe(TenantResolutionOutcome.Invalid);
        result.Tenant.ShouldBeNull();
    }

    [Theory]
    [InlineData("_platform")]
    [InlineData("_PLATFORM")]
    [InlineData("_x")]
    [InlineData("_")]
    public void Reserved_Claim_Is_Invalid(string claim)
    {
        Sut(Default).Resolve(Principal(("tid", claim))).Outcome.ShouldBe(TenantResolutionOutcome.Invalid);
        Sut(null).Resolve(Principal(("tid", claim))).Outcome.ShouldBe(TenantResolutionOutcome.Invalid);
    }

    [Fact]
    public void Multiple_Conflicting_Tenant_Claims_Are_Invalid()
    {
        var principal = Principal(("tenant_id", "a"), ("tid", "b"));

        Sut(Default).Resolve(principal).Outcome.ShouldBe(TenantResolutionOutcome.Invalid);
        TenantClaimReader.Read(Principal(("tenant_id", "a"), ("tenant_id", "b"))).Kind.ShouldBe(ClaimTenantKind.Invalid);
        TenantClaimReader.Read(Principal(("tenant_id", "a"), ("tid", "a"))).Kind.ShouldBe(ClaimTenantKind.Valid);
    }

    [Fact]
    public void Claim_Reader_Reads_The_Whole_Chain()
    {
        foreach (var name in new[] { "tenant_id", "tid", "tenant", "http://schemas.microsoft.com/identity/claims/tenantid" })
        {
            var claim = TenantClaimReader.Read(Principal((name, "acme")));
            claim.Kind.ShouldBe(ClaimTenantKind.Valid, name);
            claim.Tenant.Value.ShouldBe("acme");
        }

        TenantClaimReader.Read(Principal(("sub", "x"))).Kind.ShouldBe(ClaimTenantKind.Absent);
        TenantClaimReader.Read(null).Kind.ShouldBe(ClaimTenantKind.Absent);
    }

    [Fact]
    public void Invalid_Or_Reserved_Default_Yields_Invalid_Not_Unresolved()
    {
        foreach (var bad in new[] { "_platform", "bad value", "  ", "" })
        {
            var sut = Sut(bad);

            sut.DefaultTenant.ShouldBeNull();
            sut.Resolve(Principal(("sub", "x"))).Outcome.ShouldBe(TenantResolutionOutcome.Invalid, bad);
        }
    }

    [Fact]
    public void Unbound_Realm_Gets_Default_And_Ignores_Token_Tenant_Claim()
    {
        var sut = Sut(Default, Realm(TenantBindingSpecification.Unbound()));

        var result = sut.Resolve(Principal(("autheris:realm", "r1"), ("tenant_id", "spoofed")));

        result.Outcome.ShouldBe(TenantResolutionOutcome.DefaultAssigned);
        result.Tenant!.Value.Value.ShouldBe(Default);
    }

    [Fact]
    public void Unbound_Realm_Without_Default_Is_Unresolved()
    {
        var sut = Sut(null, Realm(TenantBindingSpecification.Unbound()));

        sut.Resolve(Principal(("autheris:realm", "r1"))).Outcome.ShouldBe(TenantResolutionOutcome.Unresolved);
    }

    [Fact]
    public void Fixed_Realm_Ignores_Default()
    {
        var sut = Sut(Default, Realm(TenantBindingSpecification.Fixed(new TenantId("pinned"))));

        var result = sut.Resolve(Principal(("autheris:realm", "r1"), ("tenant_id", "other")));

        result.Outcome.ShouldBe(TenantResolutionOutcome.FromRealm);
        result.Tenant!.Value.Value.ShouldBe("pinned");
    }

    [Fact]
    public void Fixed_Realm_With_Reserved_Tenant_Is_Invalid()
    {
        var reserved = TenantBindingSpecification.Fixed(TenantId.Platform);
        var sut = Sut(Default, Realm(reserved));

        sut.Resolve(Principal(("autheris:realm", "r1"))).Outcome.ShouldBe(TenantResolutionOutcome.Invalid);
    }

    [Fact]
    public void ClaimMapped_Realm_Maps_Absent_And_Unmapped()
    {
        var binding = TenantBindingSpecification.ClaimMapped("grp", new Dictionary<string, TenantId> { ["g1"] = new TenantId("mapped") });
        var sut = Sut(Default, Realm(binding));

        var mapped = sut.Resolve(Principal(("autheris:realm", "r1"), ("grp", "g1")));
        mapped.Outcome.ShouldBe(TenantResolutionOutcome.FromRealm);
        mapped.Tenant!.Value.Value.ShouldBe("mapped");

        sut.Resolve(Principal(("autheris:realm", "r1"))).Outcome.ShouldBe(TenantResolutionOutcome.DefaultAssigned);
        sut.Resolve(Principal(("autheris:realm", "r1"), ("grp", "unknown"))).Outcome.ShouldBe(TenantResolutionOutcome.Invalid);
    }

    [Fact]
    public void Unknown_Realm_Claim_Is_Invalid_Not_Claim_Fallback()
    {
        // Phase 5 finding (WP-DT-1): a non-blank realm claim whose realm is not registered must never fall through to the claim chain or D.
        var sut = Sut(Default, Realm(TenantBindingSpecification.Unbound()));

        sut.Resolve(Principal(("autheris:realm", "ghost"), ("tenant_id", "acme"))).Outcome.ShouldBe(TenantResolutionOutcome.Invalid);
        sut.Resolve(Principal(("autheris:realm", "ghost"))).Outcome.ShouldBe(TenantResolutionOutcome.Invalid);
    }

    [Fact]
    public void ClaimMapped_Realm_With_Several_Distinct_Values_Is_Invalid()
    {
        var binding = TenantBindingSpecification.ClaimMapped("grp", new Dictionary<string, TenantId>
        {
            ["g1"] = new TenantId("one"),
            ["g2"] = new TenantId("two")
        });
        var sut = Sut(Default, Realm(binding));

        sut.Resolve(Principal(("autheris:realm", "r1"), ("grp", "g1"), ("grp", "g2"))).Outcome.ShouldBe(TenantResolutionOutcome.Invalid);
        sut.Resolve(Principal(("autheris:realm", "r1"), ("grp", "g1"), ("grp", "g1"))).Outcome.ShouldBe(TenantResolutionOutcome.FromRealm);
    }

    [Fact]
    public void Token_Supplied_Tenant_Source_Claim_Is_Stripped()
    {
        // The resolver never reads autheris:tenant_source: a forged value cannot produce DefaultAssigned or a tenant.
        var forged = Principal(("autheris:tenant_source", "default"), ("autheris:tenant", Default));

        Sut(null).Resolve(forged).Outcome.ShouldBe(TenantResolutionOutcome.Unresolved);
        Sut(Default).Resolve(forged).Outcome.ShouldBe(TenantResolutionOutcome.DefaultAssigned);
        TenantClaimReader.Read(forged).Kind.ShouldBe(ClaimTenantKind.Absent);
    }

    [Fact]
    public void Single_Mode_Shim_Supplies_Legacy_Default_Only_Until_WpDt6()
    {
        var shim = Sut(null, mode: "Single");
        shim.DefaultTenant!.Value.Value.ShouldBe("legacy-single-tenant");
        shim.ConfiguredDefaultTenant.ShouldBeNull();
        shim.Resolve(Principal(("sub", "x"))).Outcome.ShouldBe(TenantResolutionOutcome.DefaultAssigned);

        var explicitDefault = Sut(Default, mode: "Single");
        explicitDefault.DefaultTenant!.Value.Value.ShouldBe(Default);
        explicitDefault.ConfiguredDefaultTenant!.Value.Value.ShouldBe(Default);

        Sut(null, mode: "Multi").DefaultTenant.ShouldBeNull();
    }

    [Fact]
    public void IsDefaultTenant_Matches_Only_The_Default()
    {
        var sut = Sut(Default);

        sut.IsDefaultTenant(new TenantId(Default)).ShouldBeTrue();
        sut.IsDefaultTenant(new TenantId("other")).ShouldBeFalse();
        sut.IsDefaultTenant(default).ShouldBeFalse();
        Sut(null).IsDefaultTenant(new TenantId(Default)).ShouldBeFalse();
    }

    [Fact]
    public void RequireTenant_Throws_Without_Echo()
    {
        var secret = "secret-claim-value-42!";
        var invalid = Sut(Default).Resolve(Principal(("tenant_id", secret)));
        var unresolved = Sut(null).Resolve(Principal(("sub", "x")));

        var ex1 = Should.Throw<TenantClaimInvalidException>(() => invalid.RequireTenant());
        var ex2 = Should.Throw<TenantUnresolvedException>(() => unresolved.RequireTenant());

        ex1.ErrorCode.ShouldBe("TENANT_CLAIM_INVALID");
        ex2.ErrorCode.ShouldBe("TENANT_UNRESOLVED");
        ex1.Message.ShouldNotContain(secret);
        ex1.ToString().ShouldNotContain(secret);
        ex2.ShouldBeAssignableTo<GatewaySecurityException>();
        Should.Throw<TenantUnresolvedException>(() => new TenantResolution(TenantResolutionOutcome.Unauthenticated, null).RequireTenant());
    }

    [Fact]
    public void RequireTenant_Returns_Tenant_For_Resolved_Outcomes()
    {
        Sut(Default).Resolve(Principal(("sub", "x"))).RequireTenant().Value.ShouldBe(Default);
        Sut(Default).Resolve(Principal(("tenant_id", "acme"))).RequireTenant().Value.ShouldBe("acme");
    }
}
