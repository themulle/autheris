using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Api;

[Trait("Category", "Security")]
public sealed class GovernanceClassificationEndpointsReviewerTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid OwnerSid = new("S-1-5-21-1001-30");
    private static readonly Sid OwnerGroupSid = new("S-1-5-21-1001-512");
    private static readonly TableIdentifier CustomersTable = new("crm", "public", "customers");

    private sealed class StubDomainRepo(Guid domainId) : IGovernanceDomainRepository
    {
        public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GovernanceDomain>>([]);
        public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult<GovernanceDomain?>(id == domainId
                ? new GovernanceDomain(domainId, TenantA, "Sales", null, null, OwnerGroupSid, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                : null);
        public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(TenantId tenant, Guid id, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AssignTablesAsync(TenantId tenant, Guid id, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetTableProtectionAsync(TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
            Task.FromResult(new ContainerDefaults(
                new ContainerRef(ContainerKind.Domain, domainId),
                OwnerGroupSid,
                ProtectionClass.Confidential,
                CatalogVisibilityMode.OnRequest,
                IsValid: true));
    }

    [Fact]
    public async Task ResolveReviewerAsync_Prefers_PreResolved_HttpContext_SecurityContext_Without_GroupResolutionClaims()
    {
        var domainId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var domains = new StubDomainRepo(domainId);
        var tables = new InMemoryTableMetadataRepository(new TableMetadata
        {
            Identifier = CustomersTable,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = tableId, SourceName = "crm", SchemaName = "public", TableName = "customers", IsActive = true },
            Columns = [new TableColumn { ColumnName = "email", DataType = "varchar" }]
        });
        var capabilities = new GovernanceCapabilities(domainRepository: domains);
        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            CustomersTable,
            OwnerSid.Value,
            "PENDING_OWNER_REVIEW",
            [],
            DateTimeOffset.UtcNow);

        // ClaimsPrincipal intentionally lacks GroupResolutionClaims; groups are supplied only via pre-resolved HttpContext.Items.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, OwnerSid.Value),
            new Claim("tenant_id", TenantA.Value),
            new Claim(ClaimTypes.Role, "Consumer")
        ], "Bearer"));

        var withoutItem = new DefaultHttpContext { User = principal };
        var denied = await GovernanceClassificationEndpoints.ResolveReviewerAsync(
            withoutItem,
            proposal,
            capabilities,
            domains,
            tables,
            CancellationToken.None);
        (denied.Error as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        var withPreResolved = new DefaultHttpContext { User = principal };
        withPreResolved.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = OwnerSid,
            ResolvedTenant = TenantA,
            GroupSids = new HashSet<Sid> { OwnerGroupSid },
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string> { "Consumer" },
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = true,
            Principal = principal
        };

        var resolved = await GovernanceClassificationEndpoints.ResolveReviewerAsync(
            withPreResolved,
            proposal,
            capabilities,
            domains,
            tables,
            CancellationToken.None);

        resolved.Error.ShouldBeNull();
        resolved.Sid.ShouldBe(OwnerSid);
        resolved.Role.ShouldBe("DATA_OWNER");
    }
}
