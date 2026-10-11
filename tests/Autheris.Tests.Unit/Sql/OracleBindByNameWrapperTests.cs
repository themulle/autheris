namespace Autheris.Tests.Unit.Sql;

using System.Data.Common;
using System.Reflection;
using System.Security;
using Autheris.Infrastructure.Persistence;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Xunit;

/// <summary>CR-ADG-07: no consumer of the connection factory can obtain an Oracle command that binds by position.</summary>
public sealed class OracleBindByNameWrapperTests
{
    private static BindByNameOracleConnection Connection() => new(new OracleConnection("User Id=u;Password=p;Data Source=localhost:1521/none"));

    private static bool BindByNameOf(DbCommand command) =>
        (bool)command.GetType().GetProperty("BindByName")!.GetValue(command)!;

    private static OracleCommand InnerOf(DbCommand command) =>
        (OracleCommand)command.GetType().GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(command)!;

    [Fact]
    public void EveryCommandOfTheFactoryConnection_IsCreatedWithBindByName()
    {
        using var connection = Connection();
        using var command = connection.CreateCommand();
        BindByNameOf(command).ShouldBeTrue();
        InnerOf(command).BindByName.ShouldBeTrue();
    }

    [Fact]
    public void BindByName_CannotBeDisabled()
    {
        using var connection = Connection();
        using var command = connection.CreateCommand();
        var property = command.GetType().GetProperty("BindByName")!;
        var ex = Should.Throw<TargetInvocationException>(() => property.SetValue(command, false));
        ex.InnerException.ShouldBeOfType<SecurityException>();
        BindByNameOf(command).ShouldBeTrue();
    }

    [Fact]
    public async Task EveryExecuteMethod_AssertsBindByName_BeforeTouchingTheDatabase()
    {
        using var connection = Connection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM DUAL";
        InnerOf(command).BindByName = false;   // simulate a driver-level regression; nothing in the gateway can do this

        Should.Throw<SecurityException>(() => command.ExecuteNonQuery());
        Should.Throw<SecurityException>(() => command.ExecuteScalar());
        Should.Throw<SecurityException>(() => command.ExecuteReader());
        Should.Throw<SecurityException>(() => command.Prepare());
        await Should.ThrowAsync<SecurityException>(() => command.ExecuteNonQueryAsync());
        await Should.ThrowAsync<SecurityException>(() => command.ExecuteScalarAsync());
        await Should.ThrowAsync<SecurityException>(() => command.ExecuteReaderAsync());
    }

    [Fact]
    public void ACommandCannotBeMovedToAnUnwrappedConnection()
    {
        using var connection = Connection();
        using var command = connection.CreateCommand();
        using var raw = new OracleConnection("User Id=u;Password=p;Data Source=localhost:1521/none");
        Should.Throw<SecurityException>(() => command.Connection = raw);
    }

    [Fact]
    public void SqlConnectionFactory_OnlyHandsOutWrappedOracleConnections()
    {
        // The factory source of truth: the raw OracleConnection is constructed inside the wrapper expression only.
        string factory = File.ReadAllText(FindFile("src/Autheris.Infrastructure/Persistence/SqlConnectionFactory.cs"));
        factory.ShouldContain("new BindByNameOracleConnection(new OracleConnection(");
        factory.ShouldNotContain("=> new OracleConnection(");
    }

    private static string FindFile(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, relative))) dir = Path.GetDirectoryName(dir);
        return dir is null ? throw new FileNotFoundException(relative) : Path.Combine(dir, relative);
    }

    /// <summary>CR-ADG-28: a transaction of the factory connection must not hand out the unwrapped connection.</summary>
    private sealed class FakeTransaction : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;

        protected override DbConnection? DbConnection => null;

        public override void Commit() { }

        public override void Rollback() { }
    }

    [Fact]
    public void Transaction_Connection_IsTheWrapper_AndItsCommandsBindByName()
    {
        using var connection = Connection();
        using var inner = new FakeTransaction();
        using var transaction = new BindByNameOracleTransaction(inner, connection);

        transaction.Connection.ShouldBeSameAs(connection);
        using var command = transaction.Connection!.CreateCommand();
        BindByNameOf(command).ShouldBeTrue();
        command.ShouldBeOfType<BindByNameOracleCommand>();
    }

    [Fact]
    public void Command_Transaction_RoundTripsTheWrapper_NeverTheRawTransaction()
    {
        using var connection = Connection();
        using var command = connection.CreateCommand();
        using var raw = new FakeTransaction();
        // A foreign transaction cannot be attached: only the gateway's own wrapper is accepted.
        Should.Throw<SecurityException>(() => command.Transaction = raw);
    }
}
