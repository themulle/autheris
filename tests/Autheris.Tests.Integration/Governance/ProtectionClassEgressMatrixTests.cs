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
public sealed class ProtectionClassEgressMatrixTests(GovernancePostgresFixture fixture)
{
    public static TheoryData<ProtectionClass, AccessTransport, bool, bool> Matrix() => new()
    {
        { ProtectionClass.Public, AccessTransport.Rest, false, true },
        { ProtectionClass.Public, AccessTransport.GraphQl, false, true },
        { ProtectionClass.Public, AccessTransport.OData, false, true },
        { ProtectionClass.Public, AccessTransport.WebSql, false, true },
        { ProtectionClass.Public, AccessTransport.Streaming, false, true },
        { ProtectionClass.Internal, AccessTransport.Rest, false, false },
        { ProtectionClass.Internal, AccessTransport.Rest, true, true },
        { ProtectionClass.Confidential, AccessTransport.Rest, true, false },
        { ProtectionClass.Secret, AccessTransport.Streaming, true, false }
    };

    [RequiresContainerRuntimeTheory]
    [MemberData(nameof(Matrix))]
    public async Task Protection_matrix_enforced_across_transports(
        ProtectionClass tableClass,
        AccessTransport transport,
        bool hasInternalEmployeeGroup,
        bool expectedAllowed)
    {
        const string employeeGroupSid = "S-1-5-21-EMP-1";
        // Internal access is proven only through a configured employee group (the test subject carries no member claims).
        await using var store = await GovernanceTestStore.CreatePostgreSqlAsync(
            fixture,
            "protection_egress_matrix",
            configure: o => o.Governance.AccessPolicy.Internal.EmployeeGroupSids.Add(employeeGroupSid));
        if (!store.IsAvailable) return;

        var tableId = Guid.NewGuid();
        var identifier = new TableIdentifier("crm", "public", $"matrix_{tableClass}_{transport}");
        var metadata = new TableMetadata
        {
            Identifier = identifier,
            OwnerTenantId = store.TenantA.Value,
            Table = new Table
            {
                Id = tableId,
                SourceName = "crm",
                SchemaName = "public",
                TableName = identifier.TableName,
                IsActive = true
            },
            Columns = [new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" }]
        };
        await store.TableMetadata.UpsertTableMetadataAsync(metadata);
        await store.GovernanceDomains.SetTableProtectionAsync(
            store.TenantA,
            tableId,
            new ObjectProtection(tableClass, null),
            clearColumns: null,
            new Sid("S-1-5-21-ADM-1"),
            CancellationToken.None);

        var empSid = new Sid(employeeGroupSid);
        var subject = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-CALLER-1"),
            ResolvedTenant = store.TenantA,
            GroupSids = hasInternalEmployeeGroup ? new HashSet<Sid> { empSid } : new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = true
        };

        var pdp = store.CreateAccessDecisionPoint();
        var decision = await pdp.DecideAsync(
            new AccessRequest(subject, metadata, AccessOperation.Read, transport),
            CancellationToken.None);

        decision.IsAllowed.ShouldBe(expectedAllowed);
    }
}
