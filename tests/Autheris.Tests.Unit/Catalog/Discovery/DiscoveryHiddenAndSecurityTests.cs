namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Discovery;
using Autheris.Domain.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class DiscoveryHiddenAndSecurityTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenObject_IsAbsentAcrossAllChannels_WithIdenticalMissingResponse()
    {
        var hiddenTable = DiscoveryTestBuilder.MakeTable("finance", "payroll", ownerTenantId: "tenant-a", hidden: true);
        var missingId = new TableIdentifier("finance", "dbo", "does_not_exist");

        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(hiddenTable);
        var audit = new FakeAuditWriter();
        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, audit: audit);

        var callerPrincipal = DiscoveryTestBuilder.User("S-USER", "tenant-a", scopes: "Catalog.ReadAdmin");
        var reqContext = new RequestContext(callerPrincipal, new TenantId("tenant-a"));
        var caller = DiscoveryTestBuilder.Caller(callerPrincipal);

        // Discovery core
        (await discovery.GetAsync(hiddenTable.Identifier, caller)).ShouldBeNull();
        (await discovery.GetAsync(missingId, caller)).ShouldBeNull();
        (await discovery.ListAsync(new DiscoveryQuery(Surface: DiscoverySurface.Rest), caller)).Items.ShouldBeEmpty();
        (await discovery.SearchAsync("payroll", new DiscoverySearchOptions(Surface: DiscoverySurface.Search), caller)).ShouldBeEmpty();

        // REST channel helper
        var catalogAppService = new CatalogDiscoveryService(
            repo,
            audit,
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            logger: NullLogger<CatalogDiscoveryService>.Instance,
            discoveryService: discovery);

        static string? NoLang(HttpContext _) => null;
        RequestContext Ctx(HttpContext _, string? lang) => reqContext;
        var restHidden = await CatalogDiscoveryApiHelpers.GetDatasetDetailAsync(
            "finance/dbo/payroll", catalogAppService, new DefaultHttpContext { User = callerPrincipal }, NoLang, Ctx, CancellationToken.None);
        var restMissing = await CatalogDiscoveryApiHelpers.GetDatasetDetailAsync(
            "finance/dbo/does_not_exist", catalogAppService, new DefaultHttpContext { User = callerPrincipal }, NoLang, Ctx, CancellationToken.None);
        var hiddenProblem = restHidden.ShouldBeOfType<ProblemHttpResult>();
        var missingProblem = restMissing.ShouldBeOfType<ProblemHttpResult>();
        hiddenProblem.StatusCode.ShouldBe(404);
        missingProblem.StatusCode.ShouldBe(404);
        hiddenProblem.ProblemDetails.Title.ShouldBe(missingProblem.ProblemDetails.Title);
        hiddenProblem.ProblemDetails.Detail.ShouldBe(missingProblem.ProblemDetails.Detail);

        // GraphQL channel
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = callerPrincipal } };
        var gqlHidden = await new Query().GetCatalogObjectAsync("finance", "dbo", "payroll", discoveryService: discovery, httpContextAccessor: accessor);
        var gqlMissing = await new Query().GetCatalogObjectAsync("finance", "dbo", "does_not_exist", discoveryService: discovery, httpContextAccessor: accessor);
        gqlHidden.ShouldBeNull();
        gqlMissing.ShouldBeNull();

        // MCP channel
        var mcp = new McpDatasetCatalog(
            repo,
            new FakeDiscoveryConsentRepository(),
            new DummyExecutionService(),
            discoveryService: discovery);
        await Should.ThrowAsync<TableNotFoundException>(async () =>
            await mcp.DescribeDatasetAsync(callerPrincipal, "finance.dbo.payroll"));
        await Should.ThrowAsync<TableNotFoundException>(async () =>
            await mcp.DescribeDatasetAsync(callerPrincipal, "finance.dbo.does_not_exist"));

        // Owner/GovernanceAdmin sees hidden table with IsHidden = true
        var admin = DiscoveryTestBuilder.User("S-ADMIN", "tenant-a", roles: ["GovernanceAdmin"]);
        var adminFound = await discovery.GetAsync(hiddenTable.Identifier, DiscoveryTestBuilder.Caller(admin));
        adminFound.ShouldNotBeNull();
        adminFound!.IsHidden.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Discovery_IssuesNoDataSourceQuery_AndSanitizesAndCapsUntrustedText()
    {
        var controlChars = "Title\u0000\u202EInjected\u0007";
        var longDesc = new string('x', DiscoveryTextSanitizer.MaxDescriptionRunes + 500);
        var table = DiscoveryTestBuilder.MakeTable("sales", "orders");
        table = new TableMetadata
        {
            Identifier = table.Identifier,
            Table = new Table
            {
                Id = table.Table.Id,
                SourceName = table.Table.SourceName,
                SchemaName = table.Table.SchemaName,
                TableName = table.Table.TableName,
                DisplayName = controlChars,
                Description = longDesc,
                DataSourceType = table.Table.DataSourceType,
                IsActive = true,
                Sensitivity = table.Table.Sensitivity
            },
            Columns = table.Columns,
            OwnerTenantId = table.OwnerTenantId,
            CatalogTags = table.CatalogTags
        };

        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(table);
        var pdp = FakeAccessDecisionPoint.AllowAll();
        var audit = new FakeAuditWriter();
        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp, audit: audit);

        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-1", "tenant-a"));
        var obj = await discovery.GetAsync(table.Identifier, caller);

        obj.ShouldNotBeNull();
        obj.DisplayName.ShouldNotBeNull();
        obj.DisplayName.ShouldNotContain("\u0000");
        obj.DisplayName.ShouldNotContain("\u202E");
        obj.Description!.Length.ShouldBeLessThanOrEqualTo(DiscoveryTextSanitizer.MaxDescriptionRunes);

        // Search hit and audit trail
        var page = await discovery.ListAsync(new DiscoveryQuery(Search: "orders", Surface: DiscoverySurface.Search), caller);
        page.Items.ShouldHaveSingleItem();
        audit.Entries.Count.ShouldBeGreaterThanOrEqualTo(2);
        var entry = audit.Entries[^1];
        entry.EventType.ShouldBe(AuditEventTypes.CatalogRead);
        entry.TargetTable.ShouldBe("sales.dbo.orders");
        using var doc = JsonDocument.Parse(entry.DetailsJson);
        doc.RootElement.TryGetProperty("queryHash", out var qh).ShouldBeTrue();
        qh.GetString()!.ShouldNotContain("orders");
        qh.GetString()!.Length.ShouldBe(64);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AnonymousDiscovery_AndAuditFailure_AndFlagReadFailure_FailClosed()
    {
        var table = DiscoveryTestBuilder.MakeTable("sales", "orders");
        var repo = new FakeDiscoveryTableRepository { ThrowOnSettingsRead = true };
        repo.Tables.Add(table);
        var audit = new FakeAuditWriter();
        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, audit: audit);

        var anonEx = await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await discovery.ListAsync(new DiscoveryQuery(), DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.Anonymous())));
        anonEx.Kind.ShouldBe(DiscoveryFailureKind.Unauthenticated);

        // Repository failure fails closed without leaking exception
        var user = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-1", "tenant-a"));
        repo.FailWith = new InvalidOperationException("connection string leaked here");
        (await discovery.GetAsync(table.Identifier, user)).ShouldBeNull();
        var list = await discovery.ListAsync(new DiscoveryQuery(), user);
        list.Items.ShouldBeEmpty();

        // Audit failure fails closed without returning objects
        repo.FailWith = null;
        audit.FailWithUnavailable = true;
        var auditFailPage = await discovery.ListAsync(new DiscoveryQuery(), user);
        auditFailPage.Items.ShouldBeEmpty();
        (await discovery.GetAsync(table.Identifier, user)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task KeysetAndFacets_Anonymous_RepoFailure_AndAuditFailure_FailClosed()
    {
        var table = DiscoveryTestBuilder.MakeTable("sales", "orders");
        var repo = new FakeDiscoveryTableRepository { ThrowOnSettingsRead = true };
        repo.Tables.Add(table);
        var audit = new FakeAuditWriter();
        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, audit: audit);

        var anonEx1 = await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await discovery.ListKeysetAsync(new DiscoveryKeysetQuery(), DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.Anonymous())));
        anonEx1.Kind.ShouldBe(DiscoveryFailureKind.Unauthenticated);

        var anonEx2 = await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await discovery.FacetsAsync(null, DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.Anonymous())));
        anonEx2.Kind.ShouldBe(DiscoveryFailureKind.Unauthenticated);

        var user = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-1", "tenant-a"));
        repo.FailWith = new InvalidOperationException("connection string leaked here");
        var repoFailKeyset = await discovery.ListKeysetAsync(new DiscoveryKeysetQuery(), user);
        repoFailKeyset.Items.ShouldBeEmpty();
        repoFailKeyset.TotalCount.ShouldBe(0);
        repoFailKeyset.NextKey.ShouldBeNull();
        var repoFailFacets = await discovery.FacetsAsync(null, user);
        repoFailFacets.TotalCount.ShouldBe(0);
        repoFailFacets.Domains.ShouldBeEmpty();

        repo.FailWith = null;
        audit.FailWithUnavailable = true;
        var auditFailKeyset = await discovery.ListKeysetAsync(new DiscoveryKeysetQuery(), user);
        auditFailKeyset.Items.ShouldBeEmpty();
        auditFailKeyset.NextKey.ShouldBeNull();
        var auditFailFacets = await discovery.FacetsAsync(null, user);
        auditFailFacets.TotalCount.ShouldBe(0);
        auditFailFacets.Domains.ShouldBeEmpty();
    }

    private sealed class DummyExecutionService : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(ClaimsPrincipal? principal, TableIdentifier table, int? first = null, int? after = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(ClaimsPrincipal? principal, TableIdentifier table, int? first, int? after, IReadOnlyDictionary<string, object?>? queryArguments, IReadOnlyList<string>? requestedFields = null, IReadOnlyDictionary<string, string[]>? requestHeaders = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
