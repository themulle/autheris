namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Domain.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Governance;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Drives the PDP with the real tenant access policy provider: the employee-group baseline for Internal tables
/// must come from the deployment access-policy options and is fail-closed when none is configured.
/// </summary>
[Trait("Category", "Security")]
public sealed class ProtectionEmployeeGroupBaselineTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly TableIdentifier TableId = new("crm", "public", "matrix_internal");
    private static readonly Sid EmpGroup = new("S-1-5-21-1000-2000-3000-1101");

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Internal_table_baseline_follows_configured_employee_group(bool groupConfigured, bool expectedAllowed)
    {
        var deployment = new AccessPolicyOptions();
        if (groupConfigured)
        {
            deployment.Internal.EmployeeGroupSids.Add(EmpGroup.Value);
        }

        var provider = new AccessPolicySettingsProvider(
            new EmptyPolicyRepo(),
            Options.Create(deployment),
            new InMemoryAuditWriter());

        var pdp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            new ConsentResolutionService(),
            new FakeConsentRepository(),
            protectionProvider: new InternalProtectionProvider(),
            accessPolicySettings: provider);

        var metadata = new TableMetadata
        {
            Identifier = TableId,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "crm", SchemaName = "public", TableName = "matrix_internal", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var subject = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-CALLER-1"),
            ResolvedTenant = Tenant,
            GroupSids = new HashSet<Sid> { EmpGroup },
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = true
        };

        var decision = await pdp.DecideAsync(
            new AccessRequest(subject, metadata, AccessOperation.Read, AccessTransport.Rest),
            CancellationToken.None);

        decision.IsAllowed.ShouldBe(expectedAllowed);
    }

    private sealed class EmptyPolicyRepo : IAccessPolicySettingsRepository
    {
        public Task<StoredTenantAccessPolicy?> GetAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<StoredTenantAccessPolicy?>(null);

        public Task<AccessPolicyWriteResult> UpsertAsync(TenantId tenant, TenantAccessPolicyUpdate settings, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteIfVersionAsync(TenantId tenant, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class InternalProtectionProvider : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(new EffectiveProtection(
                Container: new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
                OwnerGroupSid: null,
                TableClass: ProtectionClass.Internal,
                DeclaredTableClass: ProtectionClass.Internal,
                TableMaskCategory: null,
                Columns: new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
                Visibility: CatalogVisibilityMode.Open));
    }
}
