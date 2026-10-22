using System.Data.Common;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>Issue #277 (SEC-277-06): the recursion bounds of a recursive CTE map to a fixed, value-free limit error.</summary>
[Trait("Category", "SqlGate")]
public class DmlErrorSanitizerRecursionTests
{
    private const string DriverText = "The statement terminated. The maximum recursion 100 has been exhausted before statement completion (dbo.Secrets.Ssn)";

    private sealed class NumberedDbException(int number, string message, IReadOnlyList<object>? errors = null) : DbException(message)
    {
        public int Number { get; } = number;

        public IReadOnlyList<object> Errors { get; } = errors ?? [];
    }

    private sealed record ErrorItem(int Number);

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, 530)]
    [InlineData(TargetSqlDialect.Oracle, 32044)]
    public void ProviderError_530_32044_ResponseAndAuditContainNoDriverText(TargetSqlDialect dialect, int number)
    {
        var inner = new NumberedDbException(number, DriverText);
        var mapped = DmlErrorSanitizer.Map(dialect, new InvalidOperationException("wrapper " + DriverText, inner));

        Assert.Equal(GovernedSqlErrorCodes.LimitExceeded, mapped.Code);
        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.LimitExceeded), mapped.Message);
        Assert.Null(mapped.InnerException);
        Assert.DoesNotContain("Secrets", mapped.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("recursion", mapped.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_SqlServer530_IsLimitExceeded()
    {
        var mapped = DmlErrorSanitizer.Map(TargetSqlDialect.SqlServer, new NumberedDbException(530, DriverText));
        Assert.Equal(GovernedSqlErrorCodes.LimitExceeded, mapped.Code);
        Assert.Equal(TargetSqlDialect.SqlServer, mapped.Dialect);
    }

    [Fact]
    public void Map_SqlServer530_InTheErrorsCollection_IsLimitExceeded()
    {
        // The first error of a batch can be another number (for example an info message); the collection is checked as well.
        var error = new NumberedDbException(0, DriverText, [new ErrorItem(0), new ErrorItem(530)]);
        Assert.Equal(GovernedSqlErrorCodes.LimitExceeded, DmlErrorSanitizer.Map(TargetSqlDialect.SqlServer, error).Code);
    }

    [Fact]
    public void Map_Oracle32044_IsLimitExceeded()
    {
        var mapped = DmlErrorSanitizer.Map(TargetSqlDialect.Oracle, new NumberedDbException(32044, DriverText));
        Assert.Equal(GovernedSqlErrorCodes.LimitExceeded, mapped.Code);
        Assert.Equal(TargetSqlDialect.Oracle, mapped.Dialect);
    }

    [Fact]
    public void Map_OtherNumbers_StayProviderError()
    {
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, DmlErrorSanitizer.Map(TargetSqlDialect.SqlServer, new NumberedDbException(531, DriverText)).Code);
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, DmlErrorSanitizer.Map(TargetSqlDialect.Oracle, new NumberedDbException(32043, DriverText)).Code);

        // The numbers are dialect specific: 530 on Oracle and 32044 on SQL Server are not the recursion bound.
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, DmlErrorSanitizer.Map(TargetSqlDialect.Oracle, new NumberedDbException(530, DriverText)).Code);
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, DmlErrorSanitizer.Map(TargetSqlDialect.SqlServer, new NumberedDbException(32044, DriverText)).Code);
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, DmlErrorSanitizer.Map(TargetSqlDialect.PostgreSql, new NumberedDbException(530, DriverText)).Code);
    }

    [Fact]
    public void Map_ConstraintNumbers_KeepTheirCategory()
    {
        Assert.Equal(GovernedSqlErrorCodes.ConstraintViolation, DmlErrorSanitizer.Map(TargetSqlDialect.SqlServer, new NumberedDbException(2627, DriverText)).Code);
    }
}
