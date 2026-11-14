namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;

public sealed class GovernanceAdvancedMoatsIntegrationTests : IClassFixture<GovernanceAdvancedMoatsIntegrationTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GovernanceAdvancedMoatsIntegrationTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("GovernanceAdvancedMoatsIntegrationTests"),
            });
        }
    }

    private System.Net.Http.HttpClient CreateAuthClient(string roles = "GovernanceAdmin")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-GOV-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        return client;
    }

    [Fact]
    public async Task SchemaSunsetting_Endpoints_FullLifecycle()
    {
        var client = CreateAuthClient("ClusterAdmin");

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
}
