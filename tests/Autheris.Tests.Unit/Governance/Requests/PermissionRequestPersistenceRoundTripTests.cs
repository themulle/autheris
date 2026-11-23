using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class PermissionRequestPersistenceRoundTripTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid Requester = new("user:alice");
    private static readonly Sid Approver = new("user:owner");
    private static readonly TableIdentifier OrdersId = new("sales", "dbo", "orders");

    [Fact]
    public async Task CreateConsentRequestAsync_CatalogRequestWithEmptyTable_SkipsTableResolutionAndPreservesMetadata()
    {
        var store = new FakeStore(Guid.Empty);
        var repo = CreateRepository(store);
        var containerId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

        var created = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.Empty,
            TableIdentifier = default,
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            BusinessJustification = "Catalog visibility needed for domain discovery.",
            Status = "PENDING",
            RequestedAt = now,
            RequestedValidFrom = now,
            RequestedValidTo = DateTimeOffset.MaxValue,
            TenantId = TenantA,
            RequestKind = ConsentRequestKind.Catalog,
            GrantKind = PermissionGrantKind.Catalog,
            ContainerKind = ContainerKind.DataProduct,
            ContainerId = containerId
        });

        store.TableLookupCount.ShouldBe(0);
        created.TableId.ShouldBe(Guid.Empty);
        created.RequestKind.ShouldBe(ConsentRequestKind.Catalog);
        created.GrantKind.ShouldBe(PermissionGrantKind.Catalog);
        created.ContainerKind.ShouldBe(ContainerKind.DataProduct);
        created.ContainerId.ShouldBe(containerId);

        var loaded = await repo.GetConsentRequestAsync(created.Id);
        loaded.ShouldNotBeNull();
        loaded.TableId.ShouldBe(Guid.Empty);
        loaded.RequestKind.ShouldBe(ConsentRequestKind.Catalog);
        loaded.GrantKind.ShouldBe(PermissionGrantKind.Catalog);
        loaded.ContainerKind.ShouldBe(ContainerKind.DataProduct);
        loaded.ContainerId.ShouldBe(containerId);
    }

    [Fact]
    public async Task CreateConsentAsync_PreservesGrantKindApproverCapacitySelfGrantAndTargetColumns()
    {
        var tableId = Guid.NewGuid();
        var store = new FakeStore(tableId);
        var repo = CreateRepository(store);
        var now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

        var created = await repo.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TableId = tableId,
            TableIdentifier = OrdersId,
            ConsentRequestId = Guid.NewGuid(),
            Effect = ConsentEffect.Allow,
            TenantId = TenantA,
            GranteeType = GranteeType.User,
            GranteeSid = Requester,
            CreatedBySid = Approver,
            ValidFrom = now,
            ValidTo = now.AddDays(30),
            GrantSource = ConsentGrantSource.SelfGrant,
            GrantKind = PermissionGrantKind.Unmask,
            ApproverCapacity = ApproverCapacity.DataOwner,
            SelfGrant = true,
            TargetColumns = ["email", "tax_id"]
        });

        created.GrantKind.ShouldBe(PermissionGrantKind.Unmask);
        created.ApproverCapacity.ShouldBe(ApproverCapacity.DataOwner);
        created.SelfGrant.ShouldBeTrue();
        created.TargetColumns.ShouldBe(["email", "tax_id"]);

        var hydrated = await repo.GetConsentByIdAsync(created.Id);
        hydrated.ShouldNotBeNull();
        hydrated.GrantKind.ShouldBe(PermissionGrantKind.Unmask);
        hydrated.ApproverCapacity.ShouldBe(ApproverCapacity.DataOwner);
        hydrated.SelfGrant.ShouldBeTrue();
        hydrated.TargetColumns.ShouldBe(["email", "tax_id"]);
    }

    [Fact]
    public async Task TryClaimConsentRequestActivationAsync_PreservesGrantKindApproverCapacitySelfGrantAndTargetColumns()
    {
        var tableId = Guid.NewGuid();
        var store = new FakeStore(tableId);
        await using var conn = new FakeDbConnection(store);
        conn.Open();

        var now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = tableId,
            TableIdentifier = OrdersId,
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            BusinessJustification = "Self-granted unmask access within owner scope.",
            Status = "PENDING",
            RequestedAt = now,
            RequestedValidFrom = now,
            RequestedValidTo = now.AddDays(14),
            TenantId = TenantA,
            RequestKind = ConsentRequestKind.Unmask,
            GrantKind = PermissionGrantKind.Unmask,
            ApproverCapacity = ApproverCapacity.ItAdmin,
            SelfGrant = true,
            TargetColumns = ["ssn"]
        };

        await using var tx = await conn.BeginTransactionAsync();
        var (claimed, _, draft) = await ConsentSqlCore.TryClaimConsentRequestActivationAsync(
            conn, tx, req, allowPendingAutoApprove: true, "ALLOW", now, CancellationToken.None);

        claimed.ShouldBeTrue();
        draft.ShouldNotBeNull();
        draft.GrantKind.ShouldBe(PermissionGrantKind.Unmask);
        draft.ApproverCapacity.ShouldBe(ApproverCapacity.ItAdmin);
        draft.SelfGrant.ShouldBeTrue();
        draft.GrantSource.ShouldBe(ConsentGrantSource.SelfGrant);
        draft.TargetColumns.ShouldBe(["ssn"]);
    }

    private static GovernanceRepository CreateRepository(FakeStore store)
    {
        var dialect = new PostgreSqlGovernanceDialect(() => new FakeDbConnection(store));
        var repo = new GovernanceRepository(
            dialect,
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance,
            auditAnchorStore: new InMemoryAuditChainAnchorStore(),
            grantWriter: new DirectGrantWriter(new GovernanceRepository.GrantRecordStore(dialect)));
        repo.InitializeAudit();
        return repo;
    }

    private sealed record StoredRequestRow(Dictionary<string, object?> Params);
    private sealed record StoredConsentRow(Dictionary<string, object?> Params);
    private sealed record StoredAuditRow(long Seq, string EntryHash);

    private sealed class FakeStore(Guid tableId)
    {
        public Guid TableId { get; } = tableId;
        public int TableLookupCount { get; set; }
        public Dictionary<string, StoredRequestRow> Requests { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, StoredConsentRow> Consents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<StoredAuditRow> AuditRows { get; } = [];
    }

    private sealed class DirectGrantWriter(IGrantRecordStore store) : IGrantWriter
    {
        public async Task<GrantRecord> InsertAndGrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default)
        {
            await store.InsertConsentRowAsync(transaction, consent, 1L, ct).ConfigureAwait(false);
            return new GrantRecord(
                input.TenantId, 1L, Guid.NewGuid(), GrantRecordType.Issued, consent.Id, input.RequestId,
                consent.GrantSource, consent.ProfileAssignmentId, input.GranteeType ?? "User", input.GranteeRef ?? "u",
                input.ObjectId ?? "t", input.ScopeJson, input.ValidFrom, input.ValidUntil,
                input.RequestedBy, input.DecisionJson, input.DecidedAt, input.DecidedAt, GrantRecordChain.GenesisHash);
        }

        public Task<GrantRecord> GrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RevokeAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RecordRejectionAsync(DbTransaction transaction, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RecordExpiryAsync(DbTransaction transaction, Consent? consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeDbConnection(FakeStore store) : DbConnection
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

    private sealed class FakeDbTransaction(DbConnection conn, FakeStore store) : DbTransaction
    {
        public List<StoredConsentRow> StagedConsents { get; } = [];
        public List<StoredAuditRow> StagedAudits { get; } = [];
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit()
        {
            foreach (var c in StagedConsents)
            {
                store.Consents[Convert.ToString(c.Params["@id"])!] = c;
            }
            store.AuditRows.AddRange(StagedAudits);
        }
        public override Task CommitAsync(CancellationToken cancellationToken = default) { Commit(); return Task.CompletedTask; }
        public override void Rollback() { StagedConsents.Clear(); StagedAudits.Clear(); }
    }

    private sealed class FakeDbCommand(DbConnection conn, FakeStore store) : DbCommand
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
            if (CommandText.Contains("INSERT INTO CONSENT_REQUESTS", StringComparison.OrdinalIgnoreCase))
            {
                store.Requests[Convert.ToString(dict["@id"])!] = new StoredRequestRow(dict);
                return 1;
            }
            if (CommandText.Contains("INSERT INTO CONSENTS", StringComparison.OrdinalIgnoreCase))
            {
                var row = new StoredConsentRow(dict);
                if (DbTransaction is FakeDbTransaction tx) tx.StagedConsents.Add(row);
                else store.Consents[Convert.ToString(dict["@id"])!] = row;
                return 1;
            }
            if (CommandText.Contains("AUDIT_LOG_ENTRIES", StringComparison.OrdinalIgnoreCase) &&
                CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                var audits = new List<StoredAuditRow>();
                if (dict.TryGetValue("@id0", out _))
                {
                    for (var k = 0; dict.TryGetValue($"@id{k}", out _); k++)
                    {
                        audits.Add(new StoredAuditRow(Convert.ToInt64(dict[$"@seq{k}"]), Convert.ToString(dict[$"@hash{k}"])!));
                    }
                }
                else if (dict.TryGetValue("@id", out _))
                {
                    audits.Add(new StoredAuditRow(Convert.ToInt64(dict["@seq"]), Convert.ToString(dict["@hash"])!));
                }

                if (DbTransaction is FakeDbTransaction tx) tx.StagedAudits.AddRange(audits);
                else store.AuditRows.AddRange(audits);
                return audits.Count;
            }
            return 1;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(ExecuteNonQuery());

        public override object? ExecuteScalar()
        {
            if (CommandText.Contains("FROM TABLES", StringComparison.OrdinalIgnoreCase))
            {
                store.TableLookupCount++;
                return store.TableId == Guid.Empty ? null : store.TableId.ToString();
            }
            if (CommandText.Contains("POLICY_EPOCHS", StringComparison.OrdinalIgnoreCase)) return 2L;
            if (CommandText.Contains("SELECT COUNT(1) FROM CONSENTS", StringComparison.OrdinalIgnoreCase)) return 0L;
            if (CommandText.Contains("SELECT COUNT(*)", StringComparison.OrdinalIgnoreCase)) return (long)store.AuditRows.Count;
            return null;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var dict = ParamMap();
            if (CommandText.Contains("FROM CONSENT_REQUESTS r", StringComparison.OrdinalIgnoreCase) &&
                dict.TryGetValue("@id", out var reqIdObj) &&
                reqIdObj is string reqId &&
                store.Requests.TryGetValue(reqId, out var r))
            {
                var p = r.Params;
                var row = new object?[]
                {
                    p["@id"], p["@tid"], p["@sid"], p["@gType"], p["@gRef"],
                    p["@just"], p["@status"], p["@reqAt"], p["@reqTo"],
                    null, null, null, p["@tenantId"],
                    Db(p, "@reqIds"), Db(p, "@ctx"), Db(p, "@reqWriteRules"),
                    p["@version"], p["@workflowMode"], Db(p, "@externalSystemId"), Db(p, "@externalReference"),
                    p["@reqValidFrom"], p["@updatedAt"], p["@requestKind"], Db(p, "@profileId"),
                    p["@grantKind"], Db(p, "@targetColumns"), Db(p, "@containerKind"), Db(p, "@containerId"),
                    Db(p, "@approverCapacity"), p["@selfGrant"], Db(p, "@bundleId"), Db(p, "@decidedBy"), Db(p, "@decidedAt")
                };
                return new FakeReader(Enumerable.Range(0, row.Length).Select(i => "c" + i).ToArray(), [row]);
            }

            if (CommandText.Contains("FROM CONSENTS c", StringComparison.OrdinalIgnoreCase) &&
                dict.TryGetValue("@id", out var cIdObj) &&
                cIdObj is string cid &&
                store.Consents.TryGetValue(cid, out var c))
            {
                var p = c.Params;
                var row = new object?[]
                {
                    p["@id"], p["@tid"], Db(p, "@reqId"), p["@effect"], p["@granteeType"],
                    Db(p, "@granteeSid"), p["@validFrom"], p["@validTo"],
                    0, null, null, null,
                    OrdersId.Domain, OrdersId.Schema, OrdersId.TableName, p["@tenantId"], 0,
                    p["@grantSource"], p["@grantRecordSeq"], Db(p, "@profileAssignmentId"), Db(p, "@expiryNoticeSentAt"),
                    p["@grantKind"], Db(p, "@approverCapacity"), p["@selfGrant"], Db(p, "@targetColumns")
                };
                return new FakeReader(Enumerable.Range(0, row.Length).Select(i => "c" + i).ToArray(), [row]);
            }

            if (CommandText.Contains("SELECT entry_hash, seq", StringComparison.OrdinalIgnoreCase))
            {
                var staged = (DbTransaction as FakeDbTransaction)?.StagedAudits;
                var tail = staged is { Count: > 0 } ? staged[^1] : store.AuditRows.LastOrDefault();
                return new FakeReader(["entry_hash", "seq"], tail == null ? [] : [new object?[] { tail.EntryHash, tail.Seq }]);
            }

            return new FakeReader([], []);
        }

        private static object? Db(Dictionary<string, object?> map, string key) =>
            map.TryGetValue(key, out var val) && val is not DBNull ? val : null;
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
        public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) RemoveAt(i); }
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
}
