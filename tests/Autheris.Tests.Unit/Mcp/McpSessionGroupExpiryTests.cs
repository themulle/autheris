namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

[Trait("Category", "Security")]
public sealed class McpSessionGroupExpiryTests
{
    private const string SidGroup = "S-1-5-21-100-200-300-6001";
    private const string UserSid = "S-1-5-21-100-200-300-1001";
    private static readonly TenantId Tenant = new("tenant-mcp");

    [Fact]
    public async Task Session_denied_after_expiry()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0);
        var options = Options.Create(new GatewayOptions
        {
            Authentication = new AuthenticationOptions { GroupCacheTtlMinutes = 5 }
        });

        var store = new McpSessionStore(NullLogger<McpSessionStore>.Instance, timeProvider: time, options: options);
        // Even if caller supplies an explicit expiry beyond GroupCacheTtlMinutes, session creation bounds GroupsExpireAt to 5m.
        var session = store.CreateSession(
            servicePrincipalId: "mcp-agent",
            tenantId: Tenant.Value,
            userSid: UserSid,
            roles: ["Analyst"],
            groupSids: [SidGroup],
            groupsExpireAt: t0.AddMinutes(60));

        session.GroupsExpireAt.ShouldBe(t0.AddMinutes(5));

        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var meta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "orders",
                IsActive = true
            },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "amount", DataType = "DECIMAL" }]
        };

        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid(SidGroup),
            ValidFrom = t0.AddDays(-1),
            ValidTo = t0.AddDays(30),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
            ]
        };

        var consentRepo = new InMemoryConsentRepository(consent);
        var pdp = new AccessDecisionPoint(
            options,
            new ConsentResolutionService(timeProvider: time),
            consentRepo,
            new InMemoryConsentCacheService(),
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            timeProvider: time);
        var resourceProvider = new McpResourceProvider(
            new StubCatalogDiscoveryService(),
            new StubApiDispatcherService(),
            timeProvider: time);

        // Before expiry (t0 + 4m): Touch extends session idle timeout, groups remain fresh and access is allowed.
        time.Advance(TimeSpan.FromMinutes(4));
        var touchedBefore = store.Touch(session.SessionId).ShouldNotBeNull();
        touchedBefore.GroupsExpireAt.ShouldBe(t0.AddMinutes(5));

        var freshPrincipal = McpProtocolHandler.BuildPrincipalFromSession(touchedBefore, time)!;
        freshPrincipal.GetGroupSids().ShouldContain(new Sid(SidGroup));
        GroupResolutionClaims.Read(freshPrincipal, time.GetUtcNow()).ShouldBe(GroupResolutionStatus.Complete);

        var allowed = await pdp.DecideAsync(
            new AccessRequest(
                SecurityPrincipalContext.FromPrincipal(freshPrincipal, Tenant, timeProvider: time),
                meta,
                AccessOperation.Read,
                AccessTransport.Mcp),
            CancellationToken.None);
        allowed.IsAllowed.ShouldBeTrue();

        var freshAccessDoc = await resourceProvider.ReadAsync("autheris://governance/my-access", touchedBefore, CancellationToken.None);
        freshAccessDoc.Text.ShouldContain(SidGroup);

        // After expiry (t0 + 6m): Touch never extends GroupsExpireAt; groups degrade and data access is denied.
        time.Advance(TimeSpan.FromMinutes(2));
        var touchedAfter = store.Touch(session.SessionId).ShouldNotBeNull();
        touchedAfter.GroupsExpireAt.ShouldBe(t0.AddMinutes(5));

        var expiredPrincipal = McpProtocolHandler.BuildPrincipalFromSession(touchedAfter, time)!;
        expiredPrincipal.GetGroupSids().ShouldBeEmpty();
        GroupResolutionClaims.Read(expiredPrincipal, time.GetUtcNow()).ShouldBe(GroupResolutionStatus.Degraded);

        var denied = await pdp.DecideAsync(
            new AccessRequest(
                SecurityPrincipalContext.FromPrincipal(expiredPrincipal, Tenant, timeProvider: time),
                meta,
                AccessOperation.Read,
                AccessTransport.Mcp),
            CancellationToken.None);
        denied.IsAllowed.ShouldBeFalse();
        denied.Reasons.ShouldContain(r => r.Code == "GROUPS_DEGRADED");

        var expiredAccessDoc = await resourceProvider.ReadAsync("autheris://governance/my-access", touchedAfter, CancellationToken.None);
        expiredAccessDoc.Text.ShouldNotContain(SidGroup);
        expiredAccessDoc.Text.ShouldContain("**Groups:** None");
    }

    private sealed class StubApiDispatcherService : IApiDispatcherService
    {
        public Task<string> DescribeApiAsync(string endpoint, string? method = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> InvokeApiAsync(
            string endpoint,
            string method,
            IReadOnlyDictionary<string, object?>? parameters = null,
            object? body = null,
            RequestContext? context = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> GetOpenApiJsonAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEndpointsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetMcpToolsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetQueryGuideDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
