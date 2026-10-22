namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Lineage;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class LineageCallerContextTests
{
    private static IHttpContextAccessor CreateAccessor(string authType, string tenant, string sid, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new(ClaimTypes.NameIdentifier, sid),
            new("tenant_id", tenant)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, authType, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        return accessor;
    }

    private static CallerSecurityContext CreateCallerContext(IHttpContextAccessor accessor)
    {
        var principal = accessor.HttpContext?.User ?? new ClaimsPrincipal();
        var userSid = principal.GetUserSid() ?? new Sid("S-1-5-21-ANONYMOUS");
        var groupSids = principal.GetGroupSids().ToList();
        var roles = principal.GetUserRoles().ToList();
        var tenantId = principal.GetTenantId();
        bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
        bool isClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);

        return new CallerSecurityContext(
            userSid,
            groupSids,
            roles,
            tenantId,
            isGovAdmin,
            isClusterAdmin);
    }

    [Fact]
    public async Task ForwardAuthClusterAdmin_RevocationImpactForeignTenantConsent_Denied()
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var ownershipRepo = Substitute.For<IDataOwnershipRepository>();
        var graphStore = new LineageGraphStore();
        var sut = new LineageImpactAnalyzerService(consentRepo, ownershipRepo, graphStore, NullLogger<LineageImpactAnalyzerService>.Instance);

        var consentId = Guid.NewGuid();
        var consent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("finance", "dbo", "orders"),
            TenantId = new TenantId("foreign-tenant"),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER-1"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(consent);

        // ForwardAuth asserting ClusterAdmin is non-canonical -> IsClusterAdmin is false
        var accessor = CreateAccessor("ForwardAuth", "own-tenant", "S-1-5-21-ATTACKER", "ClusterAdmin");
        var callerContext = CreateCallerContext(accessor);
        callerContext.IsClusterAdmin.ShouldBeFalse();

        await Should.ThrowAsync<KeyNotFoundException>(
            () => sut.CalculateConsentRevocationImpactAsync(new TenantId("own-tenant"), consentId, callerContext));
    }

    [Fact]
    public async Task CanonicalClusterAdmin_ForeignTenant_Allowed()
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var ownershipRepo = Substitute.For<IDataOwnershipRepository>();
        var graphStore = new LineageGraphStore();
        ownershipRepo.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var sut = new LineageImpactAnalyzerService(consentRepo, ownershipRepo, graphStore, NullLogger<LineageImpactAnalyzerService>.Instance);

        var consentId = Guid.NewGuid();
        var consent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("finance", "dbo", "orders"),
            TenantId = new TenantId("foreign-tenant"),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER-1"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(consent);

        // Bearer auth with ClusterAdmin is canonical -> IsClusterAdmin is true
        var accessor = CreateAccessor("Bearer", "own-tenant", "S-1-5-21-ADMIN", "ClusterAdmin");
        var callerContext = CreateCallerContext(accessor);
        callerContext.IsClusterAdmin.ShouldBeTrue();

        var result = await sut.CalculateConsentRevocationImpactAsync(new TenantId("own-tenant"), consentId, callerContext);
        result.ShouldNotBeNull();
        result.Severity.ShouldNotBeNull();
    }

    [Fact]
    public async Task ForwardAuthClusterAdmin_TableConsumers_OwnerEmailsHidden()
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var ownershipRepo = Substitute.For<IDataOwnershipRepository>();
        var graphStore = new LineageGraphStore();
        var sut = new LineageImpactAnalyzerService(consentRepo, ownershipRepo, graphStore, NullLogger<LineageImpactAnalyzerService>.Instance);

        var tableId = new TableIdentifier("finance", "dbo", "orders");
        var rootNode = new LineageNode(tableId.ToString(), "orders", LineageNodeType.Table, ["downstream_app"], "Finance Team", "owner@corp.local");
        var childNode = new LineageNode("downstream_app", "downstream_app", LineageNodeType.ExternalService, [], "Ops Team", "ops@corp.local");
        graphStore.UpdateGraph([rootNode, childNode]);

        // ForwardAuth caller has IsClusterAdmin = false and is not data owner
        var accessor = CreateAccessor("ForwardAuth", "own-tenant", "S-1-5-21-USER", "ClusterAdmin");
        var callerContext = CreateCallerContext(accessor);

        var report = await sut.GetTableConsumersAsync(tableId, 30, callerContext);
        report.DownstreamConsumers.Count.ShouldBeGreaterThan(0);
        foreach (var consumer in report.DownstreamConsumers)
        {
            consumer.OwnerEmail.ShouldBeNull();
        }
    }

    [Fact]
    public async Task ForwardAuthClusterAdmin_GdprReportForeignSubject_Denied()
    {
        var lineageService = Substitute.For<ILineageImpactAnalyzerService>();
        var query = new Query();
        var accessor = CreateAccessor("ForwardAuth", "own-tenant", "S-1-5-21-CALLER", "ClusterAdmin");

        var ex = await Should.ThrowAsync<GraphQLException>(() => query.GetGdprDataDisclosureReportAsync(
            domain: "finance",
            schema: "dbo",
            tableName: "orders",
            subjectSid: "S-1-5-21-FOREIGN-SUBJECT",
            timeWindowDays: 30,
            lineageService: lineageService,
            httpContextAccessor: accessor));

        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        await lineageService.DidNotReceiveWithAnyArgs().GetGdprDataDisclosureReportAsync(default, default, default, default, default);
    }

    [Fact]
    public void Architecture_GraphQL_NoRawClusterAdminRoleChecks()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(System.IO.Path.Combine(dir, "Autheris.sln")))
        {
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        dir.ShouldNotBeNull("Must locate solution root");
        var gqlDir = System.IO.Path.Combine(dir!, "src", "Autheris.GraphQL");
        Directory.Exists(gqlDir).ShouldBeTrue();

        var csFiles = Directory.GetFiles(gqlDir, "*.cs", SearchOption.AllDirectories);
        csFiles.Length.ShouldBeGreaterThan(0);

        var rawPattern = new Regex(
            @"(IsInRole\s*\(\s*""ClusterAdmin""\s*\)|" +
            @"(roles|r)\.Contains\s*\(\s*""ClusterAdmin""|" +
            @"(roles|r)\.Equals\s*\(\s*""ClusterAdmin""|" +
            @"(role|r)\s*==\s*""ClusterAdmin"")",
            RegexOptions.Compiled);

        var violations = new List<string>();

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//") || line.StartsWith("///") || line.StartsWith('*'))
                {
                    continue;
                }

                if (rawPattern.IsMatch(line))
                {
                    violations.Add($"{System.IO.Path.GetFileName(file)}:{i + 1}: {line}");
                }
            }
        }

        violations.ShouldBeEmpty("src/Autheris.GraphQL must not contain raw ClusterAdmin role string checks; use ClusterAdminPolicy.IsCanonicalClusterAdmin.");
    }
}
