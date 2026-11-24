namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>An IT self-grant of a secret table is refused and the refusal is recorded as an audit row.</summary>
[Trait("Category", "Security")]
public sealed class SelfGrantIntegrationTests : IClassFixture<SelfGrantIntegrationTests.HostFixture>
{
    private static readonly TenantId Tenant = new("tenant-default");
    private readonly WebApplicationFactory<Program> _factory;

    public SelfGrantIntegrationTests(HostFixture factory) => _factory = factory;

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            base.ConfigureWebHost(builder);
        }

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("SelfGrantIntegrationTests"),
            });
        }
    }

    [Fact]
    public async Task Secret_self_grant_refused_with_audit_row()
    {
        var name = "secret_" + Guid.NewGuid().ToString("N")[..8];
        var identifier = new TableIdentifier("sales", "dbo", name);
        var tables = _factory.Services.GetRequiredService<ITableMetadataRepository>();
        var table = new Table { Id = Guid.NewGuid(), SourceName = identifier.Domain, SchemaName = identifier.Schema, TableName = name, Description = "secret", IsActive = true };
        var stored = await tables.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = identifier, Table = table, Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        });
        var domains = _factory.Services.GetRequiredService<IGovernanceDomainRepository>();
        await domains.SetTableProtectionAsync(Tenant, stored.Table.Id, new ObjectProtection(ProtectionClass.Secret), null, new Sid("S-1-5-21-DG"));

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-IT");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "TenantAdmin");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant.Value);
        var body = "{\"domain\":\"sales\",\"schema\":\"dbo\",\"tableName\":\"" + name + "\",\"kind\":\"data\"," +
                   "\"validTo\":\"" + DateTimeOffset.UtcNow.AddDays(30).ToString("O") + "\",\"justification\":\"Incident 4711 needs access.\"}";

        var response = await client.PostAsync("/api/v1/consent/self-grants", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        var audit = _factory.Services.GetRequiredService<IAuditLogRepository>();
        var rows = await audit.GetAuditLogEntriesAsync(500, Tenant);
        rows.ShouldContain(e => e.EventType == AuditEventTypes.SelfGrantDenied && e.ActorSid == new Sid("S-1-5-21-IT"));
    }
}
