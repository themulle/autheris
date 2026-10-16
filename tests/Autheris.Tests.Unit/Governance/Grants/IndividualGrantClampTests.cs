using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants;

/// <summary>WP-SC-16 rule 5 / SEC-SC-20: legacy permanent or over-limit individual grants are clamped at read time, never rewritten.</summary>
[Collection("IndividualGrantLimits")]
public sealed class IndividualGrantClampTests : IDisposable
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public IndividualGrantClampTests() => IndividualGrantLimits.Reset();

    public void Dispose() => IndividualGrantLimits.Reset();

    private static Consent C(GranteeType type, DateTimeOffset validTo) => new()
    {
        GranteeType = type,
        GranteeSid = type is GranteeType.User or GranteeType.ServicePrincipal ? new Sid("S-1-5-21-X") : (Sid?)null,
        RoleName = type == GranteeType.Role ? "R" : null,
        ValidFrom = From,
        ValidTo = validTo
    };

    [Fact]
    public void SEC_SC_20_IndividualConsent_FarFuture_IsInactiveAfterTheClamp()
    {
        var c = C(GranteeType.User, DateTimeOffset.MaxValue);

        c.EffectiveValidTo.ShouldBe(From.AddDays(180));
        c.IsActive(From.AddDays(179)).ShouldBeTrue();
        c.IsActive(From.AddDays(180)).ShouldBeFalse();
        c.IsActive(From.AddDays(400)).ShouldBeFalse();
        c.ValidTo.ShouldBe(DateTimeOffset.MaxValue, "nothing is rewritten");
    }

    [Theory]
    [InlineData(GranteeType.User)]
    [InlineData(GranteeType.ServicePrincipal)]
    public void SEC_SC_20_DenyConsent_IsNeverClamped_SoALegacyPermanentDenyStaysActive(GranteeType type)
    {
        var deny = new Consent
        {
            Effect = ConsentEffect.Deny, GranteeType = type, GranteeSid = new Sid("S-1-5-21-X"),
            ValidFrom = From, ValidTo = DateTimeOffset.MaxValue
        };

        deny.EffectiveValidTo.ShouldBe(DateTimeOffset.MaxValue);
        deny.IsActive(From.AddDays(400)).ShouldBeTrue("an expired deny would fail open");
    }

    [Fact]
    public void SEC_SC_20_LegacyPermanentDeny_StillDenies_EvenWithARoleAllow()
    {
        var table = new TableIdentifier("d", "s", "t");
        var now = DateTimeOffset.UtcNow;
        var deny = new Consent
        {
            TableIdentifier = table, Effect = ConsentEffect.Deny, GranteeType = GranteeType.User, GranteeSid = new Sid("S-1-5-21-X"),
            ValidFrom = now.AddDays(-400), ValidTo = now.AddYears(50)
        };
        var roleAllow = new Consent { TableIdentifier = table, GranteeType = GranteeType.Role, RoleName = "analyst", ValidFrom = now.AddDays(-400), ValidTo = now.AddYears(1) };

        var decision = new Autheris.Application.Services.ConsentResolutionService().ResolveAccess(
            new Sid("S-1-5-21-X"), new HashSet<Sid>(), new HashSet<string> { "analyst" }, table, [deny, roleAllow]);

        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void SEC_SC_20_GrantPolicy_DenyConsent_NeedsNoExpiryAndNoMaximum()
    {
        var policy = new Autheris.Application.Governance.Grants.GrantPolicy();
        var r = new Autheris.Application.Governance.Grants.GrantRequest(
            GranteeType.User, "S-1-5-21-X", Autheris.Application.Governance.Grants.GrantKind.ManifestPolicy, null, From, Effect: ConsentEffect.Deny);

        var d = policy.Evaluate(r);

        d.Allowed.ShouldBeTrue();
        d.MaxValidTo.ShouldBeNull();
        policy.Evaluate(r with { Effect = ConsentEffect.Allow }).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void SEC_SC_20_DecisionCacheTtl_IsBoundedByTheClampedExpiry_NotByTheRawValidTo()
    {
        var now = DateTimeOffset.UtcNow;
        var clamped = new Consent
        {
            GranteeType = GranteeType.User, GranteeSid = new Sid("S-1-5-21-X"), ValidFrom = now.AddDays(-180).AddMinutes(2), ValidTo = now.AddYears(5)
        };

        var ttl = Autheris.Application.Services.ConsentResolutionService.ComputeDecisionCacheTtl(false, [clamped], now);

        ttl.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void SEC_SC_20_IndividualConsent_WithinLimit_KeepsItsOwnExpiry()
    {
        var c = C(GranteeType.User, From.AddDays(10));
        c.EffectiveValidTo.ShouldBe(From.AddDays(10));
        c.IsActive(From.AddDays(11)).ShouldBeFalse();
    }

    [Fact]
    public void SEC_SC_20_ServicePrincipal_ClampIs365Days()
    {
        var c = C(GranteeType.ServicePrincipal, DateTimeOffset.MaxValue);
        c.EffectiveValidTo.ShouldBe(From.AddDays(365));
    }

    [Theory]
    [InlineData(GranteeType.Group)]
    [InlineData(GranteeType.Role)]
    public void SEC_SC_20_StandingGrants_AreNeverClamped(GranteeType type)
    {
        var to = From.AddDays(1000);
        C(type, to).EffectiveValidTo.ShouldBe(to);
        C(type, to).IsActive(From.AddDays(999)).ShouldBeTrue();
    }

    [Fact]
    public void SEC_SC_20_ClampMaximum_FollowsRealmProfile_ButOnlyShortens()
    {
        IndividualGrantLimits.Configure(TimeSpan.FromDays(30), TimeSpan.FromDays(7), TimeSpan.FromDays(60));
        C(GranteeType.User, DateTimeOffset.MaxValue).EffectiveValidTo.ShouldBe(From.AddDays(30));

        IndividualGrantLimits.Configure(TimeSpan.FromDays(9999), TimeSpan.FromDays(9999), TimeSpan.FromDays(9999));
        C(GranteeType.User, DateTimeOffset.MaxValue).EffectiveValidTo.ShouldBe(From.AddDays(180));
    }

    [Fact]
    public void SEC_SC_20_PermanentUnmaskedProfile_IsClampedTo30DaysFromCreation()
    {
        var p = new AccessProfile { ProfileId = "p", Name = "n", TenantId = TenantId.LegacySingleTenant, MaskingMode = MaskingPolicyMode.Unmasked, CreatedAt = From, ValidTo = null };

        p.EffectiveValidTo.ShouldBe(From.AddDays(30));
        p.IsActive(From.AddDays(29)).ShouldBeTrue();
        p.IsActive(From.AddDays(30)).ShouldBeFalse();
    }

    [Fact]
    public void SEC_SC_20_UnmaskedProfile_WithShorterExpiry_KeepsIt_AndRestrictiveProfilesAreUnaffected()
    {
        var unmasked = new AccessProfile { ProfileId = "p", Name = "n", TenantId = TenantId.LegacySingleTenant, MaskingMode = MaskingPolicyMode.Unmasked, CreatedAt = From, ValidTo = From.AddDays(3) };
        unmasked.EffectiveValidTo.ShouldBe(From.AddDays(3));

        foreach (var mode in new[] { MaskingPolicyMode.Default, MaskingPolicyMode.Strict })
        {
            var p = new AccessProfile { ProfileId = "p", Name = "n", TenantId = TenantId.LegacySingleTenant, MaskingMode = mode, CreatedAt = From, ValidTo = null };
            p.EffectiveValidTo.ShouldBeNull();
            p.IsActive(From.AddDays(5000)).ShouldBeTrue();
        }
    }
}

[CollectionDefinition("IndividualGrantLimits", DisableParallelization = true)]
public sealed class IndividualGrantLimitsCollection;
