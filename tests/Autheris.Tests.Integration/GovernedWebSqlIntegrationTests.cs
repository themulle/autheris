namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public class GovernedWebSqlIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GovernedWebSqlIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:AllowDml", "false");
            builder.UseSetting("Gateway:WebSql:DefaultMaxRows", "200");
            builder.UseSetting("Gateway:WebSql:MaxAllowedRows", "500");
        });
    }

    // SEC C-03: WebSQL rejects tables without catalog metadata, so every table used by these tests is registered first.
    private async Task RegisterTableAsync(string tableName, params string[] columns)
    {
        using var scope = _factory.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var cols = new List<TableColumn>();
        foreach (var c in columns)
        {
            cols.Add(new TableColumn { ColumnName = c, DataType = "varchar" });
        }

        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = tableName, TableName = tableName },
            Identifier = new TableIdentifier("default", "public", tableName),
            Columns = cols
        });
    }

    [Fact]
    public async Task WebSql_SelectQuery_StreamsJsonRowsSuccessfully()
    {
        await RegisterTableAsync("customers", "id", "name", "email", "active", "tenant_id");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant_alpha");

        var payload = new
        {
            sql = "SELECT id, name, email FROM customers WHERE active = 1"
        };

        var response = await client.PostAsJsonAsync("/api/v1/sql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("columns", out var cols).ShouldBeTrue();
        doc.RootElement.TryGetProperty("rows", out var rows).ShouldBeTrue();
        doc.RootElement.TryGetProperty("rowCount", out var count).ShouldBeTrue();
    }

    [Fact]
    public async Task WebSql_DdlStatement_IsForbiddenWith403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant_alpha");

        var payload = new
        {
            sql = "DROP TABLE sensitive_vault"
        };

        var response = await client.PostAsJsonAsync("/api/v1/sql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("DDL statements");
    }

    [Fact]
    public async Task WebSql_DmlStatement_WhenDisallowed_IsForbiddenWith403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant_alpha");

        var payload = new
        {
            sql = "DELETE FROM customers WHERE id = 1"
        };

        var response = await client.PostAsJsonAsync("/api/v1/sql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("SELECT statements");
    }

    [Fact]
    public async Task WebSql_RewriteSqlAsync_InjectsTenantRlsAndClampsLimit()
    {
        using var scope = _factory.Services.CreateScope();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_alice"),
            new Claim("tenant_id", "tenant_123")
        }, "TestAuth"));

        await RegisterTableAsync("orders", "id", "total", "email", "ssn", "tenant_id");
        string rawSql = "SELECT * FROM orders WHERE total > 100 LIMIT 5000";
        string secured = await sqlService.RewriteSqlAsync(rawSql, user, new TenantId("tenant_123"));

        // RLS subquery rewrite must contain tenant predicate
        secured.ShouldContain("tenant_id = 'tenant_123'");
        // Limit must be clamped to MaxAllowedRows (500)
        secured.ShouldContain("LIMIT 200");
        secured.ShouldNotContain("5000");
    }

    [Fact]
    public async Task WebSql_RewriteSqlAsync_WithColumnMaskingRules_PushesDownMaskingExpressions()
    {
        using var scope = _factory.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        // Register table metadata with sensitive columns
        var tableId = new TableIdentifier("default", "public", "employees");
        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "employees", TableName = "employees" },
            Identifier = tableId,
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "integer" },
                new() { ColumnName = "name", DataType = "varchar" },
                new() { ColumnName = "ssn", DataType = "varchar", IsSensitive = true },
                new() { ColumnName = "tenant_id", DataType = "varchar" }
            },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["ssn"] = new MaskingRule { RuleType = "REDACT", Replacement = "XXX-XX-XXXX" }
            }
        });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_bob"),
            new Claim("tenant_id", "tenant_456")
        }, "TestAuth"));

        string rawSql = "SELECT id, name, ssn FROM employees";
        string secured = await sqlService.RewriteSqlAsync(rawSql, user, new TenantId("tenant_456"));

        secured.ShouldContain("tenant_id = 'tenant_456'");
        secured.ShouldContain("AS employees");
    }

    [Fact]
    public async Task WebSql_AntiBypass_CteShadowing_DoesNotEvadeRlsOnPhysicalTable()
    {
        using var scope = _factory.Services.CreateScope();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_eve"),
            new Claim("tenant_id", "tenant_evil")
        }, "TestAuth"));

        await RegisterTableAsync("accounts", "id", "balance", "tenant_id");

        // Attacker attempts to shadow physical table 'accounts' inside CTE to query external rows
        string attackSql = @"
            WITH accounts AS (
                SELECT * FROM accounts WHERE tenant_id = 'victim'
            )
            SELECT * FROM accounts";

        string secured = await sqlService.RewriteSqlAsync(attackSql, user, new TenantId("tenant_evil"));

        // Physical 'accounts' inside CTE definition MUST still have RLS injected
        secured.ShouldContain("tenant_id = 'tenant_evil'");
    }

    [Fact]
    public async Task WebSql_ArrayFormatQuery_StreamsCompactJsonArrays()
    {
        await RegisterTableAsync("users", "id", "name", "tenant_id");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant_gamma");

        var payload = new
        {
            sql = "SELECT id, name FROM users"
        };

        var response = await client.PostAsJsonAsync("/api/v1/sql?format=arrays", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("columns").GetArrayLength().ShouldBeGreaterThan(0);
        doc.RootElement.GetProperty("rows").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task WebSql_UnauthenticatedRequest_WhenAnonymousDisabled_Returns401()
    {
        var secureFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "false");
        });

        var client = secureFactory.CreateClient();
        var payload = new { sql = "SELECT 1" };
        var response = await client.PostAsJsonAsync("/api/v1/sql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WebSql_JoinOnStaticallyRedactedColumn_IsRejectedWithSecurityException()
    {
        using var scope = _factory.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        var tableId = new TableIdentifier("default", "public", "crm_customers");
        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "crm_customers", TableName = "crm_customers" },
            Identifier = tableId,
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "integer" },
                new() { ColumnName = "email", DataType = "varchar" },
                new() { ColumnName = "tenant_id", DataType = "varchar" }
            },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" }
            }
        });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_test"),
            new Claim("tenant_id", "tenant_test")
        }, "TestAuth"));

        await RegisterTableAsync("orders", "id", "total", "email", "ssn", "tenant_id");
        string attackQuery = "SELECT c.id, o.id FROM crm_customers c JOIN orders o ON c.email = o.email";

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(async () =>
        {
            await sqlService.RewriteSqlAsync(attackQuery, user, new TenantId("tenant_test"));
        });

        ex.Message.ShouldContain("Security Policy Violation: Column 'email'");
        ex.Message.ShouldContain("protected by static redaction");
    }

    [Fact]
    public async Task WebSql_Ansi89CommaJoin_OnStaticallyRedactedColumn_IsRejectedWithSecurityException()
    {
        using var scope = _factory.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        var tableId = new TableIdentifier("default", "public", "crm_customers");
        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "crm_customers", TableName = "crm_customers" },
            Identifier = tableId,
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "integer" },
                new() { ColumnName = "email", DataType = "varchar" },
                new() { ColumnName = "tenant_id", DataType = "varchar" }
            },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" }
            }
        });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_test"),
            new Claim("tenant_id", "tenant_test")
        }, "TestAuth"));

        // ANSI-89 comma join syntax: FROM a, b WHERE a.col = b.col
        await RegisterTableAsync("orders", "id", "total", "email", "ssn", "tenant_id");
        string commaJoinQuery = "SELECT c.id, o.id FROM crm_customers c, orders o WHERE c.email = o.email";

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(async () =>
        {
            await sqlService.RewriteSqlAsync(commaJoinQuery, user, new TenantId("tenant_test"));
        });

        ex.Message.ShouldContain("Security Policy Violation: Column 'email'");
        ex.Message.ShouldContain("protected by static redaction");
    }

    [Fact]
    public async Task WebSql_JoinOnHmacPseudonymizedColumn_SucceedsAndPushesDownDeterministicHash()
    {
        using var scope = _factory.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        var tableId = new TableIdentifier("default", "public", "secure_users");
        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "secure_users", TableName = "secure_users", SourceType = "SQLite" },
            Identifier = tableId,
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "integer" },
                new() { ColumnName = "email", DataType = "varchar" },
                new() { ColumnName = "tenant_id", DataType = "varchar" }
            },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["email"] = new MaskingRule { RuleType = "HMAC" }
            }
        });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_test"),
            new Claim("tenant_id", "tenant_test")
        }, "TestAuth"));

        // SEC P-05: all tables of one WebSQL statement must share the data source dialect (here: SQLite).
        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "orders", TableName = "orders", SourceType = "SQLite" },
            Identifier = new TableIdentifier("default", "public", "orders"),
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "varchar" },
                new() { ColumnName = "total", DataType = "varchar" },
                new() { ColumnName = "email", DataType = "varchar" },
                new() { ColumnName = "ssn", DataType = "varchar" },
                new() { ColumnName = "tenant_id", DataType = "varchar" }
            }
        });
        string validJoinQuery = "SELECT u.id, o.id FROM secure_users u JOIN orders o ON u.email = o.email";

        string secured = await sqlService.RewriteSqlAsync(validJoinQuery, user, new TenantId("tenant_test"));

        secured.ShouldContain("gateway_hmac_sha256");
        // SEC H-13: The HMAC key (and the secret reference name) is bound as a parameter, never embedded in SQL text
        secured.ShouldNotContain("DEV_INSECURE_TEST_KEY_ONLY");
        secured.ShouldContain("@__gql_mk0");
    }

    [Fact]
    public async Task WebSql_JoinOnCasbinMaskedColumn_WithoutHmac_IsRejectedWithSecurityException()
    {
        using var scope = _factory.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        // Table metadata has NO static ColumnMaskingRules, but sensitive column marked
        var tableId = new TableIdentifier("default", "public", "hr_employees");
        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "hr_employees", TableName = "hr_employees" },
            Identifier = tableId,
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "integer" },
                new() { ColumnName = "ssn", DataType = "varchar", IsSensitive = true },
                new() { ColumnName = "tenant_id", DataType = "varchar" }
            }
        });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_test"),
            new Claim("tenant_id", "tenant_test")
        }, "TestAuth"));

        await RegisterTableAsync("payroll", "id", "ssn", "tenant_id");
        string joinQuery = "SELECT e.id, p.id FROM hr_employees e JOIN payroll p ON e.ssn = p.ssn";

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(async () =>
        {
            await sqlService.RewriteSqlAsync(joinQuery, user, new TenantId("tenant_test"));
        });

        ex.Message.ShouldContain("Security Policy Violation: Column 'ssn'");
        ex.Message.ShouldContain("protected by static redaction");
    }
}
