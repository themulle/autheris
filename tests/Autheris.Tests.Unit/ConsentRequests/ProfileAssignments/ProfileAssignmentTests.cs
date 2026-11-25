namespace Autheris.Tests.Unit.ConsentRequests.ProfileAssignments;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ProfileAssignmentTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    internal static readonly TenantId Tenant = new("tenant-a");
    internal static readonly Sid Requester = new("S-1-5-21-USER-ALICE");
    private static readonly Sid OwnerA = new("S-1-5-21-OWNER-A");
    private static readonly Sid OwnerB = new("S-1-5-21-OWNER-B");
    internal static readonly Sid OwnerC = new("S-1-5-21-OWNER-C");

    [Fact]
    public async Task PatternNewOwnerHook_QueuesOwnerApprovalForTheNewObject()
    {
        var fx = new Fixture();
        var assignment = fx.SeedApprovedAssignment();
        var patternConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = Requester,
            ProfileAssignmentId = assignment.Id,
            ProfileItemNo = 1,
            ObjectPattern = "pg.analytics.*",
            WorkflowMode = ConsentWorkflowMode.Internal,
            ApprovingOwners = [OwnerB.Value],
            ValidTo = FixedNow.AddDays(30)
        };
        var newTable = Fixture.BuildTable("pg", "analytics", "events_v2", "LOW");
        fx.Tables.Add(newTable);

        await fx.Maintenance.HandlePatternNewOwnerAsync(patternConsent, newTable, [OwnerC.Value], fx.Clock.GetUtcNow());

        var extReq = fx.Requests.Single(r => r.Id != assignment.Id);
        extReq.RequestKind.ShouldBe(ConsentRequestKind.ProfileAssignment);
        extReq.ProfileId.ShouldBe(fx.ProfileId);
        extReq.RequesterSid.ShouldBe(ProfileAssignmentMaintenanceService.PatternCoverageRequester);
        extReq.RequestedGranteeRef.ShouldBe(Requester.Value);
        ProfileAssignmentSnapshots.Parse(extReq).ExtendingConsentId.ShouldBe(patternConsent.Id);
        extReq.Status.ShouldBe("PENDING");
        fx.Items.ShouldHaveSingleItem().OwnerSid.ShouldBe(OwnerC.Value);

        // The same object is not queued twice.
        await fx.Maintenance.HandlePatternNewOwnerAsync(patternConsent, newTable, [OwnerC.Value], fx.Clock.GetUtcNow());
        fx.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task PatternNewOwnerHook_DoesNotConsumeTheGranteesPendingRequestCap()
    {
        var fx = new Fixture();
        var assignment = fx.SeedApprovedAssignment();
        var patternConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = Requester,
            ProfileAssignmentId = assignment.Id,
            ObjectPattern = "pg.analytics.*",
            WorkflowMode = ConsentWorkflowMode.Internal,
            ValidTo = FixedNow.AddDays(30)
        };

        for (var i = 0; i < 3; i++)
        {
            var table = Fixture.BuildTable("pg", "analytics", "events_" + i, "LOW");
            fx.Tables.Add(table);
            await fx.Maintenance.HandlePatternNewOwnerAsync(patternConsent, table, [OwnerC.Value], fx.Clock.GetUtcNow());
        }

        // The pending cap counts requests by requester SID; system-initiated requests never use the grantee as requester.
        fx.Requests.Count(r => r.Id != assignment.Id).ShouldBe(3);
        fx.Requests.Count(r => r.RequesterSid == Requester && r.Status == "PENDING").ShouldBe(0);
    }

    [Fact]
    public async Task PatternNewOwnerHook_IgnoresGrantWhoseAssignmentRequestIsUnknown()
    {
        var fx = new Fixture();
        var patternConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = Requester,
            ProfileAssignmentId = Guid.NewGuid(),
            ObjectPattern = "pg.analytics.*",
            WorkflowMode = ConsentWorkflowMode.Internal,
            ValidTo = FixedNow.AddDays(30)
        };
        var newTable = Fixture.BuildTable("pg", "analytics", "events_v2", "LOW");

        await fx.Maintenance.HandlePatternNewOwnerAsync(patternConsent, newTable, [OwnerC.Value], fx.Clock.GetUtcNow());

        fx.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task RestEndpoint_CreatesProfileAssignmentThroughTheEngine_AndRefusesRoleAssignee()
    {
        var fx = new Fixture();
        var sp = new ServiceCollection().AddLogging().BuildServiceProvider();
        var reqCtx = new DefaultHttpContext
        {
            RequestServices = sp,
            Response = { Body = new System.IO.MemoryStream() },
            User = fx.UserPrincipal(Requester)
        };
        reqCtx.Request.Headers["Idempotency-Key"] = "key-create-1";

        var engine = new PermissionRequestEngine(
            fx,
            new Autheris.Tests.Common.Fakes.InMemoryAuditWriter(),
            new ApproverResolver(),
            new ApproverEligibility(new Autheris.Application.Security.GovernanceCapabilities()),
            new Autheris.Application.Governance.ConsentRequests.SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            accessProfileRepository: fx,
            timeProvider: fx.Clock);
        var badRoleResult = await ConsentApiEndpoints.CreateRequestAsync(
            new CreateConsentRequestBody(Kind: "profileAssignment", ProfileId: fx.ProfileId, Grantee: "AnalystRole", GranteeType: "Role", DurationDays: 14),
            reqCtx, engine);
        await badRoleResult.ExecuteAsync(reqCtx);
        reqCtx.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        var okCtx = new DefaultHttpContext { RequestServices = sp, Response = { Body = new System.IO.MemoryStream() }, User = fx.UserPrincipal(Requester) };
        okCtx.Request.Headers["Idempotency-Key"] = "key-create-ok";
        var okResult = await ConsentApiEndpoints.CreateRequestAsync(
            new CreateConsentRequestBody(Kind: "profileAssignment", ProfileId: fx.ProfileId, Justification: "Need access", DurationDays: 14),
            okCtx, engine);
        await okResult.ExecuteAsync(okCtx);
        okCtx.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        fx.Requests.Single().RequestKind.ShouldBe(ConsentRequestKind.ProfileAssignment);
    }

    internal sealed class Fixture :
        IAccessProfileRepository, ITableMetadataRepository, IConsentApprovalRepository, IContainerOwnership,
        IProfileAssignmentStore, IConsentWorkflowConfigRepository, IConsentWorkflowAdminRepository, IGrantRecordStore, IAuditLogRepository
    {
        private long _seq;
        private string _hash = GrantRecordChain.GenesisHash;
        public Guid ProfileId { get; } = Guid.NewGuid();
        public FakeTimeProvider Clock { get; } = new(FixedNow);
        public EcdsaGrantRecordSigner Signer { get; } = EcdsaGrantRecordSigner.CreateEphemeral("profile-key");
        public Dictionary<string, AccessProfile> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TableMetadata> Tables { get; } = [];
        public Dictionary<TableIdentifier, List<string>> TableOwners { get; } = new();
        public List<ConsentRequest> Requests { get; } = [];
        public List<ProfileAssignmentItemRow> Items { get; } = [];
        public List<Consent> Consents { get; } = [];
        public List<GrantRecord> Records { get; } = [];
        public List<AuditLogEntry> Audits { get; } = [];
        public int EpochBumps { get; private set; }
        public List<TableIdentifier> EpochBumpTargets { get; } = [];
        public List<TableIdentifier> InvalidatedAfterCommit { get; } = [];
        public ProfileGrantMaterializer Materializer { get; }
        public ProfileAssignmentMaintenanceService Maintenance { get; }
        public Guid ExternalSystemId { get; } = Guid.NewGuid();

        public Fixture(bool multiSource = false, bool tenantRouteExternal = false, bool highlySensitive = false)
        {
            var t1 = BuildTable("pg", "sales", "orders", highlySensitive ? "HIGH" : "LOW");
            var t2 = BuildTable(multiSource ? "sf" : "pg", "analytics", "metrics", "LOW");
            Tables.AddRange([t1, t2]);
            TableOwners[t1.Identifier] = [OwnerA.Value];
            TableOwners[t2.Identifier] = [OwnerB.Value];

            Profiles[ProfileId.ToString("D")] = new AccessProfile
            {
                ProfileId = ProfileId.ToString("D"),
                TenantId = Tenant,
                Name = "AnalystProfile",
                MaskingMode = MaskingPolicyMode.Default,
                TargetTables = [t1.Identifier.ToString(), $"{(multiSource ? "sf" : "pg")}.analytics.*"],
                RowScope = [new GrantRowScopeEntry("sales_region", new Dictionary<string, JsonElement> { ["region"] = JsonSerializer.SerializeToElement("EMEA") })]
            };

            if (tenantRouteExternal)
            {
                Routes.Add(new ConsentWorkflowRoute(Tenant.Value, WorkflowRouteScopeKind.Tenant, Tenant.Value,
                    ConsentWorkflowMode.External, ExternalSystemId, 90, 30, false, "admin", FixedNow));
            }
            else
            {
                Routes.Add(new ConsentWorkflowRoute(Tenant.Value, WorkflowRouteScopeKind.Tenant, Tenant.Value,
                    ConsentWorkflowMode.Internal, null, 60, 30, false, "admin", FixedNow));
            }

            var writer = new GrantWriter(this, Signer, Clock);
            var router = new ConsentWorkflowRouter(this, configRepository: this, adminRepository: this, timeProvider: Clock);
            var grantPolicy = new GrantPolicy();
            var validityPolicy = GrantValidityPolicy.CreateDefault();
            Materializer = new ProfileGrantMaterializer(writer, this, Clock);
            Maintenance = new ProfileAssignmentMaintenanceService(this, this, Materializer, grantPolicy);
        }

        private List<ConsentWorkflowRoute> Routes { get; } = [];

        /// <summary>The original profile assignment request whose id becomes the assignment id of its grants.</summary>
        public ConsentRequest SeedApprovedAssignment(string status = "APPROVED")
        {
            var request = new ConsentRequest
            {
                Id = Guid.NewGuid(),
                TenantId = Tenant,
                RequesterSid = Requester,
                RequestedGranteeType = GranteeType.User,
                RequestedGranteeRef = Requester.Value,
                BusinessJustification = "Analyst onboarding for the analytics profile.",
                Status = status,
                RequestedAt = FixedNow,
                RequestedValidFrom = FixedNow,
                RequestedValidTo = FixedNow.AddDays(30),
                RequestKind = ConsentRequestKind.ProfileAssignment,
                GrantKind = PermissionGrantKind.Data,
                ProfileId = ProfileId,
                WorkflowMode = ConsentWorkflowMode.Internal
            };
            Requests.Add(request);
            return request;
        }

        public static TableMetadata BuildTable(string domain, string schema, string name, string sensitivity)
        {
            _ = sensitivity;
            return new()
            {
                ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("secret_ssn"),
                Table = new Table { Id = Guid.NewGuid(), SchemaName = schema, TableName = name },
                Identifier = new TableIdentifier(domain, schema, name),
                Columns =
                [
                    new TableColumn { ColumnName = "id" },
                    new TableColumn { ColumnName = "total" },
                    new TableColumn { ColumnName = "secret_ssn" }
                ]
            };
        }

        public ClaimsPrincipal UserPrincipal(Sid sid, bool isGovAdmin = false)
        {
            var claims = new List<Claim> { new("tid", Tenant.Value), new(ClaimTypes.PrimarySid, sid.Value), new("scp", "data.access.request") };
            if (isGovAdmin)
            {
                claims.Add(new Claim(ClaimTypes.Role, "GovernanceAdmin"));
            }
            return new(new ClaimsIdentity(claims, "test"));
        }

        public ClaimsPrincipal ApproverPrincipal(Sid sid, bool isGovAdmin = false)
        {
            var claims = new List<Claim> { new("tid", Tenant.Value), new(ClaimTypes.PrimarySid, sid.Value), new("scp", "data.access.review") };
            if (isGovAdmin) claims.Add(new Claim(ClaimTypes.Role, "GovernanceAdmin"));
            return new(new ClaimsIdentity(claims, "test"));
        }

        public ExternalDecisionAttribution ExternalAttribution(Sid? approver = null) =>
            new(ExternalSystemId, $"EXT-REF-{(approver ?? OwnerA).Value}", "APPROVE", new AssertedApprover((approver ?? OwnerA).Value, ResolvedSid: (approver ?? OwnerA).Value), FixedNow);

        public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectAsync(TenantId tenantId, string subject, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccessProfile>>([]);
        public Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccessProfile>>(Profiles.Values.ToList());
        public Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default) =>
            Task.FromResult(Profiles.TryGetValue(profileId, out var p) && p.TenantId == tenantId ? p : null);
        public Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct = default) { Profiles[profile.ProfileId] = profile; return Task.CompletedTask; }
        public Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default) => Task.FromResult(Profiles.Remove(profileId));
        public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectsAsync(TenantId tenantId, IReadOnlyCollection<string> users, IReadOnlyCollection<string> groups, IReadOnlyCollection<string> roles, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccessProfile>>([]);
        public Task<AccessProfileAssignment> AssignProfileAsync(TenantId tenantId, string profileId, AccessProfileAssignment assignment, CancellationToken ct = default) => Task.FromResult(assignment);
        public Task<AccessProfileAssignment> ApproveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, string approvedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccessProfileAssignment> RenewAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, DateTimeOffset newExpiresAt, string approvedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<DeactivatedProfileAssignment>> GetDeactivatedAssignmentsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeactivatedProfileAssignment>>([]);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>(Tables);
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(Tables.FirstOrDefault(t => t.Identifier.Equals(table)));
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, TenantId? tenantId, CancellationToken ct = default) => GetTableMetadataAsync(table, ct);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata table, CancellationToken ct = default) => Task.FromResult(table);
        public Task<bool> DeleteTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(false);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);

        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) { Requests.Add(request); return Task.FromResult(request); }
        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.FromResult(Requests.FirstOrDefault(r => r.Id == requestId));
        public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, TenantId tenantId, string? status = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>(Requests.Where(r => r.RequesterSid == requesterSid && (r.TenantId == default || r.TenantId == tenantId)).ToList());
        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateRequestStatusAsync(Guid requestId, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateRequestStatusAsync(Guid requestId, string status, string? itsmTicketId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConsentRequest>> GetExpiredPendingRequestsAsync(DateTimeOffset beforeTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        public Task<IReadOnlyList<ConsentRequest>> GetTimedOutRequestsAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) { Requests.RemoveAll(r => r.Id == requestId); return Task.CompletedTask; }

        public Task<ContainerOwnerEntry> RegisterDataOwnerAsync(ContainerOwnerEntry owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AssignTableOwnerAsync(TableIdentifier table, Guid ownerId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContainerOwnerEntry>>(TableOwners.TryGetValue(table, out var sids) ? sids.Select(s => new ContainerOwnerEntry { AdSid = new Sid(s), DisplayName = s, Email = $"{s}@x.test", IsActive = true }).ToList() : []);
        public Task<IReadOnlyList<Sid>> GetAuthorizedApproverSidsForTableAsync(TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Sid>>(TableOwners.TryGetValue(table, out var sids) ? sids.Select(s => new Sid(s)).ToList() : []);
        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
            Task.FromResult(TableOwners.TryGetValue(table, out var sids) && sids.Contains(approverSid.Value, StringComparer.OrdinalIgnoreCase));
        public Task<IReadOnlyList<TableIdentifier>> GetTablesOwnedOrDelegatedAsync(Sid userSid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableIdentifier>>([]);

        public Task<IProfileAssignmentTransaction> BeginTransactionAsync(CancellationToken ct = default) => Task.FromResult<IProfileAssignmentTransaction>(new FakeAssignmentTx(this));
        public Task UpsertAssignmentItemsAsync(DbTransaction? transaction, IReadOnlyList<ProfileAssignmentItemRow> items, CancellationToken ct = default)
        {
            foreach (var row in items)
            {
                Items.RemoveAll(i => i.TenantId == row.TenantId && i.RequestId == row.RequestId && i.ItemNo == row.ItemNo && i.OwnerSid == row.OwnerSid);
                Items.Add(row);
            }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ProfileAssignmentItemRow>> GetAssignmentItemsAsync(DbTransaction? transaction, Guid requestId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProfileAssignmentItemRow>>(Items.Where(i => i.RequestId == requestId).ToList());
        public Task<int> DecideAssignmentItemsAsync(DbTransaction transaction, Guid requestId, IReadOnlyCollection<string> targetOwnerSids, string decision, string decidedBy, DateTimeOffset decidedAt, bool decideAllPending, CancellationToken ct = default)
        {
            int n = 0;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].RequestId == requestId && Items[i].OwnerDecision == "PENDING" && (decideAllPending || targetOwnerSids.Contains(Items[i].OwnerSid, StringComparer.OrdinalIgnoreCase)))
                {
                    Items[i] = Items[i] with { OwnerDecision = decision, DecidedBy = decidedBy, DecidedAt = decidedAt };
                    n++;
                }
            }
            return Task.FromResult(n);
        }
        public Task<IReadOnlyList<Consent>> GetConsentsByProfileAssignmentAsync(DbTransaction? transaction, TenantId tenantId, Guid profileAssignmentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>(Consents.Where(c => c.TenantId == tenantId && c.ProfileAssignmentId == profileAssignmentId && !c.IsRevoked).ToList());
        public Task<Consent?> GetConsentByIdInTransactionAsync(DbTransaction transaction, Guid consentId, CancellationToken ct = default) =>
            Task.FromResult(Consents.FirstOrDefault(c => c.Id == consentId));
        public Task MarkConsentRevokedInTransactionAsync(DbTransaction transaction, Guid consentId, Sid actorSid, string reason, DateTimeOffset now, CancellationToken ct = default)
        {
            var idx = Consents.FindIndex(x => x.Id == consentId);
            Consents[idx] = CloneConsent(Consents[idx], isRevoked: true, revokedBy: actorSid, revokedAt: now, revokeReason: reason);
            return Task.CompletedTask;
        }
        public Task AddApprovingOwnersInTransactionAsync(DbTransaction transaction, Consent consent, IReadOnlyCollection<string> ownerSids, CancellationToken ct = default)
        {
            var idx = Consents.FindIndex(x => x.Id == consent.Id);
            var list = (idx >= 0 ? Consents[idx].ApprovingOwners : consent.ApprovingOwners).ToList();
            foreach (var s in ownerSids) if (!list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
            if (idx >= 0) Consents[idx] = CloneConsent(Consents[idx], approvingOwners: list);
            consent.ApprovingOwners = list;
            return Task.CompletedTask;
        }
        public Task IncrementTableEpochInTransactionAsync(DbTransaction transaction, TableIdentifier table, CancellationToken ct = default) { EpochBumps++; EpochBumpTargets.Add(table); return Task.CompletedTask; }
        public Task RecordAuditInTransactionAsync(DbTransaction transaction, AuditLogEntry entry, CancellationToken ct = default) { Audits.Add(entry); return Task.CompletedTask; }
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) { Audits.Add(entry); return Task.CompletedTask; }
        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default) { Audits.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Audits);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Audits);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task ApplyRequestStepAndStatusInTransactionAsync(DbTransaction transaction, ConsentRequest req, string? newStatus, Sid approverSid, string stepDecision, string? reason, DateTimeOffset decidedAt, CancellationToken ct = default)
        {
            var idx = Requests.FindIndex(r => r.Id == req.Id);
            var target = idx >= 0 ? Requests[idx] : req;
            var steps = target.ApprovalSteps.Concat([new ApprovalStep { Id = Guid.NewGuid(), ConsentRequestId = target.Id, ApproverSid = approverSid, Decision = stepDecision, RejectionReason = reason, DecidedAt = decidedAt }]).ToList();
            if (idx >= 0)
            {
                Requests[idx] = new ConsentRequest
                {
                    Id = target.Id, TableId = target.TableId, TableIdentifier = target.TableIdentifier, RequesterSid = target.RequesterSid,
                    RequestedGranteeType = target.RequestedGranteeType, RequestedGranteeRef = target.RequestedGranteeRef, BusinessJustification = target.BusinessJustification,
                    Status = !string.IsNullOrWhiteSpace(newStatus) ? newStatus : target.Status,
                    RequestedAt = target.RequestedAt, RequestedValidFrom = target.RequestedValidFrom, RequestedValidTo = target.RequestedValidTo,
                    UpdatedAt = decidedAt, Version = !string.IsNullOrWhiteSpace(newStatus) ? target.Version + 1 : target.Version,
                    TenantId = target.TenantId, ContextJson = target.ContextJson, WorkflowMode = target.WorkflowMode,
                    ExternalSystemId = target.ExternalSystemId, ExternalReference = target.ExternalReference,
                    RequestKind = target.RequestKind, ProfileId = target.ProfileId,
                    RequesterIdentifiers = target.RequesterIdentifiers, RequestedWriteRules = target.RequestedWriteRules,
                    IncludeReadAccess = target.IncludeReadAccess, ApprovalSteps = steps
                };
            }
            return Task.CompletedTask;
        }
        public Task InvalidateTablesAfterCommitAsync(IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default) { InvalidatedAfterCommit.AddRange(tables); return Task.CompletedTask; }

        public Task<IReadOnlyList<ConsentWorkflowRoute>> GetWorkflowRoutesAsync(TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConsentWorkflowRoute>>(Routes.Where(r => r.TenantId == tenantId.Value).ToList());
        public Task<ConsentWorkflowRoute?> ResolveWorkflowRouteAsync(TenantId tenantId, TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(Routes.FirstOrDefault(r => r.TenantId == tenantId.Value));
        public Task<ConsentWorkflowTableInputs> GetRoutingInputsForTableAsync(TenantId tenantId, TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(new ConsentWorkflowTableInputs(Routes.Where(r => r.TenantId == tenantId.Value).ToList()));
        public Task<IReadOnlyList<ConsentWorkflowRoute>> ListWorkflowRoutesAsync(TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConsentWorkflowRoute>>(Routes.Where(r => r.TenantId == tenantId.Value).ToList());
        public Task<ConsentWorkflowRoute?> GetWorkflowRouteAsync(TenantId tenantId, WorkflowRouteScopeKind scopeKind, string scopeRef, CancellationToken ct = default) => Task.FromResult<ConsentWorkflowRoute?>(null);
        public Task<ConsentWorkflowRoute> UpsertWorkflowRouteAsync(ConsentWorkflowRoute route, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteWorkflowRouteAsync(TenantId tenantId, WorkflowRouteScopeKind scopeKind, string scopeRef, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExternalWorkflowSystem> UpsertExternalWorkflowSystemAsync(ExternalWorkflowSystem system, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(ExternalWorkflowSystem? System, ConsentRerouteResult RerouteResult)> DisableExternalWorkflowSystemAsync(TenantId tenantId, Guid systemId, string actorSid, DateTimeOffset disabledAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRerouteResult> RerouteOpenRequestsAsync(TenantId tenantId, Guid? fromSystemId, ConsentWorkflowMode? fromMode, ConsentWorkflowMode targetMode, Guid? targetSystemId, string actorSid, DateTimeOffset occurredAt, IReadOnlyList<Guid>? requestIds = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemAsync(TenantId tenantId, Guid systemId, CancellationToken ct = default) =>
            Task.FromResult<ExternalWorkflowSystem?>(new ExternalWorkflowSystem(ExternalSystemId, Tenant.Value, "servicenow", "ServiceNow", "https://sn.invalid", "sec", true, "admin", FixedNow, null));
        public Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemByClientIdAsync(TenantId tenantId, string clientId, CancellationToken ct = default) => Task.FromResult<ExternalWorkflowSystem?>(null);
        public Task<ConsentRequestPage> QueryTenantConsentRequestsAsync(ConsentRequestPageQuery query, CancellationToken ct = default) => Task.FromResult(new ConsentRequestPage([], null));

        public Task<(long LastSeq, string LastHash)> LockAndReadHeadAsync(DbTransaction transaction, string tenantId, CancellationToken ct = default) => Task.FromResult((_seq, _hash));
        public Task AppendRecordAndAdvanceHeadAsync(DbTransaction transaction, GrantRecord record, CancellationToken ct = default) { Records.Add(record); _seq = record.Seq; _hash = record.RecordHash; return Task.CompletedTask; }
        public Task UpdateConsentGrantRecordSeqAsync(DbTransaction transaction, string tenantId, Guid consentId, long seq, CancellationToken ct = default) => Task.CompletedTask;
        public Task InsertConsentRowAsync(DbTransaction transaction, Consent consent, long grantRecordSeq, CancellationToken ct = default) { Consents.Add(CloneConsent(consent, grantRecordSeq: grantRecordSeq)); return Task.CompletedTask; }

        private static Consent CloneConsent(
            Consent c, bool? isRevoked = null, Sid? revokedBy = null, DateTimeOffset? revokedAt = null,
            string? revokeReason = null, long? grantRecordSeq = null, IReadOnlyList<string>? approvingOwners = null) => new()
        {
            Id = c.Id, TableId = c.TableId, TableIdentifier = c.TableIdentifier, ConsentRequestId = c.ConsentRequestId,
            Effect = c.Effect, TenantId = c.TenantId, GranteeType = c.GranteeType, GranteeSid = c.GranteeSid,
            CreatedBySid = c.CreatedBySid, ValidFrom = c.ValidFrom, ValidTo = c.ValidTo,
            IsRevoked = isRevoked ?? c.IsRevoked, RevokedBySid = revokedBy ?? c.RevokedBySid, RevokedAt = revokedAt ?? c.RevokedAt,
            RevokeReason = revokeReason ?? c.RevokeReason, Description = c.Description, WriteRules = c.WriteRules,
            ColumnRules = c.ColumnRules, RowFilters = c.RowFilters, RowScope = c.RowScope, GrantSource = c.GrantSource,
            GrantRecordSeq = grantRecordSeq ?? c.GrantRecordSeq, ProfileAssignmentId = c.ProfileAssignmentId,
            ObjectPattern = c.ObjectPattern, DataSource = c.DataSource, ColumnSetMode = c.ColumnSetMode,
            WorkflowMode = c.WorkflowMode, ProfileItemNo = c.ProfileItemNo, ApprovingOwners = approvingOwners ?? c.ApprovingOwners
        };
        public Task<IReadOnlyList<GrantRecord>> ReadTenantChainAsync(string tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GrantRecord>>(Records.OrderBy(r => r.Seq).ToList());
        public Task<IReadOnlyList<GrantRecord>> ReadConsentChainAsync(string tenantId, Guid consentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GrantRecord>>(Records.Where(r => r.ConsentId == consentId).OrderBy(r => r.Seq).ToList());
        public Task<GrantRecord?> ReadRecordBySeqAsync(string tenantId, long seq, CancellationToken ct = default) => Task.FromResult(Records.FirstOrDefault(r => r.Seq == seq));
        public Task<IReadOnlyList<GrantRecord>> ReadRecordRangeAsync(string tenantId, long fromSeq, long? toSeq, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GrantRecord>>(Records);

        private sealed class FakeAssignmentTx(Fixture fx) : DbTransaction, IProfileAssignmentTransaction
        {
            private readonly List<ConsentRequest> _reqSnap = [.. fx.Requests];
            private readonly List<ProfileAssignmentItemRow> _itemSnap = [.. fx.Items];
            private readonly List<Consent> _consentSnap = [.. fx.Consents];
            private readonly List<GrantRecord> _recSnap = [.. fx.Records];
            private readonly List<AuditLogEntry> _auditSnap = [.. fx.Audits];
            private readonly long _seqSnap = fx._seq;
            private readonly string _hashSnap = fx._hash;
            private readonly int _epochSnap = fx.EpochBumps;

            public DbTransaction Transaction => this;
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            protected override DbConnection? DbConnection => null;
            public override void Commit() { }
            public override void Rollback()
            {
                fx.Requests.Clear(); fx.Requests.AddRange(_reqSnap);
                fx.Items.Clear(); fx.Items.AddRange(_itemSnap);
                fx.Consents.Clear(); fx.Consents.AddRange(_consentSnap);
                fx.Records.Clear(); fx.Records.AddRange(_recSnap);
                fx.Audits.Clear(); fx.Audits.AddRange(_auditSnap);
                fx._seq = _seqSnap;
                fx._hash = _hashSnap;
                fx.EpochBumps = _epochSnap;
            }
            public new Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
            public new Task RollbackAsync(CancellationToken ct = default) { Rollback(); return Task.CompletedTask; }
        }
    }
}
