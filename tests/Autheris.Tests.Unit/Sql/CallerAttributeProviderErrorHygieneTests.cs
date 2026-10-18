namespace Autheris.Tests.Unit.Sql;

using System.Collections.Generic;
using System.Collections.Immutable;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Sql.Services;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>BC-129-16 (PLAN-SQL-128 SEC-128-07 verbatim): a driver error of a statement that binds a caller attribute value never logs the exception text.</summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeProviderErrorHygieneTests
{
    private static CompiledSql Compiled(params BoundParameter[] parameters) =>
        new("SELECT 1", [.. parameters], TargetSqlDialect.Sqlite, SqlStatementClass.Select, [], "test");

    [Fact]
    public void BC_129_16_ABoundCallerAttributeValue_SuppressesTheDriverText()
    {
        var bind = new BoundParameter("@p0", "p0", 0, "secret-attribute-value", SqlParameterType.String, ParameterOrigin.Policy, CallerAttributeParameterName.Format("cost_center", 0));
        GovernedSqlExecutor.BindsValueFromTheCaller(Compiled(bind), new Dictionary<string, object?>()).ShouldBeTrue();
    }

    [Fact]
    public void BC_129_16_AStatementWithoutCallerValuesOrLists_KeepsLoggingTheDriverError()
    {
        var tenant = new BoundParameter("@p0", "p0", 0, "tenant_a", SqlParameterType.String, ParameterOrigin.Tenant, "__tenant");
        GovernedSqlExecutor.BindsValueFromTheCaller(Compiled(tenant), new Dictionary<string, object?>()).ShouldBeFalse();
    }
}
