using System.Security.Claims;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.ConsentRequests;

/// <summary>
/// WP-0 (PLAN-GOV-113, SEC-113-R2-01/-02): the pure decision guard and its denial recorder.
/// Covers W-1 (read-only), W-2 (app-only shapes), W-3/W-12 (interactive shapes, also after JwtBearer mapping),
/// W-10 (null/unauthenticated), W-11 (delegated token without a human name) and W-13 (certificate, service account, audit aggregation).
/// </summary>
public class ConsentDecisionGuardTests
{
    private static readonly ConsentDecisionGuard Guard = new(new IdentitySubjectResolver());
    private const string Sid = "S-1-5-21-DATAOWNER-1";

    public static TheoryData<string, ClaimsPrincipal> InteractivePrincipals => new()
    {
        { "basic", DecisionPrincipals.Basic(Sid) },
        { "kerberos", DecisionPrincipals.Kerberos(Sid) },
        { "forwardauth", DecisionPrincipals.ForwardAuth(Sid) },
        { "entra-delegated-raw", DecisionPrincipals.EntraDelegatedRaw(Sid) },
        { "entra-delegated-mapped", DecisionPrincipals.EntraDelegatedMapped(Sid) },
        { "entra-delegated-no-human-name", DecisionPrincipals.EntraDelegatedNoHumanName(Sid) },
        { "entra-delegated-mapped-no-human-name", DecisionPrincipals.EntraDelegatedMappedNoHumanName(Sid) },
        { "adfs-mapped", DecisionPrincipals.AdfsMapped(Sid) },
        { "adfs-mapped-unique-name-only", DecisionPrincipals.AdfsMappedUniqueNameOnly(Sid) },
    };

    [Theory]
    [MemberData(nameof(InteractivePrincipals))]
    public void W3_W11_W12_InteractiveAndDelegatedPrincipals_MayDecide(string shape, ClaimsPrincipal principal)
    {
        var verdict = Guard.Evaluate(principal);

        verdict.Allowed.ShouldBeTrue($"{shape} must be allowed to decide");
    }

    public static TheoryData<string, ClaimsPrincipal> ApplicationPrincipals => new()
    {
        { "idtyp=app", DecisionPrincipals.AppOnlyIdtyp("oid-app-1", "GovernanceAdmin") },
        { "appid only", DecisionPrincipals.AppIdOnly("app-1", "GovernanceAdmin") },
        { "azp only", DecisionPrincipals.AzpOnly("app-1", "GovernanceAdmin") },
        { "client_id only", DecisionPrincipals.ClientIdOnly("app-1", "GovernanceAdmin") },
        { "service account", DecisionPrincipals.ServiceAccount("svc-1", "GovernanceAdmin") },
        { "mtls certificate", DecisionPrincipals.Certificate("CN=svc.corp", "GovernanceAdmin") },
    };

    [Theory]
    [MemberData(nameof(ApplicationPrincipals))]
    public void W2_W11_W13_ApplicationPrincipals_AreDeniedAsNotPerson(string shape, ClaimsPrincipal principal)
    {
        var verdict = Guard.Evaluate(principal);

        verdict.Allowed.ShouldBeFalse($"{shape} must not decide");
        verdict.ReasonClass.ShouldBe("not-person");
    }

    [Fact]
    public void W2_AppOnlyIdtyp_IsDeniedEvenWhenItCarriesAScopeClaim()
    {
        var principal = DecisionPrincipals.AppOnlyIdtyp("oid-app-1", "GovernanceAdmin");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("scp", "Autheris.Access"));

        Guard.Evaluate(principal).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void W13_CertificateSubject_IsDeniedEvenWhenItCarriesAScopeClaim()
    {
        var principal = DecisionPrincipals.Certificate("CN=svc.corp", "GovernanceAdmin");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("scp", "Autheris.Access"));

        Guard.Evaluate(principal).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void W11_AzpOnlyTokenWithScopeButWithoutPersonIdentifier_IsDenied()
    {
        // A scope claim alone does not make an application a person: an oid or SID is required as well.
        var principal = DecisionPrincipals.AzpOnly("app-1", "GovernanceAdmin");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("scp", "Autheris.Access"));

        var verdict = Guard.Evaluate(principal);

        verdict.Allowed.ShouldBeFalse();
        verdict.ReasonClass.ShouldBe("not-person");
    }

    [Fact]
    public void W1_ReadOnlyPrincipal_IsDeniedAsReadOnly()
    {
        var verdict = Guard.Evaluate(DecisionPrincipals.ReadOnly(Sid, "GovernanceAdmin"));

        verdict.Allowed.ShouldBeFalse();
        verdict.ReasonClass.ShouldBe("read-only");
    }

    [Fact]
    public void W10_NullPrincipal_IsDeniedByTheGuardItself()
    {
        var verdict = Guard.Evaluate(null);

        verdict.Allowed.ShouldBeFalse();
        verdict.ReasonClass.ShouldBe("unauthenticated");
    }

    [Fact]
    public void W10_UnauthenticatedPrincipal_IsDeniedByTheGuardItself()
    {
        var verdict = Guard.Evaluate(DecisionPrincipals.Unauthenticated());

        verdict.Allowed.ShouldBeFalse();
        verdict.ReasonClass.ShouldBe("unauthenticated");
    }

    // ---- Recorder (SEC-113-R2-02 c) ----

    private static ConsentDecisionDenialRecorder Recorder(RecordingAuditWriter audit, TimeProvider time) => new(audit, time);

    [Fact]
    public async Task W13_Denial_WritesOneTierAEntryWithReasonClassOnly()
    {
        var audit = new RecordingAuditWriter();
        var recorder = Recorder(audit, new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T10:00:10Z")));

        await recorder.RecordAsync(DecisionPrincipals.AppIdOnly("app-1"), "not-person", TenantId.LegacySingleTenant);

        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("CONSENT_DENIED");
        entry.Decision.ShouldBe("DENY");
        entry.DetailsJson.ShouldContain("not-person");
        entry.DetailsJson.ShouldNotContain("app-1");
    }

    [Fact]
    public async Task W13_SecondDenialOfTheSamePrincipalInTheSameMinute_WritesNoSecondEntry()
    {
        var audit = new RecordingAuditWriter();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T10:00:10Z"));
        var recorder = Recorder(audit, time);
        var principal = DecisionPrincipals.AppIdOnly("app-1");

        await recorder.RecordAsync(principal, "not-person", TenantId.LegacySingleTenant);
        time.Advance(TimeSpan.FromSeconds(20));
        await recorder.RecordAsync(principal, "not-person", TenantId.LegacySingleTenant);

        audit.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task W13_DenialInTheNextMinute_WritesANewEntry_AndOtherPrincipalsAreNotAggregated()
    {
        var audit = new RecordingAuditWriter();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T10:00:50Z"));
        var recorder = Recorder(audit, time);

        await recorder.RecordAsync(DecisionPrincipals.AppIdOnly("app-1"), "not-person", TenantId.LegacySingleTenant);
        await recorder.RecordAsync(DecisionPrincipals.AppIdOnly("app-2"), "not-person", TenantId.LegacySingleTenant);
        time.Advance(TimeSpan.FromSeconds(20));
        await recorder.RecordAsync(DecisionPrincipals.AppIdOnly("app-1"), "not-person", TenantId.LegacySingleTenant);

        audit.Entries.Count.ShouldBe(3);
    }

    [Fact]
    public async Task W13_AuditWriterFailure_NeverEscapesTheRecorder()
    {
        var audit = new RecordingAuditWriter { ThrowOnWrite = true };
        var recorder = Recorder(audit, TimeProvider.System);

        await Should.NotThrowAsync(() => recorder.RecordAsync(DecisionPrincipals.AppIdOnly("app-1"), "not-person", TenantId.LegacySingleTenant));
    }
}
