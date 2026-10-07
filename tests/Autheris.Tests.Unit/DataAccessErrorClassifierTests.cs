using System.ComponentModel;
using System.Data.Common;
using Autheris.Application.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// O7 (docs/plans/rls-subquery-in-strategy.md, OData-Härtung): database errors are classified by provider error numbers,
/// SQLSTATE and IsTransient, not by searching the message text.
/// </summary>
public sealed class DataAccessErrorClassifierTests
{
    /// <summary>Shape of Microsoft.Data.SqlClient.SqlException: an int Number property.</summary>
    private sealed class FakeSqlException(int number, string message = "error") : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed class FakePostgresException(string sqlState) : DbException("error")
    {
        public override string? SqlState { get; } = sqlState;
    }

    private sealed class FakeTransientException() : DbException("error")
    {
        public override bool IsTransient => true;
    }

    [Theory]
    [InlineData(-2)]     // SqlClient: Execution Timeout Expired
    public void SqlServerTimeout_IsTimeout(int number)
    {
        DataAccessErrorClassifier.Classify(new FakeSqlException(number)).ShouldBe(DataAccessErrorKind.Timeout);
    }

    [Theory]
    [InlineData(1205)]   // deadlock victim
    [InlineData(4060)]   // cannot open database
    [InlineData(40613)]  // database unavailable
    [InlineData(40501)]  // service busy
    [InlineData(40197)]  // service error processing request
    [InlineData(10928)]  // resource limit
    [InlineData(10929)]
    [InlineData(233)]    // connection broken
    [InlineData(53)]     // network path not found
    [InlineData(-1)]     // connection error
    public void SqlServerTransientErrors_AreUnavailable(int number)
    {
        DataAccessErrorClassifier.Classify(new FakeSqlException(number)).ShouldBe(DataAccessErrorKind.Unavailable);
    }

    [Fact]
    public void SqlServerSyntaxError_IsOther()
    {
        DataAccessErrorClassifier.Classify(new FakeSqlException(102, "Incorrect syntax near 'Timeout'")).ShouldBe(DataAccessErrorKind.Other);
    }

    [Fact]
    public void PostgresStatementTimeout_IsTimeout()
    {
        DataAccessErrorClassifier.Classify(new FakePostgresException("57014")).ShouldBe(DataAccessErrorKind.Timeout);
    }

    [Theory]
    [InlineData("40P01")] // deadlock
    [InlineData("08006")] // connection failure
    [InlineData("53300")] // too many connections
    [InlineData("57P03")] // cannot connect now
    public void PostgresTransientStates_AreUnavailable(string sqlState)
    {
        DataAccessErrorClassifier.Classify(new FakePostgresException(sqlState)).ShouldBe(DataAccessErrorKind.Unavailable);
    }

    [Fact]
    public void IsTransient_IsUnavailable()
    {
        DataAccessErrorClassifier.Classify(new FakeTransientException()).ShouldBe(DataAccessErrorKind.Unavailable);
    }

    [Fact]
    public void TimeoutException_AndWin32Timeout_AreTimeout()
    {
        DataAccessErrorClassifier.Classify(new TimeoutException()).ShouldBe(DataAccessErrorKind.Timeout);
        DataAccessErrorClassifier.Classify(new InvalidOperationException("wrapped", new Win32Exception(258))).ShouldBe(DataAccessErrorKind.Timeout);
    }

    [Fact]
    public void ConnectionPoolExhausted_IsUnavailable()
    {
        var ex = new InvalidOperationException(
            "Timeout expired.  The timeout period elapsed prior to obtaining a connection from the pool.  This may have occurred because all pooled connections were in use and max pool size was reached.");

        DataAccessErrorClassifier.Classify(ex).ShouldBe(DataAccessErrorKind.Unavailable);
    }

    [Fact]
    public void NestedTimeout_IsFoundInInnerExceptions()
    {
        var ex = new InvalidOperationException("outer", new AggregateException(new FakeSqlException(-2)));

        DataAccessErrorClassifier.Classify(ex).ShouldBe(DataAccessErrorKind.Timeout);
    }

    [Fact]
    public void UnrelatedException_IsOther()
    {
        DataAccessErrorClassifier.Classify(new InvalidOperationException("Fatal unexpected engine crash")).ShouldBe(DataAccessErrorKind.Other);
    }
}
