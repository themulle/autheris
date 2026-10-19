namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration.Validation;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Federation.CoLocation;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Security;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;
using Xunit;

public sealed class WebSqlCoLocatedDataSourcesSecurityTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA132_01_DirectDatabaseQualification_Rejected()
    {
        // 1. Four-part names (server.database.schema.table) are strictly forbidden
        var fourPartSql = "SELECT * FROM srv.db.dbo.users";
        var ex4 = Should.Throw<WebSqlPolicyException>(() => TableQualificationValidator.Validate(fourPartSql));
        ex4.Code.ShouldBe(GovernedSqlErrorCodes.InvalidIdentifierQualification);

        // 2. Explicit physical database qualification when not in authorized catalogs is forbidden
        var allowedCatalogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "crm", "sales" };
        var threePartUnauthorizedSql = "SELECT * FROM secret_db.dbo.users";
        var ex3 = Should.Throw<WebSqlPolicyException>(() => TableQualificationValidator.Validate(threePartUnauthorizedSql, allowedCatalogs));
        ex3.Code.ShouldBe(GovernedSqlErrorCodes.InvalidIdentifierQualification);

        // 3. Authorized catalog prefix is accepted
        var threePartAuthorizedSql = "SELECT * FROM crm.dbo.users";
        Should.NotThrow(() => TableQualificationValidator.Validate(threePartAuthorizedSql, allowedCatalogs));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA132_02_IndependentRLS_PerDatabaseEnforced()
    {
        var id1 = new TableIdentifier("sales", "dbo", "Orders");
        var meta1 = new TableMetadata
        {
            Identifier = id1,
            Table = new Table { TableName = "Orders", SchemaName = "dbo", SourceName = "sales", PhysicalDatabaseName = "DB_Sales", SourceType = "SqlServer" }
        };
        var id2 = new TableIdentifier("crm", "dbo", "Customers");
        var meta2 = new TableMetadata
        {
            Identifier = id2,
            Table = new Table { TableName = "Customers", SchemaName = "dbo", SourceName = "crm", PhysicalDatabaseName = "DB_CRM", SourceType = "SqlServer" }
        };

        var tables = new List<ResolvedSourceTable>
        {
            new(new TableAccessTarget(null, "dbo", "Orders", null, "dbo.Orders"), id1, meta1, "sales"),
            new(new TableAccessTarget(null, "dbo", "Customers", null, "dbo.Customers"), id2, meta2, "crm")
        };

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [id1] = TableAccessDecision.Allowed(id1, new Dictionary<string, ColumnAccessLevel>()) with
            {
                CombinedRowFilterSql = "Orders.tenant_id = 'T1'"
            },
            [id2] = TableAccessDecision.Allowed(id2, new Dictionary<string, ColumnAccessLevel>()) with
            {
                CombinedRowFilterSql = "Customers.status = 'ACTIVE'"
            }
        };

        // Assert independent isolation passes without interference
        Should.NotThrow(() => SubtreeCompiler.ValidatePolicyIsolation(tables, decisions));
        decisions[id1].CombinedRowFilterSql.ShouldBe("Orders.tenant_id = 'T1'");
        decisions[id2].CombinedRowFilterSql.ShouldBe("Customers.status = 'ACTIVE'");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA132_03_CrossDatabaseCorrelatedRLS_Rejected()
    {
        var id1 = new TableIdentifier("sales", "dbo", "Orders");
        var meta1 = new TableMetadata
        {
            Identifier = id1,
            Table = new Table { TableName = "Orders", SchemaName = "dbo", SourceName = "sales", PhysicalDatabaseName = "DB_Sales", SourceType = "SqlServer" }
        };
        var id2 = new TableIdentifier("crm", "dbo", "Customers");
        var meta2 = new TableMetadata
        {
            Identifier = id2,
            Table = new Table { TableName = "Customers", SchemaName = "dbo", SourceName = "crm", PhysicalDatabaseName = "DB_CRM", SourceType = "SqlServer" }
        };

        var tables = new List<ResolvedSourceTable>
        {
            new(new TableAccessTarget(null, "dbo", "Orders", null, "dbo.Orders"), id1, meta1, "sales"),
            new(new TableAccessTarget(null, "dbo", "Customers", null, "dbo.Customers"), id2, meta2, "crm")
        };

        // Correlated row filter referencing external database DB_CRM or Customers table fails closed
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [id1] = TableAccessDecision.Allowed(id1, new Dictionary<string, ColumnAccessLevel>()) with
            {
                CombinedRowFilterSql = "Orders.customer_id IN (SELECT id FROM DB_CRM.dbo.Customers WHERE region = 'US')"
            },
            [id2] = TableAccessDecision.Allowed(id2, new Dictionary<string, ColumnAccessLevel>())
        };

        var ex = Should.Throw<GatewaySecurityException>(() => SubtreeCompiler.ValidatePolicyIsolation(tables, decisions));
        ex.ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA132_04_PostCompileAstAssertion_ExtraneousTableBlocked()
    {
        var (tree, _) = FastSqlEngine.Default.Parse("SELECT o.id, u.secret FROM DB_Sales.dbo.Orders o JOIN master.dbo.syslogins u ON o.user_id = u.id".AsMemory());
        var statement = new SqlAstBuilder().BuildStatement(tree);

        var authorizedTargets = new List<(string? Database, string? Schema, string Table)>
        {
            ("DB_Sales", "dbo", "Orders"),
            ("DB_CRM", "dbo", "Customers")
        };

        var ex = Should.Throw<SecurityException>(() =>
            PostCompileTableTargetAssertionVisitor.AssertValid(statement, authorizedTargets));
        ex.Message.ShouldContain("SEC-ADG-02");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA132_05_ConnectTimeServerMismatch_FallbackToDuckDb()
    {
        var service = new CoLocationVerificationService();

        // Server returns secondary replica name instead of primary expected host
        var mismatchConn = new FakeDbConnection("SECONDARY_REPLICA_HOST");
        var isMatch = await service.VerifyCoLocationServerAsync(mismatchConn, "PRIMARY_HOST", "pool-mismatch");
        isMatch.ShouldBeFalse();

        // Server matches expected host
        var matchConn = new FakeDbConnection("PRIMARY_HOST\\INSTANCE_1,1433");
        var isMatch2 = await service.VerifyCoLocationServerAsync(matchConn, "PRIMARY_HOST", "pool-match");
        isMatch2.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA132_06_StartupValidation_AsymmetricGroupFails()
    {
        var validator = new CoLocationOptionsValidator();

        // 1. Asymmetric host
        var hostMismatch = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new()
                {
                    ["src1"] = new() { Provider = "SqlServer", ConnectionString = "Server=tcp:host1.corp,1433;Database=db1;User Id=usr;Password=pwd;", CoLocationGroup = "grp1" },
                    ["src2"] = new() { Provider = "SqlServer", ConnectionString = "Server=tcp:host2.corp,1433;Database=db2;User Id=usr;Password=pwd;", CoLocationGroup = "grp1" }
                }
            }
        };
        var r1 = validator.Validate(null, hostMismatch);
        r1.Failed.ShouldBeTrue();
        r1.FailureMessage.ShouldContain("mismatched server hosts");

        // 2. Asymmetric credentials
        var credMismatch = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new()
                {
                    ["src1"] = new() { Provider = "SqlServer", ConnectionString = "Server=tcp:host1.corp,1433;Database=db1;User Id=usr1;Password=pwd1;", CoLocationGroup = "grp1" },
                    ["src2"] = new() { Provider = "SqlServer", ConnectionString = "Server=tcp:host1.corp,1433;Database=db2;User Id=usr2;Password=pwd2;", CoLocationGroup = "grp1" }
                }
            }
        };
        var r2 = validator.Validate(null, credMismatch);
        r2.Failed.ShouldBeTrue();
        r2.FailureMessage.ShouldContain("asymmetric authentication credentials");

        // 3. Provider other than SqlServer
        var nonSql = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new()
                {
                    ["src1"] = new() { Provider = "SqlServer", ConnectionString = "Server=tcp:host1.corp,1433;Database=db1;User Id=usr;Password=pwd;", CoLocationGroup = "grp1" },
                    ["src2"] = new() { Provider = "PostgreSql", ConnectionString = "Host=host1.corp;Database=db2;Username=usr;Password=pwd;", CoLocationGroup = "grp1" }
                }
            }
        };
        var r3 = validator.Validate(null, nonSql);
        r3.Failed.ShouldBeTrue();
        r3.FailureMessage.ShouldContain("Only 'SqlServer' is supported");

        // 4. Single member
        var single = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new()
                {
                    ["src1"] = new() { Provider = "SqlServer", ConnectionString = "Server=tcp:host1.corp,1433;Database=db1;User Id=usr;Password=pwd;", CoLocationGroup = "grp1" }
                }
            }
        };
        var r4 = validator.Validate(null, single);
        r4.Failed.ShouldBeTrue();
        r4.FailureMessage.ShouldContain("at least 2 data sources");

        // 5. Symmetric group passes
        var symmetric = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new()
                {
                    ["src1"] = new() { Provider = "SqlServer", ConnectionString = "Server=tcp:host1.corp,1433;Database=db1;User Id=usr;Password=pwd;", CoLocationGroup = "grp1" },
                    ["src2"] = new() { Provider = "SqlServer", ConnectionString = "Server=tcp:host1.corp,1433;Database=db2;User Id=usr;Password=pwd;", CoLocationGroup = "grp1" }
                }
            }
        };
        var r5 = validator.Validate(null, symmetric);
        r5.Succeeded.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA132_07_Audit_DualTierEmitted()
    {
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var auditEntries = new List<AuditLogEntry>();
        auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => auditEntries.Add(e)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var connectorRegistry = Substitute.For<IAutherisConnectorRegistry>();
        var connector = Substitute.For<IAutherisConnector>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        var splitManager = Substitute.For<IConnectorSplitManager>();
        connector.RecordSource.Returns(recordSource);
        connector.SplitManager.Returns(splitManager);
        splitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("split-1")]);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["id"] = 1 } });

        connectorRegistry.TryGetConnectorForTable(Arg.Any<TableIdentifier>(), out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = connector; return true; });

        var maskingProvider = Substitute.For<IColumnMaskingProvider>();

        var gatewayOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new()
                {
                    ["sales"] = new() { Provider = "SqlServer", CoLocationGroup = "finance", ConnectionString = "Server=tcp:sqlhost,1433;Database=sales_db;" },
                    ["crm"] = new() { Provider = "SqlServer", CoLocationGroup = "finance", ConnectionString = "Server=tcp:sqlhost,1433;Database=crm_db;" }
                }
            },
            WebSql = new WebSqlOptions
            {
                AllowedDataSources = ["sales", "crm"]
            }
        };

        var stagingService = new FederatedStagingService(
            connectorRegistry,
            maskingProvider,
            auditRepo,
            Options.Create(gatewayOptions),
            NullLogger<FederatedStagingService>.Instance);

        var id1 = new TableIdentifier("sales", "dbo", "Orders");
        var meta1 = new TableMetadata
        {
            Identifier = id1,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            Table = new Table { TableName = "Orders", SchemaName = "dbo", SourceName = "sales", PhysicalDatabaseName = "sales_db", SourceType = "SqlServer", DataSourceType = DataSourceType.Sql }
        };
        var id2 = new TableIdentifier("crm", "dbo", "Customers");
        var meta2 = new TableMetadata
        {
            Identifier = id2,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            Table = new Table { TableName = "Customers", SchemaName = "dbo", SourceName = "crm", PhysicalDatabaseName = "crm_db", SourceType = "SqlServer", DataSourceType = DataSourceType.Sql }
        };

        var target1 = new TableAccessTarget(null, "dbo", "Orders", null, "dbo.Orders");
        var target2 = new TableAccessTarget(null, "dbo", "Customers", null, "dbo.Customers");
        var tables = new List<StagingTableRequest>
        {
            new(target1, meta1, TableAccessDecision.Allowed(id1, new Dictionary<string, ColumnAccessLevel>()), "stg_orders", ["id"]),
            new(target2, meta2, TableAccessDecision.Allowed(id2, new Dictionary<string, ColumnAccessLevel>()), "stg_customers", ["id"])
        };

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", "t1")], "Test"));
        var budget = new FederationBudget(10, 10000, 100000, 10_000_000, 50_000_000, 30, 4);

        await stagingService.StageAsync(tables, user, new TenantId("t1"), budget);

        // Verify dual-tier audit: both per-table reads and statement-level subtree read emitted
        auditEntries.Any(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.TargetTable.Contains("Orders")).ShouldBeTrue();
        auditEntries.Any(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.TargetTable.Contains("Customers")).ShouldBeTrue();

        var subtreeEntry = auditEntries.FirstOrDefault(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSubtreeRead);
        subtreeEntry.ShouldNotBeNull();
        subtreeEntry.DetailsJson.ShouldContain("\"colocated\":true");
        subtreeEntry.DetailsJson.ShouldContain("\"coLocationGroup\":\"finance\"");
    }

#nullable disable
    private sealed class FakeDbConnection : DbConnection
    {
        private readonly string _returnedServerName;
        public FakeDbConnection(string returnedServerName) => _returnedServerName = returnedServerName;
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "master";
        public override ConnectionState State => ConnectionState.Open;
        public override string DataSource => "localhost";
        public override string ServerVersion => "15.0";
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotImplementedException();
        protected override DbCommand CreateDbCommand() => new FakeDbCommand(_returnedServerName);
    }

    private sealed class FakeDbCommand : DbCommand
    {
        private readonly object _scalarResult;
        public FakeDbCommand(object scalarResult) => _scalarResult = scalarResult;
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotImplementedException();
        protected override DbTransaction DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object ExecuteScalar() => _scalarResult;
        public override Task<object> ExecuteScalarAsync(CancellationToken cancellationToken) => Task.FromResult(_scalarResult);
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotImplementedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotImplementedException();
    }
#nullable restore
}
