namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// End-to-End FinOps: HTTP -> Auth -> FinOps-Middleware -> governed WebSQL -> echte SQLite-Datei,
/// danach Kostenverbuchung (FOCUS), Budgetstatus und Sperre per 429 - als eine zusammenhaengende Kette.
/// </summary>
public sealed class FinOpsEndToEndSqliteTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Tenant = "fin_e2e";
    private readonly string _workDir;
    private readonly WebApplicationFactory<Program> _factory;

    public FinOpsEndToEndSqliteTests(WebApplicationFactory<Program> factory)
    {
        _workDir = Path.Combine(Path.GetTempPath(), "autheris-finops-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        var dbPath = Path.Combine(_workDir, "data.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE fin_cranes (id INTEGER PRIMARY KEY, serial_number TEXT NOT NULL);
INSERT INTO fin_cranes VALUES (1,'LTM-1100'),(2,'LR-1600');";
            cmd.ExecuteNonQuery();
        }

        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={dbPath}");
            b.UseSetting("Gateway:WebSql:Enabled", "true");
            b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");

            b.UseSetting("Gateway:FinOps:Enabled", "true");
            b.UseSetting("Gateway:FinOps:PricePerComputeSecond", "1000");   // 1 ms = 1 EUR
            b.UseSetting("Gateway:FinOps:DefaultMonthlyBudget", "1000000");
            b.UseSetting($"Gateway:FinOps:TenantMonthlyBudgets:{Tenant}", "0.5"); // eine Abfrage sprengt es
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
    }

    private HttpClient Client(string tenant, string roles)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-FINE2E-" + tenant);
        c.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        c.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        return c;
    }

    private async Task RegisterCatalogAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "fin_cranes", TableName = "fin_cranes", SchemaName = "main", SourceType = "Sqlite", SourceName = "default" },
            Identifier = new TableIdentifier("default", "main", "fin_cranes"),
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "integer" },
                new() { ColumnName = "serial_number", DataType = "varchar" },
            }
        });
    }

    [Fact]
    public async Task SqlQuery_IsMeteredCostedAndThenBlockedByBudget()
    {
        await RegisterCatalogAsync();
        var user = Client(Tenant, "Reader");
        var admin = Client(Tenant, "BillingAdmin");
        var sql = new { sql = "SELECT serial_number FROM fin_cranes ORDER BY id" };

        // 1) Abfrage laeuft bis in die SQLite-Datei und liefert echte Zeilen
        var first = await user.PostAsJsonAsync("/api/v1/sql", sql);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await first.Content.ReadAsStringAsync();
        body.ShouldContain("LTM-1100");
        body.ShouldContain("LR-1600");

        // 2) Kosten erscheinen als FOCUS-Record fuer genau diese Operation
        JsonElement rec = default;
        for (var i = 0; i < 40 && rec.ValueKind == JsonValueKind.Undefined; i++)
        {
            var r = await admin.GetAsync($"/api/v1/finops/focus?tenantId={Tenant}");
            r.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            foreach (var item in doc.RootElement.GetProperty("records").EnumerateArray())
            {
                if (item.GetProperty("resourceId").GetString() == "/api/v1/sql")
                {
                    rec = item.Clone();
                }
            }
            if (rec.ValueKind == JsonValueKind.Undefined) await Task.Delay(100);
        }
        rec.ValueKind.ShouldNotBe(JsonValueKind.Undefined, "kein FinOps-Record fuer /api/v1/sql");
        rec.GetProperty("subAccountId").GetString().ShouldBe(Tenant);
        rec.GetProperty("billedCost").GetDecimal().ShouldBeGreaterThan(0m);

        // 3) Budgetstatus zeigt Verbrauch und Ueberschreitung
        var budget = await admin.GetAsync($"/api/v1/finops/budget/{Tenant}");
        budget.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var b = JsonDocument.Parse(await budget.Content.ReadAsStringAsync()))
        {
            b.RootElement.GetProperty("isExceeded").GetBoolean().ShouldBeTrue();
            b.RootElement.GetProperty("budgetLimit").GetDecimal().ShouldBe(0.5m);
        }

        // 4) Naechste Abfrage wird vor der Datenbank gestoppt
        var second = await user.PostAsJsonAsync("/api/v1/sql", sql);
        second.StatusCode.ShouldBe((HttpStatusCode)429);
        (await second.Content.ReadAsStringAsync()).ShouldContain("FINOPS_BUDGET_EXCEEDED");

        // 5) Health bleibt erreichbar, anderer Mandant ist nicht betroffen
        (await user.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = await Client("fin_e2e_other", "Reader").PostAsJsonAsync("/api/v1/sql", sql);
        other.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 6) Reset (Betrieb) hebt die Sperre auf
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Autheris.Application.FinOps.Interfaces.IFinOpsAccountingService>()
                .ResetSpendAsync(Tenant);
        }
        (await user.PostAsJsonAsync("/api/v1/sql", sql)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
