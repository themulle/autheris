namespace TrinoSqlEngine.Tests.Compiler;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;

[Trait("Category", "SqlGate")]
public sealed class DatabricksBoundedDmlTests
{
    private static readonly TableIdentity ParamsTableId = new("main", "report_params");

    private static TableCatalogEntry BuildCatalogEntry(bool withTenant = true) => new(
        ParamsTableId,
        ImmutableArray.Create(
            new CatalogColumn("param_id", "varchar(64)"),
            new CatalogColumn("department_id", "varchar(64)"),
            new CatalogColumn("owner_group_sid", "varchar(64)"),
            new CatalogColumn("param_value", "varchar(256)"),
            new CatalogColumn("tenant_id", "varchar(64)")),
        TenantColumn: withTenant ? "tenant_id" : null,
        IsMetricView: false);

    private sealed class SingleTableCatalog(TableCatalogEntry entry) : ITableCatalog
    {
        public TableCatalogEntry? Resolve(SqlQualifiedName name)
        {
            if (string.Equals(name.SimpleName, entry.Identity.Table, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }

            return null;
        }
    }

    private sealed class FixedRowPolicyProvider(TableIdentity table, PolicyPredicate? predicate) : IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TableIdentity t) => predicate is not null && t.Equals(table);
        public PolicyPredicate GetPredicate(TableIdentity t) =>
            predicate ?? throw new InvalidOperationException("No policy configured.");
    }

    private sealed class NoMaskProvider : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }

    private static PolicyPredicate DepartmentRls(string allowedDept)
    {
        var expr = new BinaryExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier("department_id", IsQuoted: true)])),
            BinaryOperator.Equal,
            new PolicyParameterExpression("__pol_dept", SqlParameterType.String, ParameterOrigin.Policy));
        var values = new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
        {
            ["__pol_dept"] = new PolicyValue(allowedDept, SqlParameterType.String)
        }.ToFrozenDictionary(StringComparer.Ordinal);
        return new PolicyPredicate(
            expr,
            values,
            "dept_eq_fp",
            ImmutableHashSet.Create(StringComparer.Ordinal, "department_id"));
    }

    private static PolicyPredicate AdGroupInRls(params string[] allowedGroupSids)
    {
        var items = new List<Expression>(allowedGroupSids.Length);
        var values = new Dictionary<string, PolicyValue>(StringComparer.Ordinal);
        for (int i = 0; i < allowedGroupSids.Length; i++)
        {
            string pName = $"__pol_grp_{i}";
            items.Add(new PolicyParameterExpression(pName, SqlParameterType.String, ParameterOrigin.Policy));
            values[pName] = new PolicyValue(allowedGroupSids[i], SqlParameterType.String);
        }

        var expr = new InListExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier("owner_group_sid", IsQuoted: true)])),
            items,
            IsNotIn: false);

        return new PolicyPredicate(
            expr,
            values.ToFrozenDictionary(StringComparer.Ordinal),
            $"grp_in_{allowedGroupSids.Length}",
            ImmutableHashSet.Create(StringComparer.Ordinal, "owner_group_sid"));
    }

    private static CompileRequest CreateDatabricksRequest(
        DmlWritePermit permit,
        PolicyPredicate? rowPolicy,
        StatementPermissions statements,
        string tenantValue = "tenant-a")
    {
        var entry = BuildCatalogEntry(withTenant: true);
        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.Databricks,
            AllowExperimentalDialect = true,
            Statements = statements,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                Catalog = new SingleTableCatalog(entry),
                RowFilters = new FixedRowPolicyProvider(ParamsTableId, rowPolicy),
                Masks = new NoMaskProvider(),
                Tenant = new TenantBinding("__tenant_id", tenantValue, SqlParameterType.String),
                Dml = DmlGuardOptions.Strict,
                Write = permit
            }
        };
    }

    private static IReadOnlySet<string> AllWritableColumns() =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "param_id", "department_id", "owner_group_sid", "param_value", "tenant_id"
        };

    [Fact]
    public async Task BoundedDatabricksInsert_WithSelectRls_ExecutesWhenRowMatchesDepartment_AndRejectsBeforeDbWhenMismatched()
    {
        var engine = new FastSqlEngine();
        var permit = new DmlWritePermit(
            DmlWriteOperation.Insert,
            AllWritableColumns(),
            ImmutableHashSet<string>.Empty,
            BoundedSingleTable: true,
            PrimaryKeyColumns: ["param_id"],
            BoundedMaxRows: 10);

        const string sql = "INSERT INTO main.report_params (param_id, department_id, owner_group_sid, param_value, tenant_id) VALUES (__param_p_id, __param_p_dept, __param_p_grp, __param_p_val, 'tenant-a')";

        // Caller belongs to department 'FINANCE'.
        var compiledFinance = engine.Compile(sql.AsMemory(), CreateDatabricksRequest(permit, DepartmentRls("FINANCE"), StatementPermissions.Insert), CancellationToken.None);
        Assert.True(compiledFinance.IsPreBoundedDml);
        Assert.Equal(1L, compiledFinance.PreBoundedMaxRows);
        Assert.NotNull(compiledFinance.PreExecutionRowCheck);
        Assert.False(compiledFinance.RequiresRowCountCheck);

        var conn = new FakeDbConnection { Affected = 1 };
        var executor = CompiledSqlBinders.CheckedExecutorFor(TargetSqlDialect.Databricks);

        // 1. Matching department -> executes even with MaxAffectedRows > 0!
        var matchingParams = new Dictionary<string, object?>
        {
            ["p_id"] = "p-1",
            ["p_dept"] = "FINANCE",
            ["p_grp"] = "S-1-5-GRP-FIN",
            ["p_val"] = "2026-Q4"
        };
        int affected = await executor.ExecuteInTransactionAsync(
            conn.BeginFakeTx(), compiledFinance, matchingParams, new CheckedDmlLimits(30, MaxAffectedRows: 10));
        Assert.Equal(1, affected);
        Assert.Equal(1, conn.CommandsExecuted);

        // 2. Mismatched department ('HR') -> throws DmlCheckOptionViolationException BEFORE touching Databricks!
        var connBlocked = new FakeDbConnection { Affected = 1 };
        var wrongDeptParams = new Dictionary<string, object?>
        {
            ["p_id"] = "p-2",
            ["p_dept"] = "HR",
            ["p_grp"] = "S-1-5-GRP-HR",
            ["p_val"] = "spoofed"
        };
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() =>
            executor.ExecuteInTransactionAsync(
                connBlocked.BeginFakeTx(), compiledFinance, wrongDeptParams, new CheckedDmlLimits(30, MaxAffectedRows: 10)));
        Assert.Equal(0, connBlocked.CommandsExecuted);

        // 3. Cache hit rebind: second caller belongs to 'HR' -> same SQL template rebinds policy value to 'HR'!
        var compiledHr = engine.Compile(sql.AsMemory(), CreateDatabricksRequest(permit, DepartmentRls("HR"), StatementPermissions.Insert), CancellationToken.None);
        Assert.Equal("HR", compiledHr.PreExecutionPolicyValues["__pol_dept"].Value);
        var connHr = new FakeDbConnection { Affected = 1 };
        int hrAffected = await executor.ExecuteInTransactionAsync(
            connHr.BeginFakeTx(), compiledHr, wrongDeptParams, new CheckedDmlLimits(30, MaxAffectedRows: 10));
        Assert.Equal(1, hrAffected);
        Assert.Equal(1, connHr.CommandsExecuted);

        // And 'FINANCE' row is now rejected for the 'HR' caller!
        var connHrBlocked = new FakeDbConnection { Affected = 1 };
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() =>
            executor.ExecuteInTransactionAsync(
                connHrBlocked.BeginFakeTx(), compiledHr, matchingParams, new CheckedDmlLimits(30, MaxAffectedRows: 10)));
        Assert.Equal(0, connHrBlocked.CommandsExecuted);
    }

    [Fact]
    public async Task BoundedDatabricksInsert_WithAdGroupInListRls_EnforcesMembershipAndNullThreeValuedLogic()
    {
        var engine = new FastSqlEngine();
        var permit = new DmlWritePermit(
            DmlWriteOperation.Insert,
            AllWritableColumns(),
            ImmutableHashSet<string>.Empty,
            BoundedSingleTable: true,
            PrimaryKeyColumns: ["param_id"],
            BoundedMaxRows: 5);

        const string sql = "INSERT INTO main.report_params (param_id, department_id, owner_group_sid, param_value, tenant_id) VALUES (__param_p_id, __param_p_dept, __param_p_grp, __param_p_val, 'tenant-a')";
        var compiled = engine.Compile(sql.AsMemory(), CreateDatabricksRequest(permit, AdGroupInRls("S-1-5-21-GRP-A", "S-1-5-21-GRP-B"), StatementPermissions.Insert), CancellationToken.None);
        var executor = CompiledSqlBinders.CheckedExecutorFor(TargetSqlDialect.Databricks);

        // Allowed AD group succeeds
        var connOk = new FakeDbConnection { Affected = 1 };
        await executor.ExecuteInTransactionAsync(
            connOk.BeginFakeTx(),
            compiled,
            new Dictionary<string, object?> { ["p_id"] = "k1", ["p_dept"] = "D1", ["p_grp"] = "S-1-5-21-GRP-B", ["p_val"] = "v1" },
            new CheckedDmlLimits(30, MaxAffectedRows: 5));
        Assert.Equal(1, connOk.CommandsExecuted);

        // Foreign AD group is rejected before DB execution
        var connForeign = new FakeDbConnection { Affected = 1 };
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() =>
            executor.ExecuteInTransactionAsync(
                connForeign.BeginFakeTx(),
                compiled,
                new Dictionary<string, object?> { ["p_id"] = "k2", ["p_dept"] = "D1", ["p_grp"] = "S-1-5-21-GRP-Z", ["p_val"] = "v2" },
                new CheckedDmlLimits(30, MaxAffectedRows: 5)));
        Assert.Equal(0, connForeign.CommandsExecuted);

        // NULL owner_group_sid evaluates to UNKNOWN (not TRUE) -> rejected before DB execution
        var connNull = new FakeDbConnection { Affected = 1 };
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() =>
            executor.ExecuteInTransactionAsync(
                connNull.BeginFakeTx(),
                compiled,
                new Dictionary<string, object?> { ["p_id"] = "k3", ["p_dept"] = "D1", ["p_grp"] = null, ["p_val"] = "v3" },
                new CheckedDmlLimits(30, MaxAffectedRows: 5)));
        Assert.Equal(0, connNull.CommandsExecuted);
    }

    [Fact]
    public async Task BoundedDatabricksUpdateAndDelete_WithPkPredicate_ExecutesUnderMaxAffectedRows_AndRejectsMissingPkOrOr()
    {
        var engine = new FastSqlEngine();
        var updatePermit = new DmlWritePermit(
            DmlWriteOperation.Update,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "param_value" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "param_id" },
            BoundedSingleTable: true,
            PrimaryKeyColumns: ["param_id"],
            BoundedMaxRows: 5);

        // Single-PK UPDATE succeeds and carries SELECT RLS + tenant predicate in WHERE
        var compiledUpdate = engine.Compile(
            "UPDATE main.report_params SET param_value = __param_v WHERE param_id = __param_id".AsMemory(),
            CreateDatabricksRequest(updatePermit, DepartmentRls("FINANCE"), StatementPermissions.Update),
            CancellationToken.None);
        Assert.True(compiledUpdate.IsPreBoundedDml);
        Assert.Equal(1L, compiledUpdate.PreBoundedMaxRows);
        Assert.Contains("department_id", compiledUpdate.Sql, StringComparison.OrdinalIgnoreCase);

        var conn = new FakeDbConnection { Affected = 1 };
        var executor = CompiledSqlBinders.CheckedExecutorFor(TargetSqlDialect.Databricks);
        int updated = await executor.ExecuteInTransactionAsync(
            conn.BeginFakeTx(),
            compiledUpdate,
            new Dictionary<string, object?> { ["v"] = "new-val", ["id"] = "p-1" },
            new CheckedDmlLimits(30, MaxAffectedRows: 5));
        Assert.Equal(1, updated);
        Assert.Equal(1, conn.CommandsExecuted);

        // UPDATE without PK column in WHERE is rejected at compile time
        Assert.Throws<SecurityException>(() => engine.Compile(
            "UPDATE main.report_params SET param_value = __param_v WHERE param_value = 'old'".AsMemory(),
            CreateDatabricksRequest(updatePermit, DepartmentRls("FINANCE"), StatementPermissions.Update),
            CancellationToken.None));

        // UPDATE with top-level OR is rejected at compile time
        Assert.Throws<SecurityException>(() => engine.Compile(
            "UPDATE main.report_params SET param_value = __param_v WHERE param_id = 'a' OR param_id = 'b'".AsMemory(),
            CreateDatabricksRequest(updatePermit, DepartmentRls("FINANCE"), StatementPermissions.Update),
            CancellationToken.None));

        // DELETE with single-PK IN exceeding BoundedMaxRows is rejected at compile time
        var deletePermit = new DmlWritePermit(
            DmlWriteOperation.Delete,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty,
            BoundedSingleTable: true,
            PrimaryKeyColumns: ["param_id"],
            BoundedMaxRows: 2);

        Assert.Throws<SecurityException>(() => engine.Compile(
            "DELETE FROM main.report_params WHERE param_id IN ('1', '2', '3')".AsMemory(),
            CreateDatabricksRequest(deletePermit, DepartmentRls("FINANCE"), StatementPermissions.Delete),
            CancellationToken.None));

        // DELETE with single-PK IN within BoundedMaxRows compiles with PreBoundedMaxRows = 2
        var compiledDelete = engine.Compile(
            "DELETE FROM main.report_params WHERE param_id IN ('1', '2')".AsMemory(),
            CreateDatabricksRequest(deletePermit, DepartmentRls("FINANCE"), StatementPermissions.Delete),
            CancellationToken.None);
        Assert.True(compiledDelete.IsPreBoundedDml);
        Assert.Equal(2L, compiledDelete.PreBoundedMaxRows);

        // Exceeding executor limits.MaxAffectedRows still fails closed before touching DB
        var connOverLimit = new FakeDbConnection { Affected = 2 };
        await Assert.ThrowsAsync<SqlCompileNotSupportedException>(() =>
            executor.ExecuteInTransactionAsync(
                connOverLimit.BeginFakeTx(), compiledDelete, new Dictionary<string, object?>(), new CheckedDmlLimits(30, MaxAffectedRows: 1)));
        Assert.Equal(0, connOverLimit.CommandsExecuted);
    }

    [Fact]
    public void BoundedDatabricksInsert_WithUnsupportedSubqueryInRls_FailsClosedAtCompileTime()
    {
        var subqueryExpr = new ExistsExpression(
            new SelectStatement(
                null,
                new QuerySpecification(false, [new ColumnSelectItem(new LiteralExpression(1, LiteralType.Integer), null)], null, null, null, null),
                null,
                null));

        var policy = new PolicyPredicate(
            subqueryExpr,
            FrozenDictionary<string, PolicyValue>.Empty,
            "subquery_fp",
            ImmutableHashSet.Create(StringComparer.Ordinal, "department_id"));

        var permit = new DmlWritePermit(
            DmlWriteOperation.Insert,
            AllWritableColumns(),
            ImmutableHashSet<string>.Empty,
            BoundedSingleTable: true,
            PrimaryKeyColumns: ["param_id"],
            BoundedMaxRows: 10);

        var engine = new FastSqlEngine();
        Assert.Throws<SqlCompileNotSupportedException>(() => engine.Compile(
            "INSERT INTO main.report_params (param_id, department_id, owner_group_sid, param_value, tenant_id) VALUES ('1', 'FIN', 'G1', 'v', 'tenant-a')".AsMemory(),
            CreateDatabricksRequest(permit, policy, StatementPermissions.Insert),
            CancellationToken.None));
    }

    [Fact]
    public void CompositePrimaryKey_RequiresEveryKeyColumnInWhere()
    {
        var entry = BuildCatalogEntry();
        var col1 = new BinaryExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier("param_id", false)])),
            BinaryOperator.Equal,
            new LiteralExpression("p1", LiteralType.String));
        var col2 = new BinaryExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier("department_id", false)])),
            BinaryOperator.Equal,
            new LiteralExpression("FIN", LiteralType.String));

        // Missing second key column -> rejected
        Assert.Throws<SecurityException>(() =>
            BoundedPkPredicateValidator.Validate(col1, entry, ["param_id", "department_id"], maxRows: 10));

        // Both key columns bound with AND -> accepted with upper bound 1
        var both = new BinaryExpression(col1, BinaryOperator.And, col2);
        Assert.Equal(1L, BoundedPkPredicateValidator.Validate(both, entry, ["param_id", "department_id"], maxRows: 10));
    }

    // ---- minimal ADO.NET test double ----

    private sealed class FakeDbConnection : DbConnection
    {
        public int Affected { get; init; } = 1;
        public int CommandsExecuted { get; private set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "test";
        public override string DataSource => "databricks";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        public FakeDbTransaction BeginFakeTx() => new(this);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeDbTransaction(this);
        protected override DbCommand CreateDbCommand() => new FakeDbCommand(this);
        internal void RecordExecution() => CommandsExecuted++;
    }

    private sealed class FakeDbTransaction(FakeDbConnection conn) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class FakeDbCommand(FakeDbConnection conn) : DbCommand
    {
        private readonly FakeParamCollection _params = new();
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() { conn.RecordExecution(); return conn.Affected; }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) { conn.RecordExecution(); return Task.FromResult(conn.Affected); }
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeDbParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }

    private sealed class FakeParamCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
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

    private sealed class FakeDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; } = true;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }
}
