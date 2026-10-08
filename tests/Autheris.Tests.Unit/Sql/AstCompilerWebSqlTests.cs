namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
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
using Xunit.Abstractions;

/// <summary>
/// Wunsch 4: WebSQL through the governed service with <c>SqlRewriterEngine = AstCompiler</c>, per target dialect.
/// </summary>
public sealed class AstCompilerWebSqlTests(ITestOutputHelper output)
{
    private const string Tenant = "tenant_a";

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-AST"), new Claim("tenant_id", Tenant)], "Test"));

    internal static GovernedSqlExecutionService CreateService(
        string sourceType,
        string engine = "AstCompiler",
        string? rowFilter = null,
        Dictionary<string, object?>? rowFilterParams = null)
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "public", SourceType = sourceType },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "dept", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "created_at", DataType = "timestamp" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(table.Identifier.TableName == ci.Arg<TableIdentifier>().TableName ? table : null));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), rowFilter, hasUnconstrainedColumnAllow: true) with { RowFilterParameters = rowFilterParams });

        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        return new GovernedSqlExecutionService(
            Options.Create(new GatewayOptions
            {
                WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 500, SqlRewriterEngine = engine }
            }),
            consentResolution: resolution,
            tableRepository: repo,
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents);
    }

    private async Task<string> RewriteAsync(string sourceType, string sql, string engine = "AstCompiler")
    {
        var rewritten = await CreateService(sourceType, engine).RewriteSqlAsync(sql, User(), new TenantId(Tenant));
        output.WriteLine(rewritten);
        return rewritten;
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SqlServer")]
    [InlineData("Sqlite")]
    public async Task UnsupportedConstruct_IsBadRequest_NotServerError(string sourceType)
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            RewriteAsync(sourceType, "SELECT dept, SUM(amount) FROM orders GROUP BY CUBE (dept)"));

        ex.Message.ShouldContain("not supported");
    }

    [Theory]
    [InlineData("PostgreSQL", "COALESCE(\"amount\", 0)")]
    [InlineData("SqlServer", "COALESCE([amount], 0)")]
    [InlineData("Sqlite", "COALESCE(\"amount\", 0)")]
    public async Task Aggregates_AndBuiltins_AreEmittedUnquoted(string sourceType, string coalesce)
    {
        var sql = await RewriteAsync(sourceType, "SELECT dept, COUNT(*) AS n, SUM(COALESCE(amount, 0)) FROM orders GROUP BY dept HAVING COUNT(*) > 1");

        sql.ShouldContain("COUNT(*)");
        sql.ShouldContain("HAVING COUNT(*) > 1");
        sql.ShouldContain($"SUM({coalesce})");
    }
}
