namespace Autheris.Tests.Unit.Security;

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

/// <summary>
/// Befund 3.6 / Wunsch 8: WHERE, HAVING and ORDER BY on denied or statically masked columns used to run against the
/// replacement value (NULL or '***'): text comparisons silently returned 0 rows, numeric comparisons failed with 500.
/// Such predicates are now rejected up front with a policy violation (403).
/// </summary>
public sealed class WebSqlRedactedPredicateGuardrailTests
{
    private const string Tenant = "tenant_a";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-WEBSQL-USER"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateTable(string table) => new()
    {
        Identifier = new TableIdentifier("default", "dbo", table),
        Table = new Table { TableName = table, SchemaName = "dbo", SourceType = "SqlServer" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "email", DataType = "varchar" },
            new TableColumn { ColumnName = "salary", DataType = "int" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>
        {
            ["email"] = new() { RuleType = "REDACT", Replacement = "***" }
        }
    };

    /// <param name="redacted">Tables whose <c>email</c> is masked and <c>salary</c> denied; all other tables see every column in clear.</param>
    private static GovernedSqlExecutionService CreateService(Dictionary<string, TableMetadata> tables, ISet<string> redacted)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<TableIdentifier>();
                return Task.FromResult(tables.TryGetValue(id.TableName, out var t) ? t : null);
            });

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci =>
            {
                var tableId = ci.ArgAt<TableIdentifier>(3);
                bool isRedacted = redacted.Contains(tableId.TableName);
                var columnAccess = new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["tenant_id"] = ColumnAccessLevel.Clear,
                    ["email"] = isRedacted ? ColumnAccessLevel.Mask : ColumnAccessLevel.Clear,
                    ["salary"] = isRedacted ? ColumnAccessLevel.Deny : ColumnAccessLevel.Clear
                };
                return TableAccessDecision.Allowed(tableId, columnAccess, null, hasUnconstrainedColumnAllow: false);
            });

        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        return new GovernedSqlExecutionService(
            Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 500 } }),
            policyEnforcement: null,
            consentResolution: resolution,
            tableRepository: repo,
            connectionFactory: null,
            clientIpResolver: null,
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            secretProvider: null);
    }

    private static GovernedSqlExecutionService CreateSingleTableService() =>
        CreateService(
            new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase) { ["people"] = CreateTable("people") },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "people" });

    [Theory]
    [InlineData("SELECT id FROM dbo.people WHERE email = 'anna@example.com'", "email")]
    [InlineData("SELECT id FROM dbo.people WHERE email LIKE 'a%'", "email")]
    [InlineData("SELECT id FROM dbo.people p WHERE p.salary > 5000", "salary")]
    [InlineData("SELECT id FROM dbo.people WHERE id = 1 AND salary IS NOT NULL", "salary")]
    [InlineData("SELECT id, COUNT(*) FROM dbo.people GROUP BY id HAVING MAX(salary) > 1", "salary")]
    [InlineData("SELECT id FROM dbo.people ORDER BY email", "email")]
    [InlineData("SELECT id FROM dbo.people p ORDER BY p.salary DESC", "salary")]
    [InlineData("SELECT id FROM dbo.people WHERE id IN (SELECT id FROM dbo.people WHERE salary > 1)", "salary")]
    [InlineData("SELECT COUNT(*) FILTER (WHERE email = 'anna@example.com') FROM dbo.people", "email")]
    [InlineData("SELECT id, row_number() OVER (ORDER BY salary) FROM dbo.people", "salary")]
    public async Task Predicate_OnRedactedColumn_IsRejectedWithPolicyViolation(string sql, string column)
    {
        var service = CreateSingleTableService();

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain($"'{column}'");
        ex.Message.ShouldContain("filter predicate (WHERE/HAVING/ORDER BY)");
    }

    [Theory]
    [InlineData("SELECT id, email, salary FROM dbo.people")]
    [InlineData("SELECT id, email FROM dbo.people WHERE id > 10 ORDER BY id")]
    [InlineData("SELECT id, COUNT(*) FROM dbo.people GROUP BY id HAVING COUNT(*) > 1")]
    public async Task RedactedColumn_OnlyProjected_IsAllowed(string sql)
    {
        var service = CreateSingleTableService();

        var rewritten = await service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant));

        rewritten.ShouldNotBeNull();
    }

    [Fact]
    public async Task QualifiedPredicate_OnClearColumnOfOtherTable_WithSameName_IsAllowed()
    {
        var tables = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["people"] = CreateTable("people"),
            ["contacts"] = CreateTable("contacts"),
        };
        var service = CreateService(tables, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "people" });

        string sql = "SELECT c.id FROM dbo.contacts c JOIN dbo.people p ON c.id = p.id WHERE c.email = 'anna@example.com' ORDER BY c.salary";
        var rewritten = await service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant));

        rewritten.ShouldNotBeNull();
    }

    [Fact]
    public async Task QualifiedPredicate_OnRedactedColumnInJoinedQuery_IsRejected()
    {
        var tables = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["people"] = CreateTable("people"),
            ["contacts"] = CreateTable("contacts"),
        };
        var service = CreateService(tables, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "people" });

        string sql = "SELECT c.id FROM dbo.contacts c JOIN dbo.people p ON c.id = p.id WHERE p.email = 'anna@example.com'";
        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("people");
    }
}
