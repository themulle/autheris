using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.Governance.Contract;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

[Collection(GovernancePostgresCollection.Name)]
[Trait("Category", "Security")]
public sealed class GovernanceDomainEndpointsTests(GovernancePostgresFixture fixture)
{
    private static readonly Sid GovSid = new("S-1-5-21-1001-10");
    private static readonly Sid ItSid = new("S-1-5-21-1001-20");
    private static readonly Sid OwnerSid = new("S-1-5-21-1001-30");
    private static readonly Sid OwnerGroupSid = new("S-1-5-21-1001-512");

    private static HttpContext BuildHttp(TenantId tenant, Sid sid, string role, IReadOnlySet<Sid>? groups = null, string? ifMatch = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid.Value),
            new("tenant_id", tenant.Value),
            new(ClaimTypes.Role, role)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        var http = new DefaultHttpContext { User = principal };
        http.Items[SecurityPrincipalContext.ItemKey] = SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: tenant) with
        {
            GroupSids = groups ?? new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete
        };
        http.Items[SecurityPrincipalContext.TenantIdItemKey] = tenant;
        if (ifMatch is not null)
        {
            http.Request.Headers.IfMatch = ifMatch;
        }

        return http;
    }

    [RequiresContainerRuntimeFact]
    public async Task It_cannot_assign_owner_or_classify()
    {
        await using var store = await GovernanceTestStore.CreatePostgreSqlAsync(fixture, "domain_endpoints_it");
        if (!store.IsAvailable) return;

        var tableId = Guid.NewGuid();
        var tableRef = new TableIdentifier("crm", "public", "customers");
        await store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableRef,
            OwnerTenantId = store.TenantA.Value,
            Table = new Table
            {
                Id = tableId,
                SourceName = "crm",
                SchemaName = "public",
                TableName = "customers",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { TableId = tableId, ColumnName = "email", DataType = "varchar" }
            ]
        });

        var auditWriter = store.Services.GetRequiredService<IAuditWriter>();
        var caps = store.Services.GetRequiredService<IGovernanceCapabilities>();
        var policyProvider = store.Services.GetRequiredService<IAccessPolicySettingsProvider>();
        var domainService = new GovernanceDomainService(
            store.GovernanceDomains,
            store.TableMetadata,
            store.Consents,
            auditWriter,
            caps,
            policyProvider);

        var govHttp = BuildHttp(store.TenantA, GovSid, "GovernanceAdmin");
        var createResult = await GovernanceDomainEndpoints.CreateDomainAsync(
            govHttp,
            new GovernanceDomainEndpoints.CreateDomainRequest("Customers", OwnerGroupSid: OwnerGroupSid.Value, DefaultClass: "CONFIDENTIAL", Visibility: "ON_REQUEST"),
            domainService,
            CancellationToken.None);
        (createResult as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status201Created);

        var domainsList = await store.GovernanceDomains.ListAsync(store.TenantA);
        var created = domainsList.Single(d => d.Name == "Customers");

        await GovernanceDomainEndpoints.AssignTablesAsync(
            govHttp,
            created.Id,
            new GovernanceDomainEndpoints.AssignTablesRequest([tableId]),
            domainService,
            CancellationToken.None);

        var itHttp = BuildHttp(store.TenantA, ItSid, "TenantAdmin");
        (await GovernanceDomainEndpoints.AssignOwnerGroupAsync(
            itHttp,
            created.Id,
            new GovernanceDomainEndpoints.AssignOwnerGroupRequest(OwnerGroupSid.Value),
            domainService,
            CancellationToken.None) as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        (await GovernanceDomainEndpoints.SetTableProtectionAsync(
            itHttp,
            tableRef.ToString(),
            new GovernanceDomainEndpoints.SetProtectionRequest("PUBLIC"),
            domainService,
            CancellationToken.None) as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        var ownerHttp = BuildHttp(store.TenantA, OwnerSid, "Consumer", new HashSet<Sid> { OwnerGroupSid });
        var ownerClassify = await GovernanceDomainEndpoints.SetColumnProtectionAsync(
            ownerHttp,
            tableRef.ToString(),
            "email",
            new GovernanceDomainEndpoints.SetProtectionRequest("SECRET", "PII_EMAIL"),
            domainService,
            CancellationToken.None);
        (ownerClassify as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var (tableProt, colProts) = await store.GovernanceDomains.GetProtectionAsync(store.TenantA, tableId);
        tableProt.ShouldBeNull();
        colProts["email"].Class.ShouldBe(ProtectionClass.Secret);
        colProts["email"].MaskCategory.ShouldBe("PII_EMAIL");
    }
}
