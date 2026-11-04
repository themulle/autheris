namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security review (approveConsentRequest): four-eyes follows the canonical rule (flag OR CONFIDENTIAL/HIGH/RESTRICTED/SECRET sensitivity),
/// row filter / column masking are refused on the first step of a four-eyes request (they would be dropped and the second approver could
/// approve unrestricted), and the atomic final approval carries the description.
/// </summary>
public sealed class ApproveConsentRequestFourEyesMutationTests
{
    private static readonly Sid Approver = new("S-1-5-21-APPROVER-1");
    private static readonly Sid Requester = new("S-1-5-21-REQUESTER");

    private static IHttpContextAccessor CreateAccessor()
    {
        var services = new ServiceCollection();
        var guard = Substitute.For<IConsentDecisionGuard>();
        guard.Evaluate(Arg.Any<ClaimsPrincipal>()).Returns(ConsentDecisionVerdict.Allow());
        services.AddSingleton(guard);
        var grantPolicy = Substitute.For<IGrantPolicy>();
        grantPolicy.Evaluate(Arg.Any<GrantRequest>()).Returns(new GrantDecision(true, null, false, GrantRecertClass.None, GrantClass.Standing, "Test grant allowed"));
        services.AddSingleton(grantPolicy);

        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, Approver.Value),
            new(ClaimTypes.NameIdentifier, Approver.Value),
            new(ClaimTypes.Name, "approver_user")
        };
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims.Append(DefaultTenantCaller.Claim), "TestAuth", ClaimTypes.Name, ClaimTypes.Role)),
            RequestServices = services.BuildServiceProvider()
        };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return accessor;
    }

    private static TableMetadata CreateMetadata(string sensitivity, bool requiresFourEyes)
    {
        var tid = new TableIdentifier("finance", "dbo", "orders");
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = tid,
            Table = new Table
            {
                Id = tableId,
                SourceName = tid.Domain,
                SchemaName = tid.Schema,
                TableName = tid.TableName,
                Sensitivity = sensitivity,
                RequiresFourEyes = requiresFourEyes
            },
            Columns = new List<TableColumn>
            {
                new() { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "id", DataType = "int" },
                new() { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "amount", DataType = "decimal" }
            }
        };
    }

    private static ConsentRequest CreateRequest(TableMetadata meta, string status) => new()
    {
        Id = Guid.NewGuid(),
        TableId = meta.Table.Id,
        TableIdentifier = meta.Identifier,
        RequesterSid = Requester,
        RequestedGranteeType = GranteeType.User,
        RequestedGranteeRef = Requester.Value,
        BusinessJustification = "Quarterly reporting",
        RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
        Status = status,
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        RequesterIdentifiers = [Requester.Value]
    };

    private static ColumnMaskingInput Masking() => new()
    {
        Rules = [new ColumnMaskRuleInput { ColumnName = "amount", MaskingType = "Full" }]
    };

    private static (IConsentApprovalRepository Approval, IDataOwnershipRepository Ownership, IConsentRepository Consents, ITableMetadataRepository Metadata) CreateRepositories(
        TableMetadata meta, ConsentRequest req)
    {
        var approval = Substitute.For<IConsentApprovalRepository>();
        approval.GetConsentRequestAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(true);
        var consents = Substitute.For<IConsentRepository>();
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(meta.Identifier, Arg.Any<CancellationToken>()).Returns(meta);
        return (approval, ownership, consents, metadata);
    }

    [Fact]
    public async Task ApproveConsentRequest_HighSensitivityWithoutFlag_FirstCallWithColumnMasking_IsRejected()
    {
        var meta = CreateMetadata("HIGH", requiresFourEyes: false);
        var req = CreateRequest(meta, "PENDING");
        var (approval, ownership, consents, metadata) = CreateRepositories(meta, req);

        var ex = await Should.ThrowAsync<GraphQLException>(() => new Mutation().ApproveConsentRequestAsync(
            req.Id, null, approval, ownership, consents, metadata, CreateAccessor(), null!, columnMasking: Masking()));

        ex.Errors.ShouldContain(e => e.Code == "BAD_REQUEST" && e.Message.Contains("four-eyes"));
        await approval.DidNotReceiveWithAnyArgs().ApproveConsentRequestStepAsync(default, default);
        await approval.DidNotReceiveWithAnyArgs().ExecuteAtomicApprovalWithConsentAsync(default, default!, default!, default!, default!, default);
        await consents.DidNotReceiveWithAnyArgs().CreateConsentAsync(default!, default);
    }

    [Theory]
    [InlineData("HIGH", false)]
    [InlineData("RESTRICTED", false)]
    [InlineData("SECRET", false)]
    [InlineData("LOW", true)]
    public async Task FirstStep_OfFourEyesRequest_WithColumnMasking_IsRejected_AndNothingIsWritten(string sensitivity, bool requiresFourEyes)
    {
        var meta = CreateMetadata(sensitivity, requiresFourEyes);
        var req = CreateRequest(meta, "PENDING");
        var (approval, ownership, consents, metadata) = CreateRepositories(meta, req);

        var ex = await Should.ThrowAsync<GraphQLException>(() => new Mutation().ApproveConsentRequestAsync(
            req.Id, null, approval, ownership, consents, metadata, CreateAccessor(), null!, columnMasking: Masking()));

        ex.Errors.ShouldContain(e => e.Code == "BAD_REQUEST" && e.Message.Contains("four-eyes"));
        await approval.DidNotReceiveWithAnyArgs().ApproveConsentRequestStepAsync(default, default);
        await approval.DidNotReceiveWithAnyArgs().ExecuteAtomicApprovalWithConsentAsync(default, default!, default!, default!, default!, default);
        await consents.DidNotReceiveWithAnyArgs().CreateConsentAsync(default!, default);
    }

    [Fact]
    public async Task FirstStep_OfHighSensitivityTable_WithoutRestrictions_UsesTheStepPath()
    {
        // RequiresFourEyes is false: before the fix the flag alone decided and a HIGH table counted as a single-approval table.
        var meta = CreateMetadata("HIGH", requiresFourEyes: false);
        var req = CreateRequest(meta, "PENDING");
        var (approval, ownership, consents, metadata) = CreateRepositories(meta, req);
        approval.ApproveConsentRequestStepAsync(req.Id, Approver, Arg.Any<CancellationToken>())
            .Returns(_ => { req.Status = "PENDING_SECOND_APPROVAL"; return req; });

        var payload = await new Mutation().ApproveConsentRequestAsync(
            req.Id, null, approval, ownership, consents, metadata, CreateAccessor(), null!);

        payload.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        await approval.DidNotReceiveWithAnyArgs().ExecuteAtomicApprovalWithConsentAsync(default, default!, default!, default!, default!, default);
        await consents.DidNotReceiveWithAnyArgs().CreateConsentAsync(default!, default);
    }

    [Fact]
    public async Task SecondStep_WithColumnMasking_UsesTheAtomicPath_AndPassesTheDescription()
    {
        var meta = CreateMetadata("HIGH", requiresFourEyes: false);
        // The SQLite read model does not populate ApprovalSteps: the status alone marks the recorded first step.
        var req = CreateRequest(meta, "PENDING_SECOND_APPROVAL");
        var (approval, ownership, consents, metadata) = CreateRepositories(meta, req);
        Consent? captured = null;
        approval.ExecuteAtomicApprovalWithConsentAsync(
                req.Id, Arg.Any<ApprovalStep>(), Arg.Do<Consent>(c => captured = c),
                Arg.Any<IReadOnlyList<ConsentRowFilter>>(), Arg.Any<IReadOnlyList<ConsentColumnRule>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Consent>(2));

        var payload = await new Mutation().ApproveConsentRequestAsync(
            req.Id, null, approval, ownership, consents, metadata, CreateAccessor(), null!,
            columnMasking: Masking(), description: "Quarterly close");

        payload.Status.ShouldBe("APPROVED");
        captured.ShouldNotBeNull();
        captured!.Description.ShouldBe("Quarterly close");
        await approval.DidNotReceiveWithAnyArgs().ApproveConsentRequestStepAsync(default, default);
    }

    [Fact]
    public async Task ConsentApproveRevoke_ForeignTenantId_SameAsMissing()
    {
        var meta = CreateMetadata("NORMAL", requiresFourEyes: false);
        var missingRequestId = Guid.NewGuid();
        var foreignRequestId = Guid.NewGuid();

        var foreignReq = new ConsentRequest
        {
            Id = foreignRequestId,
            TableIdentifier = meta.Identifier,
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            TenantId = new TenantId("foreign-tenant"),
            Status = "PENDING"
        };

        var approval = Substitute.For<IConsentApprovalRepository>();
        approval.GetConsentRequestAsync(missingRequestId, Arg.Any<CancellationToken>()).Returns((ConsentRequest?)null);
        approval.GetConsentRequestAsync(foreignRequestId, Arg.Any<CancellationToken>()).Returns(foreignReq);

        var ownership = Substitute.For<IDataOwnershipRepository>();
        var consents = Substitute.For<IConsentRepository>();
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(meta.Identifier, Arg.Any<CancellationToken>()).Returns(meta);

        var accessor = CreateAccessor(); // caller is tenant "default"
        var mutation = new Mutation();

        // 1. Approve missing -> NOT_FOUND
        var missingEx = await Should.ThrowAsync<GraphQLException>(() =>
            mutation.ApproveConsentRequestAsync(missingRequestId, null, approval, ownership, consents, metadata, accessor, null!));
        missingEx.Errors[0].Code.ShouldBe("NOT_FOUND");
        missingEx.Errors[0].Message.ShouldBe($"Consent request '{missingRequestId}' was not found.");

        // 2. Approve foreign tenant -> same NOT_FOUND error code and format as missing
        var foreignEx = await Should.ThrowAsync<GraphQLException>(() =>
            mutation.ApproveConsentRequestAsync(foreignRequestId, null, approval, ownership, consents, metadata, accessor, null!));
        foreignEx.Errors[0].Code.ShouldBe("NOT_FOUND");
        foreignEx.Errors[0].Message.ShouldBe($"Consent request '{foreignRequestId}' was not found.");

        // 3. Revoke missing consent -> NOT_FOUND
        var missingConsentId = Guid.NewGuid();
        consents.GetConsentByIdAsync(missingConsentId, Arg.Any<CancellationToken>()).Returns((Consent?)null);

        var missingRevokeEx = await Should.ThrowAsync<GraphQLException>(() =>
            mutation.RevokeConsentAsync(missingConsentId, "Revoke reason", consents, ownership, accessor, CancellationToken.None));
        missingRevokeEx.Errors[0].Code.ShouldBe("NOT_FOUND");
        missingRevokeEx.Errors[0].Message.ShouldBe($"Consent '{missingConsentId}' was not found.");

        // 4. Revoke foreign consent -> same NOT_FOUND error code and format as missing
        var foreignConsentId = Guid.NewGuid();
        var foreignConsent = new Consent
        {
            Id = foreignConsentId,
            TenantId = new TenantId("foreign-tenant"),
            TableIdentifier = meta.Identifier,
            GranteeType = GranteeType.User,
            GranteeSid = Requester,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        consents.GetConsentByIdAsync(foreignConsentId, Arg.Any<CancellationToken>()).Returns(foreignConsent);

        var foreignRevokeEx = await Should.ThrowAsync<GraphQLException>(() =>
            mutation.RevokeConsentAsync(foreignConsentId, "Revoke reason", consents, ownership, accessor, CancellationToken.None));
        foreignRevokeEx.Errors[0].Code.ShouldBe("NOT_FOUND");
        foreignRevokeEx.Errors[0].Message.ShouldBe($"Consent '{foreignConsentId}' was not found.");
    }
}
