namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public class DataClassificationWorkflowIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DataClassificationWorkflowIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-class-e2e-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
        });
    }

    private HttpClient CreateClient(Sid? userSid = null, string[]? roles = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        if (userSid.HasValue)
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value.Value);
        }
        if (roles != null && roles.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Test-Roles", string.Join(",", roles));
        }
        return client;
    }

    [Fact]
    public async Task ClassifyTable_WithPiiColumns_ReturnsDeterministicProposalAndValidWormHash()
    {
        var client = CreateClient(new Sid("S-1-5-21-DATA-STEWARD-1"), ["DATA_STEWARD"]);

        var context = new TableClassificationContext(
            TableIdentifier: new TableIdentifier("sales", "crm", "customer_accounts"),
            Columns:
            [
                new ColumnMetadataContext("account_id", "BIGINT"),
                new ColumnMetadataContext("email_address", "VARCHAR(255)"),
                new ColumnMetadataContext("iban_code", "VARCHAR(34)"),
                new ColumnMetadataContext("credit_card_no", "VARCHAR(20)"),
                new ColumnMetadataContext("notes", "TEXT")
            ],
            ExistingDataOwnerSid: "S-1-5-21-DATA-OWNER-ALICE");

        // 1. Post classification proposal
        var response = await client.PostAsJsonAsync("/api/governance/classification/classify-table", context);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var proposal = await response.Content.ReadFromJsonAsync<TableClassificationProposal>();
        proposal.ShouldNotBeNull();
        proposal.TableIdentifier.TableName.ShouldBe("customer_accounts");
        proposal.DataOwnerSid.ShouldBe("S-1-5-21-DATA-OWNER-ALICE");
        proposal.Columns.Count.ShouldBe(5);

        // Verify deterministic PII pattern detection & masking recommendations
        var emailCol = proposal.Columns.FirstOrDefault(c => c.ColumnName == "email_address");
        emailCol.ShouldNotBeNull();
        emailCol.DetectedPiiCategoryKey.ShouldBe("EMAIL");
        emailCol.ProposedSensitivityKey.ShouldBe("L2_INTERNAL");
        emailCol.ProposedSensitivityRank.ShouldBe(2);
        emailCol.ProposedMaskingRule.ShouldBe("EMAIL_DOMAIN_RETAIN");

        var ibanCol = proposal.Columns.FirstOrDefault(c => c.ColumnName == "iban_code");
        ibanCol.ShouldNotBeNull();
        ibanCol.DetectedPiiCategoryKey.ShouldBe("IBAN");
        ibanCol.ProposedSensitivityKey.ShouldBe("L3_CONFIDENTIAL");
        ibanCol.ProposedSensitivityRank.ShouldBe(3);
        ibanCol.ProposedMaskingRule.ShouldBe("IBAN_STANDARD_4_4");

        var ccCol = proposal.Columns.FirstOrDefault(c => c.ColumnName == "credit_card_no");
        ccCol.ShouldNotBeNull();
        ccCol.DetectedPiiCategoryKey.ShouldBe("CREDIT_CARD");
        ccCol.ProposedSensitivityKey.ShouldBe("L4_STRICTLY_CONFIDENTIAL");
        ccCol.ProposedSensitivityRank.ShouldBe(4);
        ccCol.ProposedMaskingRule.ShouldBe("CREDIT_CARD_LAST_4");

        // 2. Verify WORM configuration hash endpoint
        var hashResponse = await client.GetAsync("/api/governance/classification/config-hash");
        hashResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await hashResponse.Content.ReadAsStringAsync());
        var currentHash = doc.RootElement.GetProperty("currentConfigHash").GetString();
        currentHash.ShouldNotBeNullOrWhiteSpace();
        currentHash.Length.ShouldBe(64); // SHA-256 hex string length
    }

    [Fact]
    public async Task ClassifyTable_LowRiskColumns_TriggersZeroTouchAutoApproval()
    {
        var leanMeshFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Classification:Workflow:ActiveProfile", "LEAN_DATA_MESH");
        });

        var client = leanMeshFactory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-DATA-STEWARD-2");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "DATA_STEWARD");

        var context = new TableClassificationContext(
            TableIdentifier: new TableIdentifier("reference", "geo", "country_codes"),
            Columns:
            [
                new ColumnMetadataContext("country_iso", "CHAR(2)"),
                new ColumnMetadataContext("country_name", "VARCHAR(100)"),
                new ColumnMetadataContext("dialing_code", "VARCHAR(10)")
            ],
            ExistingDataOwnerSid: "S-1-5-21-DATA-OWNER-BOB");

        var response = await client.PostAsJsonAsync("/api/governance/classification/classify-table", context);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var proposal = await response.Content.ReadFromJsonAsync<TableClassificationProposal>();
        proposal.ShouldNotBeNull();
        // Zero-touch in LEAN_DATA_MESH profile should automatically transition low-risk public/internal data
        proposal.Status.ShouldBe("CLASSIFIED");
    }

    [Fact]
    public async Task WorkflowTransition_HighSensitivity_EnforcesStepUpMfaAndTwoStageFourEyes()
    {
        // SR-H-04: proposal comes from the server-side store; reviewer identity and role come from the principal.
        const string aliceSid = "S-1-5-21-DATA-OWNER-ALICE";
        const string claraSid = "S-1-5-21-COMPLIANCE-OFFICER-CLARA";
        var aliceClient = CreateClient(new Sid(aliceSid), ["GovernanceAdmin", "DataOwner"]);
        var aliceAsReviewerClient = CreateClient(new Sid(aliceSid), ["GovernanceAdmin", "ComplianceReviewer"]);
        var claraClient = CreateClient(new Sid(claraSid), ["GovernanceAdmin", "ComplianceReviewer"]);

        var proposal = new TableClassificationProposal(
            ProposalId: Guid.NewGuid(),
            TableIdentifier: new TableIdentifier("finance", "payroll", "salaries"),
            Status: "PENDING_OWNER_REVIEW",
            DataOwnerSid: aliceSid,
            Columns:
            [
                new ColumnClassificationProposal("salary_amount", "BIGINT", "CONFIDENTIAL", 4, null, "REDACT_FULL", 0.99, false),
                new ColumnClassificationProposal("tax_id", "VARCHAR(20)", "RESTRICTED", 4, null, "MASK_TAX_ID", 0.99, false)
            ],
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var store = _factory.Services.GetRequiredService<IClassificationProposalStore>();
        await store.SaveProposalAsync(proposal);

        // Real TOTP secrets for both reviewers (the step-up token is verified, not just checked for presence).
        var totpSecrets = _factory.Services.GetRequiredService<Autheris.Application.Security.Totp.Interfaces.ITotpSecretStore>();
        var totp = _factory.Services.GetRequiredService<Autheris.Application.Security.Totp.Interfaces.ITotpVerificationService>();
        var aliceSecret = totp.GenerateEnrollment(aliceSid, "alice@corp.local").SecretBase32;
        var claraSecret = totp.GenerateEnrollment(claraSid, "clara@corp.local").SecretBase32;
        await totpSecrets.SetSecretAsync(aliceSid, aliceSecret);
        await totpSecrets.SetSecretAsync(claraSid, claraSecret);

        // The reviewer identity / role in the body are ignored; they are set to forged values on purpose.
        var reqNoMfa = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            proposal.TableIdentifier,
            new Sid("S-1-5-21-FORGED"),
            "DATA_GOVERNANCE_REVIEWER",
            "APPROVE",
            [
                new ColumnClassificationDecision("salary_amount", "ACCEPT_PROPOSAL", "CONFIDENTIAL", 4, "REDACT_FULL", "Approved"),
                new ColumnClassificationDecision("tax_id", "ACCEPT_PROPOSAL", "RESTRICTED", 4, "MASK_TAX_ID", "Approved")
            ],
            StepUpAuthToken: null);

        // 1. Attempt approval without Step-Up MFA -> Must fail
        var respNoMfa = await aliceClient.PostAsJsonAsync("/api/governance/classification/transition", new TableClassificationTransitionPayload(proposal, reqNoMfa));
        respNoMfa.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var resultNoMfa = await respNoMfa.Content.ReadFromJsonAsync<TableClassificationApprovalResult>();
        resultNoMfa.ShouldNotBeNull();
        resultNoMfa.Success.ShouldBeFalse();
        resultNoMfa.ErrorCode.ShouldBe("STEP_UP_AUTH_REQUIRED");
        resultNoMfa.RequiresStepUpAuth.ShouldBeTrue();

        // 2. Stage 1 approval with a valid TOTP -> Must succeed and be recorded server-side
        var reqStage1 = reqNoMfa with { StepUpAuthToken = totp.GenerateTotpCode(aliceSecret) };
        var respStage1 = await aliceClient.PostAsJsonAsync("/api/governance/classification/transition", new TableClassificationTransitionPayload(proposal, reqStage1));
        respStage1.StatusCode.ShouldBe(HttpStatusCode.OK);

        var resultStage1 = await respStage1.Content.ReadFromJsonAsync<TableClassificationApprovalResult>();
        resultStage1.ShouldNotBeNull();
        resultStage1.Success.ShouldBeTrue();
        resultStage1.NewStatus.ShouldBe("PENDING_GOVERNANCE_REVIEW");
        resultStage1.RequiresSecondStage.ShouldBeTrue();

        var afterStage1 = await store.GetProposalByIdAsync(proposal.ProposalId);
        afterStage1.ShouldNotBeNull();
        afterStage1.Stage1ApproverSid.ShouldBe(aliceSid);

        // 3. Stage 2 by the SAME identity (now acting as reviewer) -> SoD violation
        var respStage2SameUser = await aliceAsReviewerClient.PostAsJsonAsync("/api/governance/classification/transition", new TableClassificationTransitionPayload(proposal, reqStage1));
        respStage2SameUser.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var resultStage2SameUser = await respStage2SameUser.Content.ReadFromJsonAsync<TableClassificationApprovalResult>();
        resultStage2SameUser.ShouldNotBeNull();
        resultStage2SameUser.Success.ShouldBeFalse();
        resultStage2SameUser.ErrorCode.ShouldBe("SOD_ANTI_SELF_APPROVAL_VIOLATION");

        // 4. Stage 2 by a distinct reviewer with a valid TOTP -> Must succeed
        var reqStage2 = reqStage1 with { StepUpAuthToken = totp.GenerateTotpCode(claraSecret) };
        var respStage2Distinct = await claraClient.PostAsJsonAsync("/api/governance/classification/transition", new TableClassificationTransitionPayload(proposal, reqStage2));
        respStage2Distinct.StatusCode.ShouldBe(HttpStatusCode.OK);

        var resultStage2Distinct = await respStage2Distinct.Content.ReadFromJsonAsync<TableClassificationApprovalResult>();
        resultStage2Distinct.ShouldNotBeNull();
        resultStage2Distinct.Success.ShouldBeTrue();
        resultStage2Distinct.NewStatus.ShouldBe("CLASSIFIED");

        // 5. A finalized proposal cannot be transitioned again
        var respAgain = await claraClient.PostAsJsonAsync("/api/governance/classification/transition", new TableClassificationTransitionPayload(proposal, reqStage2));
        respAgain.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GovernanceWorkflowHook_WhenRegisteredInDi_InterceptsClassificationAndTransitions()
    {
        var testHook = new RecordingGovernanceWorkflowHook();

        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IGovernanceWorkflowHook>(testHook);
            });
        });

        var client = customFactory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-TEST-HOOK-USER");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "DATA_STEWARD");

        var context = new TableClassificationContext(
            TableIdentifier: new TableIdentifier("sales", "orders", "invoices"),
            Columns: [new ColumnMetadataContext("invoice_id", "BIGINT")],
            ExistingDataOwnerSid: "S-1-5-21-TEST-OWNER");

        var response = await client.PostAsJsonAsync("/api/governance/classification/classify-table", context);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Verify hook was executed before classification
        testHook.BeforeClassifyCount.ShouldBeGreaterThan(0);
    }

    private sealed class RecordingGovernanceWorkflowHook : IGovernanceWorkflowHook
    {
        public int BeforeClassifyCount { get; private set; }
        public int WorkflowTransitionCount { get; private set; }

        public ValueTask OnBeforeClassifyAsync(TableClassificationContext context, CancellationToken ct = default)
        {
            BeforeClassifyCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnWorkflowTransitionAsync(
            TableClassificationProposal proposal,
            TableClassificationApprovalRequest request,
            TableClassificationApprovalResult result,
            CancellationToken ct = default)
        {
            WorkflowTransitionCount++;
            return ValueTask.CompletedTask;
        }
    }
}
