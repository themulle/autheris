namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ScimSchemaAndStoreTests
{
    private const string ValidAdSid = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    private const string ValidGroupSid = "S-1-5-21-1000000001-1000000002-1000000003-2001";
    private const string ValidEntraOid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln not found above test output directory.");
        return dir.FullName;
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Migration0012_SqlAndShapeDelta_Exist_PassLint_AndComposeFromV0001(string engine)
    {
        var root = FindRepoRoot();
        var stepsDir = Path.Combine(root, "src", "Autheris.Infrastructure", "Persistence", "Migrations", "Steps", engine);
        var sqlPath = Path.Combine(stepsDir, "0013_scim_identity_correlation.sql");
        var shapePath = Path.Combine(stepsDir, "0013_scim_identity_correlation.shape.json");

        File.Exists(sqlPath).ShouldBeTrue($"Missing migration step SQL: {sqlPath}");
        File.Exists(shapePath).ShouldBeTrue($"Missing migration step shape sidecar: {shapePath}");

        var rawSql = File.ReadAllText(sqlPath);
        var rawShape = File.ReadAllText(shapePath);

        var step = SqlMigrationStep.Parse(engine, 13, "scim_identity_correlation", rawSql, rawShape);
        step.Version.ShouldBe(13);
        step.Name.ShouldBe("scim_identity_correlation");
        StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

        // Check expected columns, tables, and indexes in SQL DDL
        var normalizedSql = Regex.Replace(step.Sql, @"\s+", " ").ToLowerInvariant();
        normalizedSql.ShouldContain("identity_users");
        normalizedSql.ShouldContain("object_sid");
        normalizedSql.ShouldContain("immutable_id");
        normalizedSql.ShouldContain("upn");
        normalizedSql.ShouldContain("entra_object_id");
        normalizedSql.ShouldContain("identity_groups");
        normalizedSql.ShouldContain("identity_group_nesting");
        normalizedSql.ShouldContain("scim_realm_sync_state");
        normalizedSql.ShouldContain("deprovision_epoch bigint not null default 0");
        normalizedSql.ShouldContain("ix_identity_users_tenant_realm_object_sid");
        normalizedSql.ShouldContain("ix_identity_users_tenant_realm_immutable_id");
        normalizedSql.ShouldContain("ix_identity_users_tenant_realm_entra_oid");
        normalizedSql.ShouldContain("ix_identity_users_tenant_realm_upn");
        normalizedSql.ShouldContain("ix_identity_users_tenant_realm_external_id");
        normalizedSql.ShouldContain("ix_identity_groups_tenant_realm_object_sid");
        normalizedSql.ShouldContain("ix_identity_group_nesting_child");

        // Verify all embedded steps compose cleanly from v0001 through 0012
        var embeddedSteps = SqlMigrationStep.LoadEmbedded(engine);
        embeddedSteps.Select(s => s.Version).ShouldContain(13);
        var deltas = embeddedSteps.ToDictionary(s => s.Version, s => s.ShapeDelta);
        var composed = GovernanceSchemaShapeLoader.LoadAt(engine, 13, deltas);

        var users = composed.Tables.First(t => t.Name.Equals("IDENTITY_USERS", StringComparison.OrdinalIgnoreCase));
        users.Columns.Select(c => c.Name.ToLowerInvariant()).ShouldContain("object_sid");
        users.Columns.Select(c => c.Name.ToLowerInvariant()).ShouldContain("immutable_id");
        users.Columns.Select(c => c.Name.ToLowerInvariant()).ShouldContain("upn");
        users.Columns.Select(c => c.Name.ToLowerInvariant()).ShouldContain("entra_object_id");

        var groups = composed.Tables.First(t => t.Name.Equals("IDENTITY_GROUPS", StringComparison.OrdinalIgnoreCase));
        groups.Columns.Select(c => c.Name.ToLowerInvariant()).ShouldContain("object_sid");

        var nesting = composed.Tables.First(t => t.Name.Equals("IDENTITY_GROUP_NESTING", StringComparison.OrdinalIgnoreCase));
        nesting.PrimaryKey.Select(k => k.ToLowerInvariant()).ShouldBe(["tenant_id", "realm_id", "parent_group_id", "child_group_id"]);

        var syncState = composed.Tables.First(t => t.Name.Equals("SCIM_REALM_SYNC_STATE", StringComparison.OrdinalIgnoreCase));
        syncState.PrimaryKey.Select(k => k.ToLowerInvariant()).ShouldBe(["tenant_id", "realm_id"]);
        syncState.Columns.Select(c => c.Name.ToLowerInvariant()).ShouldContain("deprovision_epoch");
    }

    [Fact]
    public async Task IdentityStore_CorrelationQueries_BindTenantRealmAndNormalizedUpn()
    {
        var recorded = new List<RecordedCommand>();
        var created = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var row = new object?[]
        {
            "u-1", "ext-1", "alice", "alice@example.com", "Alice", true, DBNull.Value,
            created.ToString("O"), created.ToString("O"),
            ValidAdSid, "imm-1", "alice@example.com", ValidEntraOid
        };

        var store = new IdentityStore(
            IdentityStoreDialect.SqlServer,
            (_, _) => Task.FromResult(new IdentityStoreScope(
                new FakeDbConnection(recorded, cmd => FakeDbDataReader.SingleRow(row)),
                () => ValueTask.CompletedTask)));

        var tenant = new TenantId("tenant-a");
        var user = await store.GetUserByUpnAsync(tenant, "realm-1", "  Alice@Example.COM ");

        user.ShouldNotBeNull();
        user!.ObjectSid.ShouldBe(ValidAdSid);
        user.OnPremisesImmutableId.ShouldBe("imm-1");
        user.UserPrincipalName.ShouldBe("alice@example.com");
        user.EntraObjectId.ShouldBe(ValidEntraOid);

        recorded.Count.ShouldBe(1);
        recorded[0].CommandText.ShouldContain("tenant_id = @tenantId");
        recorded[0].CommandText.ShouldContain("realm_id = @realmId");
        recorded[0].Parameters["@tenantId"].ShouldBe("tenant-a");
        recorded[0].Parameters["@realmId"].ShouldBe("realm-1");
        recorded[0].Parameters["@upn"].ShouldBe("alice@example.com");
    }

    [Fact]
    public async Task IdentityStore_CreateUserAsync_PreCheckDuplicateObjectSid_ThrowsScimUniquenessConflictException()
    {
        var recorded = new List<RecordedCommand>();
        var store = new IdentityStore(
            IdentityStoreDialect.PostgreSql,
            (_, _) => Task.FromResult(new IdentityStoreScope(
                new FakeDbConnection(recorded, cmd =>
                {
                    if (cmd.CommandText.Contains("object_sid = @val", StringComparison.Ordinal))
                    {
                        return FakeDbDataReader.Scalar("existing-user");
                    }

                    return FakeDbDataReader.Empty();
                }),
                () => ValueTask.CompletedTask)));

        var user = new ScimUser(
            "u-2", "ext-2", "bob", "Bob", "bob@example.com", true, null, null, null,
            ObjectSid: ValidAdSid);

        var ex = await Should.ThrowAsync<ScimUniquenessConflictException>(() =>
            store.CreateUserAsync(new TenantId("tenant-a"), "realm-1", user));

        ex.AttributeName.ShouldBe("objectSid");
    }

    [Fact]
    public async Task IdentityStore_CreateUserAsync_DuplicateDbConstraint_TranslatesToScimUniquenessConflictException()
    {
        var recorded = new List<RecordedCommand>();
        var store = new IdentityStore(
            IdentityStoreDialect.SqlServer,
            (_, _) => Task.FromResult(new IdentityStoreScope(
                new FakeDbConnection(
                    recorded,
                    _ => FakeDbDataReader.Empty(),
                    onNonQuery: cmd =>
                    {
                        if (cmd.CommandText.Contains("MERGE INTO dbo.IDENTITY_USERS", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new FakeUniqueDbException("Violation of UNIQUE KEY constraint 'ix_identity_users_tenant_realm_entra_oid'.");
                        }
                    }),
                () => ValueTask.CompletedTask)));

        var user = new ScimUser(
            "u-3", "ext-3", "carol", "Carol", null, true, null, null, null,
            EntraObjectId: ValidEntraOid);

        var ex = await Should.ThrowAsync<ScimUniquenessConflictException>(() =>
            store.CreateUserAsync(new TenantId("tenant-a"), "realm-1", user));

        ex.AttributeName.ShouldBe("entraObjectId");
    }

    [Fact]
    public async Task IdentityStore_GroupNestingAndSyncState_ExecuteTenantScopedSql()
    {
        var recorded = new List<RecordedCommand>();
        var contactAt = DateTimeOffset.Parse("2026-11-14T11:00:00Z");
        var writeAt = DateTimeOffset.Parse("2026-11-14T11:05:00Z");

        var store = new IdentityStore(
            IdentityStoreDialect.PostgreSql,
            (_, _) => Task.FromResult(new IdentityStoreScope(
                new FakeDbConnection(recorded, cmd =>
                {
                    if (cmd.CommandText.Contains("FROM IDENTITY_GROUP_NESTING", StringComparison.Ordinal)
                        && cmd.CommandText.Contains("parent_group_id, child_group_id", StringComparison.Ordinal))
                    {
                        return FakeDbDataReader.SingleRow(["g-parent", "g-child"]);
                    }

                    if (cmd.CommandText.Contains("FROM SCIM_REALM_SYNC_STATE", StringComparison.Ordinal))
                    {
                        return FakeDbDataReader.SingleRow([contactAt, writeAt, 7L]);
                    }

                    return FakeDbDataReader.Empty();
                }),
                () => ValueTask.CompletedTask)));

        var tenant = new TenantId("tenant-a");
        await store.AddGroupNestingEdgeAsync(tenant, "realm-1", "g-parent", "g-child");
        var edges = await store.ListGroupNestingEdgesAsync(tenant, "realm-1");
        edges.ShouldBe([new ScimGroupNestingEdge("g-parent", "g-child")]);

        await store.DeleteGroupAsync(tenant, "realm-1", "g-parent");
        recorded.Any(c => c.CommandText.Contains("DELETE FROM IDENTITY_GROUP_NESTING", StringComparison.Ordinal)
                          && c.CommandText.Contains("parent_group_id = @groupId OR child_group_id = @groupId", StringComparison.Ordinal))
            .ShouldBeTrue();

        await store.UpsertRealmSyncStateAsync(tenant, "realm-1", contactAt, writeAt);
        var state = await store.GetRealmSyncStateAsync(tenant, "realm-1");
        state.ShouldNotBeNull();
        state!.LastContactAt.ShouldBe(contactAt);
        state.LastWriteAt.ShouldBe(writeAt);
        state.DeprovisionEpoch.ShouldBe(7L);

        foreach (var cmd in recorded)
        {
            cmd.Parameters["@tenantId"].ShouldBe("tenant-a");
            cmd.Parameters["@realmId"].ShouldBe("realm-1");
        }
    }

    private sealed record RecordedCommand(string CommandText, Dictionary<string, object?> Parameters);

    private sealed class FakeUniqueDbException(string message) : DbException(message)
    {
        public override string SqlState => "23505";
    }

    private sealed class FakeDbConnection(
        List<RecordedCommand> recorded,
        Func<FakeDbCommand, DbDataReader> onExecuteReader,
        Action<FakeDbCommand>? onNonQuery = null) : DbConnection
    {
        private string _connectionString = string.Empty;

        [AllowNull]
        public override string ConnectionString { get => _connectionString; set => _connectionString = value ?? string.Empty; }
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeDbTransaction(this);
        protected override DbCommand CreateDbCommand() => new FakeDbCommand(this, recorded, onExecuteReader, onNonQuery);
    }

    private sealed class FakeDbTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class FakeDbCommand(
        DbConnection connection,
        List<RecordedCommand> recorded,
        Func<FakeDbCommand, DbDataReader> onExecuteReader,
        Action<FakeDbCommand>? onNonQuery) : DbCommand
    {
        private readonly FakeParameterCollection _parameters = new();
        private string _commandText = string.Empty;

        [AllowNull]
        public override string CommandText { get => _commandText; set => _commandText = value ?? string.Empty; }
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeDbParameter();

        private RecordedCommand Capture()
        {
            var dict = _parameters.Items.ToDictionary(p => p.ParameterName, p => p.Value, StringComparer.Ordinal);
            var rec = new RecordedCommand(_commandText, dict);
            recorded.Add(rec);
            return rec;
        }

        public override int ExecuteNonQuery()
        {
            Capture();
            onNonQuery?.Invoke(this);
            return 1;
        }

        public override object? ExecuteScalar()
        {
            Capture();
            using var reader = onExecuteReader(this);
            return reader.Read() ? reader.GetValue(0) : null;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Capture();
            return onExecuteReader(this);
        }
    }

    private sealed class FakeDbParameter : DbParameter
    {
        private string _name = string.Empty;
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull]
        public override string ParameterName { get => _name; set => _name = value ?? string.Empty; }
        public override int Size { get; set; }
        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeParameterCollection : DbParameterCollection
    {
        public List<DbParameter> Items { get; } = [];
        public override int Count => Items.Count;
        public override object SyncRoot => ((ICollection)Items).SyncRoot;
        public override int Add(object value) { Items.Add((DbParameter)value); return Items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Items.Add((DbParameter)v); }
        public override void Clear() => Items.Clear();
        public override bool Contains(object value) => Items.Contains((DbParameter)value);
        public override bool Contains(string value) => Items.Any(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => Items.GetEnumerator();
        protected override DbParameter GetParameter(int index) => Items[index];
        protected override DbParameter GetParameter(string parameterName) => Items.First(p => p.ParameterName == parameterName);
        public override int IndexOf(object value) => Items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => Items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => Items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => Items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => Items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => Items.RemoveAt(IndexOf(parameterName));
        protected override void SetParameter(int index, DbParameter value) => Items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) { var i = IndexOf(parameterName); if (i >= 0) Items[i] = value; else Items.Add(value); }
    }

    private sealed class FakeDbDataReader(IReadOnlyList<object?[]> rows) : DbDataReader
    {
        private int _index = -1;

        public static FakeDbDataReader Empty() => new([]);
        public static FakeDbDataReader Scalar(object? value) => new([[value]]);
        public static FakeDbDataReader SingleRow(object?[] values) => new([values]);

        public override int FieldCount => rows.Count > 0 ? rows[0].Length : 0;
        public override bool HasRows => rows.Count > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => throw new NotSupportedException();
        public override bool Read() => ++_index < rows.Count;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override bool NextResult() => false;
        public override bool IsDBNull(int ordinal) => rows[_index][ordinal] is null or DBNull;
        public override object GetValue(int ordinal) => rows[_index][ordinal]!;
        public override string GetString(int ordinal) => (string)rows[_index][ordinal]!;
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(rows[_index][ordinal]!);
        public override byte GetByte(int ordinal) => Convert.ToByte(rows[_index][ordinal]!);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => Convert.ToChar(rows[_index][ordinal]!);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
        public override DateTime GetDateTime(int ordinal) => ((DateTimeOffset)rows[_index][ordinal]!).UtcDateTime;
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(rows[_index][ordinal]!);
        public override double GetDouble(int ordinal) => Convert.ToDouble(rows[_index][ordinal]!);
        public override Type GetFieldType(int ordinal) => rows[_index][ordinal]?.GetType() ?? typeof(object);
        public override float GetFloat(int ordinal) => Convert.ToSingle(rows[_index][ordinal]!);
        public override Guid GetGuid(int ordinal) => (Guid)rows[_index][ordinal]!;
        public override short GetInt16(int ordinal) => Convert.ToInt16(rows[_index][ordinal]!);
        public override int GetInt32(int ordinal) => Convert.ToInt32(rows[_index][ordinal]!);
        public override long GetInt64(int ordinal) => Convert.ToInt64(rows[_index][ordinal]!);
        public override string GetName(int ordinal) => $"col{ordinal}";
        public override int GetOrdinal(string name) => throw new NotSupportedException();
        public override int GetValues(object[] values) => throw new NotSupportedException();
        public override IEnumerator GetEnumerator() => rows.GetEnumerator();
    }
}
