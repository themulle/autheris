using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class PermissionRequestEngineSubmitTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly TableIdentifier HiddenTable = new("sales", "dbo", "secret_internal");
    private static readonly TableIdentifier ForeignTable = new("sales", "dbo", "foreign_tenant");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    private static ClaimsPrincipal UserPrincipal(string sid = "user:alice", bool authenticated = true)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new("tenant_id", TenantA.Value) };
        var identity = authenticated ? new ClaimsIdentity(claims, "Bearer") : new ClaimsIdentity(claims);
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal SpPrincipal(string clientId = "sp-client-1") =>
        new(new ClaimsIdentity(
        [
            new(ClaimTypes.PrimarySid, $"sp:{clientId}"),
            new("appid", clientId),
            new("idtyp", "app"),
            new("tenant_id", TenantA.Value)
        ], "Bearer"));

    private static PermissionRequestEngine CreateEngine(
        IConsentApprovalRepository approvals,
        ITableMetadataRepository? metadata = null,
        IAccessPolicySettingsProvider? policyProvider = null,
        ICatalogVisibilityGrantRepository? catalogRepo = null,
        IProtectionSnapshotProvider? protection = null,
        IGovernanceDomainRepository? domainRepo = null,
        FakeTimeProvider? time = null,
        InMemoryAuditWriter? audit = null)
    {
        metadata ??= DefaultMetadata();
        return new PermissionRequestEngine(
            approvals,
            audit ?? new InMemoryAuditWriter(),
            new ApproverResolver(domainRepo, protectionSnapshotProvider: protection, metadataRepository: metadata),
            new ApproverEligibility(new GovernanceCapabilities(domainRepository: domainRepo)),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            metadataRepository: metadata,
            domainRepository: domainRepo,
            protectionProvider: protection,
            accessPolicyProvider: policyProvider,
            catalogVisibilityRepository: catalogRepo,
            capabilities: new GovernanceCapabilities(domainRepository: domainRepo),
            timeProvider: time ?? new FakeTimeProvider(Now));
    }

    private static ITableMetadataRepository DefaultMetadata(params TableMetadata[] extra)
    {
        var byTable = new Dictionary<TableIdentifier, TableMetadata>
        {
            [OrdersTable] = new()
            {
                Identifier = OrdersTable,
                OwnerTenantId = TenantA.Value,
                Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
                Columns = [new TableColumn { ColumnName = "email", DataType = "VARCHAR" }, new TableColumn { ColumnName = "amount", DataType = "DECIMAL" }]
            },
            [HiddenTable] = new()
            {
                Identifier = HiddenTable,
                OwnerTenantId = TenantA.Value,
                Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "secret_internal", IsActive = true, DiscoveryHidden = true }
            },
            [ForeignTable] = new()
            {
                Identifier = ForeignTable,
                OwnerTenantId = TenantB.Value,
                Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "foreign_tenant", IsActive = true }
            }
        };
        foreach (var e in extra)
        {
            byTable[e.Identifier] = e;
        }

        return NullFake.Create<ITableMetadataRepository>((m, args) =>
        {
            if (m.Name == nameof(ITableMetadataRepository.GetTableMetadataAsync) && args is [TableIdentifier tid, ..])
            {
                return Task.FromResult(byTable.GetValueOrDefault(tid));
            }

            return NullFake.UseDefault;
        });
    }

    [Fact]
    public async Task Missing_end_date_rejected()
    {
        var approvals = NullFake.Create<IConsentApprovalRepository>();
        var engine = CreateEngine(approvals);

        var dataEx = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(
                UserPrincipal(), TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(OrdersTable),
                GranteeType.User, "user:alice", "Need orders read access for reporting.")));
        dataEx.Code.ShouldBe(GrantErrorCodes.EndDateRequired);

        var unmaskEx = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(
                UserPrincipal(), TenantA, ConsentRequestKind.Unmask, PermissionTarget.ForTable(OrdersTable, ["email"]),
                GranteeType.User, "user:alice", "Need unmasked emails for customer support.")));
        unmaskEx.Code.ShouldBe(GrantErrorCodes.EndDateRequired);
    }

    [Fact]
    public async Task Duplicate_pending_rejected()
    {
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, _) =>
            m.Name == nameof(IConsentApprovalRepository.GetRequestsByRequesterSidAsync)
                ? Task.FromResult<IReadOnlyList<ConsentRequest>>(
                [
                    new ConsentRequest
                    {
                        Id = Guid.NewGuid(),
                        TableIdentifier = OrdersTable,
                        RequesterSid = new Sid("user:alice"),
                        RequestedGranteeType = GranteeType.User,
                        RequestedGranteeRef = "user:alice",
                        RequestKind = ConsentRequestKind.Data,
                        Status = "PENDING",
                        TenantId = TenantA
                    }
                ])
                : NullFake.UseDefault);

        var engine = CreateEngine(approvals);
        var ex = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(
                UserPrincipal(), TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(OrdersTable),
                GranteeType.User, "user:alice", "Need orders data access for billing.", ValidTo: Now.AddDays(30))));
        ex.Code.ShouldBe("DUPLICATE_PENDING_REQUEST");
    }

    [Fact]
    public async Task Bundle_items_routed_individually()
    {
        var created = new List<ConsentRequest>();
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.CreateConsentRequestAsync))
            {
                var r = (ConsentRequest)args![0]!;
                created.Add(r);
                return Task.FromResult(r);
            }

            return NullFake.UseDefault;
        });

        var table2 = new TableIdentifier("hr", "dbo", "employees");
        var metadata = DefaultMetadata(new TableMetadata
        {
            Identifier = table2,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "hr", SchemaName = "dbo", TableName = "employees", IsActive = true }
        });

        var engine = CreateEngine(approvals, metadata: metadata);
        var items = new[]
        {
            new PermissionRequestSubmission(UserPrincipal(), TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(OrdersTable), GranteeType.User, "user:alice", "Quarterly sales audit reporting.", ValidTo: Now.AddDays(14)),
            new PermissionRequestSubmission(UserPrincipal(), TenantA, ConsentRequestKind.Unmask, PermissionTarget.ForTable(table2), GranteeType.User, "user:alice", "Payroll discrepancy verification.", ValidTo: Now.AddDays(14))
        };

        var result = await engine.SubmitBundleAsync(UserPrincipal(), TenantA, items);
        result.BundleRequests!.Count.ShouldBe(2);
        result.BundleId.ShouldNotBeNull();
        created.Count.ShouldBe(2);
        created.Select(c => c.BundleId).Distinct().ShouldHaveSingleItem().ShouldBe(result.BundleId);
        created[0].Id.ShouldNotBe(created[1].Id);
        created[0].RequestKind.ShouldBe(ConsentRequestKind.Data);
        created[1].RequestKind.ShouldBe(ConsentRequestKind.Unmask);
    }

    [Fact]
    public async Task ServicePrincipal_can_request_data_access()
    {
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
            m.Name == nameof(IConsentApprovalRepository.CreateConsentRequestAsync)
                ? Task.FromResult((ConsentRequest)args![0]!)
                : NullFake.UseDefault);
        var audit = new InMemoryAuditWriter();
        var engine = CreateEngine(approvals, audit: audit);

        var res = await engine.SubmitAsync(new PermissionRequestSubmission(
            SpPrincipal(),
            TenantA,
            ConsentRequestKind.Data,
            PermissionTarget.ForTable(OrdersTable),
            GranteeType.ServicePrincipal,
            "sp:sp-client-1",
            "Automated nightly ETL pipeline sync.",
            ValidTo: Now.AddDays(90)));

        res.Request.RequesterSid.ShouldBe(new Sid("sp:sp-client-1"));
        res.Request.GrantKind.ShouldBe(PermissionGrantKind.Data);
        audit.Entries.ShouldContain(e => e.DetailsJson.Contains("\"ActorKind\":\"ServicePrincipal\""));
    }

    [Fact]
    public async Task Anonymous_or_unresolved_subject_rejected()
    {
        var engine = CreateEngine(NullFake.Create<IConsentApprovalRepository>());
        var unauthenticated = UserPrincipal(authenticated: false);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "anonymous"), new Claim("tenant_id", TenantA.Value)], "Bearer"));

        (await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(unauthenticated, TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(OrdersTable),
                GranteeType.User, "user:alice", "Valid justification text here.", ValidTo: Now.AddDays(10)))))
            .Code.ShouldBe("UNAUTHENTICATED");

        (await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(anonymous, TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(OrdersTable),
                GranteeType.User, "user:alice", "Valid justification text here.", ValidTo: Now.AddDays(10)))))
            .Code.ShouldBe("UNAUTHENTICATED");
    }

    [Fact]
    public async Task Invisible_target_same_response_as_missing()
    {
        var engine = CreateEngine(NullFake.Create<IConsentApprovalRepository>());
        var missingTable = new TableIdentifier("sales", "dbo", "does_not_exist");

        var missingEx = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(UserPrincipal(), TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(missingTable),
                GranteeType.User, "user:alice", "Valid justification text here.", ValidTo: Now.AddDays(10))));

        var hiddenEx = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(UserPrincipal(), TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(HiddenTable),
                GranteeType.User, "user:alice", "Valid justification text here.", ValidTo: Now.AddDays(10))));

        var foreignEx = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(UserPrincipal(), TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(ForeignTable),
                GranteeType.User, "user:alice", "Valid justification text here.", ValidTo: Now.AddDays(10))));

        hiddenEx.Code.ShouldBe(missingEx.Code);
        hiddenEx.Message.ShouldBe(missingEx.Message);
        foreignEx.Code.ShouldBe(missingEx.Code);
        foreignEx.Message.ShouldBe(missingEx.Message);
    }

    [Fact]
    public async Task Bundle_cap_and_pending_cap_enforced()
    {
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, _) =>
            m.Name == nameof(IConsentApprovalRepository.CountPendingRequestsForRequesterAsync)
                ? Task.FromResult(10)
                : NullFake.UseDefault);

        var policyProvider = NullFake.Create<IAccessPolicySettingsProvider>((m, _) =>
            m.Name == nameof(IAccessPolicySettingsProvider.GetEffectiveAsync)
                ? Task.FromResult(TenantAccessPolicy.FromDeploymentDefaults(TenantA, new AccessPolicyOptions
                {
                    MaxBundleItems = 2,
                    MaxPendingRequestsPerRequester = 10
                }))
                : NullFake.UseDefault);

        var engine = CreateEngine(approvals, policyProvider: policyProvider);
        var tooBigBundle = Enumerable.Range(0, 3).Select(_ =>
            new PermissionRequestSubmission(UserPrincipal(), TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(OrdersTable),
                GranteeType.User, "user:alice", "Valid bundle justification text.", ValidTo: Now.AddDays(10))).ToArray();

        var bundleEx = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitBundleAsync(UserPrincipal(), TenantA, tooBigBundle));
        bundleEx.Code.ShouldBe("BUNDLE_LIMIT_EXCEEDED");

        var pendingEx = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(tooBigBundle[0]));
        pendingEx.Code.ShouldBe("PENDING_REQUEST_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task Pending_cap_per_requester()
    {
        // Deployment default (100) applies without a tenant policy: a requester at the cap cannot open another request.
        var atDefaultCap = NullFake.Create<IConsentApprovalRepository>((m, _) =>
            m.Name == nameof(IConsentApprovalRepository.CountPendingRequestsForRequesterAsync) ? Task.FromResult(100) : NullFake.UseDefault);
        var submission = new PermissionRequestSubmission(UserPrincipal(), TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(OrdersTable),
            GranteeType.User, "user:alice", "Valid justification text here.", ValidTo: Now.AddDays(10));

        var ex = await Should.ThrowAsync<ConsentRequestException>(() => CreateEngine(atDefaultCap).SubmitAsync(submission));
        ex.Code.ShouldBe("PENDING_REQUEST_LIMIT_EXCEEDED");

        // A tenant may tighten the cap.
        var tightened = NullFake.Create<IAccessPolicySettingsProvider>((m, _) =>
            m.Name == nameof(IAccessPolicySettingsProvider.GetEffectiveAsync)
                ? Task.FromResult(TenantAccessPolicy.FromDeploymentDefaults(TenantA, new AccessPolicyOptions { MaxPendingRequestsPerRequester = 3 }))
                : NullFake.UseDefault);
        var atTenantCap = NullFake.Create<IConsentApprovalRepository>((m, _) =>
            m.Name == nameof(IConsentApprovalRepository.CountPendingRequestsForRequesterAsync) ? Task.FromResult(3) : NullFake.UseDefault);
        var tightEx = await Should.ThrowAsync<ConsentRequestException>(() => CreateEngine(atTenantCap, policyProvider: tightened).SubmitAsync(submission));
        tightEx.Code.ShouldBe("PENDING_REQUEST_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task Caps_enforced()
    {
        // Deployment default bundle cap (50) applies without a tenant policy.
        var engine = CreateEngine(NullFake.Create<IConsentApprovalRepository>());
        var oversized = Enumerable.Range(0, 51).Select(_ =>
            new PermissionRequestSubmission(UserPrincipal(), TenantA, ConsentRequestKind.Data, PermissionTarget.ForTable(OrdersTable),
                GranteeType.User, "user:alice", "Valid bundle justification text.", ValidTo: Now.AddDays(10))).ToArray();
        (await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitBundleAsync(UserPrincipal(), TenantA, oversized)))
            .Code.ShouldBe("BUNDLE_LIMIT_EXCEEDED");

        // Caps are bounded: a tenant cannot raise a cap beyond the hard maximum.
        TenantAccessPolicy.FromDeploymentDefaults(TenantA, new AccessPolicyOptions { MaxBundleItems = 5000 }).MaxBundleItems.ShouldBeLessThanOrEqualTo(1000);
    }

    [Fact]
    public async Task Whitespace_or_blank_target_columns_rejected()
    {
        var engine = CreateEngine(NullFake.Create<IConsentApprovalRepository>());

        var blankOnlyEx = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(
                UserPrincipal(), TenantA, ConsentRequestKind.Data,
                PermissionTarget.ForTable(OrdersTable, ["   ", ""]),
                GranteeType.User, "user:alice", "Valid justification text here.", ValidTo: Now.AddDays(10))));
        blankOnlyEx.Code.ShouldBe("VALIDATION_ERROR");

        var mixedBlankEx = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(
                UserPrincipal(), TenantA, ConsentRequestKind.Unmask,
                PermissionTarget.ForTable(OrdersTable, ["email", " "]),
                GranteeType.User, "user:alice", "Valid justification text here.", ValidTo: Now.AddDays(10))));
        mixedBlankEx.Code.ShouldBe("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Column_scoped_request_when_metadata_absent_or_empty_rejected()
    {
        var emptyColsTable = new TableIdentifier("sales", "dbo", "no_cols");
        var metadata = DefaultMetadata(new TableMetadata
        {
            Identifier = emptyColsTable,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "no_cols", IsActive = true },
            Columns = []
        });

        var engineWithEmptyCols = CreateEngine(NullFake.Create<IConsentApprovalRepository>(), metadata: metadata);
        var emptyColsEx = await Should.ThrowAsync<ConsentRequestException>(() => engineWithEmptyCols.SubmitAsync(
            new PermissionRequestSubmission(
                UserPrincipal(), TenantA, ConsentRequestKind.Data,
                PermissionTarget.ForTable(emptyColsTable, ["email"]),
                GranteeType.User, "user:alice", "Valid justification text here.", ValidTo: Now.AddDays(10))));
        emptyColsEx.Code.ShouldBe("TARGET_NOT_FOUND");

        var engineWithoutMetadata = new PermissionRequestEngine(
            NullFake.Create<IConsentApprovalRepository>(),
            new InMemoryAuditWriter(),
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            metadataRepository: null,
            timeProvider: new FakeTimeProvider(Now));

        var noMetaEx = await Should.ThrowAsync<ConsentRequestException>(() => engineWithoutMetadata.SubmitAsync(
            new PermissionRequestSubmission(
                UserPrincipal(), TenantA, ConsentRequestKind.Unmask,
                PermissionTarget.ForTable(OrdersTable, ["email"]),
                GranteeType.User, "user:alice", "Valid justification text here.", ValidTo: Now.AddDays(10))));
        noMetaEx.Code.ShouldBe("COLUMN_METADATA_UNAVAILABLE");
    }
}
