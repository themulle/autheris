namespace Autheris.Tests.Unit.Governance.Writes;

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Writes;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

public sealed class TableWritePolicyAndBoundedWriteTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier ParamsTable = new("analytics", "main", "report_params");
    private static readonly Guid ParamsTableId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string AliceSid = "S-1-5-21-ALICE";
    private const string FinanceGroupSid = "S-1-5-21-GRP-FINANCE";

    private static TableMetadata BuildParamsMetadata(bool withTenantColumn = true) => new()
    {
        Identifier = ParamsTable,
        Table = new Table
        {
            Id = ParamsTableId,
            SourceName = "analytics",
            SourceType = "Databricks",
            PhysicalDatabaseName = "prod",
            SchemaName = "main",
            TableName = "report_params",
            IsActive = true
        },
        TenantColumnName = withTenantColumn ? "tenant_id" : null,
        PrimaryKeyColumns = ["param_id"],
        Columns =
        [
            new TableColumn { ColumnName = "param_id", DataType = "varchar(64)" },
            new TableColumn { ColumnName = "department_id", DataType = "varchar(64)" },
            new TableColumn { ColumnName = "owner_group_sid", DataType = "varchar(64)" },
            new TableColumn { ColumnName = "param_value", DataType = "varchar(256)" },
            new TableColumn { ColumnName = "updated_by_sid", DataType = "varchar(64)" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar(64)" }
        ]
    };

    private static TableWritePolicy BuildBoundedPolicy(
        bool requireRowFilter = true,
        IReadOnlyDictionary<string, string>? stamped = null) => new(
        Tenant: TenantA,
        TableId: ParamsTableId,
        Mode: TableWriteMode.BoundedSingleTable,
        AllowedOperations: [WriteOperation.Insert, WriteOperation.Update, WriteOperation.Delete],
        WritableColumns: ["param_id", "department_id", "owner_group_sid", "param_value", "updated_by_sid", "tenant_id"],
        RequirePrimaryKeyPredicate: true,
        RequireRowFilter: requireRowFilter,
        MaxRowsPerStatement: 10,
        ServerStampedColumns: stamped,
        UpdatedBy: "S-1-5-21-ADMIN",
        UpdatedAt: DateTimeOffset.UtcNow);

    private static ClaimsPrincipal CreateUserPrincipal(
        string sid = AliceSid,
        string? groupSid = FinanceGroupSid,
        bool readOnly = false,
        bool degradedGroups = false,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", TenantA.Value)
        };
        if (!string.IsNullOrEmpty(groupSid))
        {
            claims.Add(new Claim(ClaimTypes.GroupSid, groupSid));
        }

        if (readOnly)
        {
            claims.Add(new Claim("scope", "data.read"));
            claims.Add(new Claim(TokenAccessScope.ClaimType, TokenAccessScope.ReadOnlyValue, ClaimValueTypes.String, TokenAccessScope.Issuer));
        }

        claims.Add(GroupResolutionClaims.CreateMarker());
        claims.Add(GroupResolutionClaims.CreateStatus(degradedGroups ? GroupResolutionStatus.Degraded : GroupResolutionStatus.Complete));

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static PolicyPredicateRowPolicy BuildDepartmentSelectRls(string department, TargetSqlDialect dialect = TargetSqlDialect.Databricks)
    {
        var expr = new BinaryExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier("department_id", IsQuoted: true)])),
            BinaryOperator.Equal,
            new PolicyParameterExpression("__pol_dept", SqlParameterType.String, ParameterOrigin.Policy));
        var parameters = new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
        {
            ["__pol_dept"] = new PolicyValue(department, SqlParameterType.String)
        }.ToFrozenDictionary(StringComparer.Ordinal);
        var predicate = new PolicyPredicate(expr, parameters, $"dept_{department}", ImmutableHashSet.Create(StringComparer.Ordinal, "department_id"));
        return new PolicyPredicateRowPolicy(predicate, dialect, new StubPolicyRenderer());
    }

    private static PolicyPredicateRowPolicy BuildAdGroupSelectRls(TargetSqlDialect dialect, params string[] groupSids)
    {
        var items = new List<Expression>(groupSids.Length);
        var parameters = new Dictionary<string, PolicyValue>(StringComparer.Ordinal);
        for (int i = 0; i < groupSids.Length; i++)
        {
            var pName = $"__pol_grp_{i}";
            items.Add(new PolicyParameterExpression(pName, SqlParameterType.String, ParameterOrigin.Policy));
            parameters[pName] = new PolicyValue(groupSids[i], SqlParameterType.String);
        }

        var expr = new InListExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier("owner_group_sid", IsQuoted: true)])),
            items,
            IsNotIn: false);
        var predicate = new PolicyPredicate(expr, parameters.ToFrozenDictionary(StringComparer.Ordinal), "grp_in", ImmutableHashSet.Create(StringComparer.Ordinal, "owner_group_sid"));
        return new PolicyPredicateRowPolicy(predicate, dialect, new StubPolicyRenderer());
    }

    private static TableAccessDecision BuildAllowedWriteDecision(
        TableWritePolicy policy,
        IRowPolicy? rowPolicy = null)
    {
        var baseDecision = new TableAccessDecision(
            Table: ParamsTable,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase),
            CombinedRowFilterSql: null,
            DeniedReasons: [])
        {
            WriteGrant = new TableWriteDecision(
                RowScope: WriteRowScope.OwnRows,
                WritableColumns: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "param_id", "department_id", "owner_group_sid", "param_value", "tenant_id"
                },
                MaxRowsPerStatement: 10,
                GrantIds: ["grant-1"],
                Policy: policy)
        };

        return rowPolicy is not null ? baseDecision.WithAdditionalRowPolicy(rowPolicy) : baseDecision;
    }

    // ---- #1154: TableWritePolicy validation & service ----

    [Fact]
    public void TableWritePolicy_Validate_RejectsInvalidBoundedConfigurations()
    {
        var noOps = BuildBoundedPolicy() with { AllowedOperations = [] };
        Should.Throw<InvalidOperationException>(() => noOps.Validate());

        var readOnlyWithOps = BuildBoundedPolicy() with { Mode = TableWriteMode.ReadOnly };
        Should.Throw<InvalidOperationException>(() => readOnlyWithOps.Validate());

        var badStamp = BuildBoundedPolicy(stamped: new Dictionary<string, string> { ["updated_by_sid"] = "ArbitraryExpr" });
        Should.Throw<InvalidOperationException>(() => badStamp.Validate());

        var valid = BuildBoundedPolicy(stamped: new Dictionary<string, string> { ["updated_by_sid"] = TableWriteServerStampKinds.CallerSid });
        Should.NotThrow(() => valid.Validate());
    }

    [Fact]
    public async Task TableWritePolicyService_RequiresGovernanceAdmin_RejectsRowPolicyDependency_AndAuditsFailClosed()
    {
        var repo = new InMemoryTableWritePolicyRepository();
        var metaRepo = new FakeTableMetadataRepository(BuildParamsMetadata());
        var audit = new RecordingAuditWriter();
        var caps = new FakeGovernanceCapabilities();
        var depIndex = new FakeRowPolicyDependencyIndex();

        var service = new TableWritePolicyService(repo, metaRepo, audit, caps, depIndex);
        var input = new UpsertTableWritePolicyInput(
            DatasetId: ParamsTable.ToString(),
            Mode: TableWriteMode.BoundedSingleTable,
            AllowedOperations: [WriteOperation.Insert, WriteOperation.Update, WriteOperation.Delete],
            WritableColumns: ["param_id", "department_id", "param_value"],
            RequirePrimaryKeyPredicate: true,
            RequireRowFilter: true,
            MaxRowsPerStatement: 10,
            ServerStampedColumns: new Dictionary<string, string> { ["updated_by_sid"] = TableWriteServerStampKinds.CallerSid });

        // Reader cannot set policy
        var readerCtx = SecurityPrincipalContext.FromPrincipal(CreateUserPrincipal(), TenantA);
        var forbidden = await service.UpsertAsync(readerCtx, TenantA, input);
        forbidden.Status.ShouldBe(TableWritePolicyStatus.Forbidden);

        // GovernanceAdmin succeeds and audits
        var adminCtx = SecurityPrincipalContext.FromPrincipal(CreateUserPrincipal(roles: "GovernanceAdmin"), TenantA);
        var saved = await service.UpsertAsync(adminCtx, TenantA, input);
        saved.Status.ShouldBe(TableWritePolicyStatus.Ok);
        saved.Value.ShouldNotBeNull();
        saved.Value!.Mode.ShouldBe(TableWriteMode.BoundedSingleTable);
        audit.Entries.Count(e => e.Decision == "ALLOW").ShouldBe(1);

        // Table referenced by a row policy subquery cannot be enabled for writes
        depIndex.DependentTables.Add(ParamsTable);
        var depRejected = await service.UpsertAsync(adminCtx, TenantA, input);
        depRejected.Status.ShouldBe(TableWritePolicyStatus.Invalid);
        depRejected.ErrorCode.ShouldBe("WRITE_POLICY_DEPENDENCY_TABLE");
    }

    [Fact]
    public async Task WriteGrantEvaluator_EnforcesTableWritePolicy_IntersectsColumns_CapsRows_AndBlocksReadOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var consentRepo = new FakeConsentRepository(
        [
            new Consent
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                TableId = ParamsTableId,
                TableIdentifier = ParamsTable,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = new Sid(AliceSid),
                ValidFrom = now.AddDays(-1),
                ValidTo = now.AddDays(1),
                WriteRules =
                [
                    new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["param_id", "param_value", "updated_by_sid"], WriteRowScope.OwnRows, MaxRowsPerStatement: 500),
                    new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["param_value", "updated_by_sid"], WriteRowScope.OwnRows, MaxRowsPerStatement: 500)
                ]
            }
        ]);

        var policyRepo = new InMemoryTableWritePolicyRepository();
        await policyRepo.UpsertAsync(BuildBoundedPolicy(stamped: new Dictionary<string, string> { ["updated_by_sid"] = TableWriteServerStampKinds.CallerSid }));

        var evaluator = new WriteGrantEvaluator(consentRepo, writePolicies: policyRepo, requireTableWritePolicy: true);
        var user = CreateUserPrincipal();
        var meta = BuildParamsMetadata();

        // Update on param_value succeeds, capped to policy MaxRowsPerStatement (10 < 500), excludes server-stamped updated_by_sid
        var updateDecision = await evaluator.EvaluateAsync(new Autheris.Application.Policy.WriteGrants.WriteGrantQuery(
            user, TenantA, meta, WriteOperation.Update, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "param_value" }));
        updateDecision.IsAllowed.ShouldBeTrue();
        updateDecision.MaxRowsPerStatement.ShouldBe(10);
        updateDecision.WritableColumns.ShouldNotContain("updated_by_sid");
        updateDecision.Policy.ShouldNotBeNull();

        // Update targeting server-stamped column is denied
        var stampedUpdate = await evaluator.EvaluateAsync(new Autheris.Application.Policy.WriteGrants.WriteGrantQuery(
            user, TenantA, meta, WriteOperation.Update, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "updated_by_sid" }));
        stampedUpdate.IsAllowed.ShouldBeFalse();

        // ReadOnly policy blocks all writes even with an active consent write rule
        await policyRepo.UpsertAsync(new TableWritePolicy(
            Tenant: TenantA,
            TableId: ParamsTableId,
            Mode: TableWriteMode.ReadOnly,
            AllowedOperations: []));
        var blocked = await evaluator.EvaluateAsync(new Autheris.Application.Policy.WriteGrants.WriteGrantQuery(
            user, TenantA, meta, WriteOperation.Insert, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "param_value" }));
        blocked.IsAllowed.ShouldBeFalse();
    }

    // ---- #1156: BoundedWriteSqlBuilder (Databricks MERGE, PostgreSQL ON CONFLICT, SQL Server MERGE) ----

    [Fact]
    public void BoundedWriteSqlBuilder_GeneratesDialectUpsertWithSelectRlsGuardAndBoundParameters()
    {
        var meta = BuildParamsMetadata();
        var row = new List<KeyValuePair<string, object?>>
        {
            new("department_id", "FINANCE"),
            new("owner_group_sid", FinanceGroupSid),
            new("param_id", "p-42"),
            new("param_value", "0.19"),
            new("tenant_id", TenantA.Value)
        };
        var protectedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "param_id", "department_id", "tenant_id" };

        // Databricks Delta MERGE INTO
        var dbxRls = BuildDepartmentSelectRls("FINANCE", TargetSqlDialect.Databricks).Predicate;
        var dbx = BoundedWriteSqlBuilder.BuildUpsert(
            TargetSqlDialect.Databricks, meta, row, ["param_id"], protectedCols, "tenant_id", TenantA, dbxRls);
        dbx.Sql.ShouldContain("MERGE INTO `prod`.`main`.`report_params` AS `t`");
        dbx.Sql.ShouldContain("WHEN MATCHED AND (");
        dbx.Sql.ShouldContain("`t`.`department_id`");
        dbx.Sql.ShouldContain("WHEN NOT MATCHED THEN INSERT");
        dbx.Sql.ShouldNotContain("FINANCE");
        dbx.Sql.ShouldNotContain("0.19");
        dbx.Parameters.Select(p => p.Value).ShouldContain("FINANCE");
        dbx.Parameters.Select(p => p.Value).ShouldContain("0.19");

        // PostgreSQL INSERT ... ON CONFLICT DO UPDATE ... WHERE <SELECT RLS>
        var pgRls = BuildAdGroupSelectRls(TargetSqlDialect.PostgreSql, FinanceGroupSid).Predicate;
        var pg = BoundedWriteSqlBuilder.BuildUpsert(
            TargetSqlDialect.PostgreSql, meta, row, ["param_id"], protectedCols, "tenant_id", TenantA, pgRls);
        pg.Sql.ShouldContain("INSERT INTO");
        pg.Sql.ShouldContain("ON CONFLICT (\"param_id\") DO UPDATE SET");
        pg.Sql.ShouldContain("WHERE");
        pg.Sql.ShouldContain("\"owner_group_sid\"");
        pg.Sql.ShouldNotContain(FinanceGroupSid);
        pg.Parameters.Select(p => p.Value).ShouldContain(FinanceGroupSid);

        // SQL Server MERGE INTO ... WITH (HOLDLOCK)
        var mssqlRls = BuildDepartmentSelectRls("FINANCE", TargetSqlDialect.SqlServer).Predicate;
        var mssql = BoundedWriteSqlBuilder.BuildUpsert(
            TargetSqlDialect.SqlServer, meta, row, ["param_id"], protectedCols, "tenant_id", TenantA, mssqlRls);
        mssql.Sql.ShouldContain("MERGE INTO [prod].[main].[report_params] WITH (HOLDLOCK) AS [t]");
        mssql.Sql.ShouldContain("WHEN MATCHED AND (");
        mssql.Sql.ShouldEndWith(";");
    }

    // ---- #1156: BoundedRowWriteService end-to-end (RLS reuse, server-stamping, idempotency, WriteConnectionString, GraphQL) ----

    [Fact]
    public async Task BoundedRowWriteService_UpsertInsertUpdateDelete_EnforcesSelectRls_ServerStamping_AndIdempotency()
    {
        var meta = BuildParamsMetadata();
        var metaRepo = new FakeTableMetadataRepository(meta);
        var policyRepo = new InMemoryTableWritePolicyRepository();
        var policy = BuildBoundedPolicy(
            requireRowFilter: true,
            stamped: new Dictionary<string, string> { ["updated_by_sid"] = TableWriteServerStampKinds.CallerSid });
        await policyRepo.UpsertAsync(policy);

        var financeRls = BuildDepartmentSelectRls("FINANCE", TargetSqlDialect.Databricks);
        var pdp = new FakeAccessDecisionPoint(_ => BuildAllowedWriteDecision(policy, financeRls));
        var audit = new RecordingAuditWriter();
        var conn = new RecordingDbConnection();
        var connFactory = new FakeConnectionFactory(conn);
        var sessionInit = new NoOpSessionInitializer();
        var idempotency = new InMemoryIdempotencyStore();
        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["analytics"] = new DataSourceConnectionOptions
                    {
                        Provider = "Databricks",
                        ConnectionString = "Host=read.databricks.example",
                        WriteConnectionString = "Host=write.databricks.example"
                    }
                }
            }
        });

        var service = new BoundedRowWriteService(
            metaRepo, policyRepo, pdp, audit, options, connFactory, sessionInit, idempotencyStore: idempotency);
        var alice = CreateUserPrincipal();

        // 1. Upsert in caller's own department ('FINANCE') succeeds via dedicated WriteConnectionString and stamps updated_by_sid
        var okRow = new Dictionary<string, object?>
        {
            ["param_id"] = "tax_rate",
            ["department_id"] = "FINANCE",
            ["owner_group_sid"] = FinanceGroupSid,
            ["param_value"] = "0.19"
        };
        var upsertRes = await service.UpsertRowAsync(alice, TenantA, ParamsTable, okRow, "idem-1");
        upsertRes.AffectedRows.ShouldBe(1);
        upsertRes.Replayed.ShouldBeFalse();
        conn.LastConnectionString.ShouldBe("Host=write.databricks.example");
        conn.ExecutedCommands.Count.ShouldBe(1);
        conn.ExecutedCommands[0].Sql.ShouldContain("MERGE INTO");
        conn.ExecutedCommands[0].Parameters.ShouldContain(AliceSid);

        // 2. Idempotent replay with same key & payload returns cached result without executing DB again
        var replay = await service.UpsertRowAsync(alice, TenantA, ParamsTable, okRow, "idem-1");
        replay.Replayed.ShouldBeTrue();
        conn.ExecutedCommands.Count.ShouldBe(1);

        // 3. Reusing same idempotency key with different payload throws IDEMPOTENCY_CONFLICT
        var diffRow = new Dictionary<string, object?>(okRow) { ["param_value"] = "0.21" };
        var conflict = await Should.ThrowAsync<BoundedRowWriteException>(() =>
            service.UpsertRowAsync(alice, TenantA, ParamsTable, diffRow, "idem-1"));
        conflict.Code.ShouldBe("IDEMPOTENCY_CONFLICT");

        // 4. Cross-department upsert ('HR') is rejected BEFORE touching Databricks!
        var hrRow = new Dictionary<string, object?>(okRow) { ["department_id"] = "HR" };
        var rlsFail = await Should.ThrowAsync<BoundedRowWriteException>(() =>
            service.UpsertRowAsync(alice, TenantA, ParamsTable, hrRow, "idem-hr"));
        rlsFail.Code.ShouldBe("ROW_CHECK_VIOLATION");
        conn.ExecutedCommands.Count.ShouldBe(1);

        // 5. Client attempting to spoof server-stamped column is rejected!
        var spoofRow = new Dictionary<string, object?>(okRow) { ["updated_by_sid"] = "S-1-5-21-SPOOF" };
        var spoofFail = await Should.ThrowAsync<BoundedRowWriteException>(() =>
            service.InsertRowAsync(alice, TenantA, ParamsTable, spoofRow, "idem-spoof"));
        spoofFail.Code.ShouldBe("SERVER_STAMPED_COLUMN_REJECTED");

        // 6. Degraded group resolution fails closed!
        var degradedUser = CreateUserPrincipal(degradedGroups: true);
        var degradedFail = await Should.ThrowAsync<BoundedRowWriteException>(() =>
            service.UpsertRowAsync(degradedUser, TenantA, ParamsTable, okRow, "idem-degraded"));
        degradedFail.Code.ShouldBe("ACCESS_DENIED");

        // 7. UpdateByPk and DeleteByPk include PK + tenant + SELECT RLS predicate in WHERE
        var updRes = await service.UpdateRowByPkAsync(
            alice, TenantA, ParamsTable,
            new Dictionary<string, object?> { ["param_id"] = "tax_rate" },
            new Dictionary<string, object?> { ["param_value"] = "0.20" },
            "idem-upd");
        updRes.AffectedRows.ShouldBe(1);
        conn.ExecutedCommands[^1].Sql.ShouldStartWith("UPDATE ");
        conn.ExecutedCommands[^1].Sql.ShouldContain("department_id");

        var delRes = await service.DeleteRowByPkAsync(
            alice, TenantA, ParamsTable,
            new Dictionary<string, object?> { ["param_id"] = "tax_rate" },
            "idem-del");
        delRes.AffectedRows.ShouldBe(1);
        conn.ExecutedCommands[^1].Sql.ShouldStartWith("DELETE FROM ");
        conn.ExecutedCommands[^1].Sql.ShouldContain("department_id");
    }

    [Fact]
    public async Task BoundedRowWriteService_FailsClosedWhenWriteConnectionStringIsMissing()
    {
        var meta = BuildParamsMetadata();
        var metaRepo = new FakeTableMetadataRepository(meta);
        var policyRepo = new InMemoryTableWritePolicyRepository();
        var policy = BuildBoundedPolicy(requireRowFilter: false);
        await policyRepo.UpsertAsync(policy);

        var pdp = new FakeAccessDecisionPoint(_ => BuildAllowedWriteDecision(policy));
        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["analytics"] = new DataSourceConnectionOptions
                    {
                        Provider = "Databricks",
                        ConnectionString = "Host=read-only.databricks.example",
                        WriteConnectionString = null
                    }
                }
            }
        });

        var service = new BoundedRowWriteService(metaRepo, policyRepo, pdp, new RecordingAuditWriter(), options);
        var ex = await Should.ThrowAsync<BoundedRowWriteException>(() =>
            service.InsertRowAsync(
                CreateUserPrincipal(),
                TenantA,
                ParamsTable,
                new Dictionary<string, object?> { ["param_id"] = "1", ["param_value"] = "x" },
                "idem-no-write-conn"));
        ex.Code.ShouldBe("DML_WRITE_CONNECTION_MISSING");
    }

    [Fact]
    public async Task GraphQlMutation_RowWrites_RejectsReadOnlyToken_AndMapsTypedInputs()
    {
        var mutation = new Mutation();
        var rowWriteService = new FakeBoundedRowWriteService();
        var httpAccessor = new FakeHttpContextAccessor();

        // Read-only token is rejected
        httpAccessor.HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(readOnly: true) };
        var roEx = await Should.ThrowAsync<GraphQLException>(() =>
            mutation.UpsertDatasetRowAsync(
                ParamsTable.ToString(),
                [new RowFieldInput("param_id", StringValue: "k1"), new RowFieldInput("param_value", IntValue: 42)],
                "gql-key-1",
                rowWriteService,
                httpAccessor));
        roEx.Errors[0].Code.ShouldBe("READ_ONLY_TOKEN");

        // Regular authenticated user succeeds
        var okCtx = new DefaultHttpContext { User = CreateUserPrincipal() };
        okCtx.Items["TenantId"] = TenantA;
        httpAccessor.HttpContext = okCtx;
        var payload = await mutation.UpsertDatasetRowAsync(
            ParamsTable.ToString(),
            [new RowFieldInput("param_id", StringValue: "k1"), new RowFieldInput("param_value", IntValue: 42)],
            "gql-key-1",
            rowWriteService,
            httpAccessor);
        payload.AffectedRows.ShouldBe(1);
        payload.Dataset.ShouldBe(ParamsTable.ToString());
        payload.IdempotencyKey.ShouldBe("gql-key-1");
    }

    // ---- explicit lightweight test doubles (zero NSubstitute mocks) ----

    private sealed class StubPolicyRenderer : IPolicyPredicateRenderer
    {
        public RenderedPolicyPredicate Render(PolicyPredicate predicate, TargetSqlDialect dialect, string targetAlias) =>
            new("department_id = @__pol_dept", new Dictionary<string, object?> { ["__pol_dept"] = "FINANCE" });
    }

    private sealed class FakeTableMetadataRepository(TableMetadata metadata) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(table.Equals(metadata.Identifier) ? metadata : null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([metadata]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata m, CancellationToken ct = default) =>
            Task.FromResult(m);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
        public void NotifyCatalogChanged(long newVersion) { }
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGovernanceCapabilities : IGovernanceCapabilities
    {
        public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) =>
            principal?.IsInRole("GovernanceAdmin") == true;
        public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => false;
        public ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext principal, ContainerRef container, CancellationToken ct) =>
            ValueTask.FromResult(false);
        public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext principal, ContainerRef container, TenantId objectTenant, CancellationToken ct) =>
            ValueTask.FromResult(false);
        public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId objectTenant) =>
            principal?.IsInRole("GovernanceAdmin") == true;
    }

    private sealed class FakeRowPolicyDependencyIndex : IRowPolicyDependencyIndex
    {
        public HashSet<TableIdentifier> DependentTables { get; } = [];
        public bool IsPolicyDependency(TenantId tenant, TableIdentifier table) => DependentTables.Contains(table);
    }

    private sealed class FakeConsentRepository(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult(consents);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeAccessDecisionPoint(Func<AccessRequest, TableAccessDecision> decide) : IAccessDecisionPoint
    {
        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(decide(request));
        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<TableAccessDecision>>(objects.Select(o => decide(new AccessRequest(subject, o, operation, transport))).ToList());
        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConnectionFactory(RecordingDbConnection conn) : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            conn.LastConnectionString = options.ConnectionString ?? string.Empty;
            return Task.FromResult<DbConnection>(conn);
        }
    }

    private sealed class NoOpSessionInitializer : IDbSessionContextInitializer
    {
        public Task<DbTransaction?> InitializeSessionAsync(
            DbConnection connection, DatabaseDialect dialect, TenantId tenantId,
            string? userSid = null, string? purpose = null, bool requireTransaction = true, CancellationToken ct = default) =>
            Task.FromResult<DbTransaction?>(null);

        public Task<DbTransaction?> InitializeSessionAsync(
            DbConnection connection, string? provider, TenantId tenantId,
            string? userSid = null, string? purpose = null, bool requireTransaction = true, CancellationToken ct = default) =>
            Task.FromResult<DbTransaction?>(null);

        public Task InitializeSessionAsync(
            DbConnection connection, DbTransaction? tx, DatabaseDialect dialect, TenantId tenantId,
            string? userSid = null, string? purpose = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class FakeBoundedRowWriteService : IBoundedRowWriteService
    {
        public Task<BoundedRowWriteResult> InsertRowAsync(ClaimsPrincipal principal, TenantId tenantId, TableIdentifier table, IReadOnlyDictionary<string, object?> row, string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult(new BoundedRowWriteResult(table.ToString(), "Insert", 1, false, idempotencyKey));
        public Task<BoundedRowWriteResult> UpsertRowAsync(ClaimsPrincipal principal, TenantId tenantId, TableIdentifier table, IReadOnlyDictionary<string, object?> row, string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult(new BoundedRowWriteResult(table.ToString(), "Upsert", 1, false, idempotencyKey));
        public Task<BoundedRowWriteResult> UpdateRowByPkAsync(ClaimsPrincipal principal, TenantId tenantId, TableIdentifier table, IReadOnlyDictionary<string, object?> key, IReadOnlyDictionary<string, object?> patch, string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult(new BoundedRowWriteResult(table.ToString(), "UpdateByPk", 1, false, idempotencyKey));
        public Task<BoundedRowWriteResult> DeleteRowByPkAsync(ClaimsPrincipal principal, TenantId tenantId, TableIdentifier table, IReadOnlyDictionary<string, object?> key, string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult(new BoundedRowWriteResult(table.ToString(), "DeleteByPk", 1, false, idempotencyKey));
    }

    private sealed class InMemoryTableWritePolicyRepository : ITableWritePolicyRepository
    {
        private readonly ConcurrentDictionary<(string Tenant, Guid TableId), TableWritePolicy> _store = new();

        public Task<TableWritePolicy?> GetAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue((tenant.Value, tableId), out var p) ? p : null);

        public Task<IReadOnlyList<TableWritePolicy>> ListAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableWritePolicy>>(_store.Values.Where(v => v.Tenant == tenant).ToList());

        public Task<TableWritePolicy> UpsertAsync(TableWritePolicy policy, CancellationToken ct = default)
        {
            _store[(policy.Tenant.Value, policy.TableId)] = policy;
            return Task.FromResult(policy);
        }

        public Task<bool> DeleteAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
            Task.FromResult(_store.TryRemove((tenant.Value, tableId), out _));
    }

    private sealed class InMemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, object> _entries = new(StringComparer.Ordinal);
        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class =>
            Task.FromResult(_entries.TryGetValue(key, out var v) ? v as T : null);
        public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class =>
            Task.FromResult(_entries.TryAdd(key, value));
    }

    private sealed record ExecutedCommandRecord(string Sql, IReadOnlyList<object?> Parameters);

    private sealed class RecordingDbConnection : DbConnection
    {
        public string LastConnectionString { get; set; } = string.Empty;
        public List<ExecutedCommandRecord> ExecutedCommands { get; } = [];
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "prod";
        public override string DataSource => "databricks";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new RecordingDbTransaction(this);
        protected override DbCommand CreateDbCommand() => new RecordingDbCommand(this);
    }

    private sealed class RecordingDbTransaction(RecordingDbConnection conn) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class RecordingDbCommand(RecordingDbConnection conn) : DbCommand
    {
        private readonly RecordingParamCollection _params = new();
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery()
        {
            conn.ExecutedCommands.Add(new ExecutedCommandRecord(CommandText, _params.Values));
            return 1;
        }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(ExecuteNonQuery());
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new RecordingDbParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }

    private sealed class RecordingParamCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public IReadOnlyList<object?> Values => _items.Select(p => p.Value).ToList();
        public override int Count => _items.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) _items.Add((DbParameter)v!); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => _items.Exists(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) _items.RemoveAt(i); }
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class RecordingDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; } = true;
        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }
}
