namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

[Trait("Category", "Security")]
public sealed class GovernedLoggingGeneratedSqlTests
{
    private static readonly ClaimsPrincipal TestUser = Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
            new Claim("tenant_id", "tenant_a"),
            new Claim(ClaimTypes.Role, "Admin")
        ],
        "Test")));

    private static (GovernedSqlRewriter Rewriter, List<(LogLevel Level, string Message)> Logs) CreateLoggingPipeline(bool logSql, bool allowStatementText = true)
    {
        var logs = new List<(LogLevel, string)>();
        var gateway = new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" },
            Logging = new LoggingOptions { LogGeneratedSql = logSql, AllowStatementText = allowStatementText },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = "Host=x" }
                }
            }
        };
        var options = Options.Create(gateway);
        var ordersId = new TableIdentifier("default", "public", "orders");
        var orders = new TableMetadata
        {
            Identifier = ordersId,
            Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSQL", SourceName = "default", IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "bigint" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(ci.Arg<TableIdentifier>().TableName == "orders" ? orders : null));

        var decision = TableAccessDecision.Allowed(ordersId, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(), callerScopes: Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ => decision);
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));

        var rewriter = new GovernedSqlRewriter(
            options, consentResolution: resolution, tableRepository: repo, consentRepository: consents, sqlEngine: FastSqlEngine.Default,
            policyFactory: GovernedTestSupport.PolicyFactory(repo, options),
            typedPolicies: GovernedTestSupport.TypedPolicies(),
            logger: new ListLogger<GovernedSqlRewriter>(logs));
        return (rewriter, logs);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task LOG426_T04_AllowStatementText_Ceiling_SuppressesLogging()
    {
        var (rewriter, logs) = CreateLoggingPipeline(logSql: true, allowStatementText: false);

        await rewriter.RewriteCoreAsync(
            "SELECT id FROM public.orders",
            TestUser,
            new TenantId("tenant_a"),
            dataSourceName: null,
            dmlContext: null,
            CancellationToken.None);

        logs.ShouldNotContain(l => l.Message.Contains("Compiled secured SQL"));
        logs.ShouldNotContain(l => l.Message.Contains("SELECT"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task LOG426_T05_SqlTextScrubber_SanitizesLiterals_BeforeLogging()
    {
        var (rewriter, logs) = CreateLoggingPipeline(logSql: true, allowStatementText: true);

        await rewriter.RewriteCoreAsync(
            "SELECT id FROM public.orders WHERE id = 12345 AND tenant_id = 'secret_literal'",
            TestUser,
            new TenantId("tenant_a"),
            dataSourceName: null,
            dmlContext: null,
            CancellationToken.None);

        var sqlLog = logs.Find(l => l.Message.Contains("Compiled secured SQL ["));
        sqlLog.Level.ShouldBe(LogLevel.Debug);

        sqlLog.Message.ShouldNotContain("12345");
        sqlLog.Message.ShouldNotContain("secret_literal");
        sqlLog.Message.ShouldContain("Compiled secured SQL [");
    }

    [Fact]
    public async Task LogGeneratedSql_WhenFalse_EmitsNoSqlLog()
    {
        var (rewriter, logs) = CreateLoggingPipeline(logSql: false);

        await rewriter.RewriteCoreAsync(
            "SELECT id FROM public.orders",
            TestUser,
            new TenantId("tenant_a"),
            dataSourceName: null,
            dmlContext: null,
            CancellationToken.None);

        logs.ShouldNotContain(l => l.Message.Contains("Compiled secured SQL"));
    }
}
