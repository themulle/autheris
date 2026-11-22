namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.Governance.Contract;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Collection(GovernancePostgresCollection.Name)]
[Trait("Category", "Security")]
public sealed class ProtectionFailClosedIntegrationTests(GovernancePostgresFixture fixture)
{
    [RequiresContainerRuntimeFact]
    public async Task Unclassified_table_requires_grant()
    {
        await using var store = await GovernanceTestStore.CreatePostgreSqlAsync(fixture, "protection_fail_closed");
        if (!store.IsAvailable) return;

        var tableId = Guid.NewGuid();
        var identifier = new TableIdentifier("crm", "public", "unclassified_orders");
        var metadata = new TableMetadata
        {
            Identifier = identifier,
            OwnerTenantId = store.TenantA.Value,
            Table = new Table
            {
                Id = tableId,
                SourceName = "crm",
                SchemaName = "public",
                TableName = "unclassified_orders",
                IsActive = true
            },
            Columns = [new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" }]
        };
        await store.TableMetadata.UpsertTableMetadataAsync(metadata);

        var callerSid = new Sid("S-1-5-21-INT-1");
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, callerSid.Value),
            new("tenant_id", store.TenantA.Value),
            new("tid", "11111111-1111-1111-1111-111111111111"),
            new("acct", "0"),
            new("autheris:realm_kind", nameof(IdentityRealmKind.EntraWorkforce))
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        var subject = SecurityPrincipalContext.FromPrincipal(principal, store.TenantA) with
        {
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            RealmKind = IdentityRealmKind.EntraWorkforce
        };

        var pdp = store.Services.GetRequiredService<IAccessDecisionPoint>();
        var decision = await pdp.DecideAsync(
            new AccessRequest(subject, metadata, AccessOperation.Read, AccessTransport.Rest),
            CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.Reasons.ShouldContain(r => r.Step == DecisionStep.Protection && r.Code == "PROTECTION_CLASS_GRANT_REQUIRED");
    }
}
