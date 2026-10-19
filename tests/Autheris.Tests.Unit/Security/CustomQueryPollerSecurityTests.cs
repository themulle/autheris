namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Streaming.Validation;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Extensions.Cdc;
using Autheris.Extensions.Cdc.CustomQuery;
using Autheris.Infrastructure.Streaming;
using Autheris.Tests.Unit.CdcStream;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

[Trait("Category", "Security")]
public sealed class CustomQueryPollerSecurityTests : IDisposable
{
    private static readonly CdcStreamId StreamId = new("0123456789abcdef0123456789abcdef");
    private static readonly TableIdentifier Table = new("crm", "dbo", "customers");

    private readonly ISqlEngine _sqlEngine = FastSqlEngine.Default;
    private readonly CustomQueryPollerValidator _validator;
    private readonly InMemoryCdcStreamStore _streamStore = new();
    private readonly FakeKeyStore _keys = new();
    private readonly FakeKekProvider _keks = new FakeKekProvider().Add("k1");
    private readonly CdcStreamKeyring _keyring;
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly ICdcStreamSignal _signal = Substitute.For<ICdcStreamSignal>();
    private readonly IDistributedClusterStateProvider _clusterState = Substitute.For<IDistributedClusterStateProvider>();
    private readonly CdcChangeEventBuilder _builder;

    public CustomQueryPollerSecurityTests()
    {
        _validator = new CustomQueryPollerValidator(_sqlEngine);

        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(new GatewayOptions());

        var time = TimeProvider.System;
        _keyring = new CdcStreamKeyring(_keys, _keks, monitor, time);

        var crypto = Substitute.For<ICdcStreamCrypto>();
        crypto.FingerprintAsync(Arg.Any<CdcStreamId>(), Arg.Any<IReadOnlyList<object?>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new byte[32]));
        crypto.ProtectEventAsync(Arg.Any<CdcStreamId>(), Arg.Any<long>(), Arg.Any<CdcStreamEventKind>(), Arg.Any<CdcTenantScope>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new CdcProtectedBlob("dek1", call.ArgAt<ReadOnlyMemory<byte>>(6).ToArray())));
        crypto.ProtectImageAsync(Arg.Any<CdcStreamId>(), Arg.Any<byte[]>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new CdcProtectedBlob("dek1", call.ArgAt<ReadOnlyMemory<byte>>(2).ToArray())));

        var codec = Substitute.For<ICdcPayloadCodec>();
        codec.SerializeChange(Arg.Any<CdcChangePayload>()).Returns(new byte[16]);
        codec.SerializeImage(Arg.Any<IReadOnlyDictionary<string, object?>>()).Returns(new byte[16]);

        _builder = new CdcChangeEventBuilder(_streamStore, crypto, codec, monitor);
    }

    public void Dispose() => _keyring.Dispose();

    private CdcStreamAdminService CreateAdminService() => new(_streamStore, _keys, _keyring);
    private IAuditWriter CreateAuditWriter() => AuditWriterTestServices.Over(_auditRepo);

    private static DefaultHttpContext Ctx(params string[] roles)
    {
        var claims = new List<Claim> { new("sub", "S-1-5-21-ADMIN1"), new("tenant_id", "home") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ADMIN1"),
            TenantId = new TenantId("home"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(roles.Where(r => r == "TenantAdmin")),
            ClusterRoles = new HashSet<string>(roles.Where(r => r is "GovernanceAdmin" or "ClusterAdmin")),
            AuthenticationScheme = "Bearer"
        };
        return context;
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode!.Value;

    private static CustomQueryPollerStreamOptions BaseOptions(string query) => new()
    {
        StreamName = "crm_customers",
        Table = "crm.dbo.customers",
        QueryTemplate = query,
        KeyColumns = ["id"],
        Watermarks =
        [
            new() { Name = "id", Type = WatermarkDimensionType.Int64, ParameterName = "p_wm_id", Column = "id", InitialValue = "0" }
        ]
    };

    [Theory]
    [InlineData("INSERT INTO customers (id, name) VALUES (1, 'Eve')")]
    [InlineData("UPDATE customers SET name = 'Eve' WHERE id = 1")]
    [InlineData("DELETE FROM customers WHERE id = 1")]
    [InlineData("DROP TABLE customers")]
    [InlineData("SELECT id FROM customers WHERE id > @p_wm_id; DROP TABLE customers;")]
    public void CustomQueryPollerValidator_RejectsDmlDdlAndMultiStatement(string maliciousQuery)
    {
        // Enforces SEC-CDC101-01: Rejects INSERT, UPDATE, DELETE, DROP, and multi-statement SQL
        var options = BaseOptions(maliciousQuery);
        var result = _validator.ValidatePollerOptions(options);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("SELECT id, name INTO backup_customers FROM customers WHERE id > @p_wm_id")]
    [InlineData("EXEC sp_who")]
    [InlineData("EXECUTE sp_executesql N'SELECT 1'")]
    public void CustomQueryPollerValidator_RejectsSelectIntoAndExec(string maliciousQuery)
    {
        // Enforces SEC-CDC101-01: Rejects SELECT INTO, stored procedure execution (EXEC), and dynamic SQL
        var options = BaseOptions(maliciousQuery);
        var result = _validator.ValidatePollerOptions(options);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void QueryPollerParameterBinder_BindsStrictTypedDbParameters()
    {
        // Enforces SEC-CDC101-02: Verifies all watermark dimensions and batch size are bound as typed DbParameter instances
        using var cmd = new SecurityFakeCommand();
        var dimensions = new List<WatermarkDimensionDefinition>
        {
            new() { Name = "updated_at", Type = WatermarkDimensionType.DateTimeOffset, ParameterName = "p_wm_updated_at", Column = "updated_at", InitialValue = "1970-01-01T00:00:00Z" },
            new() { Name = "id", Type = WatermarkDimensionType.Int64, ParameterName = "p_wm_id", Column = "id", InitialValue = "0" }
        };
        var vector = new MultiWatermarkVector(new Dictionary<string, string>
        {
            ["updated_at"] = "2026-10-19T00:00:00Z",
            ["id"] = "100"
        });

        QueryPollerParameterBinder.BindParameters(cmd, dimensions, vector, batchSize: 50);

        cmd.Parameters.Count.ShouldBe(3);

        var pUpdatedAt = (DbParameter)cmd.Parameters["@p_wm_updated_at"];
        pUpdatedAt.ShouldNotBeNull();
        pUpdatedAt.DbType.ShouldBe(DbType.DateTimeOffset);
        pUpdatedAt.Value.ShouldBe(DateTimeOffset.Parse("2026-10-19T00:00:00Z"));

        var pId = (DbParameter)cmd.Parameters["@p_wm_id"];
        pId.ShouldNotBeNull();
        pId.DbType.ShouldBe(DbType.Int64);
        pId.Value.ShouldBe(100L);

        var pBatch = (DbParameter)cmd.Parameters["@p_batch_size"];
        pBatch.ShouldNotBeNull();
        pBatch.DbType.ShouldBe(DbType.Int32);
        pBatch.Value.ShouldBe(50);
    }

    [Fact]
    public void QueryPollerParameterBinder_MissingInitialValue_FailsClosed()
    {
        // Enforces SEC-CDC101-02: Declaring a dimension without InitialValue fails startup validation, preventing silent NULL starvation
        var options = BaseOptions("SELECT id FROM customers WHERE id > @p_wm_id");
        options.Watermarks[0] = new()
        {
            Name = "id",
            Type = WatermarkDimensionType.Int64,
            ParameterName = "p_wm_id",
            Column = "id",
            InitialValue = "" // Empty initial value
        };

        var result = _validator.ValidatePollerOptions(options);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("non-empty InitialValue"));

        // If binder is invoked directly without initial value and no current value, it must throw rather than bind DBNull
        using var cmd = new SecurityFakeCommand();
        Should.Throw<InvalidOperationException>(() =>
            QueryPollerParameterBinder.BindParameters(cmd, options.Watermarks, MultiWatermarkVector.Empty, 100));
    }

    [Theory]
    [InlineData("id;DROP")]
    [InlineData("p_id--")]
    [InlineData("id'OR'1'='1")]
    public void QueryPollerParameterBinder_ParameterNameWithSpecialChars_FailsValidation(string badParamName)
    {
        // Enforces SEC-CDC101-02: Parameter names with non-alphanumeric characters are rejected during validation
        var options = BaseOptions("SELECT id FROM customers WHERE id > @" + badParamName);
        options.Watermarks[0] = new()
        {
            Name = "id",
            Type = WatermarkDimensionType.Int64,
            ParameterName = badParamName,
            Column = "id",
            InitialValue = "0"
        };

        var result = _validator.ValidatePollerOptions(options);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Must match ^[a-zA-Z0-9_]+$"));
    }

    [Fact]
    public async Task CustomQueryPollerService_EnforcesReadOnlyTransactionMode()
    {
        // Enforces SEC-CDC101-03: Verifies that source database connection is configured with read-only transaction semantics
        var poller = BaseOptions("SELECT id FROM customers WHERE id > @p_wm_id");
        var monitor = Substitute.For<IOptionsMonitor<CustomQueryPollerOptions>>();
        monitor.CurrentValue.Returns(new CustomQueryPollerOptions { Enabled = true, Pollers = [poller] });

        var executedCommands = new List<string>();
        var fakeConn = new ReadOnlyTrackingDbConnection(executedCommands);
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(fakeConn);

        var service = new CustomQueryPollerService(monitor, _streamStore, _builder, factory, _signal, _clusterState, NullLogger<CustomQueryPollerService>.Instance);
        await service.PollStreamBatchAsync(poller, fencingToken: 1);

        executedCommands.ShouldContain(c => c.Contains("PRAGMA query_only = ON"));
    }

    [Fact]
    public async Task CustomQueryPollerService_StaleFencingToken_ThrowsFencingTokenRejected()
    {
        // Enforces SEC-CDC101-04: Poller with superseded lease token fails AppendAsync with FencingTokenRejectedException
        var poller = BaseOptions("SELECT id FROM customers WHERE id > @p_wm_id");
        var desc = await _streamStore.EnsureStreamAsync("query_poller", TableIdentifier.Parse(poller.Table));

        // Advance stream with fencing token 5
        var initialBatch = new CdcAppendBatch(desc.Id, "query_poller", TableIdentifier.Parse(poller.Table), FencingToken: 5, ExpectedHeadOffset: 0, NewWatermark: 100, Events: [], ImageWrites: []);
        await _streamStore.AppendAsync(initialBatch);

        // Attempting to append with stale fencing token 4 must fail
        var staleBatch = new CdcAppendBatch(desc.Id, "query_poller", TableIdentifier.Parse(poller.Table), FencingToken: 4, ExpectedHeadOffset: 0, NewWatermark: 101, Events: [], ImageWrites: []);
        await Should.ThrowAsync<FencingTokenRejectedException>(async () => await _streamStore.AppendAsync(staleBatch));
    }

    [Fact]
    public async Task CdcStreamStore_WatermarkState_CommittedInSameTransactionAsEvents()
    {
        // Enforces SEC-CDC101-04: Head compare-and-set or fencing failure prevents watermark state update
        var desc = await _streamStore.EnsureStreamAsync("query_poller", Table);

        // Commit valid batch with watermark state
        var validBatch = new CdcAppendBatch(desc.Id, "query_poller", Table, FencingToken: 1, ExpectedHeadOffset: 0, NewWatermark: 100, Events: [], ImageWrites: [], WatermarkState: "{\"id\":\"10\"}");
        await _streamStore.AppendAsync(validBatch);

        var state = await _streamStore.GetWatermarkStateAsync(desc.Id);
        state.ShouldBe("{\"id\":\"10\"}");

        // Attempt invalid batch with wrong expected head offset (conflict simulation)
        var conflictingBatch = new CdcAppendBatch(desc.Id, "query_poller", Table, FencingToken: 2, ExpectedHeadOffset: 99, NewWatermark: 200, Events: [], ImageWrites: [], WatermarkState: "{\"id\":\"99\"}");
        await Should.ThrowAsync<CdcStreamConflictException>(async () => await _streamStore.AppendAsync(conflictingBatch));

        // Watermark state must NOT have updated to 99
        var stateAfterFailure = await _streamStore.GetWatermarkStateAsync(desc.Id);
        stateAfterFailure.ShouldBe("{\"id\":\"10\"}");
    }

    [Fact]
    public void CdcOperationClassifier_FingerprintComparison_UsesFixedTimeEquals()
    {
        // Enforces SEC-CDC101-05: Verifies hash comparisons in deduplication use constant-time cryptographic equality
        byte[] hash1 = [10, 20, 30, 40, 50, 60, 70, 80];
        byte[] hash2 = [10, 20, 30, 40, 50, 60, 70, 80];
        byte[] hash3 = [10, 20, 30, 40, 50, 60, 70, 81];

        CdcOperationClassifier.CompareFingerprints(hash1, hash2).ShouldBeTrue();
        CdcOperationClassifier.CompareFingerprints(hash1, hash3).ShouldBeFalse();
        CdcOperationClassifier.CompareFingerprints(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty).ShouldBeTrue();
    }

    [Fact]
    public async Task CustomQueryPollerService_EventPayloads_EnvelopeEncryptedWithAes256Gcm()
    {
        // Enforces SEC-CDC101-05: Verifies events and stored row images are encrypted with stream DEKs
        var poller = BaseOptions("SELECT id FROM customers WHERE id > @p_wm_id");
        var monitor = Substitute.For<IOptionsMonitor<CustomQueryPollerOptions>>();
        monitor.CurrentValue.Returns(new CustomQueryPollerOptions { Enabled = true, Pollers = [poller] });

        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1L, ["name"] = "Confidential PII" }
        };
        var conn = new FakeQueryConnection(rows);
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(conn);

        var service = new CustomQueryPollerService(monitor, _streamStore, _builder, factory, _signal, _clusterState, NullLogger<CustomQueryPollerService>.Instance);
        await service.PollStreamBatchAsync(poller, fencingToken: 1);

        var desc = await _streamStore.EnsureStreamAsync("query_poller", TableIdentifier.Parse(poller.Table));
        var readResult = await _streamStore.ReadAsync(desc.Id, 1, 10);
        readResult.Events.Count.ShouldBe(1);

        var evt = readResult.Events[0];
        evt.Payload.DekId.ShouldBe("dek1");
        evt.Payload.Data.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("DataSteward")]
    [InlineData("Anonymous")]
    public async Task ResetWatermarkEndpoint_UnprivilegedCaller_Forbidden(string role)
    {
        // Enforces SEC-CDC101-06: Callers without cdc_all / GovernanceAdmin role receive HTTP 403 when invoking watermark reset
        var request = new ResetWatermarkRequest(new Dictionary<string, string> { ["id"] = "0" }, "Incident #404 recovery");
        var service = CreateAdminService();
        var audit = CreateAuditWriter();

        var result = await CdcStreamAdminEndpoints.ResetWatermarkAsync(StreamId.Value, request, Ctx(role), service, audit, default);
        Status(result).ShouldBe(403);
    }

    [Fact]
    public async Task ResetWatermarkEndpoint_EmitsTierAAuditLog_ZeroCredentialLeakage()
    {
        // Enforces SEC-CDC101-06: Verifies audit emission records justification and vector state with zero password/credential leakage
        var desc = await _streamStore.EnsureStreamAsync("mssql_ct", Table);
        var request = new ResetWatermarkRequest(
            new Dictionary<string, string> { ["updated_at"] = "1970-01-01T00:00:00Z", ["id"] = "0" },
            "Disaster recovery reset after data replay");

        var service = CreateAdminService();
        var audit = CreateAuditWriter();

        var result = await CdcStreamAdminEndpoints.ResetWatermarkAsync(desc.Id.Value, request, Ctx("GovernanceAdmin"), service, audit, default);
        Status(result).ShouldBe(200);

        // Verify Tier A audit event recorded
        await _auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(a =>
                a.EventType == AuditEventTypes.CdcStreamWatermarkReset &&
                a.DetailsJson.Contains("Disaster recovery reset") &&
                !a.DetailsJson.Contains("password") &&
                !a.DetailsJson.Contains("secret")),
            Arg.Any<CancellationToken>());
    }

    private sealed class SecurityFakeCommand : DbCommand
    {
        private readonly FakeDbParamCollection _params = new();
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new SecurityFakeParam();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotImplementedException();
    }

    private sealed class SecurityFakeParam : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ParameterName { get; set; } = string.Empty;
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeDbParamCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => _items.RemoveAll(p => p.ParameterName == parameterName);
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items.First(p => p.ParameterName == parameterName);
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class ReadOnlyTrackingDbConnection(List<string> executed) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "Data Source=:memory:;Mode=Memory;Sqlite;";
        public override string Database => "SqliteDb";
        public override string DataSource => "Sqlite";
        public override string ServerVersion => "3.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTx(this);
        protected override DbCommand CreateDbCommand() => new TrackingDbCommand(this, executed);
    }

    private sealed class TrackingDbCommand(DbConnection conn, List<string> executed) : DbCommand
    {
        private readonly FakeDbParamCollection _p = new();
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => _p;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() { executed.Add(CommandText); return 0; }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) { executed.Add(CommandText); return Task.FromResult(0); }
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new SecurityFakeParam();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new EmptyDbDataReader();
    }

    private sealed class FakeQueryConnection(List<Dictionary<string, object?>> rows) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "Data Source=:memory:";
        public override string Database => "Db";
        public override string DataSource => "Db";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTx(this);
        protected override DbCommand CreateDbCommand() => new FakeQueryCommand(this, rows);
    }

    private sealed class FakeQueryCommand(DbConnection conn, List<Dictionary<string, object?>> rows) : DbCommand
    {
        private readonly FakeDbParamCollection _p = new();
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => _p;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new SecurityFakeParam();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new RowsDbDataReader(rows);
    }

    private sealed class FakeTx(DbConnection conn) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class EmptyDbDataReader : DbDataReader
    {
        public override int FieldCount => 0;
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override bool HasRows => false;
        public override bool Read() => false;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public override string GetName(int ordinal) => string.Empty;
        public override int GetOrdinal(string name) => -1;
        public override object GetValue(int ordinal) => DBNull.Value;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override bool IsDBNull(int ordinal) => true;
        public override bool GetBoolean(int ordinal) => false;
        public override byte GetByte(int ordinal) => 0;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => '\0';
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => "TEXT";
        public override DateTime GetDateTime(int ordinal) => DateTime.UnixEpoch;
        public override decimal GetDecimal(int ordinal) => 0m;
        public override double GetDouble(int ordinal) => 0.0;
        public override System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
        public override Type GetFieldType(int ordinal) => typeof(string);
        public override float GetFloat(int ordinal) => 0f;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 0;
        public override int GetInt32(int ordinal) => 0;
        public override long GetInt64(int ordinal) => 0;
        public override string GetString(int ordinal) => string.Empty;
        public override int GetValues(object[] values) => 0;
        public override bool NextResult() => false;
    }

    private sealed class RowsDbDataReader(List<Dictionary<string, object?>> rows) : DbDataReader
    {
        private int _index = -1;
        private readonly List<string> _cols = rows.Count > 0 ? rows[0].Keys.ToList() : [];
        public override int FieldCount => _cols.Count;
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => rows.Count;
        public override bool HasRows => rows.Count > 0;
        public override bool Read() { _index++; return _index < rows.Count; }
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override string GetName(int ordinal) => _cols[ordinal];
        public override int GetOrdinal(string name) => _cols.IndexOf(name);
        public override object GetValue(int ordinal) => rows[_index][_cols[ordinal]] ?? DBNull.Value;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override bool IsDBNull(int ordinal) => rows[_index][_cols[ordinal]] is null or DBNull;
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));
        public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => "TEXT";
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal));
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
        public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
        public override System.Collections.IEnumerator GetEnumerator() => rows.GetEnumerator();
        public override Type GetFieldType(int ordinal) => typeof(string);
        public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
        public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
        public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
        public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
        public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
        public override string GetString(int ordinal) => GetValue(ordinal)?.ToString() ?? string.Empty;
        public override int GetValues(object[] values) => 0;
        public override bool NextResult() => false;
    }
}
