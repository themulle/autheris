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
/// SQL-2: WebSQL keys its per-table policy maps case-insensitively. Two references in one statement that fold to
/// the same key but are spelled differently can address two different physical relations (PostgreSQL quoted
/// identifiers, SQL Server with a case-sensitive collation); their policies would then overwrite each other.
/// Such statements are rejected.
/// </summary>
public sealed class WebSqlCaseCollisionSql2Tests
{
    private const string Tenant = "tenant_a";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-WEBSQL-USER"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateTable(string schema, string table, string sourceType) => new()
    {
        Identifier = new TableIdentifier("default", schema, table),
        Table = new Table { TableName = table, SchemaName = schema, SourceType = sourceType },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    private static GovernedSqlExecutionService CreateService(TableMetadata table, Func<TableIdentifier, TableAccessDecision>? decide = null)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        // Like the governance repositories: catalog lookups are case-insensitive.
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(table.Identifier.Equals(ci.Arg<TableIdentifier>()) ? table : null));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => (decide ?? (t => TableAccessDecision.Allowed(t, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true)))(ci.ArgAt<TableIdentifier>(3)));

        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

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
            secretProvider: null);
    }

    [Fact]
    public async Task SqlServer_SameTableSpelledWithDifferentCase_IsRejected()
    {
        var service = CreateService(CreateTable("dbo", "Orders", "SqlServer"));

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT a.id FROM dbo.Orders a JOIN dbo.orders b ON a.id = b.id", CreateUser(), new TenantId(Tenant)));
    }

    [Fact]
    public async Task PostgreSql_QuotedAndUnquotedVariant_IsRejected()
    {
        var service = CreateService(CreateTable("public", "orders", "PostgreSQL"));

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT a.id FROM orders a JOIN \"Orders\" b ON a.id = b.id", CreateUser(), new TenantId(Tenant)));
    }

    [Fact]
    public async Task SelfJoin_WithIdenticalSpelling_IsStillAllowed()
    {
        var service = CreateService(CreateTable("dbo", "Orders", "SqlServer"));

        var sql = await service.RewriteSqlAsync("SELECT a.id FROM dbo.Orders a JOIN dbo.Orders b ON a.id = b.id", CreateUser(), new TenantId(Tenant));

        sql.ShouldContain("tenant_id = 'tenant_a'");
    }
}
