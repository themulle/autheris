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

public sealed class Sql6JoinGuardrailTests
{
    private const string Tenant = "tenant_a";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-WEBSQL-USER"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateTable(string table, bool maskUserId)
    {
        var maskingRules = new Dictionary<string, MaskingRule>();
        if (maskUserId)
        {
            maskingRules["user_id"] = new MaskingRule
            {
                RuleType = "REDACT",
                Replacement = "REDACTED"
            };
        }

        return new TableMetadata
        {
            Identifier = new TableIdentifier("default", "dbo", table),
            Table = new Table { TableName = table, SchemaName = "dbo", SourceType = "SqlServer" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "user_id", DataType = "varchar" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ],
            ColumnMaskingRules = maskingRules
        };
    }

    private static GovernedSqlExecutionService CreateService(Dictionary<string, TableMetadata> tables)
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
                var isMasked = tables.TryGetValue(tableId.TableName, out var t) && t.ColumnMaskingRules.ContainsKey("user_id");
                var columnAccess = new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["tenant_id"] = ColumnAccessLevel.Clear,
                    ["user_id"] = isMasked ? ColumnAccessLevel.Mask : ColumnAccessLevel.Clear
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
            auditLogRepository: Substitute.For<IAuditLogRepository>(),
            connectionFactory: null,
            clientIpResolver: null,
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            secretProvider: null);
    }

    [Fact]
    public async Task JoinGuardrail_WhenDifferentTableHasMaskedColumnWithSameName_DoesNotTriggerFalsePositive()
    {
        // Table A has masked user_id. Table B and C have clear user_id.
        // Query joins B and C on user_id, referencing A without joining on A.user_id.
        var tables = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["table_a"] = CreateTable("table_a", maskUserId: true),
            ["table_b"] = CreateTable("table_b", maskUserId: false),
            ["table_c"] = CreateTable("table_c", maskUserId: false),
        };

        var service = CreateService(tables);

        // SQL-6: Table A must not be falsely rejected because b and c are joined on user_id
        string sql = "SELECT b.id FROM dbo.table_b b JOIN dbo.table_c c ON b.user_id = c.user_id, dbo.table_a a WHERE a.id = 1";
        var rewritten = await service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant));

        rewritten.ShouldNotBeNull();
    }

    [Fact]
    public async Task JoinGuardrail_WhenTableWithMaskedColumnIsActuallyJoined_ThrowsWebSqlPolicyException()
    {
        // Table A has masked user_id. Table B has clear user_id.
        // Query joins A and B on user_id -> must be rejected by SEC-JOIN-01 guardrail!
        var tables = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["table_a"] = CreateTable("table_a", maskUserId: true),
            ["table_b"] = CreateTable("table_b", maskUserId: false),
        };

        var service = CreateService(tables);

        string sql = "SELECT a.id FROM dbo.table_a a JOIN dbo.table_b b ON a.user_id = b.user_id";
        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("cannot be used in a relational JOIN predicate");
        ex.Message.ShouldContain("table_a");
    }
}
