using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Unit.ConsentRequests;
using HotChocolate;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>PLAN-GOV-158-INC1 WP-158-3: <c>setConsentDescription</c> (decisions 7, 8, 11; SEC-GOV158I1-03..06, 13).</summary>
public sealed class SetConsentDescriptionTests : IDisposable
{
    private const string NotFoundMessage = "The consent was not found.";
    private static readonly TableIdentifier Table = new("finance", "dbo", "finance_table_1");
    private static readonly Sid Alice = new("S-1-5-21-ALICE-SETDESC");

    private readonly SqliteGovernanceRepository _repository = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());
    private readonly Mutation _mutation = new();
    private readonly IDataOwnershipRepository _ownership = Substitute.For<IDataOwnershipRepository>();
    private readonly ServiceProvider _services = DecisionServices.Build();

    private async Task<string?> StoredDescriptionAsync(Guid id)
    {
        var consent = (await _repository.GetConsentByIdAsync(id))!;
        await _repository.LoadDescriptionsAsync([consent]);
        return consent.Description;
    }

    public void Dispose()
    {
        _services.Dispose();
        _repository.Dispose();
    }

    private async Task<Consent> NewConsentAsync(string? description = "before", TableIdentifier? table = null, int validDays = 30, TenantId? tenant = null)
    {
        var tableId = table ?? Table;
        var meta = await _repository.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();
        return await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id, TableIdentifier = tableId, TenantId = tenant ?? TenantId.LegacySingleTenant,
            GranteeType = GranteeType.User, GranteeSid = Alice, Description = description,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(validDays), CreatedBySid = Alice
        });
    }

    private Task<ConsentDto> SetAsync(Guid id, string? description, ClaimsPrincipal principal, IServiceProvider? services = null, ClaimsPrincipal? _ = null) =>
        _mutation.SetConsentDescriptionAsync(id, description, _repository, _repository, _ownership,
            DecisionServices.Accessor(principal, services ?? _services));

    private static ClaimsPrincipal Admin() => DecisionPrincipals.Basic("S-1-5-21-ADMIN-1", "GovernanceAdmin");

    private static ClaimsPrincipal Person(string sid = "S-1-5-21-OWNER-1") => DecisionPrincipals.Basic(sid);

    private async Task ShouldBeNotFoundAsync(Func<Task> act)
    {
        var ex = await Should.ThrowAsync<GraphQLException>(act);
        ex.Errors[0].Code.ShouldBe("NOT_FOUND");
        ex.Errors[0].Message.ShouldBe(NotFoundMessage);
    }

    private async Task<List<(string EventType, string Details, string Actor)>> AuditRowsAsync()
    {
        await using var conn = await _repository.Connections.OpenAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT event_type, details_json, actor_sid FROM AUDIT_LOG_ENTRIES WHERE event_type = 'CONSENT_DESCRIPTION_CHANGED'";
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    [Fact]
    public async Task SetDescription_Admin_Owner_Delegate_Allowed()
    {
        var consent = await NewConsentAsync();
        _ownership.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(false);
        _ownership.IsAuthorizedApproverForTableAsync(consent.TableIdentifier, new Sid("S-1-5-21-OWNER-1"), Arg.Any<CancellationToken>()).Returns(true);

        (await SetAsync(consent.Id, " by admin ", Admin())).Description.ShouldBe("by admin");
        (await SetAsync(consent.Id, "by owner or delegate", Person())).Description.ShouldBe("by owner or delegate");

        (await StoredDescriptionAsync(consent.Id)).ShouldBe("by owner or delegate");
        (await AuditRowsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task SetDescription_Null_Clears()
    {
        var consent = await NewConsentAsync();

        var dto = await SetAsync(consent.Id, "   ", Admin());

        dto.Description.ShouldBeNull();
        (await StoredDescriptionAsync(consent.Id)).ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetDescription_OtherUser_UnknownId_OtherTenant_Revoked_SameNotFound()
    {
        // T-GOV158I1-05: one message for unknown, foreign tenant, revoked, expired and not permitted; no audit row.
        var consent = await NewConsentAsync();
        var foreign = await NewConsentAsync(tenant: new TenantId("tenant-foreign"));
        var revoked = await NewConsentAsync();
        await _repository.RevokeConsentAsync(revoked.Id, Alice, "gone");
        var expired = await NewConsentAsync(validDays: 1);
        await using (var conn = await _repository.Connections.OpenAsync(CancellationToken.None))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE CONSENTS SET valid_to = @t WHERE id = @id";
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
            cmd.Parameters.AddWithValue("@id", expired.Id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        _ownership.IsAuthorizedApproverForTableAsync(default, default, default).ReturnsForAnyArgs(false);

        await ShouldBeNotFoundAsync(() => SetAsync(consent.Id, "x", Person("S-1-5-21-STRANGER")));
        await ShouldBeNotFoundAsync(() => SetAsync(Guid.NewGuid(), "x", Admin()));
        await ShouldBeNotFoundAsync(() => SetAsync(foreign.Id, "x", Admin()));
        await ShouldBeNotFoundAsync(() => SetAsync(revoked.Id, "x", Admin()));
        await ShouldBeNotFoundAsync(() => SetAsync(expired.Id, "x", Admin()));

        (await AuditRowsAsync()).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetDescription_Unauthorised_InvalidText_SameNotFoundAsUnknownId()
    {
        // T-GOV158I1-04 (SEC-GOV158I1-04): no validation oracle before authorisation.
        var consent = await NewConsentAsync();
        _ownership.IsAuthorizedApproverForTableAsync(default, default, default).ReturnsForAnyArgs(false);

        await ShouldBeNotFoundAsync(() => SetAsync(consent.Id, "bad‮text", Person("S-1-5-21-STRANGER")));
        await ShouldBeNotFoundAsync(() => SetAsync(Guid.NewGuid(), "bad‮text", Admin()));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetDescription_AuthorisedButInvalidText_ValidationError_NoEcho()
    {
        var consent = await NewConsentAsync();

        var ex = await Should.ThrowAsync<GraphQLException>(() => SetAsync(consent.Id, "evil‮text", Admin()));

        ex.Errors[0].Code.ShouldBe("VALIDATION_ERROR");
        ex.Errors[0].Message.ShouldNotContain("evil");
        ex.Errors[0].Message.ShouldNotContain("text");
        (await StoredDescriptionAsync(consent.Id)).ShouldBe("before");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ErrorMessages_NeverEchoDescription()
    {
        var consent = await NewConsentAsync();
        var over = "SECRETLONG" + new string('z', 600);

        var ex = await Should.ThrowAsync<GraphQLException>(() => SetAsync(consent.Id, over, Admin()));

        ex.Errors[0].Message.ShouldNotContain("SECRETLONG");
        ex.Errors[0].Code.ShouldBe("VALIDATION_ERROR");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetDescription_AppOnlyMtlsReadOnlyToken_Refused_BeforeLookup()
    {
        // T-GOV158I1-06 (D-7, SEC-GOV158I1-04): the guard runs first; the consent is never loaded.
        var consent = await NewConsentAsync();
        var repo = Substitute.For<IConsentRepository>();
        var descRepo = Substitute.For<IConsentDescriptionRepository>();

        foreach (var principal in new[]
        {
            DecisionPrincipals.AppOnlyIdtyp("oid-1", "GovernanceAdmin"),
            DecisionPrincipals.Certificate("CN=svc", "GovernanceAdmin"),
            DecisionPrincipals.ReadOnly("S-1-5-21-ADMIN-1", "GovernanceAdmin")
        })
        {
            var ex = await Should.ThrowAsync<GraphQLException>(() => _mutation.SetConsentDescriptionAsync(
                consent.Id, "x", repo, descRepo, _ownership, DecisionServices.Accessor(principal, _services)));
            ex.Errors[0].Code.ShouldBe("DECISION_NOT_PERMITTED");
        }

        await repo.DidNotReceiveWithAnyArgs().GetConsentByIdAsync(default, default);
        await descRepo.DidNotReceiveWithAnyArgs().UpdateConsentDescriptionWithAuditAsync(default, default, default, default!, default);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetDescription_OwnerOfOtherTable_Denied()
    {
        // T-GOV158I1-07: the owner check is made against the consent's own table.
        var consent = await NewConsentAsync();
        _ownership.IsAuthorizedApproverForTableAsync(default, default, default).ReturnsForAnyArgs(false);
        _ownership.IsAuthorizedApproverForTableAsync(new TableIdentifier("finance", "dbo", "some_other_table"), new Sid("S-1-5-21-OWNER-1"), Arg.Any<CancellationToken>()).Returns(true);

        await ShouldBeNotFoundAsync(() => SetAsync(consent.Id, "x", Person()));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetDescription_OwnershipLookupThrows_Denied()
    {
        var consent = await NewConsentAsync();
        _ownership.IsAuthorizedApproverForTableAsync(default, default, default).ReturnsForAnyArgs<Task<bool>>(_ => throw new InvalidOperationException("db down"));

        await ShouldBeNotFoundAsync(() => SetAsync(consent.Id, "x", Person()));

        (await StoredDescriptionAsync(consent.Id)).ShouldBe("before");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetDescription_Audit_NoRawText_HashPrefixOnly()
    {
        // T-GOV158I1-03.
        var consent = await NewConsentAsync("OLD-SECRET-TEXT");

        await SetAsync(consent.Id, "NEW-SECRET-TEXT", Admin());

        var rows = await AuditRowsAsync();
        var row = rows.ShouldHaveSingleItem();
        row.Details.ShouldNotContain("SECRET");
        row.Details.ShouldContain("NewLength");
        row.Details.ShouldContain("NewSha256Prefix");
        row.Details.ShouldContain(consent.Id.ToString());
        row.Actor.ShouldBe("S-1-5-21-ADMIN-1");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetDescription_AuditFailure_RollsBackUpdate()
    {
        // T-GOV158I1-03: the update and its audit row share one transaction.
        var consent = await NewConsentAsync();
        await using (var conn = await _repository.Connections.OpenAsync(CancellationToken.None))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER trg_fail_desc_audit BEFORE INSERT ON AUDIT_LOG_ENTRIES WHEN NEW.event_type = 'CONSENT_DESCRIPTION_CHANGED' BEGIN SELECT RAISE(ABORT, 'forced audit failure'); END";
            await cmd.ExecuteNonQueryAsync();
        }

        await Should.ThrowAsync<Exception>(() => SetAsync(consent.Id, "after", Admin()));

        (await StoredDescriptionAsync(consent.Id)).ShouldBe("before");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetDescription_UpdateStatement_TouchesOnlyDescription_NoEpochBump_DecisionUnchanged()
    {
        // T-GOV158I1-08.
        var consent = await NewConsentAsync();
        var epochBefore = await _repository.GetTableEpochAsync(consent.TableIdentifier);
        var before = (await _repository.GetConsentByIdAsync(consent.Id))!;

        await SetAsync(consent.Id, "after", Admin());

        (await _repository.GetTableEpochAsync(consent.TableIdentifier)).ShouldBe(epochBefore);
        var after = (await _repository.GetConsentByIdAsync(consent.Id))!;
        await _repository.LoadDescriptionsAsync([after]);
        after.Description.ShouldBe("after");
        after.ValidFrom.ShouldBe(before.ValidFrom);
        after.ValidTo.ShouldBe(before.ValidTo);
        after.IsRevoked.ShouldBe(before.IsRevoked);
        after.Effect.ShouldBe(before.Effect);
        after.GranteeSid.ShouldBe(before.GranteeSid);
        after.RenewalCount.ShouldBe(before.RenewalCount);
        after.ColumnRules.Count.ShouldBe(before.ColumnRules.Count);
        after.RowFilters.Count.ShouldBe(before.RowFilters.Count);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void ErrorSanitizingFilter_Whitelist_Unchanged_NoInvalidArgument()
    {
        // T-GOV158I1-22 / SEC-GOV158I1-13: setConsentDescription emits only whitelisted codes.
        var source = File.ReadAllText(System.IO.Path.Combine(FindRepoRoot(), "src", "Autheris.Api", "Middleware", "ErrorSanitizingFilter.cs"));
        source.ShouldContain("\"VALIDATION_ERROR\"");
        source.ShouldContain("\"NOT_FOUND\"");
        source.ShouldNotContain("INVALID_ARGUMENT");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
