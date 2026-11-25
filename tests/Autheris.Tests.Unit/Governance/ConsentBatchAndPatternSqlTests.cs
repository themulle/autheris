namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConsentBatchAndPatternSqlTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-1098-USER-1");
    private static readonly Sid GroupSid = new("S-1-5-21-1098-GROUP-1");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier DomainBOrders = new("domainB", "dbo", "orders");
    private static readonly TableIdentifier DomainBInvoices = new("domainB", "dbo", "invoices");

    [Fact]
    public async Task BatchAndPatternQueries_FilterGrantKindTablesAndSubjectsInSql()
    {
        var recorded = new List<(string Sql, IReadOnlyDictionary<string, object?> Params)>();
        var conn = new RecordingGovernanceDbConnection(recorded);
        var repo = new GovernanceRepository(
            new PostgreSqlGovernanceDialect(() => conn),
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance);

        await repo.GetActiveConsentsForSubjectsBatchAsync(
            [UserSid, GroupSid],
            [DomainBOrders, DomainBInvoices],
            FixedNow,
            TenantA);

        var batchQuery = recorded.First(c => c.Sql.Contains("FROM CONSENTS c JOIN TABLES t", StringComparison.Ordinal));
        batchQuery.Sql.ShouldContain("c.grant_kind = 'DATA'");
        batchQuery.Sql.ShouldContain("LOWER(t.table_name) IN (");
        batchQuery.Sql.ShouldContain("LOWER(t.schema_name) IN (");
        batchQuery.Sql.ShouldContain("LOWER(t.source_name) IN (");
        batchQuery.Sql.ShouldContain("c.grantee_sid_norm IN (");
        batchQuery.Params["@tenantId"].ShouldBe(TenantA.Value);
        batchQuery.Params.Values.ShouldContain("orders");
        batchQuery.Params.Values.ShouldContain("invoices");
        batchQuery.Params.Values.ShouldContain("dbo");
        batchQuery.Params.Values.ShouldContain("domainb");
        batchQuery.Params.Values.ShouldContain(UserSid.Value.ToUpperInvariant());
        batchQuery.Params.Values.ShouldContain(GroupSid.Value.ToUpperInvariant());

        recorded.Clear();
        await repo.GetActivePatternConsentsForSubjectsAsync([UserSid], TenantA, FixedNow);
        var patternQuery = recorded.ShouldHaveSingleItem();
        patternQuery.Sql.ShouldContain("c.grant_kind = 'DATA'");
        patternQuery.Sql.ShouldContain("c.object_pattern IS NOT NULL");
        patternQuery.Sql.ShouldContain("c.tenant_id = @tenantId");
        patternQuery.Sql.ShouldContain("c.grantee_sid_norm IN (@s0)");
        patternQuery.Params["@s0"].ShouldBe(UserSid.Value.ToUpperInvariant());

        recorded.Clear();
        var emptyResult = await repo.GetPatternConsentsForSubjectsWithPendingDeniesAsync([], TenantA, FixedNow);
        emptyResult.ShouldBeEmpty();
        recorded.ShouldBeEmpty();
    }
}

internal sealed class RecordingGovernanceDbConnection(List<(string Sql, IReadOnlyDictionary<string, object?> Params)> recorded) : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;
    [AllowNull] public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;
    public override void Open() => _state = ConnectionState.Open;
    public override Task OpenAsync(CancellationToken ct) { _state = ConnectionState.Open; return Task.CompletedTask; }
    public override void Close() => _state = ConnectionState.Closed;
    public override void ChangeDatabase(string databaseName) { }
    protected override DbTransaction BeginDbTransaction(IsolationLevel il) => new RecordingTx(this);
    protected override DbCommand CreateDbCommand() => new RecordingCmd(this, recorded);

    private sealed class RecordingTx(RecordingGovernanceDbConnection conn) : DbTransaction
    {
        protected override DbConnection DbConnection => conn;
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class RecordingCmd(RecordingGovernanceDbConnection conn, List<(string Sql, IReadOnlyDictionary<string, object?> Params)> recorded) : DbCommand
    {
        private readonly ParamList _params = new();
        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new Param();
        public override int ExecuteNonQuery()
        {
            if (CommandText.StartsWith("SET ", StringComparison.OrdinalIgnoreCase)) return 0;
            recorded.Add((CommandText, _params.ToDictionary()));
            return 1;
        }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken ct) => Task.FromResult(ExecuteNonQuery());
        public override object? ExecuteScalar() { recorded.Add((CommandText, _params.ToDictionary())); return null; }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { recorded.Add((CommandText, _params.ToDictionary())); return new DataTable().CreateDataReader(); }
    }

    private sealed class ParamList : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public IReadOnlyDictionary<string, object?> ToDictionary() => _items.Select((p, i) => (Key: string.IsNullOrEmpty(p.ParameterName) ? $"${i + 1}" : p.ParameterName, p.Value)).ToDictionary(static x => x.Key, static x => x.Value, StringComparer.OrdinalIgnoreCase);
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (DbParameter p in values) _items.Add(p); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) _items.RemoveAt(i); }
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) { var i = IndexOf(parameterName); if (i >= 0) _items[i] = value; }
    }

    private sealed class Param : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }
}
