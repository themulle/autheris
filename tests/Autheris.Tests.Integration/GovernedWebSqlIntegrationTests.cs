namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class GovernedWebSqlIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _workDir;
    private readonly string _dbPath;

    // X1: the governed pipeline compiles against the catalog of the database, so every table of these tests exists in a real SQLite
    // data source (tenant rows of several tenants), and the assertions are on rows, not on SQL text.
    public GovernedWebSqlIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _workDir = Path.Combine(Path.GetTempPath(), "autheris-gws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "data.db");
        Seed(_dbPath);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
            builder.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");
            builder.UseSetting("Gateway:WebSql:AllowDml", "false");
            builder.UseSetting("Gateway:WebSql:DefaultMaxRows", "200");
            builder.UseSetting("Gateway:WebSql:MaxAllowedRows", "500");
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
    }

    private static void Seed(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE customers (id TEXT, name TEXT, email TEXT, active TEXT, tenant_id TEXT);
            INSERT INTO customers VALUES ('1','Ada','ada@x.example','1','tenant_alpha'),('2','Bob','bob@x.example','0','tenant_alpha');
            CREATE TABLE users (id TEXT, name TEXT, tenant_id TEXT);
            INSERT INTO users VALUES ('1','u1','tenant_gamma');
            CREATE TABLE orders (id TEXT, total TEXT, email TEXT, ssn TEXT, tenant_id TEXT);
            CREATE TABLE employees (id TEXT, name TEXT, ssn TEXT, tenant_id TEXT);
            INSERT INTO employees VALUES ('1','Eve','123-45-6789','tenant_456'),('2','Mallory','987-65-4321','tenant_other');
            CREATE TABLE accounts (id TEXT, balance TEXT, tenant_id TEXT);
            INSERT INTO accounts VALUES ('1','10','victim'),('2','20','tenant_evil');
            CREATE TABLE crm_customers (id TEXT, email TEXT, tenant_id TEXT);
            CREATE TABLE secure_users (id TEXT, email TEXT, tenant_id TEXT);
            CREATE TABLE hr_employees (id TEXT, ssn TEXT, tenant_id TEXT);
            CREATE TABLE payroll (id TEXT, ssn TEXT, tenant_id TEXT);
            CREATE TABLE org (id TEXT, parent_id TEXT, name TEXT, ssn TEXT, tenant_id TEXT);
            INSERT INTO org VALUES ('1',NULL,'root','111-11-1111','tenant_alpha'),('2','1','child','222-22-2222','tenant_alpha'),
                ('3','2','grandchild','333-33-3333','tenant_alpha'),('9',NULL,'foreignroot','999-99-9999','tenant_other'),
                ('10','9','foreignchild','888-88-8888','tenant_other');
            """;
        cmd.ExecuteNonQuery();
        using var rows = conn.CreateCommand();
        rows.CommandText = "INSERT INTO orders VALUES " + string.Join(",", Enumerable.Range(1, 700).Select(i => $"('{i}','{i}','e{i}@x.example','000','tenant_123')")) + ",('9999','1','x','000','tenant_other');";
        rows.ExecuteNonQuery();
    }

    // SEC C-03: WebSQL rejects tables without catalog metadata, so every table used by these tests is registered first.
    private async Task RegisterTableAsync(string tableName, params string[] columns) =>
        await RegisterTableAsync(tableName, null, columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList());

    private async Task RegisterTableAsync(string tableName, Dictionary<string, MaskingRule>? masking, List<TableColumn> columns)
    {
        using var scope = _factory.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = tableName, TableName = tableName, SchemaName = "main", SourceName = "default", SourceType = "Sqlite" },
            Identifier = new TableIdentifier("default", "main", tableName),
            Columns = columns,
            ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>()
        });
    }

    private static ClaimsPrincipal User(string sid, string tenant) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, sid), new Claim(ClaimTypes.PrimarySid, sid), new Claim("tenant_id", tenant)], "TestAuth"));

    private async Task<GovernedSqlResult> RunAsync(string sql, ClaimsPrincipal user, string tenant)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>().ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql), user, new TenantId(tenant));
    }

    [Fact]
    public async Task WebSql_SelectQuery_StreamsJsonRowsSuccessfully()
    {
        await RegisterTableAsync("customers", "id", "name", "email", "active", "tenant_id");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant_alpha");

        var payload = new
        {
            sql = "SELECT id, name, email FROM customers WHERE active = '1'"
        };

        var response = await client.PostAsJsonAsync("/api/v1/sql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

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
        // WP-X0a (SEC-X1-05): a fixed message and a stable code; the response no longer explains the rejected statement class
        body.ShouldContain("SQL_POLICY_VIOLATION");
        body.ShouldNotContain("DDL statements");
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
        body.ShouldContain("SQL_POLICY_VIOLATION");
        body.ShouldNotContain("SELECT statements");
    }

    [Fact]
    public async Task WebSql_Select_ReturnsOnlyTheCallersTenantRows_AndClampsTheLimit()
    {
        await RegisterTableAsync("orders", "id", "total", "email", "ssn", "tenant_id");

        var result = await RunAsync("SELECT * FROM orders WHERE CAST(total AS INTEGER) > 100 LIMIT 5000", User("usr_alice", "tenant_123"), "tenant_123");

        // 700 rows of tenant_123 (600 with total > 100) and one foreign row; the limit is clamped to MaxAllowedRows (500)
        result.RowCount.ShouldBe(500);
        result.Truncated.ShouldBeTrue();
        result.Rows.ShouldAllBe(r => Equals(r["tenant_id"], "tenant_123"));
    }

    [Fact]
    public async Task WebSql_ColumnMaskingRules_AreAppliedInTheResult()
    {
        await RegisterTableAsync(
            "employees",
            new Dictionary<string, MaskingRule> { ["ssn"] = new MaskingRule { RuleType = "REDACT", Replacement = "XXX-XX-XXXX" } },
            [
                new TableColumn { ColumnName = "id", DataType = "varchar" }, new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true }, new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]);

        var result = await RunAsync("SELECT id, name, ssn FROM employees", User("usr_bob", "tenant_456"), "tenant_456");

        var row = result.Rows.ShouldHaveSingleItem();   // tenant_other's row is not visible
        row["name"].ShouldBe("Eve");
        row["ssn"].ShouldNotBe("123-45-6789");
    }

    [Fact]
    public async Task WebSql_AntiBypass_CteShadowing_DoesNotEvadeRlsOnPhysicalTable()
    {
        await RegisterTableAsync("accounts", "id", "balance", "tenant_id");

        // Attacker attempts to shadow physical table 'accounts' inside CTE to query external rows
        string attackSql = @"
            WITH accounts AS (
                SELECT * FROM accounts WHERE tenant_id = 'victim'
            )
            SELECT * FROM accounts";

        // The compiler refuses the self-referencing CTE (it needs WITH RECURSIVE), so nothing is executed and no row can leak. The
        // rewritten form below proves that the physical 'accounts' inside a CTE definition stays tenant scoped.
        await Should.ThrowAsync<TrinoSqlEngine.SqlCompileNotSupportedException>(() => RunAsync(attackSql, User("usr_eve", "tenant_evil"), "tenant_evil"));

        var scoped = await RunAsync(
            "WITH acc AS (SELECT * FROM accounts WHERE tenant_id = 'victim') SELECT * FROM acc",
            User("usr_eve", "tenant_evil"), "tenant_evil");
        scoped.Rows.ShouldBeEmpty();
    }

    // ---- issue #277: WITH RECURSIVE (opt-in, bounded) ----

    private const string OrgWalk =
        "WITH RECURSIVE w(id, name) AS (SELECT id, name FROM org WHERE parent_id IS NULL UNION ALL SELECT o.id, o.name FROM org o JOIN w ON o.parent_id = w.id) SELECT id, name FROM w ORDER BY id";

    private WebApplicationFactory<Program> RecursiveEnabled() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:WebSql:AllowRecursiveCte", "true");
            builder.UseSetting("Gateway:WebSql:MaxRecursionDepth", "100");
        });

    private static async Task RegisterOrgAsync(WebApplicationFactory<Program> factory, bool withSsn, Dictionary<string, MaskingRule>? masking = null)
    {
        using var scope = factory.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var columns = new List<TableColumn>
        {
            new() { ColumnName = "id", DataType = "varchar" }, new() { ColumnName = "parent_id", DataType = "varchar" },
            new() { ColumnName = "name", DataType = "varchar" }, new() { ColumnName = "tenant_id", DataType = "varchar" }
        };
        if (withSsn)
        {
            columns.Add(new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true });
        }

        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "org", TableName = "org", SchemaName = "main", SourceName = "default", SourceType = "Sqlite" },
            Identifier = new TableIdentifier("default", "main", "org"),
            Columns = columns,
            ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>()
        });
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostSqlAsync(WebApplicationFactory<Program> factory, string sql, string tenant = "tenant_alpha")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        var response = await client.PostAsJsonAsync("/api/v1/sql", new { sql });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Code(string body) => JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()!;

    private static string WithoutTrace(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return string.Join("|", doc.RootElement.EnumerateObject().Where(p => p.Name is not ("traceId" or "correlationId")).Select(p => p.Name + "=" + p.Value));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task WebSql_RecursiveCte_Enabled_Returns200AndOnlyTenantRows()
    {
        var factory = RecursiveEnabled();
        await RegisterOrgAsync(factory, withSsn: true);

        var (status, body) = await PostSqlAsync(factory, OrgWalk);

        status.ShouldBe(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(3);   // root, child, grandchild of tenant_alpha
        body.ShouldContain("grandchild");
        body.ShouldNotContain("foreign");                                  // the other tenant's hierarchy is never reached
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task WebSql_RecursiveCte_RowPolicyAndMaskApplyToBothMembers()
    {
        var factory = RecursiveEnabled();
        await RegisterOrgAsync(factory, withSsn: true, new Dictionary<string, MaskingRule> { ["ssn"] = new MaskingRule { RuleType = "REDACT", Replacement = "XXX-XX-XXXX" } });

        var (status, body) = await PostSqlAsync(factory,
            "WITH RECURSIVE w(id, ssn) AS (SELECT id, ssn FROM org WHERE parent_id IS NULL UNION ALL SELECT o.id, o.ssn FROM org o JOIN w ON o.parent_id = w.id) SELECT id, ssn FROM w ORDER BY id");

        status.ShouldBe(HttpStatusCode.OK, body);
        using (var doc = JsonDocument.Parse(body))
        {
            doc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(3);   // anchor and both recursive levels, tenant_alpha only
        }

        foreach (var raw in new[] { "111-11-1111", "222-22-2222", "333-33-3333", "999-99-9999", "888-88-8888" })
        {
            body.ShouldNotContain(raw);                                        // anchor row and recursive members are masked, other tenant absent
        }
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("WITH RECURSIVE w(id, ssn) AS (SELECT id, ssn FROM org WHERE parent_id IS NULL UNION ALL SELECT o.id, o.ssn FROM org o JOIN w ON o.parent_id = w.id) SELECT max(ssn) FROM w")]
    [InlineData("WITH RECURSIVE w(id, ssn) AS (SELECT id, ssn FROM org WHERE parent_id IS NULL UNION ALL SELECT o.id, o.ssn FROM org o JOIN w ON o.parent_id = w.id) SELECT id FROM w WHERE ssn = '222-22-2222'")]
    [InlineData("WITH RECURSIVE w(id, ssn) AS (SELECT id, '000' FROM org WHERE parent_id IS NULL UNION ALL SELECT o.id, o.ssn FROM org o JOIN w ON o.parent_id = w.id) SELECT count(*) FROM w GROUP BY ssn")]
    public async Task WebSql_RecursiveCte_MaskedColumnThroughRecursion_GuardsReject(string sql)
    {
        var factory = RecursiveEnabled();
        await RegisterOrgAsync(factory, withSsn: true, new Dictionary<string, MaskingRule> { ["ssn"] = new MaskingRule { RuleType = "REDACT", Replacement = "XXX-XX-XXXX" } });

        var (status, body) = await PostSqlAsync(factory, sql);

        status.ShouldBe(HttpStatusCode.Forbidden, body);
        body.ShouldNotContain("222-22-2222");
    }

    [Fact]
    public async Task WebSql_RecursiveCte_DeniedColumnInRecursiveMember_BehavesLikeANonRecursiveReference()
    {
        var factory = RecursiveEnabled();
        await RegisterOrgAsync(factory, withSsn: false);   // ssn is not part of the verified catalog: no access

        var recursive = await PostSqlAsync(factory,
            "WITH RECURSIVE w(id, x) AS (SELECT id, name FROM org WHERE parent_id IS NULL UNION ALL SELECT o.id, o.ssn FROM org o JOIN w ON o.parent_id = w.id) SELECT id FROM w");
        var plain = await PostSqlAsync(factory, "SELECT id, ssn FROM org");

        // The recursive member gets no wider column access than a plain reference: same failure, no row, no value.
        recursive.Status.ShouldNotBe(HttpStatusCode.OK, recursive.Body);
        recursive.Status.ShouldBe(plain.Status);
        Code(recursive.Body).ShouldBe(Code(plain.Body));
        recursive.Body.ShouldNotContain("111-11-1111");
    }

    [Fact]
    public async Task WebSql_RecursiveCte_Disabled_Returns422ConstructNotSupported()
    {
        await RegisterOrgAsync(_factory, withSsn: true);

        var (status, body) = await PostSqlAsync(_factory, OrgWalk);

        status.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        Code(body).ShouldBe("SQL_CONSTRUCT_NOT_SUPPORTED");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebSql_KeywordlessSelfReference_Returns422ConstructNotSupported(bool recursiveEnabled)
    {
        var factory = recursiveEnabled ? RecursiveEnabled() : _factory;
        await RegisterOrgAsync(factory, withSsn: true);

        var existing = await PostSqlAsync(factory, "WITH org AS (SELECT id FROM org) SELECT id FROM org");
        var unknown = await PostSqlAsync(factory, "WITH ghost AS (SELECT id FROM ghost) SELECT id FROM ghost");

        existing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, existing.Body);
        Code(existing.Body).ShouldBe("SQL_CONSTRUCT_NOT_SUPPORTED");
        unknown.Status.ShouldBe(existing.Status);
        WithoutTrace(unknown.Body).ShouldBe(WithoutTrace(existing.Body));   // no existence oracle (SEC-277-04)
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task WebSql_UserMaxRecursionHint_IsSyntaxError()
    {
        var factory = RecursiveEnabled();
        await RegisterOrgAsync(factory, withSsn: true);

        var (status, body) = await PostSqlAsync(factory, OrgWalk + " OPTION (MAXRECURSION 32000)");

        status.ShouldBe(HttpStatusCode.BadRequest, body);
        Code(body).ShouldBe("SQL_SYNTAX_ERROR");
    }

    [Fact]
    public async Task WebSql_TablelessRecursiveCte_IsRejectedLikeEveryTablelessStatement()
    {
        var factory = RecursiveEnabled();

        var (status, body) = await PostSqlAsync(factory,
            "WITH RECURSIVE t(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM t WHERE n < 3) SELECT n FROM t");

        // WebSQL statements must reference a governed catalog table (existing policy, unchanged by the recursion switch).
        status.ShouldBe(HttpStatusCode.Forbidden, body);
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

        await RegisterTableAsync(
            "crm_customers",
            new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" } },
            [new TableColumn { ColumnName = "id", DataType = "varchar" }, new TableColumn { ColumnName = "email", DataType = "varchar" }, new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }]);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_test"),
            new Claim("tenant_id", "tenant_test")
        }, "TestAuth"));

        await RegisterTableAsync("orders", "id", "total", "email", "ssn", "tenant_id");
        string attackQuery = "SELECT c.id, o.id FROM crm_customers c JOIN orders o ON c.email = o.email";

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(async () =>
        {
            await sqlService.ValidateAsync(attackQuery, user, new TenantId("tenant_test"));
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

        await RegisterTableAsync(
            "crm_customers",
            new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" } },
            [new TableColumn { ColumnName = "id", DataType = "varchar" }, new TableColumn { ColumnName = "email", DataType = "varchar" }, new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }]);

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
            await sqlService.ValidateAsync(commaJoinQuery, user, new TenantId("tenant_test"));
        });

        ex.Message.ShouldContain("Security Policy Violation: Column 'email'");
        ex.Message.ShouldContain("protected by static redaction");
    }

    [Fact]
    public async Task WebSql_JoinOnHmacPseudonymizedColumn_IsRejectedByTheCompiler()
    {
        using var scope = _factory.Services.CreateScope();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        await RegisterTableAsync(
            "secure_users",
            new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "HMAC" } },
            [new TableColumn { ColumnName = "id", DataType = "varchar" }, new TableColumn { ColumnName = "email", DataType = "varchar" }, new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }]);
        await RegisterTableAsync("orders", "id", "total", "email", "ssn", "tenant_id");

        // X1 (ast-stricter, fail closed): the compiler rejects every masked column in a predicate, including an HMAC pseudonym that the
        // legacy rewriter allowed to join on. Re-admitting it is a follow-up decision.
        var ex = await Should.ThrowAsync<System.Security.SecurityException>(() =>
            sqlService.ValidateAsync("SELECT u.id, o.id FROM secure_users u JOIN orders o ON u.email = o.email", User("usr_test", "tenant_test"), new TenantId("tenant_test")));

        Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        mapped.Code.ShouldBe("SQL_POLICY_VIOLATION");
    }

    [Fact]
    public async Task WebSql_JoinOnCasbinMaskedColumn_WithoutHmac_IsRejectedWithSecurityException()
    {
        using var scope = _factory.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        // Table metadata has NO static ColumnMaskingRules, but sensitive column marked
        await RegisterTableAsync(
            "hr_employees",
            null,
            [new TableColumn { ColumnName = "id", DataType = "varchar" }, new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true }, new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }]);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_test"),
            new Claim("tenant_id", "tenant_test")
        }, "TestAuth"));

        await RegisterTableAsync("payroll", "id", "ssn", "tenant_id");
        string joinQuery = "SELECT e.id, p.id FROM hr_employees e JOIN payroll p ON e.ssn = p.ssn";

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(async () =>
        {
            await sqlService.ValidateAsync(joinQuery, user, new TenantId("tenant_test"));
        });

        ex.Message.ShouldContain("Security Policy Violation: Column 'ssn'");
        ex.Message.ShouldContain("protected by static redaction");
    }

    [Fact]
    public async Task WebSql_UnsupportedAcceptHeader_Returns406NotAcceptable()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sql")
        {
            Content = new StringContent("""{"sql":"SELECT 1"}""", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("text/csv");
        request.Headers.Add("X-Test-User-Sid", "S-1-5-21-1");
        request.Headers.Add("X-Test-Roles", "ClusterAdmin");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotAcceptable);
    }
}
