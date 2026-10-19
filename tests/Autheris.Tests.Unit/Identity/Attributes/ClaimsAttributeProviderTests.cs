namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static CallerAttributeTestSupport;

/// <summary>PLAN-GOV-129 test 4, ST-129-03, ST-129-04 (BC-129-04): the claims provider trusts only claims of the configured issuer on an allowed identity.</summary>
[Trait("Category", "Security")]
public sealed class ClaimsAttributeProviderTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));

    private ClaimsAttributeProvider Provider(CallerAttributeOptions? options = null) =>
        new(Options_(options ?? Options()), _time);

    private static IOptions<GatewayOptions> Options_(CallerAttributeOptions options) => Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = options });

    private static CallerSubject Subject(params (string Type, string Value)[] claims) => UserSubject(claims);

    private static string[] Values(CallerAttributeResult result) =>
        Enumerable.Range(0, result.Value!.Count).Select(i => (string)result.Value[i]).ToArray();

    [Fact]
    public async Task TrustedIssuerClaim_Resolves_AsRawStrings()
    {
        var result = await Provider().ResolveAsync(Subject(("department", "100"), ("department", "200")), ClaimsDefinition(), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        Values(result).ShouldBe(["100", "200"]);
    }

    [Fact]
    public async Task ST_129_03_ClaimOfAnotherIssuer_IsMissing()
    {
        var subject = UserSubject([("department", "100")], issuer: "https://evil.example/");
        (await Provider().ResolveAsync(subject, ClaimsDefinition(), default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Theory]
    [InlineData("LOCAL AUTHORITY")]
    [InlineData("Autheris:ClaimsNormalizer")]
    public async Task ST_129_03_NormalizerAndLocalAuthorityIssuers_AreMissing(string issuer)
    {
        var subject = UserSubject([("department", "100")], issuer: issuer);
        (await Provider().ResolveAsync(subject, ClaimsDefinition(), default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_03_AClaimOnANonBearerIdentity_NeverReachesTheProvider()
    {
        var principal = Principal("ForwardAuth", [("oid", UserOid), ("department", "100")]);
        var options = Options();
        // Even if the operator lists ForwardAuth for the subject, the claim must still come from an allowed scheme of the claims provider.
        var subject = CallerSubjectFactory.Create(principal, Tenant, options);
        (await Provider(options).ResolveAsync(subject, ClaimsDefinition(), default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_03_ClaimTypeOtherThanTheConfiguredOne_IsIgnored()
    {
        var result = await Provider().ResolveAsync(Subject(("division", "100")), ClaimsDefinition(), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_03_TwoDifferingValues_ForASingleAttribute_AreFailed_Ambiguous()
    {
        var def = ClaimsDefinition(cardinality: CallerAttributeCardinality.Scalar);
        var result = await Provider().ResolveAsync(Subject(("department", "100"), ("department", "200")), def, default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        result.ReasonCode.ShouldBe("Ambiguous");
    }

    [Fact]
    public async Task SameValueTwice_ForASingleAttribute_IsNotAmbiguous()
    {
        var def = ClaimsDefinition(cardinality: CallerAttributeCardinality.Scalar);
        var result = await Provider().ResolveAsync(Subject(("department", "100"), ("department", "100")), def, default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        Values(result).ShouldBe(["100"]);
    }

    [Fact]
    public async Task ST_129_03_OversizedValue_IsFailed()
    {
        var result = await Provider().ResolveAsync(Subject(("department", new string('x', 257))), ClaimsDefinition(), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        result.ReasonCode.ShouldBe("ClaimTooLarge");
    }

    [Fact]
    public async Task ST_129_03_MoreClaimsThanTheDefinitionAllows_IsFailed()
    {
        var def = ClaimsDefinition() with { MaxValues = 2 };
        var result = await Provider().ResolveAsync(Subject(("department", "1"), ("department", "2"), ("department", "3")), def, default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
    }

    [Fact]
    public async Task StrictJsonArray_IsExpanded()
    {
        var result = await Provider().ResolveAsync(Subject(("department", "[\"a\",\"b\",3,true]")), ClaimsDefinition(), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        Values(result).ShouldBe(["a", "b", "3", "true"]);
    }

    [Theory]
    [InlineData("[{\"a\":1,\"a\":2}]")]
    [InlineData("[[\"nested\"]]")]
    [InlineData("[\"a\",]")]
    [InlineData("[\"a\"] trailing")]
    [InlineData("[null]")]
    [InlineData("[\"a\" /* c */]")]
    public async Task ST_129_03_MalformedOrNestedArrays_AreFailed(string raw)
    {
        var result = await Provider().ResolveAsync(Subject(("department", raw)), ClaimsDefinition(), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        result.ReasonCode.ShouldBe("ClaimMalformed");
    }

    [Fact]
    public async Task ST_129_04_TokenOlderThanTheBound_IsMissing()
    {
        var iat = _time.GetUtcNow().AddSeconds(-1000).ToUnixTimeSeconds().ToString();
        var def = ClaimsDefinition(extra: ("maxTokenAgeSeconds", "900"));
        var subject = Subject(("department", "100"), ("iat", iat));
        (await Provider().ResolveAsync(subject, def, default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_04_TokenWithinTheBound_Resolves_AndTimeMovesTheBoundary()
    {
        var iat = _time.GetUtcNow().AddSeconds(-100).ToUnixTimeSeconds().ToString();
        var def = ClaimsDefinition(extra: ("maxTokenAgeSeconds", "900"));
        var subject = Subject(("department", "100"), ("iat", iat));
        var provider = Provider();
        (await provider.ResolveAsync(subject, def, default)).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        _time.Advance(TimeSpan.FromSeconds(801));
        (await provider.ResolveAsync(subject, def, default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_04_BoundWithoutIat_IsMissing()
    {
        var def = ClaimsDefinition(extra: ("maxTokenAgeSeconds", "900"));
        (await Provider().ResolveAsync(Subject(("department", "100")), def, default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ApplicationSubject_NeverReadsClaims()
    {
        var principal = Principal(Bearer, [("idtyp", "app"), ("appid", "aaaaaaaa-0000-0000-0000-000000000001"), ("department", "100")]);
        var subject = CallerSubjectFactory.Create(principal, Tenant, Options());
        (await Provider().ResolveAsync(subject, ClaimsDefinition(), default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task IssuerOfTheDefinition_ThatIsNotConfiguredTrusted_IsFailed()
    {
        var def = ClaimsDefinition() with { Provider = new CallerAttributeProviderBinding("claims", new System.Collections.Generic.Dictionary<string, string> { ["claim"] = "department", ["issuer"] = "https://evil.example/", ["idpControlledAck"] = "true" }) };
        var subject = UserSubject([("department", "100")], issuer: "https://evil.example/");
        (await Provider().ResolveAsync(subject, def, default)).Outcome.ShouldBe(CallerAttributeOutcome.Failed);
    }

    [Fact]
    public void Provider_IsNotCached_AndHasTheKindClaims()
    {
        var provider = Provider();
        provider.Kind.ShouldBe("claims");
        (provider is INonCachingCallerAttributeProvider).ShouldBeTrue();
    }

    [Fact]
    public void Validator_RequiresTheIdpControlledAcknowledgement_AndATrustedIssuer()
    {
        var validator = new ClaimsAttributeDefinitionValidator(Options_(Options()));
        validator.Validate(ClaimsDefinition());
        Should.Throw<CallerAttributeAdminException>(() => validator.Validate(ClaimsDefinition(extra: ("idpControlledAck", "false"))));
        var untrusted = ClaimsDefinition() with { Provider = new CallerAttributeProviderBinding("claims", new System.Collections.Generic.Dictionary<string, string> { ["claim"] = "department", ["issuer"] = "https://evil.example/", ["idpControlledAck"] = "true" }) };
        Should.Throw<CallerAttributeAdminException>(() => validator.Validate(untrusted));
        var noClaim = ClaimsDefinition() with { Provider = new CallerAttributeProviderBinding("claims", new System.Collections.Generic.Dictionary<string, string> { ["issuer"] = TrustedIssuer, ["idpControlledAck"] = "true" }) };
        Should.Throw<CallerAttributeAdminException>(() => validator.Validate(noClaim));
        var badAge = ClaimsDefinition(extra: ("maxTokenAgeSeconds", "-5"));
        Should.Throw<CallerAttributeAdminException>(() => validator.Validate(badAge));
    }
}
