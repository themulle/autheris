namespace Autheris.Tests.Unit.Sql;

using System.Reflection;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Shouldly;
using Xunit;

/// <summary>L-A3 (later item): the provider execution guard has no silent pass-through fallback in the service either.</summary>
public sealed class GovernedSqlExecutionServiceConstructionTests
{
    [Fact]
    public void EveryPublicConstructor_RequiresTheProviderExecutionGuard()
    {
        var constructors = typeof(GovernedSqlExecutionService).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        constructors.ShouldNotBeEmpty();

        foreach (var constructor in constructors)
        {
            var guard = constructor.GetParameters().SingleOrDefault(p => p.ParameterType == typeof(IProviderExecutionGuard));
            guard.ShouldNotBeNull();
            guard.IsOptional.ShouldBeFalse("a missing guard must not turn into PassThroughExecutionGuard (SQLite statements would run without the timeout interrupt)");
            guard.HasDefaultValue.ShouldBeFalse();
        }
    }

    [Fact]
    public void ANullGuard_IsRejected()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions());

        Should.Throw<ArgumentNullException>(() => new GovernedSqlExecutionService(options, global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, executionGuard: null!, policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance))
            .ParamName.ShouldBe("executionGuard");
    }
}
