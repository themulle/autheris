namespace Autheris.Tests.Unit.Sql;

using Autheris.Application.Sql.Policy;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>D-WPC-1: the expansion factor the key-set stage budgets with is the one the compile request carries.</summary>
public sealed class CompileRequestFactoryExpansionTests
{
    [Fact]
    public void DefaultMaxExpansionFactor_IsTheEngineDefaultAndWithinTheClamp()
    {
        var engineDefault = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.Sqlite,
            Policy = null!,
            TokenGuards = new SqlTokenSecurityOptions()
        }.MaxExpansionFactor;

        CompileRequestFactory.DefaultMaxExpansionFactor.ShouldBe(engineDefault);
        CompileRequestFactory.DefaultMaxExpansionFactor.ShouldBeLessThanOrEqualTo(CompileLimits.MaxExpansionFactorLimit);
    }
}
