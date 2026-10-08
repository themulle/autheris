namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TableConsumersSecurityTests
{
    private readonly ILineageImpactAnalyzerService _lineageService = Substitute.For<ILineageImpactAnalyzerService>();
    private readonly IConsentRepository _consentRepo = Substitute.For<IConsentRepository>();
    private readonly IDataOwnershipRepository _ownershipRepo = Substitute.For<IDataOwnershipRepository>();
    private readonly Query _query = new();

    private static IHttpContextAccessor CreateAccessor(
        string userSid,
        string tenantId,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? groups = null)
    {
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.PrimarySid, userSid));
        identity.AddClaim(new Claim("tenant_id", tenantId));

        if (roles != null)
        {
            foreach (var r in roles)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, r));
            }
        }

        if (groups != null)
        {
            foreach (var g in groups)
            {
                identity.AddClaim(new Claim(ClaimTypes.GroupSid, g));
            }
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        context.Items["TenantId"] = new TenantId(tenantId);

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return accessor;
    }

    [Fact]
    public async Task TableConsumers_CallerWithoutConsent_ThrowsForbidden()
    {
        // GQL-6: Non-admin caller without consent must receive FORBIDDEN
        var accessor = CreateAccessor("S-1-5-21-USER1", "tenant-1", roles: ["Analyst"]);
        var table = new TableIdentifier("sales", "dbo", "orders");

        _consentRepo.GetActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(),
            table,
            Arg.Any<DateTimeOffset>(),
            new TenantId("tenant-1"),
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Consent>());

        _ownershipRepo.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-USER1"), Arg.Any<CancellationToken>())
            .Returns(false);

        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            _query.GetTableConsumersAsync(
                "sales", "dbo", "orders", 30,
                _lineageService,
                accessor,
                _consentRepo,
                _ownershipRepo));

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN");
        await _lineageService.DidNotReceiveWithAnyArgs().GetTableConsumersAsync(default, default, default, default);
    }

    [Fact]
    public async Task TableConsumers_CallerFromForeignTenantWithoutConsent_ThrowsForbidden()
    {
        // GQL-6: Cross-tenant isolation - caller in tenant-2 has no consent in tenant-2
        var accessor = CreateAccessor("S-1-5-21-USER2", "tenant-2", roles: ["Analyst"]);
        var table = new TableIdentifier("sales", "dbo", "orders");

        _consentRepo.GetActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(),
            table,
            Arg.Any<DateTimeOffset>(),
            new TenantId("tenant-2"),
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Consent>());

        _ownershipRepo.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-USER2"), Arg.Any<CancellationToken>())
            .Returns(false);

        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            _query.GetTableConsumersAsync(
                "sales", "dbo", "orders", 30,
                _lineageService,
                accessor,
                _consentRepo,
                _ownershipRepo));

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN");
    }

    [Fact]
    public async Task TableConsumers_CallerWithUnconditionalDeny_ThrowsForbidden()
    {
        // GQL-6: Unconditional DENY consent blocks access
        var accessor = CreateAccessor("S-1-5-21-USER1", "tenant-1", roles: ["Analyst"]);
        var table = new TableIdentifier("sales", "dbo", "orders");

        var denyConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId("tenant-1"),
            TableIdentifier = table,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER1"),
            RowFilters = [],
            ColumnRules = []
        };

        _consentRepo.GetActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(),
            table,
            Arg.Any<DateTimeOffset>(),
            new TenantId("tenant-1"),
            Arg.Any<CancellationToken>())
            .Returns(new[] { denyConsent });

        _ownershipRepo.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-USER1"), Arg.Any<CancellationToken>())
            .Returns(false);

        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            _query.GetTableConsumersAsync(
                "sales", "dbo", "orders", 30,
                _lineageService,
                accessor,
                _consentRepo,
                _ownershipRepo));

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN");
    }

    [Fact]
    public async Task TableConsumers_CallerWithActiveAllowConsent_Succeeds()
    {
        // GQL-6: Active Allow consent grants access to consumer analyses
        var accessor = CreateAccessor("S-1-5-21-USER1", "tenant-1", roles: ["Analyst"]);
        var table = new TableIdentifier("sales", "dbo", "orders");

        var allowConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId("tenant-1"),
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER1"),
            RowFilters = [],
            ColumnRules = []
        };

        _consentRepo.GetActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(),
            table,
            Arg.Any<DateTimeOffset>(),
            new TenantId("tenant-1"),
            Arg.Any<CancellationToken>())
            .Returns(new[] { allowConsent });

        var expectedReport = new TableConsumersReport("sales.dbo.orders", "LOW", 0, 0, null, [], [], []);
        _lineageService.GetTableConsumersAsync(table, 30, Arg.Any<CallerSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(expectedReport);

        var report = await _query.GetTableConsumersAsync(
            "sales", "dbo", "orders", 30,
            _lineageService,
            accessor,
            _consentRepo,
            _ownershipRepo);

        report.ShouldBe(expectedReport);
    }

    [Fact]
    public async Task TableConsumers_CallerIsDataOwner_SucceedsWithoutConsent()
    {
        // GQL-6: Data owner / approver has access even without active consent record
        var accessor = CreateAccessor("S-1-5-21-OWNER", "tenant-1", roles: ["Analyst"]);
        var table = new TableIdentifier("sales", "dbo", "orders");

        _ownershipRepo.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-OWNER"), Arg.Any<CancellationToken>())
            .Returns(true);

        var expectedReport = new TableConsumersReport("sales.dbo.orders", "LOW", 0, 0, null, [], [], []);
        _lineageService.GetTableConsumersAsync(table, 30, Arg.Any<CallerSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(expectedReport);

        var report = await _query.GetTableConsumersAsync(
            "sales", "dbo", "orders", 30,
            _lineageService,
            accessor,
            _consentRepo,
            _ownershipRepo);

        report.ShouldBe(expectedReport);
    }

    [Fact]
    public async Task TableConsumers_CallerIsGovernanceAdmin_BypassesConsentCheck()
    {
        // GQL-6: GovernanceAdmin has access without explicit consent
        var accessor = CreateAccessor("S-1-5-21-ADMIN", "tenant-1", roles: ["GovernanceAdmin"]);
        var table = new TableIdentifier("sales", "dbo", "orders");

        var expectedReport = new TableConsumersReport("sales.dbo.orders", "LOW", 0, 0, null, [], [], []);
        _lineageService.GetTableConsumersAsync(table, 30, Arg.Any<CallerSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(expectedReport);

        var report = await _query.GetTableConsumersAsync(
            "sales", "dbo", "orders", 30,
            _lineageService,
            accessor,
            _consentRepo,
            _ownershipRepo);

        report.ShouldBe(expectedReport);
        await _consentRepo.DidNotReceiveWithAnyArgs().GetActiveConsentsForSubjectsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task TableConsumers_CallerIsClusterAdmin_BypassesConsentAndTenantCheck()
    {
        // GQL-6: ClusterAdmin has cross-tenant access without explicit consent
        var accessor = CreateAccessor("S-1-5-21-CLUSTER-ADMIN", "tenant-2", roles: ["ClusterAdmin"]);
        var table = new TableIdentifier("sales", "dbo", "orders");

        var expectedReport = new TableConsumersReport("sales.dbo.orders", "LOW", 0, 0, null, [], [], []);
        _lineageService.GetTableConsumersAsync(table, 30, Arg.Any<CallerSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(expectedReport);

        var report = await _query.GetTableConsumersAsync(
            "sales", "dbo", "orders", 30,
            _lineageService,
            accessor,
            _consentRepo,
            _ownershipRepo);

        report.ShouldBe(expectedReport);
        await _consentRepo.DidNotReceiveWithAnyArgs().GetActiveConsentsForSubjectsAsync(default!, default!, default, default, default);
    }
}
