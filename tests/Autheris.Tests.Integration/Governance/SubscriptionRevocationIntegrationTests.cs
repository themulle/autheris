namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.GraphQL.Subscriptions;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.Governance.Contract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Collection(GovernancePostgresCollection.Name)]
[Trait("Category", "Security")]
public sealed class SubscriptionRevocationIntegrationTests(GovernancePostgresFixture fixture)
{
    [RequiresContainerRuntimeFact]
    public async Task Revoke_closes_stream()
    {
        await using var store = await GovernanceTestStore.CreatePostgreSqlAsync(fixture, "sub_revoke");
        if (!store.IsAvailable) return;

        var tableId = Guid.NewGuid();
        var tableRef = new TableIdentifier("crm", "public", "sub_orders");
        await store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableRef,
            OwnerTenantId = store.TenantA.Value,
            Table = new Table
            {
                Id = tableId,
                SourceName = "crm",
                SchemaName = "public",
                TableName = "sub_orders",
                IsActive = true
            },
            Columns = [new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" }]
        });
        await store.GovernanceDomains.SetTableProtectionAsync(
            store.TenantA,
            tableId,
            new ObjectProtection(ProtectionClass.Confidential, null),
            clearColumns: null,
            new Sid("S-1-5-21-ADM-1"));

        var userSid = new Sid("S-1-5-21-SUB-USER-1");
        var grant = await store.Consents.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = store.TenantA,
            TableId = tableId,
            TableIdentifier = tableRef,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            Effect = ConsentEffect.Allow,
            ColumnSetMode = ColumnSetMode.Explicit,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        var claims = new Claim[]
        {
            new(ClaimTypes.PrimarySid, userSid.Value),
            new("tenant_id", store.TenantA.Value),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADM-1"),
            new Claim("tenant_id", store.TenantA.Value),
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ], "Bearer"));

        var epochProvider = store.Services.GetRequiredService<GovernanceEpochProvider>();
        var beforeEpoch = await epochProvider.GetEpochAsync(store.TenantA);

        using var streamCts = new CancellationTokenSource();
        var lastEpoch = new[] { beforeEpoch };
        var syncLock = new Lock();

        await store.Consents.RevokeConsentAsync(grant.Id, new Sid("S-1-5-21-ADM-1"), "Revoked", ConsentRevocationAuthority.FromPrincipal(adminPrincipal));
        epochProvider.InvalidateTenant(store.TenantA.Value);

        await Subscription.PollEpochChangeAsync(
            streamCts,
            epochProvider,
            store.TenantA,
            store.Services,
            principal,
            tableRef,
            lastEpoch,
            syncLock,
            NullLogger.Instance);

        streamCts.IsCancellationRequested.ShouldBeTrue();
    }
}
