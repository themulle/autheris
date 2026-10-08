namespace Autheris.Tests.Integration;

using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.FinOps.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Integrationstest FinOps (F-AI-08): prueft die HTTP-Pipeline (Middleware-Reihenfolge, 429-Sperre,
/// Warn-Header, Rollen-/Mandantenschutz der Endpunkte) ohne externe Datenquelle.
/// Der Compute-Preis ist absichtlich extrem hoch (1 ms = 1 EUR), damit ein Request ein Budget sicher sprengt.
/// Jeder Test nutzt einen eigenen Mandanten, da der Verbrauch im Speicher der Factory liegt.
/// </summary>
public sealed class FinOpsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public FinOpsIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:FinOps:Enabled", "true");
            b.UseSetting("Gateway:FinOps:PricePerComputeSecond", "1000");
            b.UseSetting("Gateway:FinOps:DefaultMonthlyBudget", "1000000");
            b.UseSetting("Gateway:FinOps:SoftCapRatio", "0.8");
            b.UseSetting("Gateway:FinOps:TenantMonthlyBudgets:fin_cap", "0.05");
            b.UseSetting("Gateway:FinOps:TenantMonthlyBudgets:fin_warn", "100");
        });
    }

    private HttpClient Client(string tenant, string roles = "Reader")
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-FINOPS-" + tenant);
        c.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        c.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        return c;
    }

    // Beliebiger authentifizierter, kostenverursachender Request
    private static Task<HttpResponseMessage> WorkRequest(HttpClient c) => c.GetAsync("/api/auth/session");

    private static async Task<JsonElement> WaitForRecordsAsync(HttpClient admin, string tenant, int min = 1)
    {
        for (var i = 0; i < 40; i++) // bis ca. 4 s: die Verbuchung laeuft im finally der Middleware
        {
            var r = await admin.GetAsync($"/api/v1/finops/focus?tenantId={tenant}");
            r.StatusCode.ShouldBe(HttpStatusCode.OK);
            var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (doc.RootElement.GetProperty("count").GetInt32() >= min)
            {
                return doc.RootElement.Clone();
            }
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Keine FinOps-Records fuer Mandant {tenant}.");
    }

    [Fact]
    public async Task Request_IsMetered_AndAppearsInFocusRecords()
    {
        var t = "fin_meter";
        (await WorkRequest(Client(t))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var root = await WaitForRecordsAsync(Client(t, "BillingAdmin"), t);
        root.GetProperty("specVersion").GetString().ShouldBe("1.2");
        var rec = root.GetProperty("records")[0];
        rec.GetProperty("subAccountId").GetString().ShouldBe(t);
        rec.GetProperty("currency").GetString().ShouldBe("EUR");
        rec.GetProperty("serviceName").GetString().ShouldBe("Autheris");
        rec.GetProperty("pricingCategory").GetString().ShouldBe("HttpCompute");
        rec.GetProperty("billedCost").GetDecimal().ShouldBeGreaterThan(0m);
    }

    [Fact]
    public async Task ExceededBudget_Returns429_ForThatTenantOnly()
    {
        var t = "fin_cap"; // Budget 0.05 EUR
        (await WorkRequest(Client(t))).StatusCode.ShouldBe(HttpStatusCode.OK); // Spend war 0
        await WaitForRecordsAsync(Client(t, "BillingAdmin"), t);

        var blocked = await WorkRequest(Client(t));
        blocked.StatusCode.ShouldBe((HttpStatusCode)429);
        (await blocked.Content.ReadAsStringAsync()).ShouldContain("FINOPS_BUDGET_EXCEEDED");

        (await WorkRequest(Client("fin_other"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SoftCap_SetsWarningHeader_WithoutBlocking()
    {
        var t = "fin_warn"; // Budget 100 EUR, Soft-Cap 80 %
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IFinOpsAccountingService>();
            await svc.RecordUsageAsync(t, "e2e", "seed", "HttpCompute", 0, 0, computeMs: 85); // 85 EUR
        }

        var r = await WorkRequest(Client(t));
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        r.Headers.TryGetValues("X-FinOps-Budget-Warning", out var v).ShouldBeTrue();
        v!.ShouldContain("true");
    }

    [Fact]
    public async Task FinOpsEndpoints_RequireBillingRole()
    {
        var reader = Client("fin_rbac", "Reader");
        (await reader.GetAsync("/api/v1/finops/focus")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reader.GetAsync("/api/v1/finops/budget/fin_rbac")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/finops/focus")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantAdmin_CannotReadForeignTenant()
    {
        // Mandant fin_victim verursacht Kosten
        (await WorkRequest(Client("fin_victim"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await WaitForRecordsAsync(Client("fin_victim", "BillingAdmin"), "fin_victim");

        var attacker = Client("fin_attacker", "BillingAdmin");

        // Budget eines fremden Mandanten: 403 (IDOR-Schutz)
        (await attacker.GetAsync("/api/v1/finops/budget/fin_victim")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Focus mit fremder tenantId: wird auf den eigenen Mandanten umgeschrieben, keine fremden Records
        var r = await attacker.GetAsync("/api/v1/finops/focus?tenantId=fin_victim");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).ShouldNotContain("fin_victim");
    }

    [Fact]
    public async Task Budget_Endpoint_ReportsSpendAndLimit()
    {
        var t = "fin_budget";
        (await WorkRequest(Client(t))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await WaitForRecordsAsync(Client(t, "BillingAdmin"), t);

        var r = await Client(t, "BillingAdmin").GetAsync($"/api/v1/finops/budget/{t}");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("tenantId").GetString().ShouldBe(t);
        doc.RootElement.GetProperty("currentSpend").GetDecimal().ShouldBeGreaterThan(0m);
        doc.RootElement.GetProperty("budgetLimit").GetDecimal().ShouldBe(1000000m);
    }

    [Fact]
    public async Task HealthProbe_IsNeverMeteredNorBlocked()
    {
        var t = "fin_cap"; // schon ueber Budget oder gleich darueber -> Health muss trotzdem 200 liefern
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFinOpsAccountingService>()
                .RecordUsageAsync(t, "e2e", "seed", "HttpCompute", 0, 0, computeMs: 1000);
        }
        (await Client(t).GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FocusCsv_IsServedWithHeaderRow()
    {
        var t = "fin_csv";
        (await WorkRequest(Client(t))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var admin = Client(t, "BillingAdmin");
        await WaitForRecordsAsync(admin, t);

        var r = await admin.GetAsync($"/api/v1/finops/focus?tenantId={t}&format=csv");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var csv = await r.Content.ReadAsStringAsync();
        csv.ShouldStartWith("ChargePeriodStart,ChargePeriodEnd,BilledCost");
        csv.ShouldContain(t);
    }
}
