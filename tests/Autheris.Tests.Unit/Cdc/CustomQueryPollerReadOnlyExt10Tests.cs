namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Extensions.Cdc.CustomQuery;
using Shouldly;
using Xunit;

/// <summary>EXT-10: the PostgreSQL poll runs inside an explicit READ ONLY transaction (the bare SET TRANSACTION was a no-op).</summary>
[Trait("Category", "Security")]
public sealed class CustomQueryPollerReadOnlyExt10Tests
{
    private sealed class FakeNpgsqlConnection(List<string> log, bool failReadOnly = false) : DbConnection
    {
        public override string ConnectionString { get; set; } = "Host=db;Database=x";
        public override string Database => "x";
        public override string DataSource => "db";
        public override string ServerVersion => "16";
        public override ConnectionState State => ConnectionState.Open;
        public bool FailReadOnly { get; } = failReadOnly;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            log.Add("BEGIN");
            return new FakeTransaction(this, log);
        }

        protected override DbCommand CreateDbCommand() => new FakeCommand(this, log);
    }

    private sealed class FakeTransaction(DbConnection connection, List<string> log) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public bool Disposed { get; private set; }
        public override void Commit() => log.Add("COMMIT");
        public override void Rollback() => log.Add("ROLLBACK");

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FakeCommand(FakeNpgsqlConnection connection, List<string> log) : DbCommand
    {
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get => connection; set { } }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        public override object? ExecuteScalar() => null;

        public override int ExecuteNonQuery()
        {
            log.Add($"EXEC {CommandText} tx={(DbTransaction != null ? "yes" : "no")}");
            if (connection.FailReadOnly)
            {
                throw new InvalidOperationException("cannot set read only");
            }

            return 0;
        }
    }

    [Fact]
    public async Task Postgres_BeginsATransaction_SetsItReadOnly_AndAttachesItToThePollCommand()
    {
        var log = new List<string>();
        var conn = new FakeNpgsqlConnection(log);
        var cmd = conn.CreateCommand();

        var tx = await CustomQueryPollerService.EnforceReadOnlySemanticsAsync(conn, cmd, CancellationToken.None);

        tx.ShouldNotBeNull();
        log.ShouldBe(["BEGIN", "EXEC SET TRANSACTION READ ONLY; tx=yes"]);
        cmd.Transaction.ShouldBeSameAs(tx);
    }

    [Fact]
    public async Task Postgres_WhenReadOnlyCannotBeEstablished_FailsClosed_AndReleasesTheTransaction()
    {
        var log = new List<string>();
        var conn = new FakeNpgsqlConnection(log, failReadOnly: true);
        var cmd = conn.CreateCommand();

        await Should.ThrowAsync<InvalidOperationException>(() => CustomQueryPollerService.EnforceReadOnlySemanticsAsync(conn, cmd, CancellationToken.None));

        cmd.Transaction.ShouldBeNull();
    }
}
