namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebSqlFilterGuardrailTests
{
    private const string Tenant = "tenant_test";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateEmployeeTable()
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("default", "dbo", "employees"),
            Table = new Table { TableName = "employees", SchemaName = "dbo", SourceType = "SqlServer" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "salary", DataType = "int" },
                new TableColumn { ColumnName = "email", DataType = "varchar" },
                new TableColumn { ColumnName = "ssn", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["salary"] = new MaskingRule { RuleType = "REDACT", Replacement = "0" },
                ["email"] = new MaskingRule { RuleType = "HMAC" }
            }
        };
    }

    private static GovernedSqlExecutionService CreateService()
    {
        var empTable = CreateEmployeeTable();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<TableIdentifier>();
                return Task.FromResult(string.Equals(id.TableName, "employees", StringComparison.OrdinalIgnoreCase) ? empTable : null);
            });

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci =>
            {
                var tableId = ci.ArgAt<TableIdentifier>(3);
                var columnAccess = new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["name"] = ColumnAccessLevel.Clear,
                    ["salary"] = ColumnAccessLevel.Mask,
                    ["email"] = ColumnAccessLevel.Mask,
                    ["ssn"] = ColumnAccessLevel.Deny
                };
                return TableAccessDecision.Allowed(tableId, columnAccess, null, hasUnconstrainedColumnAllow: false);
            });

        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(Arg.Any<string>())
            .Returns(System.Text.Encoding.UTF8.GetBytes("super-secret-key-32-chars-long!"));

        return new GovernedSqlExecutionService(
            Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 500 } }),
            policyEnforcement: null,
            consentResolution: resolution,
            tableRepository: repo,
            auditLogRepository: null,
            connectionFactory: null,
            clientIpResolver: null,
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            secretProvider: secretProvider);
    }

    [Fact]
    public async Task FilterGuardrail_WhenMaskedColumnUsedInWhere_ThrowsWebSqlPolicyException()
    {
        var service = CreateService();
        string sql = "SELECT id, name FROM dbo.employees WHERE salary > 50000";

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("salary");
        ex.Message.ShouldContain("filter predicate");
    }

    [Fact]
    public async Task FilterGuardrail_WhenDeniedColumnUsedInWhere_ThrowsWebSqlPolicyException()
    {
        var service = CreateService();
        string sql = "SELECT id, name FROM dbo.employees WHERE ssn = '123-45-6789'";

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("ssn");
        ex.Message.ShouldContain("filter predicate");
    }

    [Fact]
    public async Task FilterGuardrail_WhenMaskedColumnUsedInHaving_ThrowsWebSqlPolicyException()
    {
        var service = CreateService();
        string sql = "SELECT name, SUM(id) FROM dbo.employees GROUP BY name HAVING salary > 1000";

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("salary");
        ex.Message.ShouldContain("filter predicate");
    }

    [Fact]
    public async Task FilterGuardrail_WhenClearColumnUsedInWhere_IsPermitted()
    {
        var service = CreateService();
        string sql = "SELECT id, name FROM dbo.employees WHERE id = 1";

        var rewritten = await service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant));

        rewritten.ShouldNotBeNull();
        rewritten.ShouldContain("id = 1");
    }

    [Fact]
    public async Task FilterGuardrail_WhenHmacMaskedColumnUsedInWhere_IsPermitted()
    {
        var service = CreateService();
        string sql = "SELECT id, name FROM dbo.employees WHERE email = 'alice@example.com'";

        var rewritten = await service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant));

        rewritten.ShouldNotBeNull();
    }
}
