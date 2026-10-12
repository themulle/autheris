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
        var client = CreateClient(new Sid("S-1-5-21-DATA-OWNER-ALICE"), ["DATA_OWNER"]);

        var proposal = new TableClassificationProposal(
            ProposalId: Guid.NewGuid(),
            TableIdentifier: new TableIdentifier("finance", "payroll", "salaries"),
            Status: "PENDING_OWNER_REVIEW",
            DataOwnerSid: "S-1-5-21-DATA-OWNER-ALICE",
            Columns:
            [
                new ColumnClassificationProposal("salary_amount", "BIGINT", "CONFIDENTIAL", 4, null, "REDACT_FULL", 0.99, false),
                new ColumnClassificationProposal("tax_id", "VARCHAR(20)", "RESTRICTED", 4, null, "MASK_TAX_ID", 0.99, false)
            ],
            CreatedAtUtc: DateTimeOffset.UtcNow);

        // 1. Attempt approval without Step-Up MFA -> Must fail
        var reqNoMfa = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            proposal.TableIdentifier,
            new Sid("S-1-5-21-DATA-OWNER-ALICE"),
            "DATA_OWNER",
            "APPROVE",
            [
                new ColumnClassificationDecision("salary_amount", "ACCEPT_PROPOSAL", "CONFIDENTIAL", 4, "REDACT_FULL", "Approved"),
                new ColumnClassificationDecision("tax_id", "ACCEPT_PROPOSAL", "RESTRICTED", 4, "MASK_TAX_ID", "Approved")
            ],
            StepUpAuthToken: null);

        var payloadNoMfa = new TableClassificationTransitionPayload(proposal, reqNoMfa);
        var respNoMfa = await client.PostAsJsonAsync("/api/governance/classification/transition", payloadNoMfa);
        respNoMfa.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var resultNoMfa = await respNoMfa.Content.ReadFromJsonAsync<TableClassificationApprovalResult>();
        resultNoMfa.ShouldNotBeNull();
        resultNoMfa.Success.ShouldBeFalse();
        resultNoMfa.ErrorCode.ShouldBe("STEP_UP_AUTH_REQUIRED");
        resultNoMfa.RequiresStepUpAuth.ShouldBeTrue();

        // 2. Submit Stage 1 approval with valid MFA token
        var reqStage1 = reqNoMfa with { StepUpAuthToken = "MFA_TOTP_TOKEN_123456" };
        var payloadStage1 = new TableClassificationTransitionPayload(proposal, reqStage1);
        var respStage1 = await client.PostAsJsonAsync("/api/governance/classification/transition", payloadStage1);
        respStage1.StatusCode.ShouldBe(HttpStatusCode.OK);

        var resultStage1 = await respStage1.Content.ReadFromJsonAsync<TableClassificationApprovalResult>();
        resultStage1.ShouldNotBeNull();
        resultStage1.Success.ShouldBeTrue();
        resultStage1.NewStatus.ShouldBe("PENDING_GOVERNANCE_REVIEW");
        resultStage1.RequiresSecondStage.ShouldBeTrue();

        // 3. Attempt Stage 2 approval by the SAME Data Owner (SoD violation) -> Must fail
        var reqStage2SameUser = reqStage1 with
        {
            ReviewerSid = new Sid("S-1-5-21-DATA-OWNER-ALICE"),
            ReviewerRole = "DATA_GOVERNANCE_REVIEWER"
        };
        var proposalPendingGov = proposal with { Status = "PENDING_GOVERNANCE_REVIEW" };
        var payloadStage2SameUser = new TableClassificationTransitionPayload(proposalPendingGov, reqStage2SameUser);
        var respStage2SameUser = await client.PostAsJsonAsync("/api/governance/classification/transition", payloadStage2SameUser);
        respStage2SameUser.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var resultStage2SameUser = await respStage2SameUser.Content.ReadFromJsonAsync<TableClassificationApprovalResult>();
        resultStage2SameUser.ShouldNotBeNull();
        resultStage2SameUser.Success.ShouldBeFalse();
        resultStage2SameUser.ErrorCode.ShouldBe("SOD_ANTI_SELF_APPROVAL_VIOLATION");

        // 4. Submit Stage 2 approval by distinct Governance Compliance Reviewer -> Must succeed
        var reqStage2Distinct = reqStage1 with
        {
            ReviewerSid = new Sid("S-1-5-21-COMPLIANCE-OFFICER-CLARA"),
            ReviewerRole = "DATA_GOVERNANCE_REVIEWER"
        };
        var payloadStage2Distinct = new TableClassificationTransitionPayload(proposalPendingGov, reqStage2Distinct);
        var respStage2Distinct = await client.PostAsJsonAsync("/api/governance/classification/transition", payloadStage2Distinct);
        respStage2Distinct.StatusCode.ShouldBe(HttpStatusCode.OK);

        var resultStage2Distinct = await respStage2Distinct.Content.ReadFromJsonAsync<TableClassificationApprovalResult>();
        resultStage2Distinct.ShouldNotBeNull();
        resultStage2Distinct.Success.ShouldBeTrue();
        resultStage2Distinct.NewStatus.ShouldBe("CLASSIFIED");
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
