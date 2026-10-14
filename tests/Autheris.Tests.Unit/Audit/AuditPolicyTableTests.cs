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

    [Fact]
    public void RebacEvents_HaveExplicitPolicy()
    {
        AuditPolicyTable.All.ShouldContainKey(AuditEventTypes.RebacTupleAdded);
        AuditPolicyTable.All.ShouldContainKey(AuditEventTypes.RebacTupleRemoved);
        AuditPolicyTable.All.ShouldContainKey(AuditEventTypes.RebacInvalidationFailed);
        AuditPolicyTable.All.ShouldContainKey(AuditEventTypes.RebacTupleSeed);
        AuditPolicyTable.For(AuditEventTypes.RebacTupleAdded).ShouldBe(AuditFailurePolicy.FailClosed);
        AuditPolicyTable.For(AuditEventTypes.RebacTupleRemoved).ShouldBe(AuditFailurePolicy.FailClosed);
        AuditPolicyTable.For(AuditEventTypes.RebacInvalidationFailed).ShouldBe(AuditFailurePolicy.FailClosed);
        AuditPolicyTable.For(AuditEventTypes.RebacTupleSeed).ShouldBe(AuditFailurePolicy.BestEffort);
        AuditEventTypes.RebacTupleAdded.ShouldBe("REBAC_TUPLE_ADDED");
        AuditEventTypes.RebacTupleRemoved.ShouldBe("REBAC_TUPLE_REMOVED");
        AuditEventTypes.RebacInvalidationFailed.ShouldBe("REBAC_INVALIDATION_FAILED");
        AuditEventTypes.RebacTupleSeed.ShouldBe("REBAC_TUPLE_SEED");
    }
}
