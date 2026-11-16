using System.Data;
using System.Security.Claims;
using Autheris.Api.Middleware;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Unit.Persistence.DialectSql;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

[Trait("Category", "Security")]
public sealed class ConsentRevocationAuthorityTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid Actor = new("S-1-5-21-ACTOR");
    private static readonly Sid Other = new("S-1-5-21-OTHER");

    private static ClaimsPrincipal PrincipalWith(string authType = "Bearer", params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authType, ClaimTypes.Name, ClaimTypes.Role));

    [Fact]
    public void FromPrincipal_TenantPrefixedGovernanceAdmin_IsTenantBound()
    {
        var principal = PrincipalWith(claims:
        [
            new Claim(ClaimTypes.PrimarySid, Actor.Value),
            new Claim(ClaimTypes.Role, "tenant-a:GovernanceAdmin")
        ]);

        var authority = ConsentRevocationAuthority.FromPrincipal(principal);

        authority.Scope.ShouldBe(ConsentRevocationScope.TenantGovernanceAdmin);
        authority.TenantId.ShouldBe(TenantA);
        authority.AllowsAdminForTenant(TenantA).ShouldBeTrue();
        authority.AllowsAdminForTenant(TenantB).ShouldBeFalse();
        authority.CanAccessTenant(TenantA).ShouldBeTrue();
        authority.CanAccessTenant(TenantB).ShouldBeFalse();
    }

    [Fact]
    public void FromPrincipal_CanonicalGovernanceAdmin_RequiresTenant()
    {
        var withTenant = PrincipalWith(claims:
        [
            new Claim(ClaimTypes.PrimarySid, Actor.Value),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", TenantA.Value)
        ]);
        var bound = ConsentRevocationAuthority.FromPrincipal(withTenant);
        bound.Scope.ShouldBe(ConsentRevocationScope.TenantGovernanceAdmin);
        bound.TenantId.ShouldBe(TenantA);
        bound.AllowsAdminForTenant(TenantA).ShouldBeTrue();
        bound.AllowsAdminForTenant(TenantB).ShouldBeFalse();

        var withoutTenant = PrincipalWith(claims:
        [
            new Claim(ClaimTypes.PrimarySid, Actor.Value),
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ]);
        var unbound = ConsentRevocationAuthority.FromPrincipal(withoutTenant);
        unbound.Scope.ShouldBe(ConsentRevocationScope.GranteeOrApproverOnly);
        unbound.AllowsAdminForTenant(TenantA).ShouldBeFalse();
        unbound.AllowsAdminForTenant(TenantB).ShouldBeFalse();
    }

    [Fact]
    public void Authority_TenantBoundWithoutTenantClaim_MatchesNothing()
    {
        var principal = PrincipalWith(claims:
        [
            new Claim(ClaimTypes.PrimarySid, Actor.Value),
            new Claim(ClaimTypes.Role, ":GovernanceAdmin")
        ]);

        var authority = ConsentRevocationAuthority.FromPrincipal(principal);

        authority.Scope.ShouldBe(ConsentRevocationScope.GranteeOrApproverOnly);
        authority.AllowsAdminForTenant(TenantA).ShouldBeFalse();
        authority.AllowsAdminForTenant(TenantB).ShouldBeFalse();
        authority.AllowsAdminForTenant(new TenantId("default")).ShouldBeFalse();
    }

    [Fact]
    public void FromPrincipal_CanonicalClusterAdmin_IsGlobal()
    {
        var principal = PrincipalWith(claims:
        [
            new Claim(ClaimTypes.PrimarySid, Actor.Value),
            new Claim(ClaimTypes.Role, "ClusterAdmin")
        ]);

        var authority = ConsentRevocationAuthority.FromPrincipal(principal);

        authority.Scope.ShouldBe(ConsentRevocationScope.ClusterAdmin);
        authority.TenantId.ShouldBeNull();
        authority.AllowsAdminForTenant(TenantA).ShouldBeTrue();
        authority.AllowsAdminForTenant(TenantB).ShouldBeTrue();
        authority.CanAccessTenant(TenantA).ShouldBeTrue();
        authority.CanAccessTenant(TenantB).ShouldBeTrue();
    }

    [Fact]
    public void FromPrincipal_ForwardAuthClusterAdmin_IsDeniedGlobalScope()
    {
        var principal = PrincipalWith(
            authType: ClusterAdminPolicy.ForwardAuthScheme,
            claims:
            [
                new Claim(ClaimTypes.PrimarySid, Actor.Value),
                new Claim(ClaimTypes.Role, "ClusterAdmin"),
                new Claim("tenant_id", TenantA.Value)
            ]);

        var authority = ConsentRevocationAuthority.FromPrincipal(principal);

        authority.Scope.ShouldNotBe(ConsentRevocationScope.ClusterAdmin);
        authority.AllowsAdminForTenant(TenantA).ShouldBeFalse();
    }

    [Fact]
    public void Revoke_DenialMessage_ContainsNoSidOrTable()
    {
        var ex = new ConsentRevocationForbiddenException();

        ex.ShouldBeAssignableTo<GatewaySecurityException>();
        ex.Message.ShouldNotContain(Actor.Value);
        ex.Message.ShouldNotContain("orders");
        ex.Message.ShouldBe("You do not have permission to revoke this consent.");
    }

    [Fact]
    public async Task GatewayExceptionHandler_MapsConsentRevocationForbiddenException_To403AccessDenied()
    {
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, new ConsentRevocationForbiddenException(), CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.ShouldContain("ACCESS_DENIED");
    }

    [Fact]
    public async Task Revoke_BlankActorOrSystemString_HasNoBypass()
    {
        using var conn = DialectKit.PostgreSql.NewConnection();
        await using var repo = CreateRepo(conn);
        var authority = ConsentRevocationAuthority.FromPrincipal(null);

        await Should.ThrowAsync<ArgumentException>(() =>
            repo.RevokeConsentAsync(Guid.NewGuid(), new Sid("   "), "reason", authority, CancellationToken.None));

        conn.Commands.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Revoke_TokenGovernanceAdmin_OtherTenantConsent_ReturnsNotFound_AndDoesNotCheckApprover()
    {
        using var conn = DialectKit.PostgreSql.NewConnection();
        await using var repo = CreateRepo(conn);
        var consentId = Guid.NewGuid();
        conn.EnqueueReader(RevokeTargetRows(("Sales", "Crm", "Orders", TenantB.Value, "User", Other.Value, 0)));

        var principal = PrincipalWith(claims:
        [
            new Claim(ClaimTypes.PrimarySid, Actor.Value),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", TenantA.Value)
        ]);

        var foreignEx = await Should.ThrowAsync<KeyNotFoundException>(() =>
            repo.RevokeConsentAsync(consentId, Actor, "revoke", ConsentRevocationAuthority.FromPrincipal(principal), CancellationToken.None));

        conn.EnqueueReader(RevokeTargetRows());
        var missingEx = await Should.ThrowAsync<KeyNotFoundException>(() =>
            repo.RevokeConsentAsync(consentId, Actor, "revoke", ConsentRevocationAuthority.FromPrincipal(principal), CancellationToken.None));

        foreignEx.Message.ShouldBe(missingEx.Message);
        foreignEx.Message.ShouldBe($"Consent with ID '{consentId}' does not exist.");
        conn.Commands.ShouldNotContain(c => c.CommandText.Contains("ROLE_MEMBERS", StringComparison.OrdinalIgnoreCase));
        conn.Commands.ShouldNotContain(c => c.CommandText.Contains("UPDATE CONSENTS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Replace_PartialDenial_RollsBackWholeManifest()
    {
        using var conn = DialectKit.PostgreSql.NewConnection();
        var recordedAudits = new List<AuditLogEntry>();
        await using var repo = CreateRepo(conn, recordedAudits);
        var ownedId = Guid.NewGuid();
        var deniedId = Guid.NewGuid();
        // First revoke target: self-revokable in TenantA
        conn.EnqueueReader(RevokeTargetRows(("Sales", "Crm", "Orders", TenantA.Value, "User", Actor.Value, 0)));
        // Event metadata lookup for first revoked consent (empty -> Internal workflow, no external system)
        conn.EnqueueReader(new DataTable());
        // Second revoke target: owned by someone else in TenantA; owner/delegate scalar checks both return 0
        conn.EnqueueReader(RevokeTargetRows(("Sales", "Crm", "Secrets", TenantA.Value, "User", Other.Value, 0)));

        var newConsent = new Consent
        {
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("Sales", "Crm", "Orders"),
            TenantId = TenantA,
            GranteeType = GranteeType.User,
            GranteeSid = Other,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(5),
            CreatedBySid = Actor
        };

        var ex = await Should.ThrowAsync<ConsentRevocationForbiddenException>(() =>
            repo.ReplaceConsentsAsync([ownedId, deniedId], [newConsent], Actor, "manifest", ConsentRevocationAuthority.FromPrincipal(null)));

        ex.Message.ShouldNotContain(Actor.Value);
        ex.Message.ShouldNotContain("Secrets");
        conn.Commands.ShouldNotContain(c => c.CommandText.Contains("INSERT INTO CONSENTS", StringComparison.OrdinalIgnoreCase));
        conn.Commands.ShouldNotContain(c => c.CommandText.Contains("ROLE_MEMBERS", StringComparison.OrdinalIgnoreCase));
        recordedAudits.ShouldContain(a => a.EventType == AuditEventTypes.ConsentDenied && a.Decision == "DENY" && a.TenantId == TenantA);
    }

    private static DataTable RevokeTargetRows(params (string Source, string Schema, string Table, string TenantId, string GranteeType, string GranteeSid, int IsRevoked)[] entries)
    {
        var table = new DataTable();
        table.Columns.Add("source_name", typeof(string));
        table.Columns.Add("schema_name", typeof(string));
        table.Columns.Add("table_name", typeof(string));
        table.Columns.Add("tenant_id", typeof(string));
        table.Columns.Add("grantee_type", typeof(string));
        table.Columns.Add("grantee_sid", typeof(string));
        table.Columns.Add("is_revoked", typeof(int));
        foreach (var e in entries)
        {
            table.Rows.Add(e.Source, e.Schema, e.Table, e.TenantId, e.GranteeType, e.GranteeSid, e.IsRevoked);
        }
        return table;
    }

    private sealed class StubGrantWriter : Autheris.Application.Governance.Grants.Records.IGrantWriter
    {
        private static Autheris.Domain.Grants.GrantRecord StubRecord(
            Autheris.Domain.Grants.GrantRecordType type,
            Consent? consent,
            Autheris.Application.Governance.Grants.Records.GrantRecordInput input) =>
            new(input.TenantId, 1L, Guid.NewGuid(), type, consent?.Id, input.RequestId,
                consent?.GrantSource ?? ConsentGrantSource.Declarative, consent?.ProfileAssignmentId,
                input.GranteeType ?? "User", input.GranteeRef ?? string.Empty, input.ObjectId ?? string.Empty,
                input.ScopeJson, input.ValidFrom, input.ValidUntil, input.RequestedBy,
                input.DecisionJson, input.DecidedAt, input.DecidedAt, Autheris.Application.Governance.Grants.Records.GrantRecordChain.GenesisHash);

        public Task<Autheris.Domain.Grants.GrantRecord> InsertAndGrantAsync(System.Data.Common.DbTransaction transaction, Consent consent, Autheris.Application.Governance.Grants.Records.GrantRecordInput input, CancellationToken ct = default) =>
            Task.FromResult(StubRecord(Autheris.Domain.Grants.GrantRecordType.Issued, consent, input));

        public Task<Autheris.Domain.Grants.GrantRecord> GrantAsync(System.Data.Common.DbTransaction transaction, Consent consent, Autheris.Application.Governance.Grants.Records.GrantRecordInput input, CancellationToken ct = default) =>
            Task.FromResult(StubRecord(Autheris.Domain.Grants.GrantRecordType.Issued, consent, input));

        public Task<Autheris.Domain.Grants.GrantRecord> RevokeAsync(System.Data.Common.DbTransaction transaction, Consent consent, Autheris.Application.Governance.Grants.Records.GrantRecordInput input, CancellationToken ct = default) =>
            Task.FromResult(StubRecord(Autheris.Domain.Grants.GrantRecordType.Revoked, consent, input));

        public Task<Autheris.Domain.Grants.GrantRecord> RecordRejectionAsync(System.Data.Common.DbTransaction transaction, Autheris.Application.Governance.Grants.Records.GrantRecordInput input, CancellationToken ct = default) =>
            Task.FromResult(StubRecord(Autheris.Domain.Grants.GrantRecordType.Rejected, null, input));

        public Task<Autheris.Domain.Grants.GrantRecord> RecordExpiryAsync(System.Data.Common.DbTransaction transaction, Consent? consent, Autheris.Application.Governance.Grants.Records.GrantRecordInput input, CancellationToken ct = default) =>
            Task.FromResult(StubRecord(Autheris.Domain.Grants.GrantRecordType.Expired, consent, input));
    }

    private static GovernanceRepository CreateRepo(RecordingDbConnection conn, List<AuditLogEntry>? recordedAudits = null) =>
        new(
            new PostgreSqlGovernanceDialect(() => conn),
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance,
            auditRecorder: recordedAudits is null
                ? null
                : (entry, _, _) =>
                {
                    recordedAudits.Add(entry);
                    return Task.CompletedTask;
                },
            auditAnchorStore: new InMemoryAuditChainAnchorStore(),
            grantWriter: new StubGrantWriter());
}
