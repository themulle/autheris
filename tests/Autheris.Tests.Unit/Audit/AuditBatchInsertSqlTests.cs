using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>WP-PF-04: the shared multi-row audit insert is bounded and well-formed on every engine.</summary>
public sealed class AuditBatchInsertSqlTests
{
    private static string Build(int rows, string? table = null)
    {
        var type = typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly.GetType("Autheris.Infrastructure.Persistence.AuditBatchInsertSql")!;
        var method = type.GetMethod("Build", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        try
        {
            return (string)method.Invoke(null, [rows, table ?? "AUDIT_LOG_ENTRIES"])!;
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            throw ex.InnerException!;
        }
    }

    [Fact]
    public void Build_OneRow_HasThirteenNamedParameters()
    {
        var sql = Build(1);

        sql.ShouldStartWith("INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type,");
        sql.ShouldContain("VALUES (@id0, @occ0, @event0, @actor0, @target0, @col0, @dec0, @trace0, @det0, @prev0, @hash0, @tenantId0, @seq0)");
        sql.Count(c => c == '@').ShouldBe(13);
    }

    [Fact]
    public void Build_MaxRows_StaysBelowTheSqlServerParameterLimit()
    {
        var sql = Build(100);

        sql.Count(c => c == '@').ShouldBe(1300);
        (sql.Count(c => c == '@') < 2100).ShouldBeTrue();
        sql.ShouldContain("@seq99)");
    }

    [Fact]
    public void Build_UsesTheQualifiedTableName() =>
        Build(2, "dbo.AUDIT_LOG_ENTRIES").ShouldStartWith("INSERT INTO dbo.AUDIT_LOG_ENTRIES (");

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Build_OutOfRange_Throws(int rows) => Should.Throw<ArgumentOutOfRangeException>(() => Build(rows));
}
