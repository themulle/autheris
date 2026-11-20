namespace Autheris.Tests.Unit.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>WP-SIMPL-48 PR 1: Composite key pairwise checks, null rejection, cross-source KeySet enforcement, fail-closed timeout/overflow & max-age cap.</summary>
public sealed class RowPolicyEvaluationTests
{
    private static readonly TenantId Tenant = new("tenant_lwe");

    [Fact]
    [Trait("Category", "Security")]
    public void Composite_key_compared_pairwise_not_per_column()
    {
        VirtualFilterKeyType[] types = [VirtualFilterKeyType.IntegerNumber, VirtualFilterKeyType.Text];
        var keySet = VirtualFilterKeySet.Create(types, new object?[][]
        {
            [1L, "A"],
            [2L, "B"]
        });

        VirtualFilterKey.TryCreate(types, [1L, "A"], out var pair1A).ShouldBeTrue();
        VirtualFilterKey.TryCreate(types, [2L, "B"], out var pair2B).ShouldBeTrue();
        VirtualFilterKey.TryCreate(types, [1L, "B"], out var crossed1B).ShouldBeTrue();
        VirtualFilterKey.TryCreate(types, [2L, "A"], out var crossed2A).ShouldBeTrue();

        keySet.Contains(pair1A).ShouldBeTrue();
        keySet.Contains(pair2B).ShouldBeTrue();
        keySet.Contains(crossed1B).ShouldBeFalse();
        keySet.Contains(crossed2A).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Null_key_never_matches()
    {
        VirtualFilterKeyType[] types = [VirtualFilterKeyType.IntegerNumber, VirtualFilterKeyType.Text];
        var keySet = VirtualFilterKeySet.Create(types, new object?[][]
        {
            [1L, null],
            [null, "A"],
            [null, null]
        });

        keySet.Count.ShouldBe(0);
        keySet.DroppedKeyCount.ShouldBe(3);

        VirtualFilterKey.TryCreate(types, [1L, null], out _).ShouldBeFalse();
        VirtualFilterKey.TryCreate(types, [null, "A"], out _).ShouldBeFalse();
        keySet.Contains(VirtualFilterKey.TryCreate(types, [1L, "A"], out var valid) ? valid : default!).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cross_source_policy_requires_keyset()
    {
        var liveFilter = new VirtualFilter
        {
            TenantId = Tenant,
            Name = "cross_src_policy",
            Source = "source_a",
            EvaluationMode = RowPolicyEvaluationMode.Live,
            KeyColumns = ["client.client_id"],
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("source_a", "conf", "client"),
                FromAlias = "client",
                Where = [new FilterCondition("client.region", FilterConditionOperator.Eq, "EU")]
            }
        };

        var targetSqlTable = new TableMetadata
        {
            Identifier = new TableIdentifier("source_b", "sales", "orders"),
            Table = new Table { SourceName = "source_b", SchemaName = "sales", TableName = "orders", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "client_id", DataType = "integer" }]
        };

        var binding = new FilterBinding
        {
            FilterName = "cross_src_policy",
            TargetPattern = "source_b.sales.orders"
        };
        var profile = new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = "cross_profile",
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER"),
            Scope = "source_b.*.*",
            Uncovered = UncoveredPolicy.Skip,
            Bindings = [binding]
        };

        // Direct evaluation / write validation rejects Live mode on cross-source target
        var bindingEx = Should.Throw<VirtualFilterBindingException>(() =>
            RowPolicyParameterBinder.EnsureCrossSourceAllowed(liveFilter, targetSqlTable.Identifier.Domain));
        bindingEx.Code.ShouldBe(VirtualFilterErrorCodes.RowPolicyCrossSourceRequiresKeySet);

        // SaveProfileAsync also enforces KeySet for cross-source bindings
        var repo = new SingleSnapshotRepo(new VirtualFilterSnapshot(1, [liveFilter], []));
        var admin = new VirtualFilterAdministrationService(repo, NullAuditLogRepository.Instance);
        var saveEx = await Should.ThrowAsync<VirtualFilterBindingException>(() =>
            admin.SaveProfileAsync(profile, new VirtualFilterActor(new Sid("S-1-5-21-ADMIN"), IsSync: false)));
        saveEx.Code.ShouldBe(VirtualFilterErrorCodes.RowPolicyCrossSourceRequiresKeySet);

        // Runtime MandatoryRowFilterResolver denies even under UncoveredPolicy.Skip
        var resolver = new MandatoryRowFilterResolver(
            new FixedSnapshotProvider(new VirtualFilterSnapshot(1, [liveFilter], [profile])),
            new StructuredFilterSqlBuilder());
        var outcome = await resolver.ResolveAsync(new MandatoryFilterQuery(new Sid("S-1-5-21-USER"), new HashSet<Sid>(), new HashSet<string>(), Tenant, targetSqlTable));
        outcome.IsDenied.ShouldBeTrue();
        outcome.DenyReason.ShouldNotBeNull().ShouldContain(VirtualFilterErrorCodes.RowPolicyCrossSourceRequiresKeySet);

        // KeySet mode permits the cross-source binding
        var keySetFilter = liveFilter with { EvaluationMode = RowPolicyEvaluationMode.KeySet };
        Should.NotThrow(() => RowPolicyParameterBinder.EnsureCrossSourceAllowed(keySetFilter, targetSqlTable.Identifier.Domain));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Keyset_failure_timeout_overflow_deny()
    {
        var filter = new VirtualFilter
        {
            TenantId = Tenant,
            Name = "keyset_filter",
            Source = "source_a",
            EvaluationMode = RowPolicyEvaluationMode.KeySet,
            KeyColumns = ["client.client_id"],
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("source_a", "conf", "client"),
                FromAlias = "client",
                Where = [new FilterCondition("client.active", FilterConditionOperator.Eq, "1")]
            }
        };

        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                RequireTenantColumn = false,
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["source_a"] = new() { Provider = "PostgreSql", ConnectionString = "Host=stub;Database=db;" }
                }
            },
            VirtualFilters = new VirtualFilterOptions
            {
                Federation = new VirtualFilterFederationOptions
                {
                    MaxAuthorizedKeys = 2,
                    KeyCacheTtlSeconds = 60,
                    KeyQueryTimeoutSeconds = 5
                }
            }
        });

        var clientMeta = new TableMetadata
        {
            Identifier = new TableIdentifier("source_a", "conf", "client"),
            Table = new Table { SourceName = "source_a", SchemaName = "conf", TableName = "client", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "client_id", DataType = "integer" }]
        };

        var factory = new ScriptedConnectionFactory();
        var cache = new VirtualFilterKeySetCache(options);
        var provider = new SqlVirtualFilterKeySetProvider(new SingleTableCatalog(clientMeta), factory, options, cache);

        // 1. Source failure denies with SourceUnavailable and must NOT cache the failure
        factory.Mode = ScriptedConnectionFactory.FailureMode.ThrowError;
        var errEx = await Should.ThrowAsync<GatewaySecurityException>(() =>
            provider.GetAsync(filter, [VirtualFilterKeyType.IntegerNumber], Tenant).AsTask());
        errEx.ErrorCode.ShouldBe(VirtualFilterErrorCodes.SourceUnavailable);

        // 2. Timeout/cancellation denies and does not cache
        factory.Mode = ScriptedConnectionFactory.FailureMode.Timeout;
        var timeoutEx = await Should.ThrowAsync<GatewaySecurityException>(() =>
            provider.GetAsync(filter, [VirtualFilterKeyType.IntegerNumber], Tenant).AsTask());
        timeoutEx.ErrorCode.ShouldBe(VirtualFilterErrorCodes.SourceUnavailable);

        // 3. Overflow (> MaxAuthorizedKeys = 2) denies with KeySetTooLarge (never truncated, never cached)
        factory.Mode = ScriptedConnectionFactory.FailureMode.ReturnRows;
        factory.RowsToReturn = [[1L], [2L], [3L]];
        var overEx = await Should.ThrowAsync<GatewaySecurityException>(() =>
            provider.GetAsync(filter, [VirtualFilterKeyType.IntegerNumber], Tenant).AsTask());
        overEx.ErrorCode.ShouldBe(VirtualFilterErrorCodes.KeySetTooLarge);

        // 4. Subsequent valid load of <= MaxAuthorizedKeys succeeds immediately because failures/overflow were never cached
        factory.RowsToReturn = [[10L], [20L]];
        var validSet = await provider.GetAsync(filter, [VirtualFilterKeyType.IntegerNumber], Tenant);
        validSet.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Keyset_max_age_capped_by_global_maximum()
    {
        var baseFilter = new VirtualFilter
        {
            TenantId = Tenant,
            Name = "ttl_filter",
            Source = "source_a",
            EvaluationMode = RowPolicyEvaluationMode.KeySet,
            KeyColumns = ["client.client_id"],
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("source_a", "conf", "client"),
                FromAlias = "client"
            }
        };

        var options = new GatewayOptions
        {
            VirtualFilters = new VirtualFilterOptions
            {
                Federation = new VirtualFilterFederationOptions { KeyCacheTtlSeconds = 45 }
            }
        };

        RowPolicyParameterBinder.ComputeEffectiveKeySetMaxAge(
            baseFilter with { KeySetMaxAge = TimeSpan.FromHours(5) },
            options).ShouldBe(TimeSpan.FromHours(1));

        RowPolicyParameterBinder.ComputeEffectiveKeySetMaxAge(
            baseFilter with { KeySetMaxAge = TimeSpan.FromMinutes(5) },
            options).ShouldBe(TimeSpan.FromMinutes(5));

        RowPolicyParameterBinder.ComputeEffectiveKeySetMaxAge(
            baseFilter with { KeySetMaxAge = null },
            options).ShouldBe(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task SaveFilter_KeySetMaxKeysExceedingGlobalMaxAuthorizedKeys_Rejected()
    {
        var repo = new SingleSnapshotRepo(new VirtualFilterSnapshot(0, [], []));
        var opts = Options.Create(new GatewayOptions
        {
            VirtualFilters = new VirtualFilterOptions
            {
                Federation = new VirtualFilterFederationOptions { MaxAuthorizedKeys = 500 }
            }
        });
        var admin = new VirtualFilterAdministrationService(repo, NullAuditLogRepository.Instance, opts);
        var actor = new VirtualFilterActor(new Sid("S-1-5-21-ADMIN"), IsSync: false);
        var overLimit = new VirtualFilter
        {
            TenantId = new TenantId("tenant_a"),
            Name = "bounded_policy",
            Source = "src",
            KeyColumns = ["t.id"],
            EvaluationMode = RowPolicyEvaluationMode.KeySet,
            KeySetMaxKeys = 501,
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("src", "dbo", "t"),
                FromAlias = "t"
            }
        };

        await Should.ThrowAsync<ArgumentException>(() => admin.SaveFilterAsync(overLimit, actor));

        var atLimit = overLimit with { KeySetMaxKeys = 500 };
        var saved = await admin.SaveFilterAsync(atLimit, actor);
        saved.KeySetMaxKeys.ShouldBe(500);
    }

    private sealed class SingleSnapshotRepo(VirtualFilterSnapshot snap) : IVirtualFilterRepository
    {
        public Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default) => Task.FromResult(snap);
        public Task<long> GetGenerationAsync(CancellationToken ct = default) => Task.FromResult(snap.Generation);
        public Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedSnapshotProvider(VirtualFilterSnapshot snap) : IVirtualFilterSnapshotProvider
    {
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) => new(snap);
        public void Invalidate() { }
    }

    private sealed class SingleTableCatalog(TableMetadata meta) : ITableMetadataRepository
    {
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([meta]);
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(table.Equals(meta.Identifier) ? meta : null);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? keyDirections = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task FlushPendingVersionChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ScriptedConnectionFactory : ISqlConnectionFactory
    {
        public enum FailureMode { ThrowError, Timeout, ReturnRows }
        public FailureMode Mode { get; set; }
        public List<object?[]> RowsToReturn { get; set; } = [];

        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            if (Mode == FailureMode.ThrowError) throw new InvalidOperationException("simulated source down");
            if (Mode == FailureMode.Timeout) throw new TimeoutException("simulated timeout");
            return Task.FromResult<DbConnection>(new FakeDbConnection(RowsToReturn));
        }
    }

    private sealed class FakeDbConnection(List<object?[]> rows) : DbConnection
    {
        [AllowNull] public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "db";
        public override string DataSource => "stub";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTransaction(this);
        protected override DbCommand CreateDbCommand() => new FakeCommand(rows);
    }

    private sealed class FakeTransaction(DbConnection conn) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class FakeCommand(List<object?[]> rows) : DbCommand
    {
        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection { get; } = new FakeParams();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeParam();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new FakeReader(rows);
    }

    private sealed class FakeParams : DbParameterCollection
    {
        private readonly List<object> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _items.Add(value); return _items.Count - 1; }
        public override void AddRange(Array values) { }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains(value);
        public override bool Contains(string value) => false;
        public override void CopyTo(Array array, int index) { }
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf(value);
        public override int IndexOf(string parameterName) => -1;
        public override void Insert(int index, object value) { }
        public override void Remove(object value) => _items.Remove(value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { }
        protected override DbParameter GetParameter(int index) => (DbParameter)_items[index];
        protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) { }
    }

    private sealed class FakeParam : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = string.Empty;
        [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeReader(List<object?[]> rows) : DbDataReader
    {
        private int _idx = -1;
        public override int FieldCount => rows.Count > 0 ? rows[0].Length : 1;
        public override object this[int ordinal] => rows[_idx][ordinal]!;
        public override object this[string name] => throw new NotSupportedException();
        public override int Depth => 0;
        public override bool HasRows => rows.Count > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override bool Read() => ++_idx < rows.Count;
        public override bool NextResult() => false;
        public override bool IsDBNull(int ordinal) => rows[_idx][ordinal] is null or DBNull;
        public override object GetValue(int ordinal) => rows[_idx][ordinal]!;
        public override int GetValues(object[] values) => throw new NotSupportedException();
        public override string GetName(int ordinal) => $"c{ordinal}";
        public override int GetOrdinal(string name) => 0;
        public override string GetDataTypeName(int ordinal) => "object";
        public override Type GetFieldType(int ordinal) => typeof(object);
        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
    }
}
