using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Autheris.Tests.Unit.Governance.Grants.Records;

public sealed class GrantWriterCallSiteTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly TenantId Tenant = Autheris.Tests.Common.TestTenants.Default;

    [Fact]
    public async Task InsertAndGrantAsync_SignsAppendsAndInsertsConsentWithAssignedSeq()
    {
        var store = new RecordingGrantStore();
        var signer = EcdsaGrantRecordSigner.CreateEphemeral("unit-key");
        var writer = new GrantWriter(store, signer);
        using var conn = new StubDbConnection();
        await using var tx = await conn.BeginTransactionAsync();

        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = OrdersTable,
            TenantId = Tenant,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.Role,
            RoleName = "Analysts",
            ValidFrom = FixedNow,
            ValidTo = FixedNow.AddDays(14),
            GrantSource = ConsentGrantSource.Declarative
        };
        var input = new GrantRecordInput(
            Tenant.Value,
            "{}",
            "approver",
            "{\"Effect\":\"Deny\"}",
            FixedNow,
            GrantSource: ConsentGrantSource.Declarative,
            ValidFrom: consent.ValidFrom,
            ValidUntil: consent.ValidTo);

        var record = await writer.InsertAndGrantAsync(tx, consent, input);

        record.Seq.ShouldBe(1L);
        record.RecordType.ShouldBe(GrantRecordType.Issued);
        record.GranteeRef.ShouldBe("Analysts");
        store.Events.ShouldBe(["AppendRecordAndAdvanceHeadAsync", "InsertConsentRowAsync"]);
        store.InsertedConsentSeqs.ShouldBe([1L]);
        store.InsertedConsentIds.ShouldBe([consent.Id]);
    }

    [Fact]
    public async Task GrantRecordSqlCore_InsertConsentRowAsync_BindsAssignedGrantRecordSeq()
    {
        using var conn = new StubDbConnection();
        await using var tx = await conn.BeginTransactionAsync();
        var dialect = new PostgreSqlGovernanceDialect(() => conn);
        var core = new GrantRecordSqlCore(dialect);
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = OrdersTable,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-TEST"),
            ValidFrom = FixedNow,
            ValidTo = FixedNow.AddDays(7),
            GrantSource = ConsentGrantSource.ConsentRequest
        };

        await core.InsertConsentRowAsync(tx, consent, 99L);

        var cmd = conn.ExecutedCommands.ShouldHaveSingleItem();
        cmd.Sql.ShouldContain("INSERT INTO CONSENTS");
        cmd.Parameters["@grantRecordSeq"].ShouldBe(99L);
        cmd.Parameters["@id"].ShouldBe(consent.Id.ToString());
    }

    [Fact]
    public async Task GovernanceRepository_WithoutGrantWriter_FailsClosed_OnConsentInsertAndRevoke()
    {
        using var conn = new StubDbConnection();
        var dialect = new PostgreSqlGovernanceDialect(() => conn);
        var repo = new GovernanceRepository(
            dialect,
            options: Options.Create(new GatewayOptions()),
            auditAnchorStore: new InMemoryAuditChainAnchorStore());
        repo.InitializeAudit();

        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = OrdersTable,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-TEST"),
            ValidFrom = FixedNow,
            ValidTo = FixedNow.AddDays(7)
        };

        await Should.ThrowAsync<GrantRecordUnavailableException>(() => repo.CreateConsentAsync(consent));
        await Should.ThrowAsync<GrantRecordUnavailableException>(() => repo.RevokeConsentAsync(
            consent.Id, new Sid("S-1-5-21-ADMIN"), "revoke", ConsentRevocationAuthority.FromPrincipal(null)));
        await Should.ThrowAsync<GrantRecordUnavailableException>(() => repo.RevokeSystemConsentAsync(
            consent.Id, Guid.NewGuid(), new Sid("system:sync"), "sync-revoke"));
    }

    [Fact]
    public async Task CompositionRoot_ResolvesGrantRecordStoreAndGovernanceRepository_WithoutCircularDependency()
    {
        var services = new ServiceCollection();
        var env = new StubHostEnvironment();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = Autheris.Tests.Common.Governance.GovernanceTestConnectionStrings.NonConnectingPostgreSql
            }
        };
        services.AddSingleton<IHostEnvironment>(env);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddAutherisStorage(options, env);
        services.AddAutherisGovernance(options);
        services.AddAutherisSecurity(options, env);

        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        sp.GetRequiredService<IGrantRecordStore>().ShouldBeOfType<GrantRecordSqlCore>();
        sp.GetRequiredService<IGrantWriter>().ShouldBeOfType<GrantWriter>();
        sp.GetRequiredService<GovernanceRepository>().ShouldNotBeNull();
    }

    private sealed class RecordingGrantStore : IGrantRecordStore
    {
        private readonly List<GrantRecord> _records = [];
        public List<string> Events { get; } = [];
        public List<long> InsertedConsentSeqs { get; } = [];
        public List<Guid> InsertedConsentIds { get; } = [];

        public Task<(long LastSeq, string LastHash)> LockAndReadHeadAsync(DbTransaction transaction, string tenantId, CancellationToken ct = default)
        {
            var last = _records.Count == 0 ? null : _records[^1];
            return Task.FromResult(last is null ? (0L, GrantRecordChain.GenesisHash) : (last.Seq, last.RecordHash));
        }

        public Task AppendRecordAndAdvanceHeadAsync(DbTransaction transaction, GrantRecord record, CancellationToken ct = default)
        {
            Events.Add(nameof(AppendRecordAndAdvanceHeadAsync));
            _records.Add(record);
            return Task.CompletedTask;
        }

        public Task UpdateConsentGrantRecordSeqAsync(DbTransaction transaction, string tenantId, Guid consentId, long seq, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task InsertConsentRowAsync(DbTransaction transaction, Consent consent, long grantRecordSeq, CancellationToken ct = default)
        {
            Events.Add(nameof(InsertConsentRowAsync));
            InsertedConsentIds.Add(consent.Id);
            InsertedConsentSeqs.Add(grantRecordSeq);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GrantRecord>> ReadTenantChainAsync(string tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GrantRecord>>(_records);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed record RecordedCommand(string Sql, Dictionary<string, object?> Parameters);

    private sealed class StubDbConnection : DbConnection
    {
        public List<RecordedCommand> ExecutedCommands { get; } = [];
        [AllowNull] public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "stub";
        public override string DataSource => "stub";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new StubDbTransaction(this);
        protected override DbCommand CreateDbCommand() => new StubDbCommand(this);
    }

    private sealed class StubDbTransaction(StubDbConnection conn) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class StubDbCommand(StubDbConnection conn) : DbCommand
    {
        private readonly StubParamCollection _params = new();
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
        protected override DbParameter CreateDbParameter() => new StubParam();
        public override object? ExecuteScalar() => null;

        public override int ExecuteNonQuery()
        {
            conn.ExecutedCommands.Add(new RecordedCommand(
                CommandText,
                _params.Cast<StubParam>().ToDictionary(p => p.ParameterName, p => p.Value, StringComparer.OrdinalIgnoreCase)));
            return 1;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            new DataTable().CreateDataReader();
    }

    private sealed class StubParam : DbParameter
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

    private sealed class StubParamCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => ((ICollection)_items).SyncRoot;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (DbParameter p in values) _items.Add(p); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) _items.RemoveAt(i); }
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) { var i = IndexOf(parameterName); if (i >= 0) _items[i] = value; }
    }
}
