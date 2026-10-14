using System.Reflection;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

public sealed class AuditPolicyTableTests
{
    [Fact]
    public void EveryAuditEventType_HasAnExplicitPolicy()
    {
        var constants = typeof(AuditEventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        constants.ShouldNotBeEmpty();
        foreach (var eventType in constants)
        {
            AuditPolicyTable.All.ContainsKey(eventType).ShouldBeTrue($"AuditPolicyTable has no explicit policy for {eventType}.");
        }
    }

    [Fact]
    public void UnknownEventType_DefaultsToFailClosed()
    {
        AuditPolicyTable.For("SOMETHING_NEW").ShouldBe(AuditFailurePolicy.FailClosed);
        AuditPolicyTable.For(null).ShouldBe(AuditFailurePolicy.FailClosed);
    }

    [Fact]
    public void DataAccessAndAdminEvents_AreFailClosed()
    {
        AuditPolicyTable.For(AuditEventTypes.TableQuery).ShouldBe(AuditFailurePolicy.FailClosed);
        AuditPolicyTable.For(AuditEventTypes.AuditConfigChanged).ShouldBe(AuditFailurePolicy.FailClosed);
        AuditPolicyTable.For(AuditEventTypes.TenantCollisionDenied).ShouldBe(AuditFailurePolicy.FailClosed);
    }
}
