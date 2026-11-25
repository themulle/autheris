using Autheris.Application.Governance.Ownership;
using Autheris.Tests.Unit.TestSupport;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.ConsentRequests;

[Trait("Category", "Security")]
public sealed class WriteConsentWorkflowTests
{
    private static readonly Sid Requester = new("S-1-5-21-WRITE-REQ");
    private static readonly Sid Approver = new("S-1-5-21-WRITE-APP");
    private static readonly TenantId Tenant = Autheris.Tests.Common.TestTenants.Default;
    private static readonly TableIdentifier OrdersId = new("sales", "dbo", "orders");

    private static TableMetadata CreateOrdersMetadata(string sensitivity = "LOW", bool requiresFourEyes = false)
    {
        var tableId = Guid.NewGuid();
        var sensitive = requiresFourEyes ||
            (!string.Equals(sensitivity, "LOW", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(sensitivity, "NORMAL", StringComparison.OrdinalIgnoreCase));
        return new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table
            {
                Id = tableId,
                SourceName = OrdersId.Domain,
                SchemaName = OrdersId.Schema,
                TableName = OrdersId.TableName
            },
            ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(sensitive ? new[] { "amount" } : Array.Empty<string>()),
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "id", DataType = "int", WriteProtected = false },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "status", DataType = "varchar", WriteProtected = false },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "amount", DataType = "decimal", WriteProtected = false },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "created_by", DataType = "varchar", WriteProtected = true }
            ]
        };
    }

    private static ClaimsPrincipal CreatePrincipal(Sid sid) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.PrimarySid, sid.Value),
        new Claim(ClaimTypes.NameIdentifier, sid.Value),
        new Claim(ClaimTypes.Name, "test_user"),
        DefaultTenantCaller.Claim
    ], "TestAuth", ClaimTypes.Name, ClaimTypes.Role));

    private static IHttpContextAccessor CreateAccessor(Sid sid)
    {
        var services = new ServiceCollection();
        var guard = Substitute.For<IConsentDecisionGuard>();
        guard.Evaluate(Arg.Any<ClaimsPrincipal>()).Returns(ConsentDecisionVerdict.Allow());
        services.AddSingleton(guard);
        services.AddSingleton<IGrantPolicy>(new GrantPolicy());

        var context = new DefaultHttpContext
        {
            User = CreatePrincipal(sid),
            RequestServices = services.BuildServiceProvider()
        };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return accessor;
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task RequestedWriteRulesJson_And_ConsentWriteRules_RoundTrip_And_ActivationRespectsWriteOnly()
    {
        var ruleId = Guid.NewGuid();
        var rule = new WriteRule(ruleId, WriteOperation.Insert, ["id", "status"], WriteRowScope.OwnRows, null, 25);

        var writeOnlyReq = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = OrdersId,
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            BusinessJustification = "Round-trip",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = Tenant,
            RequestedWriteRules = [rule],
            IncludeReadAccess = false
        };
        var readWriteReq = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = writeOnlyReq.TableId,
            TableIdentifier = OrdersId,
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            BusinessJustification = "Round-trip rw",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = Tenant,
            RequestedWriteRules = [rule],
            IncludeReadAccess = true
        };

        var woJson = ConsentWriteRulesSql.SerializeRequestedWriteRules(writeOnlyReq);
        var (woRules, woIncludeRead) = ConsentWriteRulesSql.DeserializeRequestedWriteRules(woJson);
        woIncludeRead.ShouldBeFalse();
        woRules.ShouldHaveSingleItem().Columns.ShouldBe(["id", "status"]);

        var rwJson = ConsentWriteRulesSql.SerializeRequestedWriteRules(readWriteReq);
        var (rwRules, rwIncludeRead) = ConsentWriteRulesSql.DeserializeRequestedWriteRules(rwJson);
        rwIncludeRead.ShouldBeTrue();
        rwRules.ShouldHaveSingleItem().Id.ShouldBe(ruleId);

        // Activation via TryClaimConsentRequestActivationAsync + InsertActivatedColumnAndWriteRulesAsync + Hydration via HydrateConsentsBatchAsync
        var store = new FakeGovernanceStore(writeOnlyReq.TableId);
        await using var conn = new FakeGovernanceDbConnection(store);
        conn.Open();

        await using (var tx = await conn.BeginTransactionAsync())
        {
            var (claimed, consentId, draft) = await ConsentSqlCore.TryClaimConsentRequestActivationAsync(conn, tx, writeOnlyReq, true, "ALLOW", DateTimeOffset.UtcNow, CancellationToken.None);
            claimed.ShouldBeTrue();
            draft.ShouldNotBeNull();
            await ConsentSqlCore.InsertActivatedColumnAndWriteRulesAsync(conn, tx, writeOnlyReq, consentId, CancellationToken.None);
            await tx.CommitAsync();
        }

        store.ColumnRulesCount.ShouldBe(0);
        store.WriteRules.ShouldHaveSingleItem();

        var consentToHydrate = new Consent
        {
            Id = store.WriteRules[0].ConsentId,
            TableId = writeOnlyReq.TableId,
            TableIdentifier = OrdersId,
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = Requester
        };
        await ConsentSqlCore.HydrateConsentsBatchAsync(conn, [consentToHydrate], CancellationToken.None);
        var hydratedRule = consentToHydrate.WriteRules.ShouldHaveSingleItem();
        hydratedRule.Id.ShouldBe(ruleId);
        hydratedRule.Operation.ShouldBe(WriteOperation.Insert);
        hydratedRule.RowScope.ShouldBe(WriteRowScope.OwnRows);
        hydratedRule.MaxRowsPerStatement.ShouldBe(25);
        hydratedRule.Columns.ShouldBe(["id", "status"]);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task CreateConsent_WithWriteRules_EmitsWriteGrantChangedAuditInSameTransaction()
    {
        AuditPolicyTable.All.ContainsKey(AuditEventTypes.WriteGrantChanged).ShouldBeTrue();
        AuditPolicyTable.For(AuditEventTypes.WriteGrantChanged).ShouldBe(AuditFailurePolicy.FailClosed);

        var tableId = Guid.NewGuid();
        var store = new FakeGovernanceStore(tableId);
        var dialect = new PostgreSqlGovernanceDialect(() => new FakeGovernanceDbConnection(store));
        var repo = new GovernanceRepository(
            dialect,
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance,
            auditAnchorStore: new InMemoryAuditChainAnchorStore(),
            grantWriter: new DirectConsentGrantWriter(new GovernanceRepository.GrantRecordStore(dialect)));
        repo.InitializeAudit();

        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["status", "amount"], WriteRowScope.OwnRows, null, 40);
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = tableId,
            TableIdentifier = OrdersId,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = Requester,
            CreatedBySid = Approver,
            ValidFrom = DateTimeOffset.UtcNow,
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            WriteRules = [rule]
        };

        await repo.CreateConsentAsync(consent, CancellationToken.None);

        store.WriteRules.ShouldHaveSingleItem();
        store.AuditRows.Count.ShouldBe(2);
        var writeAudit = store.AuditRows.Single(r => r.EventType == AuditEventTypes.WriteGrantChanged);
        writeAudit.ActorSid.ShouldBe(Approver.Value);
        writeAudit.TargetTable.ShouldBe(OrdersId.ToString());
        using var doc = JsonDocument.Parse(writeAudit.DetailsJson);
        doc.RootElement.GetProperty("ConsentId").GetGuid().ShouldBe(consent.Id);
        doc.RootElement.GetProperty("ApproverSid").GetString().ShouldBe(Approver.Value);
        doc.RootElement.GetProperty("Operations").EnumerateArray().Select(e => e.GetString()).ShouldBe(["Update"]);
        doc.RootElement.GetProperty("Columns").EnumerateArray().Select(e => e.GetString()).ShouldBe(["status", "amount"]);
        doc.RootElement.GetProperty("RowScopes").EnumerateArray().Select(e => e.GetString()).ShouldBe(["OwnRows"]);
    }

    private sealed record StoredWriteRule(
        string Id, string TenantId, Guid ConsentId, int Operation, int RowScope, string? RowFilterJson, long? MaxRows, string ColumnsJson);

    private sealed record StoredAuditRow(
        long Seq, string Id, string OccurredAt, string EventType, string ActorSid, string TargetTable, string DetailsJson, string EntryHash);

    private sealed class FakeGovernanceStore(Guid tableId)
    {
        public Guid TableId { get; } = tableId;
        public List<StoredWriteRule> WriteRules { get; } = [];
        public List<StoredAuditRow> AuditRows { get; } = [];
        public int ColumnRulesCount { get; set; }
        public (string? Hash, long Seq) GetAuditTail() => AuditRows.Count == 0 ? (null, 0) : (AuditRows[^1].EntryHash, AuditRows[^1].Seq);
    }

    private sealed class FakeGovernanceDbConnection(FakeGovernanceStore store) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        [AllowNull] public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;
        public override void Open() => _state = ConnectionState.Open;
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeDbTransaction(this, store);
        protected override ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DbTransaction>(new FakeDbTransaction(this, store));
        protected override DbCommand CreateDbCommand() => new FakeDbCommand(this, store);
    }

    private sealed class FakeDbTransaction(DbConnection conn, FakeGovernanceStore store) : DbTransaction
    {
        public List<StoredWriteRule> StagedWriteRules { get; } = [];
        public List<StoredAuditRow> StagedAudits { get; } = [];
        public int StagedColumnRules { get; set; }
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit()
        {
            store.WriteRules.AddRange(StagedWriteRules);
            store.AuditRows.AddRange(StagedAudits);
            store.ColumnRulesCount += StagedColumnRules;
        }
        public override Task CommitAsync(CancellationToken cancellationToken = default) { Commit(); return Task.CompletedTask; }
        public override void Rollback() { StagedWriteRules.Clear(); StagedAudits.Clear(); StagedColumnRules = 0; }
    }

    private sealed class FakeDbCommand(DbConnection conn, FakeGovernanceStore store) : DbCommand
    {
        private readonly FakeParams _params = new();
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
        protected override DbParameter CreateDbParameter() => new FakeParam();

        private Dictionary<string, object?> ParamMap() =>
            _params.Cast<FakeParam>().ToDictionary(p => p.ParameterName, p => p.Value, StringComparer.OrdinalIgnoreCase);

        public override int ExecuteNonQuery()
        {
            var dict = ParamMap();
            if (CommandText.Contains("UPDATE CONSENT_REQUESTS", StringComparison.OrdinalIgnoreCase)) return 1;
            if (CommandText.Contains("INSERT INTO CONSENT_COLUMN_RULES", StringComparison.OrdinalIgnoreCase))
            {
                if (DbTransaction is FakeDbTransaction tx) tx.StagedColumnRules++;
                else store.ColumnRulesCount++;
                return 1;
            }
            if (CommandText.Contains("INSERT INTO CONSENT_WRITE_RULES", StringComparison.OrdinalIgnoreCase))
            {
                var r = new StoredWriteRule(
                    Convert.ToString(dict["@id"])!,
                    Convert.ToString(dict["@tenantId"])!,
                    Guid.Parse(Convert.ToString(dict["@consentId"])!),
                    Convert.ToInt32(dict["@op"]),
                    Convert.ToInt32(dict["@scope"]),
                    dict["@rowFilterJson"] is null or DBNull ? null : Convert.ToString(dict["@rowFilterJson"]),
                    dict["@maxRows"] is null or DBNull ? null : Convert.ToInt64(dict["@maxRows"]),
                    Convert.ToString(dict["@colsJson"])!);
                if (DbTransaction is FakeDbTransaction tx) tx.StagedWriteRules.Add(r);
                else store.WriteRules.Add(r);
                return 1;
            }
            if (CommandText.Contains("AUDIT_LOG_ENTRIES", StringComparison.OrdinalIgnoreCase) &&
                CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                var list = new List<StoredAuditRow>();
                if (dict.TryGetValue("@id0", out _))
                {
                    for (int k = 0; dict.TryGetValue($"@id{k}", out var idK); k++)
                    {
                        list.Add(new StoredAuditRow(
                            Convert.ToInt64(dict[$"@seq{k}"]),
                            Convert.ToString(idK)!,
                            Convert.ToString(dict[$"@occ{k}"])!,
                            Convert.ToString(dict[$"@event{k}"]) ?? string.Empty,
                            Convert.ToString(dict[$"@actor{k}"]) ?? string.Empty,
                            Convert.ToString(dict[$"@target{k}"]) ?? string.Empty,
                            Convert.ToString(dict[$"@det{k}"]) ?? "{}",
                            Convert.ToString(dict[$"@hash{k}"])!));
                    }
                }
                else if (dict.TryGetValue("@id", out var singleId))
                {
                    list.Add(new StoredAuditRow(
                        Convert.ToInt64(dict["@seq"]),
                        Convert.ToString(singleId)!,
                        Convert.ToString(dict["@occ"])!,
                        Convert.ToString(dict["@event"]) ?? string.Empty,
                        Convert.ToString(dict["@actor"]) ?? string.Empty,
                        Convert.ToString(dict["@target"]) ?? string.Empty,
                        Convert.ToString(dict["@det"]) ?? "{}",
                        Convert.ToString(dict["@hash"])!));
                }
                if (DbTransaction is FakeDbTransaction tx) tx.StagedAudits.AddRange(list);
                else store.AuditRows.AddRange(list);
                return list.Count;
            }
            return 1;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(ExecuteNonQuery());

        public override object? ExecuteScalar()
        {
            if (CommandText.Contains("POLICY_EPOCHS", StringComparison.OrdinalIgnoreCase)) return 2L;
            if (CommandText.Contains("SELECT COUNT(1) FROM CONSENTS", StringComparison.OrdinalIgnoreCase)) return 0L;
            if (CommandText.Contains("SELECT COUNT(*)", StringComparison.OrdinalIgnoreCase)) return (long)store.AuditRows.Count;
            if (CommandText.Contains("FROM TABLES", StringComparison.OrdinalIgnoreCase)) return store.TableId.ToString();
            return null;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (CommandText.Contains("FROM CONSENT_WRITE_RULES", StringComparison.OrdinalIgnoreCase))
            {
                var rows = store.WriteRules.Select(r => new object?[]
                {
                    r.Id, r.ConsentId.ToString(), r.Operation, r.RowScope, r.RowFilterJson, r.MaxRows, r.ColumnsJson
                }).ToArray();
                return new FakeReader(["id", "consent_id", "operation", "row_scope", "row_filter_json", "max_rows_per_statement", "columns_json"], rows);
            }
            if (CommandText.Contains("SELECT entry_hash, seq", StringComparison.OrdinalIgnoreCase))
            {
                var staged = (DbTransaction as FakeDbTransaction)?.StagedAudits;
                var (hash, seq) = staged is { Count: > 0 } ? (staged[^1].EntryHash, staged[^1].Seq) : store.GetAuditTail();
                return new FakeReader(["entry_hash", "seq"], hash == null ? [] : [new object?[] { hash, seq }]);
            }
            return new FakeReader([], []);
        }
    }

    private sealed class FakeParam : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = string.Empty;
        [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeParams : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { int i = IndexOf(parameterName); if (i >= 0) RemoveAt(i); }
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class FakeReader(string[] cols, object?[][] rows) : DbDataReader
    {
        private int _index = -1;
        public override int FieldCount => cols.Length;
        public override bool HasRows => rows.Length > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override int Depth => 0;
        public override object this[int ordinal] => rows[_index][ordinal] ?? DBNull.Value;
        public override object this[string name] => this[GetOrdinal(name)];
        public override bool Read() => ++_index < rows.Length;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override bool NextResult() => false;
        public override bool IsDBNull(int ordinal) => rows[_index][ordinal] is null or DBNull;
        public override string GetString(int ordinal) => Convert.ToString(rows[_index][ordinal])!;
        public override int GetInt32(int ordinal) => Convert.ToInt32(rows[_index][ordinal]);
        public override long GetInt64(int ordinal) => Convert.ToInt64(rows[_index][ordinal]);
        public override object GetValue(int ordinal) => rows[_index][ordinal] ?? DBNull.Value;
        public override int GetOrdinal(string name) => Array.IndexOf(cols, name);
        public override string GetName(int ordinal) => cols[ordinal];
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(rows[_index][ordinal]);
        public override byte GetByte(int ordinal) => Convert.ToByte(rows[_index][ordinal]);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => Convert.ToChar(rows[_index][ordinal]);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => string.Empty;
        public override Type GetFieldType(int ordinal) => typeof(object);
        public override float GetFloat(int ordinal) => Convert.ToSingle(rows[_index][ordinal]);
        public override double GetDouble(int ordinal) => Convert.ToDouble(rows[_index][ordinal]);
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(rows[_index][ordinal]);
        public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
        public override short GetInt16(int ordinal) => Convert.ToInt16(rows[_index][ordinal]);
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(rows[_index][ordinal]);
        public override int GetValues(object[] values) => 0;
        public override IEnumerator GetEnumerator() => rows.GetEnumerator();
    }

    private sealed class DirectConsentGrantWriter(IGrantRecordStore store) : IGrantWriter
    {
        public async Task<GrantRecord> InsertAndGrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default)
        {
            await store.InsertConsentRowAsync(transaction, consent, 1L, ct).ConfigureAwait(false);
            return new GrantRecord(input.TenantId, 1L, Guid.NewGuid(), GrantRecordType.Issued, consent.Id, input.RequestId, consent.GrantSource, consent.ProfileAssignmentId, input.GranteeType ?? "User", input.GranteeRef ?? "u", input.ObjectId ?? "t", input.ScopeJson, input.ValidFrom, input.ValidUntil, input.RequestedBy, input.DecisionJson, input.DecidedAt, input.DecidedAt, GrantRecordChain.GenesisHash);
        }

        public Task<GrantRecord> GrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RevokeAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RecordRejectionAsync(DbTransaction transaction, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RecordExpiryAsync(DbTransaction transaction, Consent? consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
