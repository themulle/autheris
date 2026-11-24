namespace Autheris.Tests.Unit.ConsentRequests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Endpoint-level behaviour of the decider routes: inbox, container grant overview, grant revoke tenant binding and the
/// catalog grant revoke route (W3-11, SEC-1009-04, SEC-1009-36, SEC-1009-43).
/// </summary>
[Trait("Category", "Security")]
public sealed class DecisionEndpointTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Guid DomainId = Guid.NewGuid();
    private static readonly Sid OwnerGroup = new("group:S-1-5-21-SALES-OWNERS");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");

    private static readonly IServiceProvider Services = new ServiceCollection().AddLogging().BuildServiceProvider();

    private static (DefaultHttpContext Context, SecurityPrincipalContext Security) Caller(
        string sid,
        string? role = null,
        Sid[]? groups = null,
        bool app = false,
        TenantId? tenant = null)
    {
        var resolved = tenant ?? TenantA;
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new("tenant_id", resolved.Value) };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (app)
        {
            claims.Add(new Claim("idtyp", "app"));
            claims.Add(new Claim("azp", "client-1"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        var security = new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = resolved,
            GroupSids = groups?.ToHashSet() ?? new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = role is null ? new HashSet<string>() : new HashSet<string>([role], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = principal
        };
        var ctx = new DefaultHttpContext { RequestServices = Services, User = principal, Response = { Body = new MemoryStream() } };
        ctx.Items[SecurityPrincipalContext.ItemKey] = security;
        ctx.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("N");
        return (ctx, security);
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(DefaultHttpContext ctx, IResult result)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return (ctx.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static (GrantRevokeAuthority Authority, IGovernanceDomainRepository Domains, GovernanceCapabilities Caps, ApproverResolver Resolver) Authority()
    {
        var domains = NullFake.Create<IGovernanceDomainRepository>((m, args) =>
            m.Name == nameof(IGovernanceDomainRepository.GetAsync)
                ? Task.FromResult<GovernanceDomain?>(new GovernanceDomain((Guid)args![1]!, TenantA, "Sales", null, null, OwnerGroup, ProtectionClass.Confidential, CatalogVisibilityMode.Open, false, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                : NullFake.UseDefault);
        var protection = new StubProtection();
        var caps = new GovernanceCapabilities(domainRepository: domains);
        var resolver = new ApproverResolver(domains, protectionSnapshotProvider: protection);
        return (new GrantRevokeAuthority(resolver, new ApproverEligibility(caps), caps), domains, caps, resolver);
    }

    private static PermissionInboxService Inbox(out List<ConsentPageQuery> grantQueries)
    {
        var (_, domains, caps, resolver) = Authority();
        var queries = new List<ConsentPageQuery>();
        grantQueries = queries;
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, _) =>
            m.Name == nameof(IConsentApprovalRepository.QueryTenantConsentRequestsAsync)
                ? Task.FromResult(new ConsentRequestPage([], null))
                : NullFake.UseDefault);
        var consents = NullFake.Create<IConsentRepository>((m, args) =>
        {
            if (m.Name == nameof(IConsentRepository.GetConsentPageAsync))
            {
                queries.Add((ConsentPageQuery)args![0]!);
                return Task.FromResult(new ConsentPage([], null));
            }

            return NullFake.UseDefault;
        });
        return new PermissionInboxService(approvals, consents, resolver, new ApproverEligibility(caps), new SeparationOfDutiesService(), caps, domains);
    }

    [Fact]
    public async Task Inbox_of_an_application_identity_is_empty()
    {
        var (ctx, _) = Caller("app:client-1", app: true);

        var (status, body) = await ExecuteAsync(ctx, await ConsentApiEndpoints.GetInboxAsync(ctx, Inbox(out _)));

        status.ShouldBe(StatusCodes.Status200OK);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        json.GetProperty("items").GetArrayLength().ShouldBe(0);
        json.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("%%%")]
    [InlineData("bm9waXBl")]
    public async Task Inbox_rejects_a_malformed_cursor_with_400(string cursor)
    {
        var (ctx, _) = Caller("user:owner", groups: [OwnerGroup]);

        var (status, body) = await ExecuteAsync(ctx, await ConsentApiEndpoints.GetInboxAsync(ctx, Inbox(out _), cursor: cursor));

        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("INVALID_CURSOR");
    }

    [Fact]
    public async Task Container_overview_hides_containers_from_applications_and_unknown_kinds_with_404()
    {
        var (appCtx, _) = Caller("app:client-1", app: true);
        var (appStatus, appBody) = await ExecuteAsync(appCtx, await ConsentApiEndpoints.GetContainerGrantsAsync("domain", DomainId, appCtx, Inbox(out var appQueries)));
        appStatus.ShouldBe(StatusCodes.Status404NotFound);
        appBody.ShouldContain("RESOURCE_NOT_FOUND");
        appQueries.ShouldBeEmpty();

        var (ownerCtx, _) = Caller("user:owner", groups: [OwnerGroup]);
        var (kindStatus, _) = await ExecuteAsync(ownerCtx, await ConsentApiEndpoints.GetContainerGrantsAsync("nonsense", DomainId, ownerCtx, Inbox(out _)));
        kindStatus.ShouldBe(StatusCodes.Status404NotFound);

        var (strangerCtx, _) = Caller("user:mallory");
        var (strangerStatus, strangerBody) = await ExecuteAsync(strangerCtx, await ConsentApiEndpoints.GetContainerGrantsAsync("domain", DomainId, strangerCtx, Inbox(out _)));
        strangerStatus.ShouldBe(StatusCodes.Status404NotFound);
        strangerBody.ShouldContain("RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Container_overview_rejects_a_malformed_cursor_with_400()
    {
        var (ctx, _) = Caller("user:owner", groups: [OwnerGroup]);

        var (status, body) = await ExecuteAsync(ctx, await ConsentApiEndpoints.GetContainerGrantsAsync("domain", DomainId, ctx, Inbox(out var queries), cursor: "garbage"));

        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("INVALID_CURSOR");
        queries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Container_overview_returns_items_and_separate_sections_for_an_owner_member()
    {
        var (ctx, _) = Caller("user:owner", groups: [OwnerGroup]);

        var (status, body) = await ExecuteAsync(ctx, await ConsentApiEndpoints.GetContainerGrantsAsync("domain", DomainId, ctx, Inbox(out var queries)));

        status.ShouldBe(StatusCodes.Status200OK);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        json.GetProperty("items").GetArrayLength().ShouldBe(0);
        json.TryGetProperty("patternGrants", out _).ShouldBeTrue();
        json.GetProperty("patternGrantsTruncated").GetBoolean().ShouldBeFalse();
        json.TryGetProperty("catalogGrants", out _).ShouldBeTrue();
        queries.ShouldHaveSingleItem().DomainId.ShouldBe(DomainId);
    }

    [Fact]
    public async Task Cluster_admin_cannot_revoke_a_grant_of_another_tenant()
    {
        var grant = Grant(TenantB);
        var repo = new RevokeRecordingConsentRepository(grant);
        var audit = new InMemoryAuditWriter();
        var (ctx, _) = Caller("user:root", "ClusterAdmin");

        var result = await ConsentApiEndpoints.RevokeGrantAsync(
            grant.Id, new RevokeConsentGrantBody("cleanup of stale access"), ctx, repo.Proxy,
            NullFake.Create<IConsentApprovalRepository>(), NullFake.Create<IConsentWorkflowConfigRepository>(),
            revokeAuthority: Authority().Authority, auditWriter: audit);
        var (status, _) = await ExecuteAsync(ctx, result);

        status.ShouldBe(StatusCodes.Status404NotFound);
        repo.Revocations.ShouldBeEmpty();
        audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Owner_revoke_passes_the_verified_capacity_to_the_repository()
    {
        var grant = Grant(TenantA);
        var repo = new RevokeRecordingConsentRepository(grant);
        var (ctx, _) = Caller("user:owner", groups: [OwnerGroup]);

        var result = await ConsentApiEndpoints.RevokeGrantAsync(
            grant.Id, new RevokeConsentGrantBody("no longer needed"), ctx, repo.Proxy,
            NullFake.Create<IConsentApprovalRepository>(), NullFake.Create<IConsentWorkflowConfigRepository>(),
            revokeAuthority: Authority().Authority, auditWriter: new InMemoryAuditWriter());
        var (status, _) = await ExecuteAsync(ctx, result);

        status.ShouldBe(StatusCodes.Status200OK);
        var authority = repo.Revocations.ShouldHaveSingleItem();
        authority.VerifiedCapacity.ShouldBe(nameof(GrantRevokeCapacity.ContainerOwner));
        authority.CanAccessTenant(TenantA).ShouldBeTrue();
        authority.CanAccessTenant(TenantB).ShouldBeFalse();
    }

    [Fact]
    public async Task Catalog_grant_revoke_by_owner_member_is_audited_with_capacity()
    {
        var catalog = new CatalogGrantStore(CatalogGrant(TenantA));
        var audit = new InMemoryAuditWriter();
        var (ctx, _) = Caller("user:owner", groups: [OwnerGroup]);

        var (status, _) = await ExecuteAsync(ctx, await ConsentApiEndpoints.RevokeCatalogGrantAsync(
            catalog.Grant.Id, new RevokeConsentGrantBody("visibility no longer needed"), ctx, catalog.Proxy, Authority().Authority, audit));

        status.ShouldBe(StatusCodes.Status200OK);
        catalog.Revoked.ShouldHaveSingleItem().ShouldBe(catalog.Grant.Id);
        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.ConsentRevoked);
        entry.DetailsJson.ShouldNotBeNull().ShouldContain("\"Capacity\":\"ContainerOwner\"");
        entry.DetailsJson.ShouldContain("CATALOG");
    }

    [Fact]
    public async Task Catalog_grant_revoke_denies_strangers_with_an_audited_403()
    {
        var catalog = new CatalogGrantStore(CatalogGrant(TenantA));
        var audit = new InMemoryAuditWriter();
        var (ctx, _) = Caller("user:mallory");

        var (status, _) = await ExecuteAsync(ctx, await ConsentApiEndpoints.RevokeCatalogGrantAsync(
            catalog.Grant.Id, new RevokeConsentGrantBody("trying to hide a grant"), ctx, catalog.Proxy, Authority().Authority, audit));

        status.ShouldBe(StatusCodes.Status403Forbidden);
        catalog.Revoked.ShouldBeEmpty();
        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.ConsentDenied);
        entry.Decision.ShouldBe("DENY");
    }

    [Fact]
    public async Task Catalog_grant_revoke_of_another_tenant_or_by_an_application_is_404()
    {
        var foreign = new CatalogGrantStore(CatalogGrant(TenantB));
        var (dgCtx, _) = Caller("user:dg", "GovernanceAdmin");
        var (status, _) = await ExecuteAsync(dgCtx, await ConsentApiEndpoints.RevokeCatalogGrantAsync(
            foreign.Grant.Id, new RevokeConsentGrantBody("cross tenant attempt"), dgCtx, foreign.Proxy, Authority().Authority, new InMemoryAuditWriter()));
        status.ShouldBe(StatusCodes.Status404NotFound);
        foreign.Revoked.ShouldBeEmpty();

        var local = new CatalogGrantStore(CatalogGrant(TenantA));
        var (appCtx, _) = Caller("app:client-1", app: true);
        var (appStatus, _) = await ExecuteAsync(appCtx, await ConsentApiEndpoints.RevokeCatalogGrantAsync(
            local.Grant.Id, new RevokeConsentGrantBody("application attempt"), appCtx, local.Proxy, Authority().Authority, new InMemoryAuditWriter()));
        appStatus.ShouldBe(StatusCodes.Status404NotFound);
        local.Revoked.ShouldBeEmpty();
    }

    private static Consent Grant(TenantId tenant) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant,
        TableIdentifier = OrdersTable,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("user:alice"),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(30)
    };

    private static CatalogVisibilityGrant CatalogGrant(TenantId tenant) => new()
    {
        TenantId = tenant,
        ContainerKind = ContainerKind.Domain,
        ContainerId = DomainId,
        GranteeSid = new Sid("user:alice"),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1)
    };

    private sealed class RevokeRecordingConsentRepository
    {
        public RevokeRecordingConsentRepository(Consent grant)
        {
            Proxy = NullFake.Create<IConsentRepository>((m, args) =>
            {
                if (m.Name == nameof(IConsentRepository.GetConsentByIdAsync))
                {
                    return Task.FromResult<Consent?>((Guid)args![0]! == grant.Id ? grant : null);
                }

                if (m.Name == nameof(IConsentRepository.RevokeConsentAsync))
                {
                    Revocations.Add((ConsentRevocationAuthority)args![3]!);
                    return Task.CompletedTask;
                }

                return NullFake.UseDefault;
            });
        }

        public IConsentRepository Proxy { get; }

        public List<ConsentRevocationAuthority> Revocations { get; } = [];
    }

    private sealed class CatalogGrantStore
    {
        public CatalogGrantStore(CatalogVisibilityGrant grant)
        {
            Grant = grant;
            Proxy = NullFake.Create<ICatalogVisibilityGrantRepository>((m, args) =>
            {
                if (m.Name == nameof(ICatalogVisibilityGrantRepository.GetByIdAsync))
                {
                    // The store is tenant-scoped: a grant of another tenant is never returned.
                    var tenant = (TenantId)args![0]!;
                    return Task.FromResult<CatalogVisibilityGrant?>(tenant == grant.TenantId && (Guid)args[1]! == grant.Id ? grant : null);
                }

                if (m.Name == nameof(ICatalogVisibilityGrantRepository.RevokeAsync))
                {
                    Revoked.Add((Guid)args![1]!);
                    return Task.FromResult(true);
                }

                return NullFake.UseDefault;
            });
        }

        public CatalogVisibilityGrant Grant { get; }

        public ICatalogVisibilityGrantRepository Proxy { get; }

        public List<Guid> Revoked { get; } = [];
    }

    private sealed class StubProtection : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(new EffectiveProtection(
                new ContainerRef(ContainerKind.Domain, DomainId),
                OwnerGroup,
                ProtectionClass.Confidential,
                ProtectionClass.Confidential,
                null,
                new Dictionary<string, ColumnProtection>(),
                CatalogVisibilityMode.Open));
    }
}
