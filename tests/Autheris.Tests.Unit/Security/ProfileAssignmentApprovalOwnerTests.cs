namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ProfileAssignmentApprovalOwnerTests
{
    private const string Tenant = "tenant-approval";
    private const string ProfileId = "profile-unmasked-1";
    private const string CreatorSid = "S-1-5-21-CREATOR";
    private const string ApproverSid = "S-1-5-21-APPROVER";
    private const string GranteeSid = "S-1-5-21-GRANTEE";

    private static AccessProfileAssignment CreateAssignment(string subject, string? approver = null) => new()
    {
        Subject = subject,
        SubjectType = AccessProfileSubjectType.User,
        Kind = AccessProfileAssignmentKind.Standard,
        CreatedBy = CreatorSid,
        ApprovedBy = approver,
        AssignedAt = DateTimeOffset.UtcNow
    };

    private static TableMetadata CreateTable(string db, string schema, string table) => new()
    {
        Identifier = new TableIdentifier(db, schema, table),
        Table = new Table { Id = Guid.NewGuid(), SourceName = db, SchemaName = schema, TableName = table }
    };

    private static (DefaultHttpContext Context, IDataOwnershipRepository Owners, ITableMetadataRepository Tables, IAccessProfileRepository Profiles)
        Setup(AccessProfile profile)
    {
        var owners = Substitute.For<IDataOwnershipRepository>();
        var tables = Substitute.For<ITableMetadataRepository>();
        var profiles = Substitute.For<IAccessProfileRepository>();
        var audit = Substitute.For<IAuditWriter>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(owners);
        services.AddSingleton(tables);
        services.AddSingleton(audit);
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.PrimarySid, ApproverSid),
                new Claim(ClaimTypes.Role, "GovernanceAdmin"),
                new Claim("tenant_id", Tenant)
            ], "TestAuth"))
        };

        profiles.GetProfileAsync(Arg.Is<TenantId>(t => t.Value == Tenant), ProfileId, Arg.Any<CancellationToken>())
            .Returns(profile);

        profiles.ApproveAssignmentAsync(Arg.Is<TenantId>(t => t.Value == Tenant), ProfileId, AccessProfileSubjectType.User, GranteeSid, ApproverSid, Arg.Any<CancellationToken>())
            .Returns(CreateAssignment(GranteeSid, ApproverSid));

        return (context, owners, tables, profiles);
    }

    [Fact]
    public async Task Approve_UnmaskedWithWildcardPattern_Returns403()
    {
        var profile = new AccessProfile
        {
            ProfileId = ProfileId,
            Name = "Unmasked Wildcard",
            TenantId = new TenantId(Tenant),
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["sales.*"], // Wildcard pattern
            Assignments = [CreateAssignment(GranteeSid)]
        };

        var (context, _, _, repo) = Setup(profile);
        var req = new ApproveProfileAssignmentRequest { Subject = GranteeSid, SubjectType = "User" };

        var result = await GovernanceEndpoints.ApproveProfileAssignmentAsync(ProfileId, req, context, repo, CancellationToken.None);

        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.ShouldContain("DATA_OWNER_APPROVAL_REQUIRED");
    }

    [Fact]
    public async Task Approve_UnmaskedWithZeroMatchedTables_Returns403()
    {
        var profile = new AccessProfile
        {
            ProfileId = ProfileId,
            Name = "Unmasked Concrete",
            TenantId = new TenantId(Tenant),
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["sales.public.orders"], // Concrete pattern
            Assignments = [CreateAssignment(GranteeSid)]
        };

        var (context, _, tables, repo) = Setup(profile);
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<TableMetadata>()); // 0 tables matched

        var req = new ApproveProfileAssignmentRequest { Subject = GranteeSid, SubjectType = "User" };
        var result = await GovernanceEndpoints.ApproveProfileAssignmentAsync(ProfileId, req, context, repo, CancellationToken.None);

        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.ShouldContain("DATA_OWNER_APPROVAL_REQUIRED");
    }

    [Fact]
    public async Task Approve_UnmaskedWithOneMatchedTable_ApproverNotOwner_Returns403()
    {
        var profile = new AccessProfile
        {
            ProfileId = ProfileId,
            Name = "Unmasked Concrete",
            TenantId = new TenantId(Tenant),
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["sales.public.orders"],
            Assignments = [CreateAssignment(GranteeSid)]
        };

        var (context, owners, tables, repo) = Setup(profile);
        var tableMeta = CreateTable("sales", "public", "orders");
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([tableMeta]);

        // Approver is NOT owner
        owners.IsAuthorizedApproverForTableAsync(tableMeta.Identifier, Arg.Is<Sid>(s => s.Value == ApproverSid), Arg.Any<CancellationToken>())
            .Returns(false);

        var req = new ApproveProfileAssignmentRequest { Subject = GranteeSid, SubjectType = "User" };
        var result = await GovernanceEndpoints.ApproveProfileAssignmentAsync(ProfileId, req, context, repo, CancellationToken.None);

        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.ShouldContain("DATA_OWNER_APPROVAL_REQUIRED");
    }

    [Fact]
    public async Task Approve_UnmaskedWithOneMatchedTable_ApproverIsOwner_Returns200()
    {
        var profile = new AccessProfile
        {
            ProfileId = ProfileId,
            Name = "Unmasked Concrete",
            TenantId = new TenantId(Tenant),
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["sales.public.orders"],
            Assignments = [CreateAssignment(GranteeSid)]
        };

        var (context, owners, tables, repo) = Setup(profile);
        var tableMeta = CreateTable("sales", "public", "orders");
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([tableMeta]);

        // Approver IS owner
        owners.IsAuthorizedApproverForTableAsync(tableMeta.Identifier, Arg.Is<Sid>(s => s.Value == ApproverSid), Arg.Any<CancellationToken>())
            .Returns(true);

        var req = new ApproveProfileAssignmentRequest { Subject = GranteeSid, SubjectType = "User" };
        var result = await GovernanceEndpoints.ApproveProfileAssignmentAsync(ProfileId, req, context, repo, CancellationToken.None);

        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Approve_MaskedWithZeroMatchedTables_Returns200()
    {
        var profile = new AccessProfile
        {
            ProfileId = ProfileId,
            Name = "Masked Profile",
            TenantId = new TenantId(Tenant),
            MaskingMode = MaskingPolicyMode.Default, // Not Unmasked!
            TargetTables = ["sales.public.orders"],
            Assignments = [CreateAssignment(GranteeSid)]
        };

        var (context, _, tables, repo) = Setup(profile);
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<TableMetadata>()); // 0 tables matched

        var req = new ApproveProfileAssignmentRequest { Subject = GranteeSid, SubjectType = "User" };
        var result = await GovernanceEndpoints.ApproveProfileAssignmentAsync(ProfileId, req, context, repo, CancellationToken.None);

        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }
}
