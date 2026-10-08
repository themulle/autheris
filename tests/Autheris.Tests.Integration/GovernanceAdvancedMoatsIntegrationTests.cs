namespace Autheris.Tests.Integration;

using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

public sealed class GovernanceAdvancedMoatsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GovernanceAdvancedMoatsIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    private System.Net.Http.HttpClient CreateAuthClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-GOV-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");
        return client;
    }

    [Fact]
    public async Task PolicySimulationSandbox_Endpoint_ReplaysDraftPolicy()
    {
        var client = CreateAuthClient();

        var draftCsv = @"
p, S-1-5-21-USER-1, legacy-single-tenant, Orders, read, true, allow
p, S-1-5-21-USER-2, legacy-single-tenant, Orders, read, true, deny
";

        var request = new PolicySimulationRequest(
            DraftPolicyCsv: draftCsv,
            TargetTable: "Orders",
            Limit: 50
        );

        var response = await client.PostAsJsonAsync("/api/governance/policy-simulation/replay", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PolicySimulationResult>();
        result.ShouldNotBeNull();
        result.SimulatedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task PolicySimulationSandbox_DraftForForeignTenant_Returns400()
    {
        var client = CreateAuthClient();

        // The caller resolves to legacy-single-tenant; a draft for another tenant is invalid client input.
        var request = new PolicySimulationRequest(
            DraftPolicyCsv: "p, S-1-5-21-USER-1, other-tenant, Orders, read, true, allow",
            TargetTable: "Orders",
            Limit: 50
        );

        var response = await client.PostAsJsonAsync("/api/governance/policy-simulation/replay", request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SchemaSunsetting_Endpoints_FullLifecycle()
    {
        var client = CreateAuthClient();

        var baseTime = DateTimeOffset.UtcNow;
        var ruleId = Guid.NewGuid();
        var rule = new FieldSunsettingRule(
            Id: ruleId,
            TargetTable: "Customers",
            FieldName: "old_identifier",
            DeprecatedAt: baseTime.AddDays(-10),
            SunsetAt: baseTime.AddDays(10),
            ReplacementField: "new_uuid",
            DeprecationReason: "Migration to global UUID standard",
            BrownoutWindow: TimeSpan.FromDays(3)
        );

        // 1. Register rule
        var registerResponse = await client.PostAsJsonAsync("/api/governance/sunsetting/rules", rule);
        registerResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        // 2. Query rules
        var getRulesResponse = await client.GetAsync("/api/governance/sunsetting/rules");
        getRulesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rules = await getRulesResponse.Content.ReadFromJsonAsync<FieldSunsettingRule[]>();
        rules.ShouldNotBeNull();
        rules.ShouldContain(r => r.TargetTable == "Customers" && r.FieldName == "old_identifier");

        // 3. Evaluate during Warning phase (now - 5 days)
        var evalWarningResponse = await client.PostAsJsonAsync("/api/governance/sunsetting/evaluate", new EvaluateFieldSunsettingRequest(
            TargetTable: "Customers",
            FieldName: "old_identifier",
            EvaluationDate: baseTime.AddDays(-5)
        ));
        evalWarningResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        evalWarningResponse.Headers.Contains("Sunset").ShouldBeTrue();
        var evalResult = await evalWarningResponse.Content.ReadFromJsonAsync<SunsettingEvaluationResult>();
        evalResult.ShouldNotBeNull();
        evalResult.Phase.ShouldBe(SunsettingPhase.Warning);
        evalResult.DeprecationNotice.ShouldContain("new_uuid");

        // 4. Evaluate after SunsetAt (Hard Sunset) -> 410 Gone
        var evalHardSunsetResponse = await client.PostAsJsonAsync("/api/governance/sunsetting/evaluate", new EvaluateFieldSunsettingRequest(
            TargetTable: "Customers",
            FieldName: "old_identifier",
            EvaluationDate: baseTime.AddDays(15)
        ));
        evalHardSunsetResponse.StatusCode.ShouldBe(HttpStatusCode.Gone);
        evalHardSunsetResponse.Headers.Contains("Sunset").ShouldBeTrue();
        var hardSunsetText = await evalHardSunsetResponse.Content.ReadAsStringAsync();
        hardSunsetText.ShouldContain("permanently decommissioned");
        hardSunsetText.ShouldContain("new_uuid");
    }

    [Fact]
    public async Task DifferentialPrivacy_Endpoints_BudgetAndPerturbation()
    {
        var client = CreateAuthClient();
        var clientId = "analyst-marketing-01";

        // 1. Initial budget
        var budgetResponse = await client.GetAsync($"/api/governance/differential-privacy/budget/{clientId}");
        budgetResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var initialBudget = await budgetResponse.Content.ReadFromJsonAsync<PrivacyBudget>();
        initialBudget.ShouldNotBeNull();
        initialBudget.ConsumedEpsilon.ShouldBe(0.0);
        initialBudget.TotalDailyEpsilonBudget.ShouldBe(10.0);

        // 2. Perturbation
        var perturbRequest = new DifferentialPrivacyPerturbationRequest(
            ClientId: clientId,
            Value: 1250.0,
            Epsilon: 1.5,
            Sensitivity: 1.0,
            CohortCount: 15,
            MinimumCohortSize: 5
        );

        var perturbResponse = await client.PostAsJsonAsync("/api/governance/differential-privacy/perturb", perturbRequest);
        perturbResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        perturbResponse.Headers.GetValues("X-Privacy-Budget-Consumed").ShouldContain("1.50");
        perturbResponse.Headers.GetValues("X-Privacy-Budget-Remaining").ShouldContain("8.50");

        var perturbResult = await perturbResponse.Content.ReadFromJsonAsync<DifferentialPrivacyPerturbationResult>();
        perturbResult.ShouldNotBeNull();
        perturbResult.IsSuppressed.ShouldBeFalse();
        perturbResult.PerturbedValue.ShouldNotBeNull();

        // 3. Small-cohort suppression (k < 5)
        var suppressRequest = new DifferentialPrivacyPerturbationRequest(
            ClientId: clientId,
            Value: 300.0,
            Epsilon: 0.5,
            Sensitivity: 1.0,
            CohortCount: 2, // k < 5
            MinimumCohortSize: 5
        );

        var suppressResponse = await client.PostAsJsonAsync("/api/governance/differential-privacy/perturb", suppressRequest);
        suppressResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var suppressResult = await suppressResponse.Content.ReadFromJsonAsync<DifferentialPrivacyPerturbationResult>();
        suppressResult.ShouldNotBeNull();
        suppressResult.IsSuppressed.ShouldBeTrue();
        suppressResult.PerturbedValue.ShouldBeNull();

        // 4. Exhaust budget (attempt 9.0 when remaining is 8.5) -> 429 Too Many Requests
        var exhaustRequest = new DifferentialPrivacyPerturbationRequest(
            ClientId: clientId,
            Value: 50.0,
            Epsilon: 9.0,
            CohortCount: 20
        );

        var exhaustResponse = await client.PostAsJsonAsync("/api/governance/differential-privacy/perturb", exhaustRequest);
        exhaustResponse.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        exhaustResponse.Headers.GetValues("X-Privacy-Budget-Exhausted").ShouldContain("true");

        // 5. Reset budget
        var resetResponse = await client.PostAsync($"/api/governance/differential-privacy/budget/{clientId}/reset", null);
        resetResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budgetAfterReset = await client.GetFromJsonAsync<PrivacyBudget>($"/api/governance/differential-privacy/budget/{clientId}");
        budgetAfterReset.ShouldNotBeNull();
        budgetAfterReset.ConsumedEpsilon.ShouldBe(0.0);
        budgetAfterReset.RemainingEpsilon.ShouldBe(10.0);
    }
}
