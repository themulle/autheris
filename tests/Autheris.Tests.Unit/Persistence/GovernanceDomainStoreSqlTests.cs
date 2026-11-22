using System.Data;
using System.Data.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Unit;
using Autheris.Tests.Unit.Persistence.DialectSql;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence;

[Trait("Category", "Security")]
public sealed class GovernanceDomainStoreSqlTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid Actor = new("S-1-5-21-1001");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Dialects() => DialectKit.Names();

    [Fact]
    public void AllSqlTemplates_AreConstantParameterizedAndTenantScoped()
    {
        foreach (var sql in GovernanceDomainStore.AllSqlTemplates)
        {
            if (sql.StartsWith("INSERT INTO", StringComparison.Ordinal) || sql.StartsWith("MERGE ", StringComparison.Ordinal))
            {
                sql.ShouldContain("tenant_id");
                sql.ShouldContain("@tenantId");
            }
            else
            {
                sql.ShouldContain("tenant_id = @tenantId");
            }

            sql.ShouldNotContain("'");
            sql.ShouldNotContain("+");
            sql.ShouldNotContain("$");
            sql.ShouldNotContain("{0}");
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ListAsync_BindsTenantLimitAndOffset_AndClampsAboveCap(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);

        var result = await store.ListAsync(TenantA, limit: 9999, offset: 10);

        result.ShouldBeEmpty();
        var cmd = conn.Commands.Last();
        cmd.CommandText.ShouldBe(dialectName == "SqlServer" ? GovernanceDomainStore.ListSsSql : GovernanceDomainStore.ListPgSql);
        cmd.Parameters.ShouldContain(p => p.Name == "@tenantId" && Equals(p.Value, TenantA.Value));
        cmd.Parameters.ShouldContain(p => p.Name == "@limit" && Equals(p.Value, IGovernanceDomainRepository.MaxPageSize));
        cmd.Parameters.ShouldContain(p => p.Name == "@offset" && Equals(p.Value, 10));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ListAsync_RejectsInvalidPagingAndInvalidTenants(string dialectName)
    {
        var (store, _) = CreateStore(dialectName);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.ListAsync(TenantA, limit: 0, offset: 0));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.ListAsync(TenantA, limit: -1, offset: 0));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.ListAsync(TenantA, limit: 10, offset: -1));
        await Should.ThrowAsync<ArgumentException>(() => store.ListAsync(default));
        await Should.ThrowAsync<ArgumentException>(() => store.ListAsync(TenantId.Platform));
        await Should.ThrowAsync<ArgumentException>(() => store.GetAsync(default, Guid.NewGuid()));
        await Should.ThrowAsync<ArgumentException>(() => store.GetAsync(TenantId.Platform, Guid.NewGuid()));
        await Should.ThrowAsync<ArgumentException>(() => store.EnsureUnassignedAsync(default));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task UpsertAsync_InsertAndCasUpdate_BindAllParametersWithoutConcatenation(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var domainId = Guid.NewGuid();
        var domain = new GovernanceDomain(
            domainId,
            TenantA,
            "  Human Resources  ",
            "HR tables",
            "hr@example.com",
            new Sid("S-1-5-21-512"),
            ProtectionClass.Confidential,
            CatalogVisibilityMode.OnRequest,
            IsSystemUnassigned: false,
            Version: 0,
            FixedNow,
            FixedNow);

        var created = await store.UpsertAsync(domain, expectedVersion: 0, Actor);
        created.Status.ShouldBe(GovernanceDomainWriteStatus.Created);
        created.Domain.ShouldNotBeNull();
        created.Domain.Name.ShouldBe("Human Resources");
        created.Domain.Version.ShouldBe(1);

        var insertCmd = conn.Commands.Last();
        insertCmd.CommandText.ShouldBe(GovernanceDomainStore.InsertDomainSql);
        insertCmd.Parameters.ShouldContain(p => p.Name == "@tenantId" && Equals(p.Value, TenantA.Value));
        insertCmd.Parameters.ShouldContain(p => p.Name == "@id" && Equals(p.Value, domainId.ToString("D")));
        insertCmd.Parameters.ShouldContain(p => p.Name == "@name" && Equals(p.Value, "Human Resources"));
        insertCmd.Parameters.ShouldContain(p => p.Name == "@defaultClass" && Equals(p.Value, "CONFIDENTIAL"));
        insertCmd.Parameters.ShouldContain(p => p.Name == "@visibility" && Equals(p.Value, "ON_REQUEST"));

        conn.EnqueueReader(DomainTable(domainId, "Human Resources", "CONFIDENTIAL", "ON_REQUEST", false, 2));
        var updated = await store.UpsertAsync(created.Domain with { Name = "HR Core" }, expectedVersion: 1, Actor);
        updated.Status.ShouldBe(GovernanceDomainWriteStatus.Updated);
        updated.Domain!.Version.ShouldBe(2);

        var updateCmd = conn.Commands.First(c => c.CommandText == GovernanceDomainStore.UpdateDomainSql);
        updateCmd.Parameters.ShouldContain(p => p.Name == "@expectedVersion" && Equals(p.Value, 1));
        updateCmd.Parameters.ShouldContain(p => p.Name == "@newVersion" && Equals(p.Value, 2));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task UpsertAsync_MapsVersionMismatchAndNotFoundCleanly(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var domain = new GovernanceDomain(
            Guid.NewGuid(), TenantA, "Finance", null, null, null,
            ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, FixedNow, FixedNow);

        conn.NonQueryResult = 0;
        conn.ScalarResult = 1;
        var conflict = await store.UpsertAsync(domain, expectedVersion: 1, Actor);
        conflict.Status.ShouldBe(GovernanceDomainWriteStatus.Conflict);

        var (stubStore, stubConn, _) = CreateStubDialectStore();
        stubConn.NonQueryResult = 0;
        stubConn.ScalarResult = null;
        var notFound = await stubStore.UpsertAsync(domain, expectedVersion: 1, Actor);
        notFound.Status.ShouldBe(GovernanceDomainWriteStatus.NotFound);
    }

    [Fact]
    public async Task UpsertAsync_AndEnsureUnassignedAsync_HandleUniqueViolationsViaDialect()
    {
        var (store, conn, dialect) = CreateStubDialectStore();
        dialect.UniqueViolation = true;
        conn.ThrowOnNonQuery = new FakeUniqueDbException();
        var domain = new GovernanceDomain(
            Guid.NewGuid(), TenantA, "Finance", null, null, null,
            ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 0, FixedNow, FixedNow);

        var inserted = await store.UpsertAsync(domain, expectedVersion: 0, Actor);
        inserted.Status.ShouldBe(GovernanceDomainWriteStatus.Conflict);

        var updated = await store.UpsertAsync(domain with { Version = 1 }, expectedVersion: 1, Actor);
        updated.Status.ShouldBe(GovernanceDomainWriteStatus.Conflict);

        conn.EnqueueReader(DomainTable(Guid.Empty, "Unassigned", "CONFIDENTIAL", "ON_REQUEST", true, 1));
        var unassigned = await store.EnsureUnassignedAsync(TenantA);
        unassigned.Id.ShouldBe(Guid.Empty);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task UpsertAndDelete_EnforceSystemUnassignedInvariants(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var unassigned = new GovernanceDomain(
            Guid.Empty, TenantA, "Unassigned", null, null, null,
            ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, IsSystemUnassigned: true, Version: 1, FixedNow, FixedNow);

        await Should.ThrowAsync<InvalidOperationException>(() => store.UpsertAsync(unassigned, 0, Actor));
        await Should.ThrowAsync<InvalidOperationException>(() => store.UpsertAsync(unassigned with { Name = "Renamed" }, 1, Actor));
        await Should.ThrowAsync<InvalidOperationException>(() => store.UpsertAsync(unassigned with { Visibility = CatalogVisibilityMode.Open }, 1, Actor));
        await Should.ThrowAsync<InvalidOperationException>(() => store.UpsertAsync(unassigned with { DefaultClass = ProtectionClass.Public }, 1, Actor));
        await Should.ThrowAsync<InvalidOperationException>(() => store.UpsertAsync(unassigned with { Id = Guid.NewGuid() }, 0, Actor));
        await Should.ThrowAsync<InvalidOperationException>(() => store.UpsertAsync(unassigned with { Id = Guid.NewGuid(), IsSystemUnassigned = false }, 0, Actor));
        await Should.ThrowAsync<InvalidOperationException>(() => store.DeleteAsync(TenantA, Guid.Empty, 1));
        conn.Commands.Count(c => !c.CommandText.StartsWith("SET QUOTED_IDENTIFIER", StringComparison.Ordinal)).ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task DeleteAsync_RemovesAssignmentsThenDomainInTransaction(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var domainId = Guid.NewGuid();

        var deleted = await store.DeleteAsync(TenantA, domainId, expectedVersion: 2);

        deleted.ShouldBeTrue();
        var txCmds = conn.Commands.Where(c => c.TransactionSet).ToList();
        txCmds.Select(c => c.CommandText).ShouldBe([GovernanceDomainStore.DeleteDomainTablesByDomainSql, GovernanceDomainStore.DeleteDomainSql]);
        txCmds[1].Parameters.ShouldContain(p => p.Name == "@expectedVersion" && Equals(p.Value, 2));
        txCmds[1].Parameters.ShouldContain(p => p.Name == "@isSystemFalse" && Equals(p.Value, false));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task EnsureUnassignedAsync_UsesDialectIdempotentInsertAndReadsDomain(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        conn.EnqueueReader(DomainTable(Guid.Empty, "Unassigned", "CONFIDENTIAL", "ON_REQUEST", true, 1));

        var domain = await store.EnsureUnassignedAsync(TenantA);

        domain.Id.ShouldBe(Guid.Empty);
        domain.Name.ShouldBe("Unassigned");
        domain.IsSystemUnassigned.ShouldBeTrue();
        domain.DefaultClass.ShouldBe(ProtectionClass.Confidential);
        domain.Visibility.ShouldBe(CatalogVisibilityMode.OnRequest);

        var expectedSql = dialectName == "SqlServer"
            ? GovernanceDomainStore.EnsureUnassignedSsSql
            : GovernanceDomainStore.EnsureUnassignedPgSql;
        conn.Commands.ShouldContain(c => c.CommandText == expectedSql);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task AssignTablesAsync_EnforcesBatchCapAndRunsInTransaction(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var tooMany = Enumerable.Range(0, IGovernanceDomainRepository.MaxBatchAssignCount + 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.AssignTablesAsync(TenantA, Guid.NewGuid(), tooMany, Actor));

        var domainId = Guid.NewGuid();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        conn.ScalarResult = 1;
        await store.AssignTablesAsync(TenantA, domainId, [t1, t2, t1], Actor);

        var upsertSql = dialectName == "SqlServer"
            ? GovernanceDomainStore.UpsertDomainTableSsSql
            : GovernanceDomainStore.UpsertDomainTablePgSql;
        var upsertCommands = conn.Commands.Where(c => c.CommandText == upsertSql).ToList();
        upsertCommands.Count.ShouldBe(2);
        upsertCommands.ShouldAllBe(c => c.TransactionSet);
        upsertCommands[0].Parameters.ShouldContain(p => p.Name == "@tableId" && Equals(p.Value, t1.ToString("D")));
        upsertCommands[1].Parameters.ShouldContain(p => p.Name == "@tableId" && Equals(p.Value, t2.ToString("D")));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task SetProtectionAsync_RejectsPublicMaskCategoryAndEmptyTableId(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var tableId = Guid.NewGuid();

        foreach (var badMask in new[] { "PUBLIC", "public", " Public ", new string('X', 65), "bad\nmask" })
        {
            await Should.ThrowAsync<ArgumentException>(() =>
                store.SetProtectionAsync(TenantA, tableId, "", new ObjectProtection(ProtectionClass.Confidential, badMask), Actor));
        }

        await Should.ThrowAsync<ArgumentException>(() =>
            store.SetProtectionAsync(TenantA, tableId, "   ", new ObjectProtection(ProtectionClass.Confidential), Actor));
        await Should.ThrowAsync<ArgumentException>(() =>
            store.SetProtectionAsync(TenantA, Guid.Empty, "", new ObjectProtection(ProtectionClass.Confidential), Actor));
        conn.Commands.Count(c => !c.CommandText.StartsWith("SET QUOTED_IDENTIFIER", StringComparison.Ordinal)).ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task SetTableProtectionAsync_UpdatesTableAndClearsRedundantColumnsTransactionally(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var tableId = Guid.NewGuid();

        await store.SetTableProtectionAsync(TenantA, tableId, new ObjectProtection(ProtectionClass.Confidential, "PII"), ["email", "EMAIL", "ssn"], Actor);

        var txCmds = conn.Commands.Where(c => c.TransactionSet).ToList();
        txCmds.Count.ShouldBe(5);
        txCmds[1].CommandText.ShouldBe(GovernanceDomainStore.ClearColumnClassSql);
        txCmds[2].CommandText.ShouldBe(GovernanceDomainStore.DeleteEmptyColumnProtectionSql);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetProtectionAsync_FailsClosedToConfidentialOnUnknownClassString(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var tableId = Guid.NewGuid();
        var table = new DataTable();
        table.Columns.Add("column_name", typeof(string));
        table.Columns.Add("protection_class", typeof(string));
        table.Columns.Add("mask_category", typeof(string));
        table.Rows.Add("", "UNKNOWN_TIER", DBNull.Value);
        table.Rows.Add("ssn", DBNull.Value, "PII_SSN");
        table.Rows.Add("email", "SECRET", "PUBLIC");
        conn.EnqueueReader(table);

        var (tbl, cols) = await store.GetProtectionAsync(TenantA, tableId);

        tbl.ShouldBe(new ObjectProtection(ProtectionClass.Confidential, null));
        cols["ssn"].ShouldBe(new ObjectProtection(null, "PII_SSN"));
        cols["email"].ShouldBe(new ObjectProtection(ProtectionClass.Secret, null));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetContainerForTableAsync_FailsClosedToUnassignedWhenNoRowsExist_AndReadsContainerWhenPresent(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var tableId = Guid.NewGuid();

        var missing = await store.GetContainerForTableAsync(TenantA, tableId);
        missing.ShouldBe(ContainerDefaults.Unassigned);
        missing.IsValid.ShouldBeFalse();

        var domainId = Guid.NewGuid();
        var containerRows = new DataTable();
        containerRows.Columns.Add("id", typeof(string));
        containerRows.Columns.Add("owner_group_sid", typeof(string));
        containerRows.Columns.Add("default_class", typeof(string));
        containerRows.Columns.Add("visibility", typeof(string));
        containerRows.Rows.Add(domainId.ToString("D"), "S-1-5-21-900", "UNKNOWN_TIER", "OPEN");
        conn.EnqueueReader(containerRows);

        var resolved = await store.GetContainerForTableAsync(TenantA, tableId);
        resolved.IsValid.ShouldBeTrue();
        resolved.Container.ShouldBe(new ContainerRef(ContainerKind.Domain, domainId));
        resolved.OwnerGroupSid.ShouldBe(new Sid("S-1-5-21-900"));
        resolved.DefaultClass.ShouldBe(ProtectionClass.Confidential);
        resolved.Visibility.ShouldBe(CatalogVisibilityMode.Open);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task SetProtectionAsync_NormalizesOutOfRangeEnumToConfidential_AndDeletesWhenCleared(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var tableId = Guid.NewGuid();

        await store.SetProtectionAsync(TenantA, tableId, "", new ObjectProtection((ProtectionClass)99, "  PII_HR  "), Actor);
        var upsertCmd = conn.Commands.Last();
        upsertCmd.Parameters.ShouldContain(p => p.Name == "@protectionClass" && Equals(p.Value, "CONFIDENTIAL"));
        upsertCmd.Parameters.ShouldContain(p => p.Name == "@maskCategory" && Equals(p.Value, "PII_HR"));

        await store.SetProtectionAsync(TenantA, tableId, "", new ObjectProtection(null, null), Actor);
        conn.Commands.Last().CommandText.ShouldBe(GovernanceDomainStore.DeleteProtectionSql);
    }

    [Fact]
    public async Task AssignTablesAsync_ThrowsKeyNotFoundWhenDomainMissing_AndEffectiveProtectionMatches()
    {
        var (store, conn, _) = CreateStubDialectStore();
        conn.ScalarResult = null;
        await Should.ThrowAsync<KeyNotFoundException>(() => store.AssignTablesAsync(TenantA, Guid.NewGuid(), [Guid.NewGuid()], Actor));

        var missingContainer = await store.GetContainerForTableAsync(TenantA, Guid.NewGuid());
        var resolved = Autheris.Application.Governance.Protection.EffectiveProtectionResolver.Resolve(missingContainer, null, null, ["id"]);
        resolved.Container.ShouldBe(new ContainerRef(ContainerKind.Domain, Guid.Empty));
        resolved.TableClass.ShouldBe(ProtectionClass.Confidential);
        resolved.Visibility.ShouldBe(CatalogVisibilityMode.OnRequest);
    }

    private static (GovernanceDomainStore Store, RecordingDbConnection Conn) CreateStore(string dialectName)
    {
        var kit = DialectKit.Of(dialectName);
        var conn = kit.NewConnection();
        conn.ScalarResult = 1;
        IGovernanceSqlDialect dialect = dialectName == "SqlServer"
            ? new SqlServerGovernanceDialect(() => conn)
            : new PostgreSqlGovernanceDialect(() => conn);
        return (new GovernanceDomainStore(dialect, new FakeTimeProvider(FixedNow)), conn);
    }

    private static (GovernanceDomainStore Store, ThrowingRecordingConnection Conn, StubGovernanceDialect Dialect) CreateStubDialectStore()
    {
        var conn = new ThrowingRecordingConnection();
        var dialect = new StubGovernanceDialect(conn);
        return (new GovernanceDomainStore(dialect, new FakeTimeProvider(FixedNow)), conn, dialect);
    }

    private static DataTable DomainTable(Guid id, string name, string? defaultClass, string visibility, bool isSystem, int version)
    {
        var dt = new DataTable();
        dt.Columns.Add("id", typeof(string));
        dt.Columns.Add("name", typeof(string));
        dt.Columns.Add("description", typeof(string));
        dt.Columns.Add("contact", typeof(string));
        dt.Columns.Add("owner_group_sid", typeof(string));
        dt.Columns.Add("default_class", typeof(string));
        dt.Columns.Add("visibility", typeof(string));
        dt.Columns.Add("is_system", typeof(bool));
        dt.Columns.Add("version", typeof(int));
        dt.Columns.Add("created_at", typeof(DateTimeOffset));
        dt.Columns.Add("updated_at", typeof(DateTimeOffset));
        dt.Rows.Add(id.ToString("D"), name, DBNull.Value, DBNull.Value, DBNull.Value, (object?)defaultClass ?? DBNull.Value, visibility, isSystem, version, FixedNow, FixedNow);
        return dt;
    }

    private sealed class FakeUniqueDbException : DbException;

    private sealed class ThrowingRecordingConnection : DbConnection
    {
        private readonly Queue<DataTable> _readers = new();
        private string _connectionString = string.Empty;
        public int NonQueryResult { get; set; } = 1;
        public object? ScalarResult { get; set; }
        public DbException? ThrowOnNonQuery { get; set; }
        public void EnqueueReader(DataTable dt) => _readers.Enqueue(dt);
        internal DataTable? DequeueReader() => _readers.Count > 0 ? _readers.Dequeue() : null;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => _connectionString; set => _connectionString = value ?? string.Empty; }
        public override string Database => "stub";
        public override string DataSource => "stub";
        public override string ServerVersion => "0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new StubTransaction(this);
        protected override DbCommand CreateDbCommand() => new StubCommand(this);

        private sealed class StubTransaction(DbConnection owner) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            protected override DbConnection DbConnection => owner;
            public override void Commit() { }
            public override void Rollback() { }
        }

        private sealed class StubCommand(ThrowingRecordingConnection owner) : DbCommand
        {
            private readonly FakeDbParameterCollection _params = new();
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get => owner; set { } }
            protected override DbParameterCollection DbParameterCollection => _params;
            protected override DbTransaction? DbTransaction { get; set; }
            public override void Cancel() { }
            public override int ExecuteNonQuery() => owner.ThrowOnNonQuery is { } ex ? throw ex : owner.NonQueryResult;
            public override object? ExecuteScalar() => owner.ScalarResult;
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => new FakeDbParameter();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => (owner.DequeueReader() ?? new DataTable()).CreateDataReader();
        }
    }

    private sealed class StubGovernanceDialect(DbConnection conn) : IGovernanceSqlDialect
    {
        public bool UniqueViolation { get; set; }
        public string Upsert(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> valueColumns) => string.Empty;
        public string UpsertAccumulate(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> setOnInsertColumns, ReadOnlySpan<string> counterColumns) => string.Empty;
        public string InsertReturning(string column) => string.Empty;
        public string LockRowsForUpdate => string.Empty;
        public string UtcNow => string.Empty;
        public string JsonValue(string column, string path) => string.Empty;
        public string StringAggregate(string expr, string separator) => string.Empty;
        public string LimitWithoutOrder(int n) => string.Empty;
        public bool IsUniqueViolation(DbException ex) => UniqueViolation;
        public bool IsTransient(DbException ex) => false;
        public IParameterBudgetProvider ParameterBudget => DatabaseParameterBudgetProvider.Instance;
        public DbConnection CreateConnection() => conn;
    }
}
