namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
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
/// Phase 5 L-4: a statement that the compiler or the analyzer refuses with an engine error (unsupported construct, reserved identifier,
/// syntax error) is audited like a policy rejection, with the stable code of the error mapper and never its message, and is then rethrown
/// unchanged so that every error surface still maps it.
/// </summary>
public sealed class GovernedSqlRejectionAuditTests
{
    private const string Tenant = "tenant_a";
    private const string DataSource = "default";

    private static ClaimsPrincipal Writer() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-L4"), new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Writer")], "Test"));

    private static (GovernedSqlExecutionService Service, List<AuditLogEntry> Audit) CreateService(global::Autheris.Application.Audit.IAuditWriter? writer = null)
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSQL", SourceName = DataSource },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "dept", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(table.Identifier.TableName == ci.Arg<TableIdentifier>().TableName ? table : null));
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true));
        var audit = new List<AuditLogEntry>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(audit.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        var service = global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            Options.Create(new GatewayOptions
            {
                WebSql = new WebSqlOptions { Enabled = true, AllowDml = true, DmlWriterRoles = ["Writer"], DefaultDataSourceName = DataSource, DefaultMaxRows = 100, MaxAllowedRows = 500 }
            }), executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            tableRepository: repo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(repo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            auditLogRepository: auditRepo,
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents, policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
            auditWriter: writer);
        return (service, audit);
    }

    private static JsonElement Details(AuditLogEntry entry) => JsonDocument.Parse(entry.DetailsJson).RootElement;

    [Theory]
    [InlineData("SELECT dept, SUM(amount) FROM orders GROUP BY AUTO", "SQL_CONSTRUCT_NOT_SUPPORTED")]
    [InlineData("SELECT id AS autheris_insights FROM orders", "SQL_RESERVED_IDENTIFIER")]
    [InlineData("SELEC id FROM orders", "SQL_SYNTAX_ERROR")]
    // refused while the statement is analysed, before it is known as DML: audited as a denied query
    [InlineData("UPDATE orders SET amount = 1 WHERE autheris_ins = 1", "SQL_RESERVED_IDENTIFIER")]
    public async Task AnEngineRejectedQuery_IsAuditedAsDenied_WithTheStableCode_AndRethrown(string sql, string code)
    {
        var (service, audit) = CreateService();

        var ex = await Should.ThrowAsync<Exception>(() => service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql), Writer(), new TenantId(Tenant)));

        ex.ShouldNotBeOfType<System.Security.SecurityException>();   // the engine error itself, not a wrapped policy error
        Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        mapped.Code.ShouldBe(code);
        var denied = audit.ShouldHaveSingleItem();
        denied.EventType.ShouldBe(AuditEventTypes.WebSqlQueryDenied);
        denied.Decision.ShouldBe("DENY");
        var details = Details(denied);
        details.GetProperty("reasonCode").GetString().ShouldBe(code);
        details.GetProperty("error").GetString().ShouldBe(code);
        denied.DetailsJson.ShouldNotContain(ex.Message.Length > 0 && ex.Message != code ? ex.Message : "\u0000");   // never the message
    }

    [Theory]
    [InlineData("UPDATE orders SET amount = (SELECT SUM(amount) FROM orders GROUP BY AUTO) WHERE id = 1", "SQL_CONSTRUCT_NOT_SUPPORTED")]
    public async Task AnEngineRejectedDml_IsAuditedAsDmlRejected_WithTheStableCode_AndRethrown(string sql, string code)
    {
        var (service, audit) = CreateService();

        var ex = await Should.ThrowAsync<Exception>(() => service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql), Writer(), new TenantId(Tenant)));

        Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        mapped.Code.ShouldBe(code);
        var rejected = audit.ShouldHaveSingleItem();
        rejected.EventType.ShouldBe(AuditEventTypes.WebSqlDmlRejected);
        rejected.Decision.ShouldBe("DENY");
        Details(rejected).GetProperty("reason").GetString().ShouldBe(code);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_277_09_RecursiveQueryRejectedBeforeCompile_IsAuditedWithTheRecursiveFlag()
    {
        var (service, audit) = CreateService();

        await Should.ThrowAsync<TrinoSqlEngine.SqlCompileNotSupportedException>(() => service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("WITH RECURSIVE t(n) AS (SELECT id FROM orders UNION ALL SELECT n + 1 FROM t WHERE n < 3) SELECT n FROM t"), Writer(), new TenantId(Tenant)));

        var denied = audit.ShouldHaveSingleItem();
        denied.EventType.ShouldBe(AuditEventTypes.WebSqlQueryDenied);
        Details(denied).GetProperty("recursiveCte").GetBoolean().ShouldBeTrue();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_277_09_RecursiveDmlRejected_IsAuditedWithTheRecursiveFlag()
    {
        var (service, audit) = CreateService();

        await Should.ThrowAsync<TrinoSqlEngine.SqlCompileNotSupportedException>(() => service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE orders SET amount = 1 WHERE id IN (WITH RECURSIVE t(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM t WHERE n < 3) SELECT n FROM t)"),
            Writer(), new TenantId(Tenant)));

        var rejected = audit.ShouldHaveSingleItem();
        rejected.EventType.ShouldBe(AuditEventTypes.WebSqlDmlRejected);
        Details(rejected).GetProperty("recursiveCte").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task NonRecursiveRejection_HasNoRecursiveFlag()
    {
        var (service, audit) = CreateService();

        await Should.ThrowAsync<Exception>(() => service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest("SELEC id FROM orders"), Writer(), new TenantId(Tenant)));

        audit.ShouldHaveSingleItem().DetailsJson.ShouldNotContain("recursiveCte");
    }

    [Fact]
    public async Task GovernedSqlExecutor_DeniedQuery_WritesDenyThroughWriter()
    {
        var writer = Substitute.For<global::Autheris.Application.Audit.IAuditWriter>();
        var (service, audit) = CreateService(writer);

        using var cts = new CancellationTokenSource();
        var ex = await Should.ThrowAsync<Exception>(() => service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest("SELEC id FROM orders"), Writer(), new TenantId(Tenant), cts.Token));

        ex.ShouldNotBeOfType<OperationCanceledException>();
        Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.TryMap(ex, out _).ShouldBeTrue("the engine syntax error itself, not a wrapped or audit error");
        audit.ShouldBeEmpty("the repository is only reachable through the writer");
        // A DENY record never depends on the request token.
        await writer.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.WebSqlQueryDenied && e.Decision == "DENY"), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task GovernedSqlExecutor_DmlFailedEvent_HasExplicitPolicy()
    {
        AuditEventTypes.WebSqlDmlFailed.ShouldBe("WEBSQL_DML_FAILED");
        Autheris.Application.Audit.AuditPolicyTable.All.ShouldContainKey(AuditEventTypes.WebSqlDmlFailed);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnArgumentException_WithoutAParseCause_IsAuditedAsBadRequest()
    {
        var (service, audit) = CreateService();
        string tooLong = "SELECT id FROM orders WHERE dept = '" + new string('x', 70_000) + "'";

        await Should.ThrowAsync<ArgumentException>(() => service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(tooLong), Writer(), new TenantId(Tenant)));

        var denied = audit.ShouldHaveSingleItem();
        denied.EventType.ShouldBe(AuditEventTypes.WebSqlQueryDenied);
        Details(denied).GetProperty("reasonCode").GetString().ShouldBe("SQL_BAD_REQUEST");
    }

    [Fact]
    public async Task ACancelledRequest_IsNotAuditedAsARejection()
    {
        var (service, audit) = CreateService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest("SELECT id FROM orders"), Writer(), new TenantId(Tenant), cts.Token));

        audit.ShouldBeEmpty();
    }
}
