namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #107 (SEC-RF107-14, SEC-RF107-06e): REST administration of the row filter key-set cache settings over real requests.
/// Authorization (401/403), cross-tenant 404 with an identical body, strict input (numeric enums, unknown properties, caps), the rule type
/// checks on save and the epoch bump. The governance store is the in-memory SQLite database of the test host.
/// </summary>
[Trait("Category", "Security")]
public sealed class RowFilterEvaluationEndpointAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string OwnTenant = "tenant-default";
    private const string OtherTenant = "tenant-other";
    private readonly WebApplicationFactory<Program> _factory;

    public RowFilterEvaluationEndpointAuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    private HttpClient CreateClient(string? roles, string tenant = OwnTenant)
    {
        var client = _factory.CreateClient();
        if (roles != null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-RFEVAL");
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
            client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        }

        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<(TableIdentifier Table, Guid RuleId)> SeedRuleAsync(
        ConsentEffect effect = ConsentEffect.Allow, RowFilterType type = RowFilterType.SubqueryCorrelated, string? temporal = null)
    {
        _ = CreateClient(null); // starts the host
        var repo = _factory.Services.GetRequiredService<IGovernanceRepository>();
        var table = new TableIdentifier("Sales", "Crm", "orders_" + Guid.NewGuid().ToString("N")[..8]);
        var entity = new Table { Id = Guid.NewGuid(), SourceName = table.Domain, SchemaName = table.Schema, TableName = table.TableName, DisplayName = table.TableName, IsActive = true };
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = table, Table = entity, Columns = [new TableColumn { TableId = entity.Id, ColumnName = "customer_id", DataType = "INT" }]
        });
        var columnId = (await repo.GetTableMetadataAsync(table))!.Columns[0].Id;
        var consent = await repo.CreateConsentAsync(new Consent
        {
            TableId = entity.Id, TableIdentifier = table, TenantId = new TenantId(OwnTenant), GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-alice"), Effect = effect,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(10),
            RowFilters =
            [
                new ConsentRowFilter
                {
                    TableColumnId = columnId, ColumnName = "customer_id", FilterType = type,
                    DependentTable = new TableIdentifier("Crm", "Dbo", "customers"), ForeignKeyColumn = "customer_id", PrimaryKeyColumn = "id",
                    SubqueryFilterPredicateJson = "[]", TargetTemporalColumn = temporal
                }
            ]
        });
        var stored = (await repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-alice")], table, DateTimeOffset.UtcNow, null)).Single(c => c.Id == consent.Id);
        return (table, stored.RowFilters.Single().Id);
    }

    private static string RuleUrl(Guid id) => $"/api/v1/governance/row-filters/{id:D}/evaluation";

    private static string ObjectUrl(string dataset) => $"/api/v1/catalog/datasets/{dataset}/row-filter-strategy";

    // ---- authorization ----

    [Fact]
    public async Task Anonymous_Returns401_OnEveryRoute()
    {
        var client = CreateClient(null);
        var id = Guid.NewGuid();

        (await client.GetAsync(RuleUrl(id))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PutAsync(RuleUrl(id), Json("{}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync(ObjectUrl("sales.dbo.orders"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PutAsync(ObjectUrl("sales.dbo.orders"), Json("{}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.DeleteAsync(ObjectUrl("sales.dbo.orders"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NonAdmin_Returns403_OnEveryRoute_WithoutChangingAnything()
    {
        var (table, ruleId) = await SeedRuleAsync();
        var client = CreateClient("DataConsumer");

        (await client.GetAsync(RuleUrl(ruleId))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PutAsync(RuleUrl(ruleId), Json("""{"strategy":"KeySetCache"}"""))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync(ObjectUrl(table.ToString()))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PutAsync(ObjectUrl(table.ToString()), Json("""{"strategy":"KeySetCache"}"""))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.DeleteAsync(ObjectUrl(table.ToString()))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var repo = _factory.Services.GetRequiredService<IGovernanceRepository>();
        (await repo.GetTableMetadataAsync(table))!.RowFilterEvaluation.ShouldBeNull();
    }

    // ---- rule level ----

    [Fact]
    public async Task Rule_Put_Get_RoundTrips_AndBumpsTheTableEpoch()
    {
        var (table, ruleId) = await SeedRuleAsync();
        var admin = CreateClient("GovernanceAdmin");
        var repo = _factory.Services.GetRequiredService<IGovernanceRepository>();
        var epoch = await repo.GetTableEpochAsync(table);

        var put = await admin.PutAsync(RuleUrl(ruleId), Json("""{"strategy":"KeySetCache","durationSeconds":120,"maxKeys":500}"""));

        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("stored").GetProperty("strategy").GetString().ShouldBe("KeySetCache");
        doc.RootElement.GetProperty("stored").GetProperty("durationSeconds").GetInt32().ShouldBe(120);
        doc.RootElement.GetProperty("effective").GetProperty("strategySource").GetString().ShouldBe("Rule");
        doc.RootElement.GetProperty("effective").GetProperty("maxKeys").GetInt32().ShouldBe(500);
        (await repo.GetTableEpochAsync(table)).ShouldBeGreaterThan(epoch);

        var get = await admin.GetAsync(RuleUrl(ruleId));
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await get.Content.ReadAsStringAsync()).ShouldBe(await put.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rule_Put_EmptyBody_ClearsToInherit()
    {
        var (_, ruleId) = await SeedRuleAsync();
        var admin = CreateClient("GovernanceAdmin");
        await admin.PutAsync(RuleUrl(ruleId), Json("""{"strategy":"KeySetCache"}"""));

        var cleared = await admin.PutAsync(RuleUrl(ruleId), Json("{}"));

        cleared.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("stored").GetProperty("strategy").ValueKind.ShouldBe(JsonValueKind.Null);
        doc.RootElement.GetProperty("effective").GetProperty("strategySource").GetString().ShouldBe("Global");
    }

    [Fact]
    public async Task Rule_OtherTenant_GetsTheSame404BodyAsAnUnknownId_AndChangesNothing_SEC_RF107_14a()
    {
        var (table, ruleId) = await SeedRuleAsync();
        var foreign = CreateClient("GovernanceAdmin", OtherTenant);
        var unknown = Guid.NewGuid();

        var foreignGet = await foreign.GetAsync(RuleUrl(ruleId));
        var unknownGet = await foreign.GetAsync(RuleUrl(unknown));
        var foreignPut = await foreign.PutAsync(RuleUrl(ruleId), Json("""{"strategy":"KeySetCache"}"""));
        var unknownPut = await foreign.PutAsync(RuleUrl(unknown), Json("""{"strategy":"KeySetCache"}"""));
        var malformed = await foreign.GetAsync("/api/v1/governance/row-filters/not-a-guid/evaluation");

        foreach (var response in new[] { foreignGet, unknownGet, foreignPut, unknownPut, malformed })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var body = await foreignGet.Content.ReadAsStringAsync();
        (await unknownGet.Content.ReadAsStringAsync()).ShouldBe(body);
        (await foreignPut.Content.ReadAsStringAsync()).ShouldBe(body);
        (await unknownPut.Content.ReadAsStringAsync()).ShouldBe(body);
        (await malformed.Content.ReadAsStringAsync()).ShouldBe(body);
        body.ShouldNotContain(ruleId.ToString("D"));
        body.ShouldNotContain(unknown.ToString("D"));

        var repo = _factory.Services.GetRequiredService<IGovernanceRepository>();
        var consents = await repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-alice")], table, DateTimeOffset.UtcNow, null);
        consents.Single().RowFilters.Single().KeySetStrategy.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"strategy":1}""")]
    [InlineData("""{"strategy":"1"}""")]
    [InlineData("""{"strategy":"keysetcache"}""")]
    [InlineData("""{"strategy":"Bogus"}""")]
    [InlineData("""{"strategy":true}""")]
    [InlineData("""{"strategy":"KeySetCache","unknown":1}""")]
    [InlineData("""{"durationSeconds":"60"}""")]
    [InlineData("""{"durationSeconds":0}""")]
    [InlineData("""{"durationSeconds":-5}""")]
    [InlineData("""{"durationSeconds":99999999}""")]
    [InlineData("""{"maxKeys":0}""")]
    [InlineData("""{"maxKeys":10001}""")]
    [InlineData("""{"maxKeys":99999999999}""")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task Rule_Put_InvalidInput_Returns400_AndWritesNothing_SEC_RF107_14b(string body)
    {
        var (table, ruleId) = await SeedRuleAsync();
        var admin = CreateClient("GovernanceAdmin");
        var repo = _factory.Services.GetRequiredService<IGovernanceRepository>();
        var epoch = await repo.GetTableEpochAsync(table);

        var response = await admin.PutAsync(RuleUrl(ruleId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await repo.GetTableEpochAsync(table)).ShouldBe(epoch);
        var consents = await repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-alice")], table, DateTimeOffset.UtcNow, null);
        consents.Single().RowFilters.Single().KeySetStrategy.ShouldBeNull();
    }

    [Fact]
    public async Task Rule_Put_AboveCaps_Returns400_WithTheCapInTheReason()
    {
        var (_, ruleId) = await SeedRuleAsync();

        var response = await CreateClient("GovernanceAdmin").PutAsync(RuleUrl(ruleId), Json("""{"maxKeys":2001}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("2000");
    }

    [Fact]
    public async Task Rule_Put_KeySetOnDenyRule_Returns400_SEC_RF107_06e()
    {
        var (_, ruleId) = await SeedRuleAsync(effect: ConsentEffect.Deny);

        (await CreateClient("GovernanceAdmin").PutAsync(RuleUrl(ruleId), Json("""{"strategy":"KeySetCache"}"""))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rule_Put_KeySetOnNonCorrelatedRule_Returns400_SEC_RF107_06e()
    {
        var (_, ruleId) = await SeedRuleAsync(type: RowFilterType.SimpleColumnPredicate);

        (await CreateClient("GovernanceAdmin").PutAsync(RuleUrl(ruleId), Json("""{"strategy":"KeySetCache"}"""))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rule_Put_KeySetOnTemporalRule_Returns400_SEC_RF107_06e()
    {
        var (_, ruleId) = await SeedRuleAsync(temporal: "order_date");

        (await CreateClient("GovernanceAdmin").PutAsync(RuleUrl(ruleId), Json("""{"strategy":"KeySetCache"}"""))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- object level ----

    [Fact]
    public async Task Object_Put_Get_Delete_RoundTrip_AndBumpsTheEpoch()
    {
        var (table, _) = await SeedRuleAsync();
        var admin = CreateClient("GovernanceAdmin");
        var repo = _factory.Services.GetRequiredService<IGovernanceRepository>();
        var epoch = await repo.GetTableEpochAsync(table);

        var put = await admin.PutAsync(ObjectUrl(table.ToString()), Json("""{"strategy":"KeySetCache","durationSeconds":60}"""));

        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await repo.GetTableEpochAsync(table)).ShouldBeGreaterThan(epoch);
        using (var doc = JsonDocument.Parse(await (await admin.GetAsync(ObjectUrl(table.ToString()))).Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("effective").GetProperty("strategySource").GetString().ShouldBe("Object");
            doc.RootElement.GetProperty("effective").GetProperty("durationSeconds").GetInt32().ShouldBe(60);
        }

        var deleted = await admin.DeleteAsync(ObjectUrl(table.ToString()));
        deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await repo.GetTableMetadataAsync(table))!.RowFilterEvaluation.ShouldBeNull();
    }

    [Fact]
    public async Task Object_UnknownDataset_Returns404_WithAFixedBody()
    {
        var admin = CreateClient("GovernanceAdmin");

        var one = await admin.PutAsync(ObjectUrl("sales.dbo.nothere"), Json("""{"strategy":"KeySetCache"}"""));
        var two = await admin.GetAsync(ObjectUrl("sales.dbo.other_missing"));
        var three = await admin.DeleteAsync(ObjectUrl(Guid.NewGuid().ToString("D")));

        one.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        two.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        three.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await one.Content.ReadAsStringAsync();
        (await two.Content.ReadAsStringAsync()).ShouldBe(body);
        (await three.Content.ReadAsStringAsync()).ShouldBe(body);
        body.ShouldNotContain("nothere");
    }

    [Theory]
    [InlineData("""{"strategy":2}""")]
    [InlineData("""{"strategy":"KeySetCache","extra":true}""")]
    [InlineData("""{"durationSeconds":99999999}""")]
    [InlineData("""{"maxKeys":-1}""")]
    public async Task Object_Put_InvalidInput_Returns400_SEC_RF107_14b(string body)
    {
        var (table, _) = await SeedRuleAsync();

        var response = await CreateClient("GovernanceAdmin").PutAsync(ObjectUrl(table.ToString()), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var repo = _factory.Services.GetRequiredService<IGovernanceRepository>();
        (await repo.GetTableMetadataAsync(table))!.RowFilterEvaluation.ShouldBeNull();
    }
}
