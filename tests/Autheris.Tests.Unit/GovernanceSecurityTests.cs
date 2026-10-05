using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using NSubstitute;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class GovernanceSecurityTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repository;
    private readonly Mutation _mutation;

    public GovernanceSecurityTests()
    {
        var epochService = new EpochValidationService();
        _repository = new SqliteGovernanceRepository(epochService);
        _mutation = new Mutation();
    }

    public void Dispose()
    {
        _repository.Dispose();
    }

    private sealed class IsolatedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static IHttpContextAccessor CreateAccessor(Sid userSid, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, userSid.Value),
            new(ClaimTypes.NameIdentifier, userSid.Value),
            new("objectSid", userSid.Value),
            new(ClaimTypes.Name, $"CORP\\{userSid.Value}")
        };
        foreach (var r in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, r));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth", ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var context = new DefaultHttpContext { User = principal };
        return new IsolatedHttpContextAccessor(context);
    }

    private static IHttpContextAccessor CreateAccessorWithTenant(Sid userSid, TenantId tenantId, params string[] roles)
    {
        var accessor = CreateAccessor(userSid, roles);
        accessor.HttpContext!.Items["TenantId"] = tenantId;
        return accessor;
    }

    [Fact]
    public async Task RevokeConsent_WhenCalledByUnauthorizedUser_ThrowsForbidden()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var granteeSid = new Sid("S-1-5-21-GRANTEE-1");
        var unauthorizedUserSid = new Sid("S-1-5-21-ATTACKER-99");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = granteeSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        var accessor = CreateAccessor(unauthorizedUserSid);

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.RevokeConsentAsync(consent.Id, "Malicious revocation", _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN");
    }

    [Fact]
    public async Task RevokeConsent_WhenCalledByTableDataOwner_SucceedsAndRecordsAudit()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var ownerSid = new Sid("S-1-5-21-DATAOWNER-1");
        var granteeSid = new Sid("S-1-5-21-GRANTEE-2");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = granteeSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        var accessor = CreateAccessor(ownerSid);

        var result = await _mutation.RevokeConsentAsync(consent.Id, "Legitimate owner revocation", _repository, accessor);
        result.ShouldBeTrue();

        var activeConsents = await _repository.GetActiveConsentsForSubjectsAsync(new[] { granteeSid }, table, DateTimeOffset.UtcNow);
        activeConsents.ShouldBeEmpty();

        var auditEntries = await _repository.GetAuditLogEntriesAsync(10);
        auditEntries.ShouldContain(e => e.EventType == "CONSENT_REVOKED" && e.ActorSid == ownerSid);
    }

    [Fact]
    public async Task RevokeConsent_WhenCalledByGranteeSelf_Succeeds()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var granteeSid = new Sid("S-1-5-21-GRANTEE-SELF");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = granteeSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        var accessor = CreateAccessor(granteeSid);

        var result = await _mutation.RevokeConsentAsync(consent.Id, "Self revocation", _repository, accessor);
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task ApproveConsent_WhenUserHasDataOwnerRole_ButNotOwnerOfSpecificTable_ThrowsForbidden()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQUESTER-1");
        var genericDataOwnerSid = new Sid("S-1-5-21-GENERIC-DATAOWNER-NOT-ASSIGNED");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Testing role bypass",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(14)
        });

        // User has AD role 'DataOwner', but is NOT registered as owner for finance_table_1
        var accessor = CreateAccessor(genericDataOwnerSid, "DataOwner");

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.ApproveConsentRequestAsync(req.Id, "key-test-do-role", _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN");
    }

    [Fact]
    public async Task ConsentRequest_StateMachine_PreventsReApprovalOrRejection()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-SM");
        var approverSid = new Sid("S-1-5-21-DATAOWNER-1");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "State machine test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        var approved = await _repository.ApproveConsentRequestStepAsync(req.Id, approverSid);
        approved.Status.ShouldBe("APPROVED");

        // Attempting to approve an already APPROVED request must throw InvalidOperationException
        var anotherApprover = new Sid("S-1-5-21-APPROVER-2");
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _repository.ApproveConsentRequestStepAsync(req.Id, anotherApprover);
        });

        // Attempting to reject an already APPROVED request must throw InvalidOperationException
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _repository.RejectConsentRequestAsync(req.Id, anotherApprover, "Too late");
        });
    }

    [Fact]
    public async Task Idempotency_WhenDifferentUsersUseSameKey_ExecutesSeparately()
    {
        var userA = new Sid("S-1-5-21-USER-AAA");
        var userB = new Sid("S-1-5-21-USER-BBB");
        const string sharedIdempotencyKey = "shared-client-key-42";

        var accessorA = CreateAccessor(userA);
        var accessorB = CreateAccessor(userB);

        var payloadA = await _mutation.RequestTableAccessAsync(
            "finance", "dbo", "finance_table_1", "Reason A", 7, sharedIdempotencyKey, _repository, accessorA);

        var payloadB = await _mutation.RequestTableAccessAsync(
            "finance", "dbo", "finance_table_1", "Reason B", 7, sharedIdempotencyKey, _repository, accessorB);

        // Payloads must not collide!
        payloadA.RequestId.ShouldNotBe(payloadB.RequestId);
    }

    [Fact]
    public async Task GetPendingRequestsForApprover_ReturnsOnlyAssignedTables()
    {
        var tableFinance = new TableIdentifier("finance", "dbo", "finance_table_1");
        var tableSales = new TableIdentifier("sales", "dbo", "sales_table_1");

        var metaFinance = await _repository.GetTableMetadataAsync(tableFinance);
        var metaSales = await _repository.GetTableMetadataAsync(tableSales);
        metaFinance.ShouldNotBeNull();
        metaSales.ShouldNotBeNull();

        // DATAOWNER-1 owns finance_table_1 (seeded). Let's submit requests for finance and sales.
        var ownerFinance = new Sid("S-1-5-21-DATAOWNER-1");
        var requester = new Sid("S-1-5-21-SOME-USER");

        var reqFinance = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = metaFinance.Table.Id,
            TableIdentifier = tableFinance,
            RequesterSid = requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester.Value,
            BusinessJustification = "Finance access",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(14)
        });

        var reqSales = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = metaSales.Table.Id,
            TableIdentifier = tableSales,
            RequesterSid = requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester.Value,
            BusinessJustification = "Sales access",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(14)
        });

        var pendingForFinanceOwner = await _repository.GetPendingRequestsForApproverAsync(ownerFinance);

        pendingForFinanceOwner.ShouldContain(r => r.Id == reqFinance.Id);
        pendingForFinanceOwner.ShouldNotContain(r => r.Id == reqSales.Id);
    }

    [Fact]
    public async Task GetPendingRequests_WhenDelegationExpired_ExcludesRequests()
    {
        // Expired delegation must not grant approval view
        var tableFinance = new TableIdentifier("finance", "dbo", "finance_table_1");
        var metaFinance = await _repository.GetTableMetadataAsync(tableFinance);
        metaFinance.ShouldNotBeNull();

        var owners = await _repository.GetDataOwnersForTableAsync(tableFinance);
        var activeOwner = owners.First(o => o.IsActive);

        var delegateSid = new Sid("S-1-5-21-EXPIRED-DELEGATE");

        // Expired delegation: valid_to is in the past
        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            DataOwnerId = activeOwner.Id,
            DelegateSid = delegateSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
            ValidTo = DateTimeOffset.UtcNow.AddMinutes(-1), // Expired!
            Reason = "Vacation coverage in the past"
        });

        var requester = new Sid("S-1-5-21-REQUESTER-EXPIRED");
        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = metaFinance.Table.Id,
            TableIdentifier = tableFinance,
            RequesterSid = requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester.Value,
            BusinessJustification = "Access request",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        var pendingForDelegate = await _repository.GetPendingRequestsForApproverAsync(delegateSid);
        pendingForDelegate.ShouldNotContain(r => r.Id == req.Id);
    }

    [Fact]
    public async Task RevokeConsent_GroupGrantee_CannotBeRevokedByMemberViaGranteeSelf()
    {
        // GranteeSelf must strictly apply only to GranteeType.User
        var table = new TableIdentifier("sales", "dbo", "sales_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var groupSid = new Sid("S-1-5-21-SALES-GROUP");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group, // GROUP, not User!
            GranteeSid = groupSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        // Caller claiming to have the group's SID directly
        var accessor = CreateAccessor(groupSid);

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.RevokeConsentAsync(consent.Id, "Trying self-revoke on group", _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN");
    }

    [Fact]
    public async Task RejectConsentRequest_WhenSecondStepRejection_IncrementsStepNumberWithoutUniqueConstraintViolation()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_5");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();
        meta.Table.RequiresFourEyes.ShouldBeTrue();

        var requester = new Sid("S-1-5-21-REQ-1");
        var approver1 = new Sid("S-1-5-21-APP-1");
        var approver2 = new Sid("S-1-5-21-APP-2");

        var owners = await _repository.GetDataOwnersForTableAsync(table);
        var primaryOwner = owners[0];

        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            Id = Guid.NewGuid(),
            DataOwnerId = primaryOwner.Id,
            DelegateSid = approver1,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Reason = "Four eyes step 1 delegation"
        });

        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            Id = Guid.NewGuid(),
            DataOwnerId = primaryOwner.Id,
            DelegateSid = approver2,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Reason = "Four eyes step 2 delegation"
        });

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester.Value,
            BusinessJustification = "Four eyes rejection test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Step 1: Approved by Approver 1 -> Status is PENDING_SECOND_APPROVAL
        var approvedStep1 = await _repository.ApproveConsentRequestStepAsync(req.Id, approver1);
        approvedStep1.Status.ShouldBe("PENDING_SECOND_APPROVAL");

        // Step 2: Rejected by Approver 2 -> Must succeed without UNIQUE constraint violation on step_number
        var rejectedReq = await _repository.RejectConsentRequestAsync(req.Id, approver2, "Second approver rejected");
        rejectedReq.Status.ShouldBe("REJECTED");
    }

    [Fact]
    public async Task GetTransitiveRoles_ReturnsRoleNamesAsStrings()
    {
        var userSid = new Sid("S-1-5-21-TRANSITIVE-USER");
        var roles = await _repository.GetTransitiveRolesAsync(userSid);
        roles.ShouldNotBeNull();
        roles.ShouldBeAssignableTo<IReadOnlySet<string>>();
    }

    [Fact]
    public async Task IsAuthorizedApprover_WhenDirectOwnerOrDelegatedOrUnauthorized()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var directOwnerSid = new Sid("S-1-5-21-DATAOWNER-1");
        var delegateSid = new Sid("S-1-5-21-DELEGATE-TEST");
        var unrelatedSid = new Sid("S-1-5-21-RANDOM-USER");

        // 1. Direct owner is authorized
        var isDirect = await _repository.IsAuthorizedApproverForTableAsync(table, directOwnerSid);
        isDirect.ShouldBeTrue();

        // 2. Unrelated user is not authorized
        var isUnrelated = await _repository.IsAuthorizedApproverForTableAsync(table, unrelatedSid);
        isUnrelated.ShouldBeFalse();

        // 3. Delegate to delegateSid
        var owners = await _repository.GetDataOwnersForTableAsync(table);
        var primaryOwner = owners.First(o => o.AdSid == directOwnerSid);

        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            Id = Guid.NewGuid(),
            DataOwnerId = primaryOwner.Id,
            DelegateSid = delegateSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Reason = "Vacation delegation"
        });

        // Delegate is now authorized
        var isDelegate = await _repository.IsAuthorizedApproverForTableAsync(table, delegateSid);
        isDelegate.ShouldBeTrue();
    }

    [Fact]
    public async Task ApproveConsent_WhenUserIsDelegatedApprover_Succeeds()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var directOwnerSid = new Sid("S-1-5-21-DATAOWNER-1");
        var delegateSid = new Sid("S-1-5-21-DELEGATE-APPROVE");
        var requesterSid = new Sid("S-1-5-21-REQUESTER-DELEGATE");

        var owners = await _repository.GetDataOwnersForTableAsync(table);
        var primaryOwner = owners.First(o => o.AdSid == directOwnerSid);

        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            Id = Guid.NewGuid(),
            DataOwnerId = primaryOwner.Id,
            DelegateSid = delegateSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Reason = "Coverage delegation"
        });

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Delegate approval test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        var accessor = CreateAccessor(delegateSid);
        var result = await _mutation.ApproveConsentRequestAsync(req.Id, "key-del-app", _repository, accessor);
        result.Status.ShouldBe("APPROVED");
    }

    [Fact]
    public async Task RevokeConsent_WhenCalledDirectlyOnRepoByUnauthorizedUser_ThrowsUnauthorizedAccessException()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var granteeSid = new Sid("S-1-5-21-GRANTEE-DIRECT-REVOKE");
        var attackerSid = new Sid("S-1-5-21-ATTACKER-REVOKE");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = granteeSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        // Calling repo.RevokeConsentAsync directly without authorization must throw UnauthorizedAccessException
        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
        {
            await _repository.RevokeConsentAsync(consent.Id, attackerSid, "Malicious direct revocation");
        });
    }

    [Fact]
    public async Task ApproveConsentRequestStep_WhenCalledDirectlyOnRepoByUnauthorizedUser_ThrowsUnauthorizedAccessException()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-DIRECT-APP");
        var unauthorizedApproverSid = new Sid("S-1-5-21-UNAUTHORIZED-APPROVER");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Direct approval auth test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Calling repo.ApproveConsentRequestStepAsync directly without authorization must throw UnauthorizedAccessException
        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
        {
            await _repository.ApproveConsentRequestStepAsync(req.Id, unauthorizedApproverSid);
        });
    }

    [Fact]
    public async Task RejectConsentRequest_WhenCalledDirectlyOnRepoByUnauthorizedUser_ThrowsUnauthorizedAccessException()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-DIRECT-REJ");
        var unauthorizedApproverSid = new Sid("S-1-5-21-UNAUTHORIZED-APPROVER");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Direct rejection auth test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Calling repo.RejectConsentRequestAsync directly without authorization must throw UnauthorizedAccessException
        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
        {
            await _repository.RejectConsentRequestAsync(req.Id, unauthorizedApproverSid, "Malicious rejection");
        });
    }

    private sealed class DummyHostEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void SqliteGovernanceRepository_InProductionWithoutKeyVaultSecret_ThrowsInvalidOperationException()
    {
        var epochService = new EpochValidationService();
        var prodEnv = new DummyHostEnvironment("Production");
        var opts = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
        {
            GovernanceDb = new Autheris.Domain.Options.GovernanceDbOptions
            {
                ConnectionString = "Data Source=test_prod.db;Mode=ReadWriteCreate;"
            }
        });

        Should.Throw<InvalidOperationException>(() =>
        {
            using var repo = new SqliteGovernanceRepository(epochService, opts, prodEnv, secretProvider: null);
        });
    }

    [Fact]
    public async Task ServiceNowClient_WithoutConfiguredBaseUrl_ReturnsError()
    {
        // EXT-MOVE: the former extension duplicate ServiceNowClient was removed; the remaining ServiceNowTableApiClient
        // must fail closed (no request, no invented ticket) when no ServiceNow base URL is configured.
        using var httpClient = new System.Net.Http.HttpClient();
        var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions());
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Autheris.Extensions.Itsm.ServiceNowTableApiClient>.Instance;
        var client = new Autheris.Extensions.Itsm.ServiceNowTableApiClient(httpClient, options, logger);

        var result = await client.CreateAccessTicketAsync(new ItsmTicketRequest(
            new TenantId("tenant-a"),
            new Sid("S-1-5-21-1234"),
            new TableIdentifier("finance", "dbo", "invoices"),
            "Need access",
            7,
            null,
            null));

        result.Success.ShouldBeFalse();
        result.TicketReference.ShouldBeNull();
        result.ErrorCode.ShouldBe("CONFIGURATION_ERROR");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public async Task RequestTableAccess_WithTooShortJustification_ThrowsInvalidArgument(string justification)
    {
        var accessor = CreateAccessor(new Sid("S-1-5-21-USER-1"));
        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.RequestTableAccessAsync("finance", "dbo", "finance_table_1", justification, 7, null, _repository, _repository, accessor);
        });
        ex.Errors.ShouldContain(e => e.Code == "INVALID_ARGUMENT");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(366)]
    public async Task RequestTableAccess_WithInvalidDuration_ThrowsInvalidArgument(int durationDays)
    {
        var accessor = CreateAccessor(new Sid("S-1-5-21-USER-1"));
        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.RequestTableAccessAsync("finance", "dbo", "finance_table_1", "Valid justification here", durationDays, null, _repository, _repository, accessor);
        });
        ex.Errors.ShouldContain(e => e.Code == "INVALID_ARGUMENT");
    }

    [Fact]
    public async Task ApproveConsentRequest_SelfApproval_ThrowsForbiddenDueToFourEyesPrinciple()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        // The requester is also the owner of the table (S-1-5-21-DATAOWNER-1)
        var ownerAndRequesterSid = new Sid("S-1-5-21-DATAOWNER-1");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = ownerAndRequesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = ownerAndRequesterSid.Value,
            BusinessJustification = "Trying to approve myself",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            TenantId = TenantId.LegacySingleTenant
        });

        // Caller is the same requester
        var accessor = CreateAccessor(ownerAndRequesterSid);

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.ApproveConsentRequestAsync(req.Id, null, _repository, _repository, _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN" && e.Message.Contains("Funktionstrennung verletzt"));
    }

    [Fact]
    public async Task ApproveConsentRequest_CrossTenantAccess_ThrowsForbidden()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-1");
        var approverSid = new Sid("S-1-5-21-DATAOWNER-1");

        // Request belongs to tenant-alpha
        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Tenant alpha request",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            TenantId = new TenantId("tenant-alpha")
        });

        // Approver is authenticated in tenant-beta
        var accessor = CreateAccessorWithTenant(approverSid, new TenantId("tenant-beta"));

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.ApproveConsentRequestAsync(req.Id, null, _repository, _repository, _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN" && e.Message.Contains("Mandantenübergreifender Zugriff verboten"));
    }

    [Fact]
    public async Task RejectConsentRequest_WithShortReason_ThrowsInvalidArgument()
    {
        var approverSid = new Sid("S-1-5-21-DATAOWNER-1");
        var accessor = CreateAccessor(approverSid);

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.RejectConsentRequestAsync(Guid.NewGuid(), "no", null, _repository, _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "INVALID_ARGUMENT");
    }

    [Fact]
    public async Task RejectConsentRequest_CrossTenantAccess_ThrowsForbidden()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-2");
        var approverSid = new Sid("S-1-5-21-DATAOWNER-1");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Tenant alpha request to reject",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            TenantId = new TenantId("tenant-alpha")
        });

        var accessor = CreateAccessorWithTenant(approverSid, new TenantId("tenant-beta"));

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.RejectConsentRequestAsync(req.Id, "Valid rejection reason", null, _repository, _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN" && e.Message.Contains("Mandantenübergreifender Zugriff verboten"));
    }

    [Fact]
    public async Task RevokeConsent_CrossTenantAccess_ThrowsForbidden()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var ownerSid = new Sid("S-1-5-21-DATAOWNER-1");
        var granteeSid = new Sid("S-1-5-21-GRANTEE-3");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = granteeSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            TenantId = new TenantId("tenant-alpha")
        });

        var accessor = CreateAccessorWithTenant(ownerSid, new TenantId("tenant-beta"));

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.RevokeConsentAsync(consent.Id, "Valid revocation reason", _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN" && e.Message.Contains("Mandantenübergreifender Zugriff verboten"));
    }

    [Fact]
    public async Task Query_GetGdprDataDisclosureReport_ForForeignSubjectWithoutPrivacyRole_ThrowsForbidden()
    {
        var query = new Query();
        var callerSid = new Sid("S-1-5-21-CALLER-1");
        var foreignSid = "S-1-5-21-FOREIGN-USER";
        var accessor = CreateAccessor(callerSid); // Standard user, no PrivacyAdmin or GovernanceAdmin

        var lineageService = NSubstitute.Substitute.For<ILineageImpactAnalyzerService>();

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await query.GetGdprDataDisclosureReportAsync(
                domain: "finance",
                schema: "dbo",
                tableName: "finance_table_1",
                subjectSid: foreignSid,
                timeWindowDays: 365,
                lineageService: lineageService,
                httpContextAccessor: accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN" && e.Message.Contains("DSGVO-Auskunftsberichte für fremde Identitäten erfordern PrivacyAdmin- oder GovernanceAdmin-Rechte."));
    }

    [Fact]
    public async Task Query_Unauthenticated_ThrowsUnauthorized()
    {
        var query = new Query();
        var unauthenticatedAccessor = new IsolatedHttpContextAccessor(new DefaultHttpContext()); // No User / not authenticated
        var lineageService = NSubstitute.Substitute.For<ILineageImpactAnalyzerService>();

        var ex1 = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await query.CalculateConsentRevocationImpactAsync(Guid.NewGuid(), lineageService, unauthenticatedAccessor);
        });
        ex1.Errors.ShouldContain(e => e.Code == "UNAUTHORIZED");

        var ex2 = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await query.GetTableConsumersAsync("finance", "dbo", "invoices", 30, lineageService, unauthenticatedAccessor);
        });
        ex2.Errors.ShouldContain(e => e.Code == "UNAUTHORIZED");

        var ex3 = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await query.GetGdprDataDisclosureReportAsync("finance", "dbo", "invoices", null, 365, lineageService, unauthenticatedAccessor);
        });
        ex3.Errors.ShouldContain(e => e.Code == "UNAUTHORIZED");
    }

    [Fact]
    public async Task FinanceAndHrQuery_Unauthenticated_ThrowsUnauthorized()
    {
        var unauthenticatedAccessor = new IsolatedHttpContextAccessor(new DefaultHttpContext());
        var executionService = NSubstitute.Substitute.For<IGatewayExecutionService>();

        var finQuery = new FinanceQuery();
        var hrQuery = new HrQuery();

        var ex1 = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await finQuery.GetInvoicesAsync(50, 0, executionService, unauthenticatedAccessor);
        });
        ex1.Errors.ShouldContain(e => e.Code == "UNAUTHORIZED");

        var ex2 = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await finQuery.GetInvoicesWithItemsAsync(10, executionService, unauthenticatedAccessor);
        });
        ex2.Errors.ShouldContain(e => e.Code == "UNAUTHORIZED");

        var ex3 = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await hrQuery.GetEmployeesAsync(50, 0, executionService, unauthenticatedAccessor);
        });
        ex3.Errors.ShouldContain(e => e.Code == "UNAUTHORIZED");
    }

    [Fact]
    public async Task ApproveConsentRequest_ClusterAdminSelfApproval_ThrowsForbidden()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var clusterAdminSid = new Sid("S-1-5-21-CLUSTER-ADMIN");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = clusterAdminSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = clusterAdminSid.Value,
            BusinessJustification = "Admin self approval attempt",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            TenantId = TenantId.LegacySingleTenant
        });

        var accessor = CreateAccessor(clusterAdminSid, "ClusterAdmin");

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.ApproveConsentRequestAsync(req.Id, null, _repository, _repository, _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN" && e.Message.Contains("Funktionstrennung verletzt"));
    }

    [Fact]
    public async Task Query_GetGdprDataDisclosureReport_WhenSubjectSidNull_DefaultsToCallerSelf()
    {
        var query = new Query();
        var callerSid = new Sid("S-1-5-21-STANDARD-CALLER");
        var accessor = CreateAccessor(callerSid);

        var lineageService = NSubstitute.Substitute.For<ILineageImpactAnalyzerService>();

        await query.GetGdprDataDisclosureReportAsync(
            domain: "finance",
            schema: "dbo",
            tableName: "finance_table_1",
            subjectSid: null,
            timeWindowDays: 365,
            lineageService: lineageService,
            httpContextAccessor: accessor);

        await lineageService.Received(1).GetGdprDataDisclosureReportAsync(
            NSubstitute.Arg.Any<TableIdentifier?>(),
            NSubstitute.Arg.Is<Sid?>(s => s.HasValue && s.Value == callerSid),
            NSubstitute.Arg.Any<int>(),
            NSubstitute.Arg.Any<CallerSecurityContext>(),
            NSubstitute.Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public async Task Consents_PersistAndRetrieve_TenantIdCorrectly()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var granteeSid = new Sid("S-1-5-21-TENANT-ISOLATED-GRANTEE");
        var tenantAlpha = new TenantId("tenant-isolated-alpha");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = granteeSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            TenantId = tenantAlpha
        });

        var retrieved = await _repository.GetConsentByIdAsync(consent.Id);
        retrieved.ShouldNotBeNull();
        retrieved.TenantId.ShouldBe(tenantAlpha);
    }

    // ---------- security review 2026-10-05 (recheck 3): E-9, R3-1, R2-5 ----------

    private async Task<ConsentRequest> CreateExternalRequestAsync(string requester)
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        return await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = new Sid(requester),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester,
            BusinessJustification = "ITSM governed request",
            Status = "PENDING_EXTERNAL_APPROVAL",
            ItsmTicketId = $"T-{Guid.NewGuid():N}",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });
    }

    [Fact]
    public async Task E9_PendingExternalApproval_CannotBeApprovedOutsideItsm()
    {
        var req = await CreateExternalRequestAsync("S-1-5-21-REQ-E9");

        // The data owner may not bypass the change board via GraphQL / direct repository call.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _repository.ApproveConsentRequestStepAsync(req.Id, new Sid("S-1-5-21-DATAOWNER-1")));

        (await _repository.GetConsentRequestAsync(req.Id))!.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
    }

    [Fact]
    public async Task R2_5_ItsmApprovalWithoutNamedApprover_OnOwnedTable_IsRejected()
    {
        var req = await CreateExternalRequestAsync("S-1-5-21-REQ-R25");

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            _repository.ApproveConsentRequestStepAsync(req.Id, new Sid("ITSM_SERVICENOW:inst-a"), isExternalItsmApproval: true, itsmApproverAccount: null));
    }

    [Fact]
    public async Task R3_1_ItsmApprovalWithOwnerAccount_IsAccepted_AndForeignApproverIsRejected()
    {
        var foreign = await CreateExternalRequestAsync("S-1-5-21-REQ-R31A");
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            _repository.ApproveConsentRequestStepAsync(foreign.Id, new Sid("ITSM_SERVICENOW:inst-a:someone@corp.local"), true, "someone@corp.local"));

        var req = await CreateExternalRequestAsync("S-1-5-21-REQ-R31B");
        var approved = await _repository.ApproveConsentRequestStepAsync(
            req.Id, new Sid("ITSM_SERVICENOW:inst-a:dataowner@corp.local"), true, "dataowner@corp.local");
        approved.Status.ShouldBe("APPROVED");
    }

    [Fact]
    public async Task E9_ItsmApproval_OnInternalRequest_IsRejected()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta!.Table.Id,
            TableIdentifier = table,
            RequesterSid = new Sid("S-1-5-21-REQ-E9B"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-REQ-E9B",
            BusinessJustification = "internal request",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _repository.ApproveConsentRequestStepAsync(req.Id, new Sid("ITSM_SERVICENOW:inst-a:dataowner@corp.local"), true, "dataowner@corp.local"));
    }
}
