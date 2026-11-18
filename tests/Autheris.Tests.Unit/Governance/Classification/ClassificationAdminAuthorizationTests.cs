namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;
using Autheris.Tests.Common.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ClassificationAdminAuthorizationTests
{
    private const string ColumnId = "col-email";
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier Customers = new("crm", "public", "customers");
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static ClassificationAdminCaller ClusterAdmin() => new(TenantA, "S-1-CLUSTER", IsClusterAdmin: true, IsGovernanceAdmin: true);
    private static ClassificationAdminCaller GovernanceAdmin(string tenant = "tenant-a") => new(new TenantId(tenant), "S-1-GOV", IsClusterAdmin: false, IsGovernanceAdmin: true);
    private static ClassificationAdminCaller Owner() => new(TenantA, "S-1-OWNER", IsClusterAdmin: false, IsGovernanceAdmin: false);
    private static ClassificationAdminCaller Stranger() => new(TenantA, "S-1-STRANGER", IsClusterAdmin: false, IsGovernanceAdmin: false);

    private static (ClassificationAdminService Service, TestInMemoryClassificationStore Store) Build(string? tableOwnerTenant = null)
    {
        var store = new TestInMemoryClassificationStore(TenantA.Value);
        store.ColumnTables[ColumnId] = Customers;

        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Sid>(1).Value == "S-1-OWNER");

        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = Customers,
            OwnerTenantId = tableOwnerTenant,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "crm", SchemaName = "public", TableName = "customers", IsActive = true }
        });

        return (new ClassificationAdminService(store, ownership, tables, new FakeTimeProvider(Now)), store);
    }

    private static ClassificationDefinitionInput Definition(string name = "Custom") =>
        new(name, null, null, 50, true, true, ApproverMode.DataOwner, null, 30);

    private static MaskRuleInput Rule(CatalogTextVisibility text = CatalogTextVisibility.Visible) =>
        new(MaskFunction.Hash, null, "kv-pii", text);

    [Fact]
    public async Task Star_scope_write_requires_cluster_admin()
    {
        var (service, store) = Build();

        var denied = await service.UpsertClassificationAsync(GovernanceAdmin(), "CUSTOM", Definition(), ClassificationAdminScope.Global, null);
        denied.Status.ShouldBe(ClassificationAdminStatus.Forbidden);
        denied.ErrorCode.ShouldBe(ClassificationAdminErrorCodes.ScopeForbidden);
        store.Definitions.ContainsKey("CUSTOM").ShouldBeFalse();

        (await service.UpsertMaskRuleAsync(GovernanceAdmin(), "PII", Rule(), ClassificationAdminScope.Global, null))
            .Status.ShouldBe(ClassificationAdminStatus.Forbidden);
        (await service.AssignColumnClassificationAsync(GovernanceAdmin(), ColumnId, "PII", ClassificationAdminScope.Global))
            .Status.ShouldBe(ClassificationAdminStatus.Forbidden);
        (await service.UpsertMaskOverrideAsync(GovernanceAdmin(), ColumnId, "PII", Rule(), ClassificationAdminScope.Global))
            .Status.ShouldBe(ClassificationAdminStatus.Forbidden);
        (await service.RemoveColumnClassificationAsync(GovernanceAdmin(), ColumnId, "PII", ClassificationAdminScope.Global))
            .Status.ShouldBe(ClassificationAdminStatus.Forbidden);
        (await service.DeleteMaskOverrideAsync(GovernanceAdmin(), ColumnId, null, ClassificationAdminScope.Global))
            .Status.ShouldBe(ClassificationAdminStatus.Forbidden);
        store.ColumnClassifications.ShouldBeEmpty();
        store.ColumnMaskOverrides.ShouldBeEmpty();

        var allowed = await service.UpsertClassificationAsync(ClusterAdmin(), "CUSTOM", Definition(), ClassificationAdminScope.Global, null);
        allowed.Status.ShouldBe(ClassificationAdminStatus.Ok);
        allowed.Value!.Scope.ShouldBe(ClassificationScopes.Global);
        allowed.Value.UpdatedBy.ShouldBe("S-1-CLUSTER");
    }

    [Fact]
    public async Task Tenant_governance_admin_writes_are_bound_to_the_authenticated_tenant()
    {
        var (service, store) = Build();

        var own = await service.UpsertClassificationAsync(GovernanceAdmin(), "CUSTOM", Definition(), ClassificationAdminScope.Tenant, null);
        own.Status.ShouldBe(ClassificationAdminStatus.Ok);
        own.Value!.Scope.ShouldBe("tenant-a");

        var other = await service.UpsertClassificationAsync(GovernanceAdmin(), "OTHER", Definition(), ClassificationAdminScope.Tenant, "tenant-b");
        other.Status.ShouldBe(ClassificationAdminStatus.NotFound);
        store.Definitions.ContainsKey("OTHER").ShouldBeFalse();

        var byCluster = await service.UpsertClassificationAsync(ClusterAdmin(), "OTHER", Definition(), ClassificationAdminScope.Tenant, "tenant-b");
        byCluster.Status.ShouldBe(ClassificationAdminStatus.Ok);
        byCluster.Value!.Scope.ShouldBe("tenant-b");
    }

    [Fact]
    public async Task Cross_tenant_classification_read_returns_404()
    {
        var (service, _) = Build();

        var crossTenantRead = await service.GetClassificationAsync(GovernanceAdmin(), "PII", ClassificationAdminScope.Tenant, "tenant-b");
        crossTenantRead.Status.ShouldBe(ClassificationAdminStatus.NotFound);

        var crossTenantRule = await service.GetMaskRuleAsync(GovernanceAdmin(), "PII", ClassificationAdminScope.Tenant, "tenant-b");
        crossTenantRule.Status.ShouldBe(ClassificationAdminStatus.NotFound);

        var builtIn = await service.GetClassificationAsync(GovernanceAdmin(), "PII", ClassificationAdminScope.Global, null);
        builtIn.Status.ShouldBe(ClassificationAdminStatus.Ok);
        builtIn.Value!.Code.ShouldBe("PII");
    }

    [Fact]
    public async Task Vocabulary_and_rule_writes_require_governance_admin()
    {
        var (service, _) = Build();

        (await service.UpsertClassificationAsync(Owner(), "CUSTOM", Definition(), ClassificationAdminScope.Tenant, null))
            .Status.ShouldBe(ClassificationAdminStatus.Forbidden);
        (await service.UpsertMaskRuleAsync(Owner(), "PII", Rule(), ClassificationAdminScope.Tenant, null))
            .Status.ShouldBe(ClassificationAdminStatus.Forbidden);
        (await service.GetClassificationAsync(Stranger(), "PII", ClassificationAdminScope.Tenant, null))
            .Status.ShouldBe(ClassificationAdminStatus.Forbidden);
    }

    [Fact]
    public async Task Column_assignment_requires_owner_delegate_or_governance_admin()
    {
        var (service, store) = Build();

        var denied = await service.AssignColumnClassificationAsync(Stranger(), ColumnId, "PII", ClassificationAdminScope.Tenant);
        denied.Status.ShouldBe(ClassificationAdminStatus.Forbidden);
        store.ColumnClassifications.ShouldBeEmpty();

        var byOwner = await service.AssignColumnClassificationAsync(Owner(), ColumnId, "PII", ClassificationAdminScope.Tenant);
        byOwner.Status.ShouldBe(ClassificationAdminStatus.Ok);
        byOwner.Value!.Source.ShouldBe("OWNER");
        byOwner.Value.Scope.ShouldBe("tenant-a");
        byOwner.Value.AssignedBy.ShouldBe("S-1-OWNER");

        var byAdmin = await service.AssignColumnClassificationAsync(GovernanceAdmin(), ColumnId, "FINANCIAL", ClassificationAdminScope.Tenant);
        byAdmin.Status.ShouldBe(ClassificationAdminStatus.Ok);
        byAdmin.Value!.Source.ShouldBe("ADMIN");

        (await service.AssignColumnClassificationAsync(Owner(), ColumnId, "NOT_A_CODE", ClassificationAdminScope.Tenant))
            .Status.ShouldBe(ClassificationAdminStatus.Invalid);
        (await service.AssignColumnClassificationAsync(Owner(), "unknown-col", "PII", ClassificationAdminScope.Tenant))
            .Status.ShouldBe(ClassificationAdminStatus.NotFound);
    }

    [Fact]
    public async Task Column_of_another_tenants_table_is_not_found_for_a_tenant_admin()
    {
        var (service, store) = Build(tableOwnerTenant: "tenant-b");

        (await service.AssignColumnClassificationAsync(GovernanceAdmin(), ColumnId, "PII", ClassificationAdminScope.Tenant))
            .Status.ShouldBe(ClassificationAdminStatus.NotFound);
        store.ColumnClassifications.ShouldBeEmpty();

        (await service.AssignColumnClassificationAsync(ClusterAdmin(), ColumnId, "PII", ClassificationAdminScope.Tenant))
            .Status.ShouldBe(ClassificationAdminStatus.Ok);
    }

    [Fact]
    public async Task Removing_column_classification_requires_owner_or_admin_and_is_audited()
    {
        var (service, store) = Build();
        await service.AssignColumnClassificationAsync(GovernanceAdmin(), ColumnId, "PII", ClassificationAdminScope.Tenant);

        var denied = await service.RemoveColumnClassificationAsync(Stranger(), ColumnId, "PII", ClassificationAdminScope.Tenant);
        denied.Status.ShouldBe(ClassificationAdminStatus.Forbidden);
        store.ColumnClassifications.Count.ShouldBe(1);

        var removed = await service.RemoveColumnClassificationAsync(Owner(), ColumnId, "PII", ClassificationAdminScope.Tenant);
        removed.Status.ShouldBe(ClassificationAdminStatus.Ok);
        store.ColumnClassifications.ShouldBeEmpty();

        (await service.RemoveColumnClassificationAsync(Owner(), ColumnId, "PII", ClassificationAdminScope.Tenant))
            .Status.ShouldBe(ClassificationAdminStatus.NotFound);

        var removalRoute = AdminRoutes().Single(r => r.Method == "DELETE" && r.Pattern.EndsWith("/classifications/{code}", StringComparison.Ordinal));
        removalRoute.Audit.EventType.ShouldBe(AuditEventTypes.ColumnClassificationChanged);
        removalRoute.Audit.Level.ShouldBe(AuditLevel.Full);
    }

    [Fact]
    public async Task Mask_override_requires_owner_or_admin_and_supports_the_column_wide_code()
    {
        var (service, store) = Build();

        (await service.UpsertMaskOverrideAsync(Stranger(), ColumnId, "PII", Rule(), ClassificationAdminScope.Tenant))
            .Status.ShouldBe(ClassificationAdminStatus.Forbidden);

        var columnWide = await service.UpsertMaskOverrideAsync(
            Owner(), ColumnId, ClassificationAdminService.ParseOverrideCode("_all"), Rule(CatalogTextVisibility.Hidden), ClassificationAdminScope.Tenant);
        columnWide.Status.ShouldBe(ClassificationAdminStatus.Ok);
        columnWide.Value!.Code.ShouldBeNull();
        store.ColumnMaskOverrides.ShouldHaveSingleItem().CatalogText.ShouldBe(CatalogTextVisibility.Hidden);

        (await service.DeleteMaskOverrideAsync(Owner(), ColumnId, null, ClassificationAdminScope.Tenant))
            .Status.ShouldBe(ClassificationAdminStatus.Ok);
        store.ColumnMaskOverrides.ShouldBeEmpty();
    }

    [Fact]
    public void Every_admin_route_carries_authorization_and_an_audit_policy()
    {
        var routes = AdminRoutes();
        routes.Count.ShouldBe(8);
        routes.ShouldAllBe(r => r.Policy != null && r.Audit != null);

        routes.Where(r => r.Pattern.StartsWith("/api/v1/governance/", StringComparison.Ordinal))
            .ShouldAllBe(r => r.Policy == "GovernanceAdmin");
        routes.Where(r => r.Pattern.StartsWith("/api/v1/catalog/columns/", StringComparison.Ordinal))
            .ShouldAllBe(r => r.Policy == "Approver");

        routes.Where(r => r.Method is "PUT" or "DELETE").ShouldAllBe(r => r.Audit.Level == AuditLevel.Full);
        routes.Single(r => r.Method == "PUT" && r.Pattern.EndsWith("/mask-rule", StringComparison.Ordinal))
            .Audit.EventType.ShouldBe(AuditEventTypes.MaskRuleChanged);
        routes.Single(r => r.Method == "PUT" && r.Pattern.EndsWith("/mask-overrides/{code}", StringComparison.Ordinal))
            .Audit.EventType.ShouldBe(AuditEventTypes.MaskOverrideChanged);
        routes.Single(r => r.Method == "PUT" && r.Pattern == "/api/v1/governance/classifications/{code}")
            .Audit.EventType.ShouldBe(AuditEventTypes.ClassificationChanged);
        routes.Single(r => r.Method == "PUT" && r.Pattern.EndsWith("/classifications/{code}", StringComparison.Ordinal) && r.Pattern.Contains("/columns/", StringComparison.Ordinal))
            .Audit.EventType.ShouldBe(AuditEventTypes.ColumnClassificationChanged);
    }

    private sealed record AdminRoute(string Method, string Pattern, string? Policy, AuditPolicy Audit);

    private static System.Collections.Generic.List<AdminRoute> AdminRoutes()
    {
        using var routes = new TestEndpointRouteBuilder(permissiveServices: true);
        routes.MapClassificationAdminEndpoints();
        return routes.DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => new AdminRoute(
                e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Single(),
                e.RoutePattern.RawText!,
                e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).FirstOrDefault(),
                e.Metadata.GetMetadata<AuditPolicy>()!))
            .ToList();
    }
}
