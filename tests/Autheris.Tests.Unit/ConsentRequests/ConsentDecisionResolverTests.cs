using System.Security.Claims;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using HotChocolate;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.ConsentRequests;

/// <summary>
/// WP-0 (PLAN-GOV-113) through the direct approve/reject resolvers on SQLite: W-1..W-5, W-8, W-11..W-14.
/// The three-engine status guard (W-6, W-7, W-9) lives in <see cref="ConsentDecisionStatusGuardContractBase"/>.
/// </summary>
public sealed class ConsentDecisionResolverTests : IDisposable
{
    private const string OwnerSid = "S-1-5-21-DATAOWNER-1";
    private const string Reason = "Not needed for this purpose";
    private static readonly TableIdentifier Table = new("finance", "dbo", "finance_table_1");
    private static readonly TableIdentifier FourEyesTable = new("finance", "dbo", "finance_table_5");

    private readonly SqliteGovernanceRepository _repository = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());
    private readonly Mutation _mutation = new();

    public void Dispose() => _repository.Dispose();

    private async Task<ConsentRequest> NewRequestAsync(
        TableIdentifier? table = null,
        string? requester = null,
        GranteeType grantee = GranteeType.User,
        int validDays = 7)
    {
        table ??= Table;
        var meta = await _repository.GetTableMetadataAsync(table.Value);
        meta.ShouldNotBeNull();
        var sid = requester ?? "S-1-5-21-REQ-" + Guid.NewGuid().ToString("N")[..8];
        return await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table.Value,
            RequesterSid = new Sid(sid),
            RequestedGranteeType = grantee,
            RequestedGranteeRef = grantee == GranteeType.Role ? "Analysts" : sid,
            BusinessJustification = "WP-0 test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(validDays)
        });
    }

    private Task<ConsentRequestPayload> ApproveAsync(Guid id, ClaimsPrincipal principal, ServiceProvider services) =>
        _mutation.ApproveConsentRequestAsync(id, null, _repository, _repository, _repository, DecisionServices.Accessor(principal, services));

    private Task<ConsentRequestPayload> RejectAsync(Guid id, ClaimsPrincipal principal, ServiceProvider services) =>
        _mutation.RejectConsentRequestAsync(id, Reason, null, _repository, _repository, DecisionServices.Accessor(principal, services));

    private static async Task ShouldBeDeniedAsync(Func<Task> action, string code)
    {
        var ex = await Should.ThrowAsync<GraphQLException>(action);
        ex.Errors.ShouldContain(e => e.Code == code);
    }

    private async Task AssertUntouchedAsync(Guid id, string expectedStatus = "PENDING", int expectedSteps = 0)
    {
        var req = await _repository.GetConsentRequestAsync(id);
        req.ShouldNotBeNull();
        req.Status.ShouldBe(expectedStatus);
        (await CountStepsAsync(id)).ShouldBe(expectedSteps);
        using var cmd = _repository.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM CONSENTS WHERE consent_request_id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(0, "no consent may exist");
    }

    private async Task<int> CountStepsAsync(Guid id)
    {
        using var cmd = _repository.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>The repository authorizes admins by role membership (the claim alone only skips the resolver's owner check).</summary>
    private async Task GrantGovernanceAdminAsync(string sid)
    {
        using var cmd = _repository.RawConnection().CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO ROLES (id, role_name, description) VALUES (@rid, 'GovernanceAdmin', 'Governance Administrator');
            INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
            SELECT @m, id, 'User', @sid FROM ROLES WHERE role_name = 'GovernanceAdmin';";
        cmd.Parameters.AddWithValue("@rid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@m", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@sid", sid);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------------- W-1 ----------------

    [Fact]
    public async Task W1_ReadOnlyPrincipal_ApproveAndReject_AreDeniedBeforeAnyRepositoryCall_AndAudited()
    {
        var audit = new RecordingAuditWriter();
        using var services = DecisionServices.Build(audit);
        var approval = Substitute.For<IConsentApprovalRepository>();
        var ownership = Substitute.For<IDataOwnershipRepository>();
        var consents = Substitute.For<IConsentRepository>();
        var principal = DecisionPrincipals.ReadOnly(OwnerSid, "GovernanceAdmin");
        var accessor = DecisionServices.Accessor(principal, services);

        await ShouldBeDeniedAsync(() => _mutation.ApproveConsentRequestAsync(
            Guid.NewGuid(), null, approval, ownership, consents, accessor), "DECISION_NOT_PERMITTED");
        approval.ReceivedCalls().ShouldBeEmpty();
        ownership.ReceivedCalls().ShouldBeEmpty();
        consents.ReceivedCalls().ShouldBeEmpty();

        await ShouldBeDeniedAsync(() => _mutation.RejectConsentRequestAsync(
            Guid.NewGuid(), Reason, null, approval, ownership, accessor), "DECISION_NOT_PERMITTED");
        approval.ReceivedCalls().ShouldBeEmpty();
        ownership.ReceivedCalls().ShouldBeEmpty();

        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("CONSENT_DENIED");
        entry.DetailsJson.ShouldContain("read-only");
    }

    // ---------------- W-2 / W-13 ----------------

    public static TheoryData<string> ApplicationShapes => new() { "idtyp-app", "appid-only", "azp-only", "client-id-only", "service-account", "mtls-certificate" };

    private static ClaimsPrincipal Application(string shape, params string[] roles) => shape switch
    {
        "idtyp-app" => DecisionPrincipals.AppOnlyIdtyp("oid-app-1", roles),
        "appid-only" => DecisionPrincipals.AppIdOnly("app-1", roles),
        "azp-only" => DecisionPrincipals.AzpOnly("app-1", roles),
        "client-id-only" => DecisionPrincipals.ClientIdOnly("app-1", roles),
        "service-account" => DecisionPrincipals.ServiceAccount("svc-1", roles),
        "mtls-certificate" => DecisionPrincipals.Certificate("CN=svc.corp", roles),
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    [Theory]
    [MemberData(nameof(ApplicationShapes))]
    public async Task W2_W13_ApplicationWithGovernanceAdmin_CannotApproveOrReject(string shape)
    {
        using var services = DecisionServices.Build();
        var req = await NewRequestAsync();
        var app = Application(shape, "GovernanceAdmin");

        await ShouldBeDeniedAsync(() => ApproveAsync(req.Id, app, services), "DECISION_NOT_PERMITTED");
        await ShouldBeDeniedAsync(() => RejectAsync(req.Id, app, services), "DECISION_NOT_PERMITTED");

        await AssertUntouchedAsync(req.Id);
    }

    [Fact]
    public async Task W2_PersonPlusApplication_FourEyesCannotComplete()
    {
        using var services = DecisionServices.Build();
        var req = await NewRequestAsync(FourEyesTable);

        var step1 = await ApproveAsync(req.Id, DecisionPrincipals.Basic("S-1-5-21-APPROVER-A"), services);
        step1.Status.ShouldBe("PENDING_SECOND_APPROVAL");

        await ShouldBeDeniedAsync(() => ApproveAsync(req.Id, DecisionPrincipals.AppIdOnly("app-1", "GovernanceAdmin"), services), "DECISION_NOT_PERMITTED");
        await ShouldBeDeniedAsync(() => ApproveAsync(req.Id, DecisionPrincipals.AppOnlyIdtyp("oid-app-1", "GovernanceAdmin"), services), "DECISION_NOT_PERMITTED");

        var after = await _repository.GetConsentRequestAsync(req.Id);
        after!.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        (await CountStepsAsync(req.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task W2_PersonCannotApproveOwnRequestThroughAnApplication()
    {
        using var services = DecisionServices.Build();
        const string person = "S-1-5-21-REQ-OWN";
        var req = await NewRequestAsync(requester: person);
        var app = DecisionPrincipals.AppOnlyIdtyp(person, "GovernanceAdmin");

        await ShouldBeDeniedAsync(() => ApproveAsync(req.Id, app, services), "DECISION_NOT_PERMITTED");

        await AssertUntouchedAsync(req.Id);
    }

    // ---------------- W-3 / W-11 / W-12 ----------------

    public static TheoryData<string> InteractiveShapes => new()
    {
        "basic", "kerberos", "forwardauth", "entra-raw", "entra-mapped", "entra-no-human-name", "entra-mapped-no-human-name", "adfs-mapped", "adfs-unique-name-only"
    };

    private static ClaimsPrincipal Interactive(string shape, string sid, params string[] roles) => shape switch
    {
        "basic" => DecisionPrincipals.Basic(sid, roles),
        "kerberos" => DecisionPrincipals.Kerberos(sid, roles),
        "forwardauth" => DecisionPrincipals.ForwardAuth(sid, roles),
        "entra-raw" => DecisionPrincipals.EntraDelegatedRaw(sid, roles),
        "entra-mapped" => DecisionPrincipals.EntraDelegatedMapped(sid, roles),
        "entra-no-human-name" => DecisionPrincipals.EntraDelegatedNoHumanName(sid, roles),
        "entra-mapped-no-human-name" => DecisionPrincipals.EntraDelegatedMappedNoHumanName(sid, roles),
        "adfs-mapped" => DecisionPrincipals.AdfsMapped(sid, roles),
        "adfs-unique-name-only" => DecisionPrincipals.AdfsMappedUniqueNameOnly(sid, roles),
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private async Task<string> EnsureDelegateAsync(string delegateSid)
    {
        var owners = await _repository.GetDataOwnersForTableAsync(Table);
        var primary = owners.First(o => o.AdSid == OwnerSid);
        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            Id = Guid.NewGuid(),
            DataOwnerId = primary.Id,
            DelegateSid = new Sid(delegateSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Reason = "W-3 delegate"
        });
        return delegateSid;
    }

    [Theory]
    [MemberData(nameof(InteractiveShapes))]
    public async Task W3_W11_W12_InteractivePrincipals_OwnerDelegateAndAdmin_CanApprove(string shape)
    {
        using var services = DecisionServices.Build();
        var delegateSid = await EnsureDelegateAsync("S-1-5-21-DELEGATE-W3");
        await GrantGovernanceAdminAsync("S-1-5-21-ADMIN-W3");

        foreach (var principal in new[]
        {
            Interactive(shape, OwnerSid),
            Interactive(shape, delegateSid),
            Interactive(shape, "S-1-5-21-ADMIN-W3", "GovernanceAdmin"),
        })
        {
            var req = await NewRequestAsync();
            var result = await ApproveAsync(req.Id, principal, services);
            result.Status.ShouldBe("APPROVED");
        }
    }

    [Theory]
    [MemberData(nameof(InteractiveShapes))]
    public async Task W3_W11_W12_InteractivePrincipals_OwnerDelegateAndAdmin_CanReject(string shape)
    {
        using var services = DecisionServices.Build();
        var delegateSid = await EnsureDelegateAsync("S-1-5-21-DELEGATE-W3");
        await GrantGovernanceAdminAsync("S-1-5-21-ADMIN-W3");

        foreach (var principal in new[]
        {
            Interactive(shape, OwnerSid),
            Interactive(shape, delegateSid),
            Interactive(shape, "S-1-5-21-ADMIN-W3", "GovernanceAdmin"),
        })
        {
            var req = await NewRequestAsync();
            var result = await RejectAsync(req.Id, principal, services);
            result.Status.ShouldBe("REJECTED");
        }
    }

    [Fact]
    public async Task W11_AzpOnlyAndIdtypAppTokensWithoutScope_AreDenied_ButTheDelegatedTokenWithScopeIsAllowed()
    {
        using var services = DecisionServices.Build();
        var req = await NewRequestAsync();

        await ShouldBeDeniedAsync(() => ApproveAsync(req.Id, DecisionPrincipals.AzpOnly(OwnerSid, "GovernanceAdmin"), services), "DECISION_NOT_PERMITTED");
        await ShouldBeDeniedAsync(() => ApproveAsync(req.Id, DecisionPrincipals.AppOnlyIdtyp(OwnerSid, "GovernanceAdmin"), services), "DECISION_NOT_PERMITTED");

        var result = await ApproveAsync(req.Id, DecisionPrincipals.EntraDelegatedNoHumanName(OwnerSid), services);
        result.Status.ShouldBe("APPROVED");
    }

    // ---------------- condition 3: failure after the final step ----------------

    [Fact]
    public async Task Condition3_ConsentCreationFailsAfterTheFinalStep_LogsIdsOnlyAtError_AndActivationRecoversTheRequest()
    {
        var logs = new CapturingLoggerProvider();
        using var services = DecisionServices.Build(logs: logs);
        var req = await NewRequestAsync();
        var consents = Substitute.For<IConsentRepository>();
        consents.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns<Task<Consent>>(_ => throw new TimeoutException("db down while storing " + req.BusinessJustification + " for " + req.RequestedGranteeRef));

        var thrown = await Should.ThrowAsync<TimeoutException>(() => _mutation.ApproveConsentRequestAsync(
            req.Id, null, _repository, _repository, consents, DecisionServices.Accessor(DecisionPrincipals.Basic(OwnerSid), services)));
        thrown.ShouldNotBeNull();

        // The final step is committed: APPROVED without a consent.
        (await _repository.GetConsentRequestAsync(req.Id))!.Status.ShouldBe("APPROVED");
        (await CountStepsAsync(req.Id)).ShouldBe(1);

        // One Error entry: request id, tenant id and the exception type; no exception object, no justification, grantee or message text.
        var error = logs.Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldContain(req.Id.ToString());
        error.Message.ShouldContain(req.TenantId.Value);
        error.Message.ShouldContain(nameof(TimeoutException));
        error.Message.ShouldNotContain(req.BusinessJustification);
        error.Message.ShouldNotContain(req.RequestedGranteeRef);
        error.Message.ShouldNotContain("db down");
        error.Exception.ShouldBeNull();

        // Recoverable by the existing activation (status 'APPROVED' path).
        await _repository.ActivateConsentAsync(req.Id, new Sid(OwnerSid));
        using var cmd = _repository.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM CONSENTS WHERE consent_request_id = @id";
        cmd.Parameters.AddWithValue("@id", req.Id.ToString());
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(1, "activation completes the consent");
        (await _repository.GetConsentRequestAsync(req.Id))!.Status.ShouldBe("APPROVED");
    }

    // ---------------- W-4 / W-14 composition ----------------

    [Fact]
    public async Task W4_GuardMissingFromDi_BothResolversThrow_NeverAllow()
    {
        using var services = DecisionServices.Build(withGuard: false);
        var req = await NewRequestAsync();
        var owner = DecisionPrincipals.Basic(OwnerSid);

        await Should.ThrowAsync<InvalidOperationException>(() => ApproveAsync(req.Id, owner, services));
        await Should.ThrowAsync<InvalidOperationException>(() => RejectAsync(req.Id, owner, services));

        await AssertUntouchedAsync(req.Id);
    }

    [Fact]
    public async Task W4_MissingRequestServices_BothResolversThrow_NeverAllow()
    {
        var req = await NewRequestAsync();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = DecisionPrincipals.Basic(OwnerSid) };
        var accessor = new TestHttpContextAccessor(context);

        await Should.ThrowAsync<InvalidOperationException>(() => _mutation.ApproveConsentRequestAsync(req.Id, null, _repository, _repository, _repository, accessor));
        await Should.ThrowAsync<InvalidOperationException>(() => _mutation.RejectConsentRequestAsync(req.Id, Reason, null, _repository, _repository, accessor));

        await AssertUntouchedAsync(req.Id);
    }

    [Fact]
    public async Task W14_GrantPolicyMissingFromDi_BothResolversThrow()
    {
        using var services = DecisionServices.Build(withGrantPolicy: false);
        var req = await NewRequestAsync();
        var owner = DecisionPrincipals.Basic(OwnerSid);

        await Should.ThrowAsync<InvalidOperationException>(() => ApproveAsync(req.Id, owner, services));
        await Should.ThrowAsync<InvalidOperationException>(() => RejectAsync(req.Id, owner, services));

        await AssertUntouchedAsync(req.Id);
    }

    [Fact]
    public async Task W14_ConfiguredStricterIndividualGrantOptions_AreHonouredOnThePreStepEvaluation()
    {
        using var services = DecisionServices.Build(grantOptions: new IndividualGrantOptions { RegularMaxDays = 3, SensitiveMaxDays = 3 });
        var req = await NewRequestAsync(validDays: 7);

        await ShouldBeDeniedAsync(() => ApproveAsync(req.Id, DecisionPrincipals.Basic(OwnerSid), services), GrantErrorCodes.ExceedsMaximum);

        await AssertUntouchedAsync(req.Id);
    }

    // ---------------- W-5 ----------------

    [Fact]
    public async Task W5_GrantPolicyDenial_AnswersBeforeAnyWrite_AndTheRequestStaysRejectable()
    {
        using var services = DecisionServices.Build();
        var req = await NewRequestAsync(validDays: 400);

        await ShouldBeDeniedAsync(() => ApproveAsync(req.Id, DecisionPrincipals.Basic(OwnerSid), services), GrantErrorCodes.ExceedsMaximum);
        await AssertUntouchedAsync(req.Id);

        var rejected = await RejectAsync(req.Id, DecisionPrincipals.Basic(OwnerSid), services);
        rejected.Status.ShouldBe("REJECTED");
    }

    [Fact]
    public async Task W5_TableSensitivityRaisedAfterSubmission_IsDeniedBeforeTheStep()
    {
        using var services = DecisionServices.Build();
        var table = await NewTableAsync(fourEyes: false);
        var req = await NewRequestAsync(table, validDays: 120); // fine for a regular table (180 days), too long for a sensitive one (90 days)
        var meta = await _repository.GetTableMetadataAsync(table);
        GrantClassifier.Classify([meta]).ShouldBe(GrantSensitivity.Regular, "the precondition: the table is regular at submission");

        await SetColumnSensitivityAsync(table, sensitive: true);

        await ShouldBeDeniedAsync(() => ApproveAsync(req.Id, DecisionPrincipals.Basic(AdminSid), services), GrantErrorCodes.ExceedsMaximum);
        await AssertUntouchedAsync(req.Id);
        (await RejectAsync(req.Id, DecisionPrincipals.Basic(AdminSid), services)).Status.ShouldBe("REJECTED");
    }

    [Fact]
    public async Task W5_UnknownMetadata_StaysSensitive_SoTheSensitiveMaximumApplies()
    {
        using var services = DecisionServices.Build();
        var table = await NewTableAsync(fourEyes: false);
        var req = await NewRequestAsync(table, validDays: 120);
        var accessor = DecisionServices.Accessor(DecisionPrincipals.Basic(AdminSid), services);

        // No metadata repository: GrantClassifier.Classify([null]) is Sensitive (90 days), so 120 days is refused.
        await ShouldBeDeniedAsync(() => _mutation.ApproveConsentRequestAsync(
            req.Id, null, _repository, _repository, _repository, null!, accessor, null!, default), GrantErrorCodes.ExceedsMaximum);

        await AssertUntouchedAsync(req.Id);
    }

    [Fact]
    public async Task W5_SecondStepIsEvaluatedBeforeTheStepToo()
    {
        using var first = DecisionServices.Build();
        using var tightened = DecisionServices.Build(grantOptions: new IndividualGrantOptions { SensitiveMaxDays = 30 });
        var table = await NewTableAsync(fourEyes: true);
        var req = await NewRequestAsync(table, validDays: 60); // four-eyes tables are sensitive (90 days): fine for step 1

        var step1 = await ApproveAsync(req.Id, DecisionPrincipals.Basic("S-1-5-21-FE-APPROVER-A"), first);
        step1.Status.ShouldBe("PENDING_SECOND_APPROVAL");

        // The operator tightened the class maximum between the two steps: the second step is denied before it writes anything.
        await ShouldBeDeniedAsync(() => ApproveAsync(req.Id, DecisionPrincipals.Basic("S-1-5-21-FE-APPROVER-B"), tightened), GrantErrorCodes.ExceedsMaximum);

        var after = await _repository.GetConsentRequestAsync(req.Id);
        after!.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        (await CountStepsAsync(req.Id)).ShouldBe(1);

        (await RejectAsync(req.Id, DecisionPrincipals.Basic("S-1-5-21-FE-APPROVER-B"), tightened)).Status.ShouldBe("REJECTED");
    }

    private const string AdminSid = "S-1-5-21-FE-APPROVER-A";

    /// <summary>A fresh table (regular unless four-eyes) whose approvers are governance admins, so repository authorization holds.</summary>
    private async Task<TableIdentifier> NewTableAsync(bool fourEyes)
    {
        await GrantGovernanceAdminAsync("S-1-5-21-FE-APPROVER-A");
        await GrantGovernanceAdminAsync("S-1-5-21-FE-APPROVER-B");
        var id = new TableIdentifier("sales", "crm", "w5_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table { Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, Sensitivity = "LOW", RequiresFourEyes = fourEyes, IsActive = true };
        await _repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        });
        return id;
    }

    /// <summary>Changes the catalog metadata through the repository (a raw UPDATE would bypass its metadata cache).</summary>
    private async Task SetColumnSensitivityAsync(TableIdentifier table, bool sensitive)
    {
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();
        await _repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = meta.Identifier,
            Table = meta.Table,
            Columns = [new TableColumn { TableId = meta.Table.Id, ColumnName = "id", DataType = "INT", IsSensitive = sensitive }]
        });
    }

    [Theory]
    [InlineData(GranteeType.Role)]
    [InlineData(GranteeType.Group)]
    public async Task W5_RoleAndGroupGrantees_StillSkipThePolicy(GranteeType grantee)
    {
        using var services = DecisionServices.Build();
        var req = await NewRequestAsync(grantee: grantee, validDays: 400);

        var result = await ApproveAsync(req.Id, DecisionPrincipals.Basic(OwnerSid), services);

        result.Status.ShouldBe("APPROVED");
    }

    [Fact]
    public async Task W5_ValidRequest_ApprovesAsBefore_AndCreatesTheConsent()
    {
        using var services = DecisionServices.Build();
        var req = await NewRequestAsync(validDays: 7);

        var result = await ApproveAsync(req.Id, DecisionPrincipals.Basic(OwnerSid), services);

        result.Status.ShouldBe("APPROVED");
        using var cmd = _repository.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM CONSENTS WHERE consent_request_id = @id";
        cmd.Parameters.AddWithValue("@id", req.Id.ToString());
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(1);
    }

    // ---------------- W-13 denial audit through the resolver ----------------

    [Fact]
    public async Task W13_DenialAudit_IsAggregatedPerPrincipalAndMinute()
    {
        var audit = new RecordingAuditWriter();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T10:00:10Z"));
        using var services = DecisionServices.Build(audit, time: time);
        var app = DecisionPrincipals.Certificate("CN=svc.corp", "GovernanceAdmin");

        await ShouldBeDeniedAsync(() => ApproveAsync(Guid.NewGuid(), app, services), "DECISION_NOT_PERMITTED");
        await ShouldBeDeniedAsync(() => RejectAsync(Guid.NewGuid(), app, services), "DECISION_NOT_PERMITTED");

        audit.Entries.ShouldHaveSingleItem().EventType.ShouldBe("CONSENT_DENIED");

        time.Advance(TimeSpan.FromMinutes(1));
        await ShouldBeDeniedAsync(() => ApproveAsync(Guid.NewGuid(), app, services), "DECISION_NOT_PERMITTED");
        audit.Entries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task W13_AuditWriterFailure_StillAnswersDecisionNotPermitted()
    {
        var audit = new RecordingAuditWriter { ThrowOnWrite = true };
        using var services = DecisionServices.Build(audit);
        var app = DecisionPrincipals.ServiceAccount("svc-1", "GovernanceAdmin");

        await ShouldBeDeniedAsync(() => ApproveAsync(Guid.NewGuid(), app, services), "DECISION_NOT_PERMITTED");
        await ShouldBeDeniedAsync(() => RejectAsync(Guid.NewGuid(), app, services), "DECISION_NOT_PERMITTED");
    }

    [Fact]
    public async Task W10_UnauthenticatedPrincipal_IsDeniedByTheGuard_NotByTheLaterSidCheck_WithoutRepositoryCall()
    {
        var audit = new RecordingAuditWriter();
        using var services = DecisionServices.Build(audit);
        var approval = Substitute.For<IConsentApprovalRepository>();
        var ownership = Substitute.For<IDataOwnershipRepository>();
        var consents = Substitute.For<IConsentRepository>();
        var accessor = DecisionServices.Accessor(DecisionPrincipals.Unauthenticated(), services);

        await ShouldBeDeniedAsync(() => _mutation.ApproveConsentRequestAsync(Guid.NewGuid(), null, approval, ownership, consents, accessor), "DECISION_NOT_PERMITTED");
        await ShouldBeDeniedAsync(() => _mutation.RejectConsentRequestAsync(Guid.NewGuid(), Reason, null, approval, ownership, accessor), "DECISION_NOT_PERMITTED");

        approval.ReceivedCalls().ShouldBeEmpty();
        ownership.ReceivedCalls().ShouldBeEmpty();
        consents.ReceivedCalls().ShouldBeEmpty();
    }

    // ---------------- W-8 / W-7 source-level ----------------

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(System.IO.Path.Combine(current.FullName, "Autheris.sln"))) return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("Repo root not found");
    }

    private static string ResolverBody(string source, string signatureStart)
    {
        var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, signatureStart);
        var open = source.IndexOf("\n    {", start, StringComparison.Ordinal);
        var end = source.IndexOf("\n    }\n", open, StringComparison.Ordinal);
        return source[(open + 6)..end];
    }

    [Theory]
    [InlineData("public async Task<ConsentRequestPayload> ApproveConsentRequestAsync(\n        Guid requestId,\n        string? idempotencyKey,\n        [Service] IConsentApprovalRepository approvalRepository = default!,")]
    [InlineData("public async Task<ConsentRequestPayload> RejectConsentRequestAsync(\n        Guid requestId,\n        string reason,\n        string? idempotencyKey,\n        [Service] IConsentApprovalRepository approvalRepository = default!,")]
    public void W8_FirstStatementOfBothResolvers_IsTheGuardCall(string signature)
    {
        var source = File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Autheris.GraphQL", "Types", "MutationTypes.cs")).Replace("\r\n", "\n");
        var body = ResolverBody(source, signature);

        var firstStatement = body.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));

        firstStatement.ShouldStartWith("await EnsureDecisionPermittedAsync(");
        body.ShouldNotContain("?? new");
        body.ShouldNotContain("GetService<");
        body.ShouldNotContain("GetService(");
        body.IndexOf("EnsureDecisionPermittedAsync(", StringComparison.Ordinal)
            .ShouldBeLessThan(body.IndexOf("GetAuthenticatedUserSid(", StringComparison.Ordinal));
    }

    [Fact]
    public void W8_ApproveResolver_EvaluatesThePolicyBeforeTheStep()
    {
        var source = File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Autheris.GraphQL", "Types", "MutationTypes.cs")).Replace("\r\n", "\n");
        var body = ResolverBody(source, "public async Task<ConsentRequestPayload> ApproveConsentRequestAsync(\n        Guid requestId,\n        string? idempotencyKey,\n        [Service] IConsentApprovalRepository approvalRepository = default!,");

        body.IndexOf("policy.Evaluate(", StringComparison.Ordinal).ShouldBeGreaterThan(-1);
        body.IndexOf("policy.Evaluate(", StringComparison.Ordinal)
            .ShouldBeLessThan(body.IndexOf("ApproveConsentRequestStepAsync(", StringComparison.Ordinal));
    }

    [Fact]
    public void W7_ItsmWebhookHandler_DoesNotUseTheDecisionGuard()
    {
        var source = File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Autheris.Extensions", "Itsm", "ItsmWebhookHandler.cs"));

        source.ShouldNotContain("IConsentDecisionGuard");
        source.ShouldNotContain("ConsentDecisionGuard");
    }
}
