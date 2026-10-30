namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Validation;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

public class Issue113AccessRequestsSecurityTests
{
    private static TableMetadata CreateSampleMetadata(string domain = "finance", string schema = "dbo", string table = "orders")
    {
        var tid = new TableIdentifier(domain, schema, table);
        return new TableMetadata
        {
            Identifier = tid,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = domain,
                SchemaName = schema,
                TableName = table
            },
            Columns = new List<TableColumn>
            {
                new() { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" },
                new() { Id = Guid.NewGuid(), ColumnName = "region", DataType = "varchar" },
                new() { Id = Guid.NewGuid(), ColumnName = "amount", DataType = "decimal" },
                new() { Id = Guid.NewGuid(), ColumnName = "customer_sid", DataType = "varchar" }
            }
        };
    }

    private static ClaimsPrincipal CreateReadOnlyPrincipal(string sid, params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new(ClaimTypes.NameIdentifier, sid),
            new("oid", Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "test_user")
        };
        if (scopes.Length > 0)
        {
            claims.Add(new Claim("scp", string.Join(" ", scopes)));
        }
        var identity = new ClaimsIdentity(claims, "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreatePersonPrincipal(string sid, params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new(ClaimTypes.NameIdentifier, sid),
            new("oid", Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "person_user")
        };
        if (scopes.Length > 0)
        {
            claims.Add(new Claim("scp", string.Join(" ", scopes)));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ClaimsPrincipal CreateMachineAppPrincipal(string appId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("idtyp", "app"),
                new Claim("appid", appId),
                new Claim("oid", Guid.NewGuid().ToString()),
                new Claim("roles", "AppRole")
            ],
            "Bearer"));
    }

    private static ClaimsPrincipal CreateCertificatePrincipal(string subject)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, subject),
                new Claim(ClaimTypes.AuthenticationMethod, "x509")
            ],
            "Certificate"));
    }

    private static IHttpContextAccessor CreateAccessor(ClaimsPrincipal principal, IServiceProvider services)
    {
        var context = new DefaultHttpContext
        {
            User = principal,
            RequestServices = services
        };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return accessor;
    }

    private static IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        var guard = Substitute.For<IConsentDecisionGuard>();
        guard.Evaluate(Arg.Any<ClaimsPrincipal>()).Returns(callInfo =>
        {
            var p = callInfo.Arg<ClaimsPrincipal>();
            if (p?.IsReadOnly() == true) return ConsentDecisionVerdict.Deny(ConsentDecisionDenialReasons.ReadOnly);
            if (string.Equals(p?.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase))
                return ConsentDecisionVerdict.Deny(ConsentDecisionDenialReasons.NotPerson);
            return ConsentDecisionVerdict.Allow();
        });
        services.AddSingleton(guard);
        var grantPolicy = Substitute.For<IGrantPolicy>();
        grantPolicy.Evaluate(Arg.Any<GrantRequest>()).Returns(new GrantDecision(true, null, false, GrantRecertClass.None, GrantClass.Standing, "Test grant allowed"));
        services.AddSingleton(grantPolicy);
        return services.BuildServiceProvider();
    }

    // ---------------- SEC-TEST-113-01 ----------------
    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_01_ReadOnlyToken_CannotExecuteApproveMutation()
    {
        var services = CreateServices();
        var principal = CreateReadOnlyPrincipal("S-1-5-21-APPROVER", "data.access.review");
        var accessor = CreateAccessor(principal, services);

        var approvalRepo = Substitute.For<IConsentApprovalRepository>();
        var ownershipRepo = Substitute.For<IDataOwnershipRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var metaRepo = Substitute.For<ITableMetadataRepository>();

        var mutation = new Mutation();
        var ex = await Should.ThrowAsync<GraphQLException>(() => mutation.ApproveConsentRequestAsync(
            Guid.NewGuid(), null, approvalRepo, ownershipRepo, consentRepo, metaRepo, accessor));

        ex.Errors.ShouldContain(e => e.Code == "DECISION_NOT_PERMITTED");
        await approvalRepo.DidNotReceiveWithAnyArgs().ApproveConsentRequestStepAsync(default, default);
    }

    // ---------------- SEC-TEST-113-02 ----------------
    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_02_ReadOnlyToken_CannotExecuteRejectMutation()
    {
        var services = CreateServices();
        var principal = CreateReadOnlyPrincipal("S-1-5-21-APPROVER", "data.access.review");
        var accessor = CreateAccessor(principal, services);

        var approvalRepo = Substitute.For<IConsentApprovalRepository>();
        var ownershipRepo = Substitute.For<IDataOwnershipRepository>();

        var mutation = new Mutation();
        var ex = await Should.ThrowAsync<GraphQLException>(() => mutation.RejectConsentRequestAsync(
            Guid.NewGuid(), "Need more detail", null, approvalRepo, ownershipRepo, accessor));

        ex.Errors.ShouldContain(e => e.Code == "DECISION_NOT_PERMITTED");
        await approvalRepo.DidNotReceiveWithAnyArgs().RejectConsentRequestAsync(
            Arg.Any<Guid>(), Arg.Any<Sid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---------------- SEC-TEST-113-03 ----------------
    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_03_ReadOnlyToken_WithoutSelfServiceScope_CannotExecuteRequestTableAccess()
    {
        var principal = CreateReadOnlyPrincipal("S-1-5-21-USER"); // no data.access.request scope
        var result = await ExecuteSelfServiceGraphQLAsync("mutation { requestTableAccess }", principal);

        result.Errors.ShouldNotBeNull();
        result.Errors.ShouldContain(e => e.Code == "READ_ONLY_TOKEN");
    }

    // ---------------- SEC-TEST-113-04 ----------------
    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_04_ReadOnlyToken_WithSelfServiceScope_CanExecuteRequestTableAccess()
    {
        var principal = CreateReadOnlyPrincipal("S-1-5-21-USER", "data.access.request");
        var result = await ExecuteSelfServiceGraphQLAsync("mutation { requestTableAccess }", principal);

        (result.Errors ?? []).ShouldBeEmpty();
        TestMutation.RequestTableAccessCalls.ShouldBe(1);
    }

    // ---------------- SEC-TEST-113-05 ----------------
    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_05_ReadOnlyToken_WithSelfServiceScope_CanExecuteWithdrawAccessRequest()
    {
        var principal = CreateReadOnlyPrincipal("S-1-5-21-USER", "data.access.request");
        var result = await ExecuteSelfServiceGraphQLAsync("mutation { withdrawAccessRequest }", principal);

        (result.Errors ?? []).ShouldBeEmpty();
        TestMutation.WithdrawAccessRequestCalls.ShouldBe(1);
    }

    // ---------------- SEC-TEST-113-06 ----------------
    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_06_ReadOnlyToken_WithSelfServiceScope_CannotExecuteOtherMutations()
    {
        var principal = CreateReadOnlyPrincipal("S-1-5-21-USER", "data.access.request");
        var result = await ExecuteSelfServiceGraphQLAsync("mutation { revokeConsent }", principal);

        result.Errors.ShouldNotBeNull();
        result.Errors.ShouldContain(e => e.Code == "READ_ONLY_TOKEN");
        TestMutation.RevokeConsentCalls.ShouldBe(0);
    }

    // ---------------- SEC-TEST-113-07 ----------------
    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_07_ReadOnlyToken_CannotBypassClassifierViaBatchedOrAliasedMutation()
    {
        var principal = CreateReadOnlyPrincipal("S-1-5-21-USER", "data.access.request");
        // Mixed mutation with allowed and disallowed field
        var doc = "mutation Mixed { a: requestTableAccess b: revokeConsent }";
        var result = await ExecuteSelfServiceGraphQLAsync(doc, principal);

        result.Errors.ShouldNotBeNull();
        result.Errors.ShouldContain(e => e.Code == "READ_ONLY_TOKEN");
        TestMutation.RequestTableAccessCalls.ShouldBe(0);
        TestMutation.RevokeConsentCalls.ShouldBe(0);
    }

    // ---------------- SEC-TEST-113-08 ----------------
    [Theory]
    [InlineData("machine-app")]
    [InlineData("cert")]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_08_MachineTokens_CannotSubmitAccessRequest(string tokenType)
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var approvalRepo = Substitute.For<IConsentApprovalRepository>();
        var service = new ConsentRequestService(metaRepo, approvalRepo);

        var principal = tokenType == "machine-app"
            ? CreateMachineAppPrincipal("app-daemon-1")
            : CreateCertificatePrincipal("CN=service.internal");

        var submission = new ConsentRequestSubmission(
            "finance", "dbo", "orders", "Valid justification", 30, null,
            new Sid("S-1-5-MACHINE"), principal, Autheris.Tests.Common.TestTenants.Default);

        var ex = await Should.ThrowAsync<ConsentRequestException>(() => service.SubmitAsync(submission));
        ex.Code.ShouldBe("FORBIDDEN");
        await approvalRepo.DidNotReceiveWithAnyArgs().CreateConsentRequestAsync(default!);
    }

    // ---------------- SEC-TEST-113-09 ----------------
    [Theory]
    [InlineData("machine-app")]
    [InlineData("cert")]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_09_MachineTokens_CannotWithdrawAccessRequest(string tokenType)
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var approvalRepo = Substitute.For<IConsentApprovalRepository>();
        var service = new ConsentRequestService(metaRepo, approvalRepo);

        var principal = tokenType == "machine-app"
            ? CreateMachineAppPrincipal("app-daemon-1")
            : CreateCertificatePrincipal("CN=service.internal");

        var ex = await Should.ThrowAsync<ConsentRequestException>(() => service.WithdrawAsync(Guid.NewGuid(), principal));
        ex.Code.ShouldBe("FORBIDDEN");
    }

    // ---------------- SEC-TEST-113-10 ----------------
    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_10_UserCannotWithdrawAnotherUsersAccessRequest()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var approvalRepo = Substitute.For<IConsentApprovalRepository>();
        var service = new ConsentRequestService(metaRepo, approvalRepo);

        var requestId = Guid.NewGuid();
        var originalRequester = new Sid("S-1-5-21-ORIGINAL");
        var attacker = CreatePersonPrincipal("S-1-5-21-ATTACKER");

        approvalRepo.GetConsentRequestAsync(requestId).Returns(new ConsentRequest
        {
            Id = requestId,
            RequesterSid = originalRequester,
            Status = "PENDING",
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            RequesterIdentifiers = [originalRequester.Value]
        });

        var ex = await Should.ThrowAsync<ConsentRequestException>(() => service.WithdrawAsync(requestId, attacker));
        ex.Code.ShouldBe("FORBIDDEN");
        await approvalRepo.DidNotReceiveWithAnyArgs().WithdrawConsentRequestAsync(default, default);
    }

    // ---------------- SEC-TEST-113-11 ----------------
    [Theory]
    [InlineData("APPROVED")]
    [InlineData("REJECTED")]
    [InlineData("WITHDRAWN")]
    [InlineData("EXPIRED")]
    [Trait("Category", "Security")]
    public async Task SEC_TEST_113_11_NonPendingAccessRequest_CannotBeWithdrawn(string nonPendingStatus)
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var approvalRepo = Substitute.For<IConsentApprovalRepository>();
        var service = new ConsentRequestService(metaRepo, approvalRepo);

        var requestId = Guid.NewGuid();
        var requesterSid = new Sid("S-1-5-21-USER");
        var principal = CreatePersonPrincipal(requesterSid.Value);

        approvalRepo.GetConsentRequestAsync(requestId).Returns(new ConsentRequest
        {
            Id = requestId,
            RequesterSid = requesterSid,
            Status = nonPendingStatus,
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            RequesterIdentifiers = [requesterSid.Value]
        });

        var ex = await Should.ThrowAsync<ConsentRequestException>(() => service.WithdrawAsync(requestId, principal));
        ex.Code.ShouldBe("INVALID_STATE");
        await approvalRepo.DidNotReceiveWithAnyArgs().WithdrawConsentRequestAsync(default, default);
    }

    // ---------------- SEC-TEST-113-12 ----------------
    [Fact]
    [Trait("Category", "Security")]
    public void SEC_TEST_113_12_RowFilterAstValidator_RejectsInvalidColumns()
    {
        var meta = CreateSampleMetadata();
        var input = new RowFilterInput
        {
            Predicates =
            [
                new() { ColumnName = "credit_card_number", Operator = "=", ValueType = "string", ValueJson = "\"4111\"" }
            ]
        };

        var ex = Should.Throw<ConsentRequestException>(() => RowFilterAstValidator.Validate(input, meta));
        ex.Code.ShouldBe("BAD_REQUEST");
        ex.Message.ShouldContain("credit_card_number");
    }

    // ---------------- SEC-TEST-113-13 ----------------
    [Theory]
    [InlineData("EXEC")]
    [InlineData("DELETE")]
    [InlineData("MATCHES")]
    [InlineData("RLIKE")]
    [Trait("Category", "Security")]
    public void SEC_TEST_113_13_RowFilterAstValidator_RejectsInvalidOperators(string disallowedOp)
    {
        var meta = CreateSampleMetadata();
        var input = new RowFilterInput
        {
            Predicates =
            [
                new() { ColumnName = "region", Operator = disallowedOp, ValueType = "string", ValueJson = "\"EMEA\"" }
            ]
        };

        var ex = Should.Throw<ConsentRequestException>(() => RowFilterAstValidator.Validate(input, meta));
        ex.Code.ShouldBe("BAD_REQUEST");
        ex.Message.ShouldContain(disallowedOp);
    }

    // ---------------- SEC-TEST-113-14 ----------------
    [Theory]
    [InlineData("1; DROP TABLE orders--")]
    [InlineData("' OR '1'='1")]
    [InlineData("(SELECT password FROM users LIMIT 1)")]
    [InlineData("UNION SELECT * FROM accounts")]
    [Trait("Category", "Security")]
    public void SEC_TEST_113_14_RowFilterAstValidator_RejectsSqlInjectionAndSubqueries(string maliciousValue)
    {
        var meta = CreateSampleMetadata();
        var input = new RowFilterInput
        {
            Predicates =
            [
                new() { ColumnName = "region", Operator = "=", ValueType = "string", ValueJson = $"\"{maliciousValue}\"" }
            ]
        };

        var ex = Should.Throw<ConsentRequestException>(() => RowFilterAstValidator.Validate(input, meta));
        ex.Code.ShouldBe("BAD_REQUEST");
    }

    // ---------------- Helper execution pipeline ----------------
    private static async Task<OperationResult> ExecuteSelfServiceGraphQLAsync(string document, ClaimsPrincipal principal)
    {
        TestMutation.Reset();
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<TestQuery>()
            .AddMutationType<TestMutation>()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseRequest<ReadOnlyOperationMiddleware>()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseOperationExecution()
            .BuildRequestExecutorAsync();

        var request = OperationRequestBuilder.New()
            .SetDocument(document)
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();

        return (OperationResult)await executor.ExecuteAsync(request);
    }

    public sealed class TestQuery
    {
        public string Ping() => "pong";
    }

    public sealed class TestMutation
    {
        public static int RequestTableAccessCalls { get; set; }
        public static int WithdrawAccessRequestCalls { get; set; }
        public static int RevokeConsentCalls { get; set; }

        public static void Reset()
        {
            RequestTableAccessCalls = 0;
            WithdrawAccessRequestCalls = 0;
            RevokeConsentCalls = 0;
        }

        public bool RequestTableAccess()
        {
            RequestTableAccessCalls++;
            return true;
        }

        public bool WithdrawAccessRequest()
        {
            WithdrawAccessRequestCalls++;
            return true;
        }

        public bool RevokeConsent()
        {
            RevokeConsentCalls++;
            return true;
        }
    }
}
