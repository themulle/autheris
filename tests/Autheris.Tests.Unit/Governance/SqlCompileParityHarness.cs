namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Services;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Query analysis wired to a real governed SQL service over the same catalog and consents, so a test can compare the analysis
/// verdict with the refusal execution gives for the same principal and statement.
/// </summary>
internal sealed class SqlCompileParityHarness
{
    public const string DataSource = "demo";
    public const string Table = "demo.main.t";
    public static readonly TenantId TenantA = new("tenant-a");

    public InMemoryAuditLogRepository Audit { get; } = new();
    public SpySqlConnectionFactory Connections { get; } = new();
    public RecordingCostQuota CostQuota { get; } = new();
    public GovernedSqlExecutionService Governed { get; }
    public QueryAnalysisService Analysis { get; }

    public SqlCompileParityHarness(ISqlCompileParityCheck? compileCheck = null, params Consent[] consents)
    {
        var tables = new InMemoryTableMetadataRepository([DemoTable()]);
        var consentRepository = new InMemoryConsentRepository(consents);
        Governed = GovernedSqlExecutionServiceFactory.Create(
            Options.Create(new GatewayOptions
            {
                WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = DataSource, DefaultMaxRows = 100, MaxAllowedRows = 500 }
            }),
            Audit,
            executionGuard: Autheris.Tests.Unit.GovernedTestSupport.Guard,
            consentResolution: new ConsentResolutionService(),
            tableRepository: tables,
            policyFactory: Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(tables),
            typedPolicies: Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            connectionFactory: Connections,
            environment: new TestHostEnvironment("Development"),
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepository,
            costQuota: CostQuota);
        Analysis = new QueryAnalysisService(
            tables,
            consentRepository,
            Audit,
            compileCheck: compileCheck ?? new GovernedSqlCompileParityCheck(Governed));
    }

    public static ClaimsPrincipal Caller(string sid = "S-1-USER", string tenant = "tenant-a", params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sid),
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", tenant)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")));
    }

    /// <summary>A simulated target as the endpoint builds it, with a completed group resolution (see the report: an unmarked target is denied by the pipeline).</summary>
    public static ClaimsPrincipal Target(string sid) =>
        Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(QueryAnalysisService.CreateSimulatedTargetPrincipal(sid, null, "tenant-a"));

    /// <summary>Allow consent: id, region and amount clear, masked_col masked, secret_col denied.</summary>
    public static Consent AllowConsent(string sid = "S-1-USER") => new()
    {
        TenantId = TenantA,
        TableIdentifier = new TableIdentifier("demo", "main", "t"),
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(sid),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(5),
        ColumnRules =
        [
            new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "tenant_id", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "masked_col", AccessLevel = ColumnAccessLevel.Mask },
            new ConsentColumnRule { ColumnName = "secret_col", AccessLevel = ColumnAccessLevel.Deny }
        ]
    };

    private static TableMetadata DemoTable() => new()
    {
        Identifier = new TableIdentifier("demo", "main", "t"),
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = DataSource,
            SchemaName = "main",
            TableName = "t",
            SourceType = "PostgreSQL",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "region", DataType = "text" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" },
            new TableColumn { ColumnName = "masked_col", DataType = "text" },
            new TableColumn { ColumnName = "secret_col", DataType = "text" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ],
        PrimaryKeyColumns = ["id"]
    };

    public Task<QueryAnalysisResult> AnalyzeAsync(string sql, ClaimsPrincipal caller, ClaimsPrincipal? target = null) =>
        Analysis.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, DataSource, "q1")], caller, target);

    /// <summary>The refusal execution gives for the statement, or null when execution accepts it.</summary>
    public async Task<SqlErrorMapping?> ExecutionRefusalAsync(string sql, ClaimsPrincipal principal)
    {
        try
        {
            await Governed.ValidateAsync(sql, principal, principal.GetTenantId(), DataSource, CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (SqlCompilerErrorMapper.TryMap(ex, out _))
        {
            SqlCompilerErrorMapper.TryMap(ex, out var mapping);
            return mapping;
        }
    }
}

/// <summary>Compile check double: records the calls and answers with a fixed failure, a fixed exception or success.</summary>
internal sealed class FakeCompileParityCheck(SqlCompileParityFailure? failure = null, Exception? exception = null) : ISqlCompileParityCheck
{
    public int Calls { get; private set; }

    public Task<SqlCompileParityFailure?> CheckAsync(string sql, ClaimsPrincipal principal, string? dataSourceName, CancellationToken ct)
    {
        Calls++;
        return exception is null ? Task.FromResult(failure) : Task.FromException<SqlCompileParityFailure?>(exception);
    }
}
