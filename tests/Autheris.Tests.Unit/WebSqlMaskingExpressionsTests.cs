namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebSqlMaskingExpressionsTests
{
    private static TableMetadata CreateTableWithMaskingRules()
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "customers"),
            Table = new Table
            {
                TableName = "customers",
                SchemaName = "dbo",
                SourceName = "sales",
                SourceType = "PostgreSQL"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "email", DataType = "varchar" },
                new TableColumn { ColumnName = "iban", DataType = "varchar" },
                new TableColumn { ColumnName = "ssn", DataType = "varchar" },
                new TableColumn { ColumnName = "secret_note", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule { RuleType = "MASK_EMAIL" },
                ["iban"] = new MaskingRule { RuleType = "MASK_IBAN" },
                ["ssn"] = new MaskingRule { RuleType = "REDACT" },
                ["secret_note"] = new MaskingRule { RuleType = "NULLIFY" }
            }
        };
    }

    private static GovernedSqlExecutionService CreateService()
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "sales",
                AllowedDataSources = ["sales"]
            }
        };

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(CreateTableWithMaskingRules()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(
                ci.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel>
                {
                    ["email"] = ColumnAccessLevel.Mask,
                    ["iban"] = ColumnAccessLevel.Mask,
                    ["ssn"] = ColumnAccessLevel.Mask,
                    ["secret_note"] = ColumnAccessLevel.Mask
                }));

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        return new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: resolution,
            tableRepository: tableRepo,
            auditLogRepository: null,
            connectionFactory: null,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepo);
    }

    private static ClaimsPrincipal CreateUser()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
            new(ClaimTypes.Name, "alice"),
            new("tenant_id", "tenant-1")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task RewriteSqlAsync_MaskEmail_ProducesEmailMaskExpressionNotGenericAsterisks()
    {
        var service = CreateService();
        var user = CreateUser();

        var sql = await service.RewriteSqlAsync("SELECT email FROM sales.dbo.customers", user, new TenantId("tenant-1"));

        // Should NOT fall through to "'***'"
        sql.ShouldNotContain("AS email FROM");
        sql.ShouldContain("***@***");
        sql.ShouldNotBe("SELECT '***' AS email FROM sales.dbo.customers");
    }

    [Fact]
    public async Task RewriteSqlAsync_MaskIban_ProducesIbanMaskExpressionNotGenericAsterisks()
    {
        var service = CreateService();
        var user = CreateUser();

        var sql = await service.RewriteSqlAsync("SELECT iban FROM sales.dbo.customers", user, new TenantId("tenant-1"));

        // Should NOT fall through to "'***'"
        sql.ShouldContain("** **** **** ");
    }
}
