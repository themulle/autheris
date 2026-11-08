using System;
using System.Reflection;
using Autheris.Infrastructure.Persistence.Dialects;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class SqlServerGovernanceDialectTests
{
    private readonly SqlServerGovernanceDialect _dialect = new();

    [Fact]
    public void Upsert_emits_merge_with_holdlock()
    {
        string[] keys = ["table_id"];
        string[] values = ["epoch", "updated_at"];

        var sql = _dialect.Upsert(GovernanceTable.PolicyEpochs, keys, values);

        sql.ShouldStartWith("MERGE INTO [POLICY_EPOCHS] WITH (HOLDLOCK) AS target");
        sql.ShouldContain("USING (VALUES (@table_id, @epoch, @updated_at)) AS source");
        sql.ShouldContain("ON (target.[table_id] = source.[table_id])");
        sql.ShouldContain("WHEN MATCHED THEN UPDATE SET target.[epoch] = source.[epoch], target.[updated_at] = source.[updated_at]");
        sql.ShouldContain("WHEN NOT MATCHED THEN INSERT ([table_id], [epoch], [updated_at]) VALUES (source.[table_id], source.[epoch], source.[updated_at]);");
    }

    [Fact]
    public void Members_conform_to_IGovernanceSqlDialect_contract()
    {
        var interfaceType = typeof(IGovernanceSqlDialect);
        var methods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var properties = interfaceType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var totalMembers = methods.Length + properties.Length;
        totalMembers.ShouldBeLessThanOrEqualTo(15, "IGovernanceSqlDialect must stay bounded (<= 15 members)");
    }

    [Fact]
    public void Dialect_specific_fragments()
    {
        _dialect.LockRowsForUpdate.ShouldBe("WITH (UPDLOCK, ROWLOCK)");
        _dialect.UtcNow.ShouldBe("SYSUTCDATETIME()");
        _dialect.InsertReturning("id").ShouldBe("OUTPUT inserted.[id]");
        _dialect.LimitWithoutOrder(10).ShouldBe("ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY");
        _dialect.StringAggregate("role_name", ",").ShouldBe("STRING_AGG(role_name, ',')");
        _dialect.JsonValue("details", "appId").ShouldBe("JSON_VALUE([details], '$.appId')");
        _dialect.ParameterBudget.GetMaxParameters(Autheris.Domain.Common.DatabaseDialect.SqlServer).ShouldBe(2100);
    }

    [Fact]
    public void CreateConnection_returns_QuotedIdentifierSqlConnection_when_connection_string_provided()
    {
        var dialect = new SqlServerGovernanceDialect("Server=localhost;Database=test;Trusted_Connection=True;");
        using var conn = dialect.CreateConnection();
        conn.ShouldNotBeNull();
        conn.GetType().Name.ShouldBe("QuotedIdentifierSqlConnection");
    }
}
