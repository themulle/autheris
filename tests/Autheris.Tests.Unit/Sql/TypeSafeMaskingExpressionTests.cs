namespace Autheris.Tests.Unit.Sql;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TypeSafeMaskingExpressionTests
{
    private static GovernedSqlExecutionService CreateService(TableMetadata tableMeta, TableAccessDecision decision)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(tableMeta));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<System.DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(decision);

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "testds",
                AllowedDataSources = ["testds"]
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["testds"] = new()
                    {
                        Provider = tableMeta.Dialect.ToString()
                    }
                }
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);

        return new GovernedSqlExecutionService(
            Options.Create(options),
            consentResolution: resolution,
            tableRepository: repo,
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents);
    }

    [Theory]
    [InlineData("int", "0")]
    [InlineData("bigint", "0")]
    [InlineData("numeric(10,2)", "0")]
    [InlineData("date", "'1970-01-01'")]
    [InlineData("varchar(50)", "'***'")]
    public async Task Rewrite_MaskedColumn_ProducesTypeSafeDefaultExpression(string dataType, string expectedMask)
    {
        var tableId = new TableIdentifier("testds", "public", "test_items");
        var tableMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { TableName = "test_items", SchemaName = "public", SourceName = "testds", SourceType = "PostgreSql" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "secret_val", DataType = dataType }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["secret_val"] = new MaskingRule { RuleType = "REDACT" }
            }
        };

        var decision = TableAccessDecision.Allowed(
            tableId,
            new Dictionary<string, ColumnAccessLevel> { ["secret_val"] = ColumnAccessLevel.Mask },
            hasUnconstrainedColumnAllow: true);

        var service = CreateService(tableMeta, decision);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "Test"));

        var resultSql = await service.RewriteSqlAsync("SELECT id, secret_val FROM test_items", user, new TenantId("tenant_a"));

        resultSql.ShouldContain(expectedMask);
    }
}
