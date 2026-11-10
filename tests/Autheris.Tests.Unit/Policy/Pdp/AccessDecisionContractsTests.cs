namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AccessDecisionContractsTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Enums_HaveExpectedMemberCountsAndNames()
    {
        // AccessOperation
        var operations = Enum.GetNames<AccessOperation>();
        operations.Length.ShouldBe(6);
        operations.ShouldBe(["Read", "Insert", "Update", "Delete", "Execute", "Discover"]);

        // AccessTransport
        var transports = Enum.GetNames<AccessTransport>();
        transports.Length.ShouldBe(16);
        transports.ShouldBe([
            "GraphQl", "Rest", "OData", "WebSql", "Trino", "Mcp",
            "Procedure", "Analytics", "Olap", "Federation", "ArrowExport",
            "Streaming", "Lakehouse", "EnvoyExtAuthz", "Discovery", "Internal"
        ]);

        // DecisionStep
        var steps = Enum.GetNames<DecisionStep>();
        steps.Length.ShouldBe(5);
        steps.ShouldBe(["Precondition", "ExplicitDeny", "Grant", "Narrowing", "DefaultDeny"]);

        // GrantSourceKind
        var sources = Enum.GetNames<GrantSourceKind>();
        sources.Length.ShouldBe(6);
        sources.ShouldBe(["ConsentRequest", "AccessProfile", "Owner", "Bulk", "Declarative", "AccessPlan"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DecisionReason_Defaults_Properties_Immutability_AndSerialization()
    {
        // Defaults
        var defaultReason = new DecisionReason(DecisionStep.Precondition, "IDENTITY_INCOMPLETE", null, null);
        defaultReason.Step.ShouldBe(DecisionStep.Precondition);
        defaultReason.Code.ShouldBe("IDENTITY_INCOMPLETE");
        defaultReason.SourceKind.ShouldBeNull();
        defaultReason.SourceId.ShouldBeNull();
        defaultReason.Column.ShouldBeNull();

        // Parameterized
        var reasonWithColumn = new DecisionReason(DecisionStep.ExplicitDeny, "COL_DENY", "Grant", "grant-1", "ssn");
        reasonWithColumn.Step.ShouldBe(DecisionStep.ExplicitDeny);
        reasonWithColumn.Code.ShouldBe("COL_DENY");
        reasonWithColumn.SourceKind.ShouldBe("Grant");
        reasonWithColumn.SourceId.ShouldBe("grant-1");
        reasonWithColumn.Column.ShouldBe("ssn");

        // Value equality
        var identical = new DecisionReason(DecisionStep.ExplicitDeny, "COL_DENY", "Grant", "grant-1", "ssn");
        reasonWithColumn.ShouldBe(identical);
        (reasonWithColumn == identical).ShouldBeTrue();

        // Immutability
        typeof(DecisionReason).IsValueType.ShouldBeTrue();
        foreach (var prop in typeof(DecisionReason).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            prop.CanRead.ShouldBeTrue();
            (prop.SetMethod == null || prop.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit))).ShouldBeTrue();
        }

        // Serialization
        var json = JsonSerializer.Serialize(reasonWithColumn);
        var deserialized = JsonSerializer.Deserialize<DecisionReason>(json);
        deserialized.ShouldBe(reasonWithColumn);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DecisionGrantRef_Defaults_Properties_Immutability_AndSerialization()
    {
        var validFrom = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

        // Default ValidUntil
        var grantDefault = new DecisionGrantRef("grant-001", GrantSourceKind.ConsentRequest, validFrom);
        grantDefault.GrantId.ShouldBe("grant-001");
        grantDefault.Source.ShouldBe(GrantSourceKind.ConsentRequest);
        grantDefault.ValidFrom.ShouldBe(validFrom);
        grantDefault.ValidUntil.ShouldBeNull();

        // Parameterized ValidUntil
        var validUntil = validFrom.AddHours(4);
        var grantWithUntil = new DecisionGrantRef("grant-002", GrantSourceKind.Owner, validFrom, validUntil);
        grantWithUntil.GrantId.ShouldBe("grant-002");
        grantWithUntil.Source.ShouldBe(GrantSourceKind.Owner);
        grantWithUntil.ValidFrom.ShouldBe(validFrom);
        grantWithUntil.ValidUntil.ShouldBe(validUntil);

        // Value equality
        var same = new DecisionGrantRef("grant-002", GrantSourceKind.Owner, validFrom, validUntil);
        grantWithUntil.ShouldBe(same);
        (grantWithUntil == same).ShouldBeTrue();

        // Immutability
        typeof(DecisionGrantRef).IsValueType.ShouldBeTrue();
        foreach (var prop in typeof(DecisionGrantRef).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            prop.CanRead.ShouldBeTrue();
            (prop.SetMethod == null || prop.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit))).ShouldBeTrue();
        }

        // Serialization
        var json = JsonSerializer.Serialize(grantWithUntil);
        var deserialized = JsonSerializer.Deserialize<DecisionGrantRef>(json);
        deserialized.ShouldBe(grantWithUntil);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AccessRequest_Defaults_Properties_AndImmutability()
    {
        var subject = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-USER"),
            ResolvedTenant = new TenantId("tenant-a"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        var table = new TableMetadata { Identifier = new TableIdentifier("sales", "crm", "orders") };

        // Defaults
        var request = new AccessRequest(subject, table, AccessOperation.Read, AccessTransport.Rest);
        request.Subject.ShouldBe(subject);
        request.Object.ShouldBe(table);
        request.Operation.ShouldBe(AccessOperation.Read);
        request.Transport.ShouldBe(AccessTransport.Rest);
        request.Columns.ShouldBeNull();
        request.SupplementalConsents.ShouldBeNull();
        request.Write.ShouldBeNull();
        request.Trace.ShouldBeFalse();

        // Explicit values
        var write = new WriteGrantQuery();
        var columns = new List<string> { "id", "amount" };
        var explicitRequest = new AccessRequest(
            subject,
            table,
            AccessOperation.Insert,
            AccessTransport.GraphQl,
            columns,
            [],
            write,
            Trace: true);

        explicitRequest.Operation.ShouldBe(AccessOperation.Insert);
        explicitRequest.Transport.ShouldBe(AccessTransport.GraphQl);
        explicitRequest.Columns.ShouldBe(columns);
        explicitRequest.SupplementalConsents.ShouldBeEmpty();
        explicitRequest.Write.ShouldBe(write);
        explicitRequest.Trace.ShouldBeTrue();

        // Immutability: with copy creates distinct instance
        var traced = request with { Trace = true };
        traced.Trace.ShouldBeTrue();
        request.Trace.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void IAccessDecisionPoint_Interface_ContractVerification()
    {
        var iface = typeof(IAccessDecisionPoint);
        iface.IsInterface.ShouldBeTrue();

        // DecideAsync
        var decideMethod = iface.GetMethod(nameof(IAccessDecisionPoint.DecideAsync));
        decideMethod.ShouldNotBeNull();
        decideMethod.ReturnType.ShouldBe(typeof(ValueTask<TableAccessDecision>));
        var decideParams = decideMethod.GetParameters();
        decideParams.Length.ShouldBe(2);
        decideParams[0].ParameterType.ShouldBe(typeof(AccessRequest));
        decideParams[1].ParameterType.ShouldBe(typeof(CancellationToken));

        // DecideManyAsync
        var decideManyMethod = iface.GetMethod(nameof(IAccessDecisionPoint.DecideManyAsync));
        decideManyMethod.ShouldNotBeNull();
        decideManyMethod.ReturnType.ShouldBe(typeof(ValueTask<IReadOnlyList<TableAccessDecision>>));
        var manyParams = decideManyMethod.GetParameters();
        manyParams.Length.ShouldBe(5);
        manyParams[0].ParameterType.ShouldBe(typeof(SecurityPrincipalContext));
        manyParams[1].ParameterType.ShouldBe(typeof(IReadOnlyList<TableMetadata>));
        manyParams[2].ParameterType.ShouldBe(typeof(AccessOperation));
        manyParams[3].ParameterType.ShouldBe(typeof(AccessTransport));
        manyParams[4].ParameterType.ShouldBe(typeof(CancellationToken));

        // ExplainAsync
        var explainMethod = iface.GetMethod(nameof(IAccessDecisionPoint.ExplainAsync));
        explainMethod.ShouldNotBeNull();
        explainMethod.ReturnType.ShouldBe(typeof(ValueTask<AccessExplanation>));
        var explainParams = explainMethod.GetParameters();
        explainParams.Length.ShouldBe(2);
        explainParams[0].ParameterType.ShouldBe(typeof(AccessRequest));
        explainParams[1].ParameterType.ShouldBe(typeof(CancellationToken));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ExplanationRecords_Defaults_Immutability_AndSerialization()
    {
        // ColumnStateExplanation defaults and serialization
        var colDefault = new ColumnStateExplanation("email", ColumnAccessLevel.Mask);
        colDefault.ColumnName.ShouldBe("email");
        colDefault.Level.ShouldBe(ColumnAccessLevel.Mask);
        colDefault.MaskType.ShouldBeNull();

        var colWithMask = new ColumnStateExplanation("ssn", ColumnAccessLevel.Mask, "hash-sha256");
        colWithMask.MaskType.ShouldBe("hash-sha256");
        var colJson = JsonSerializer.Serialize(colWithMask);
        var colRoundtrip = JsonSerializer.Deserialize<ColumnStateExplanation>(colJson);
        colRoundtrip.ShouldBe(colWithMask);

        // EffectiveGroupsExplanation
        var groups = new EffectiveGroupsExplanation(["S-1-5-21-G1", "S-1-5-21-G2"], "Token", "Resolved");
        groups.GroupSids.ShouldBe(["S-1-5-21-G1", "S-1-5-21-G2"]);
        groups.Source.ShouldBe("Token");
        groups.Status.ShouldBe("Resolved");
        var groupsJson = JsonSerializer.Serialize(groups);
        var groupsRoundtrip = JsonSerializer.Deserialize<EffectiveGroupsExplanation>(groupsJson);
        groupsRoundtrip.ShouldNotBeNull();
        groupsRoundtrip.GroupSids.ShouldBe(groups.GroupSids);
        groupsRoundtrip.Source.ShouldBe(groups.Source);
        groupsRoundtrip.Status.ShouldBe(groups.Status);

        // AccessExplanation
        var reason = new DecisionReason(DecisionStep.Grant, "GRANT_ALLOW", "Grant", "grant-1");
        var grantRef = new DecisionGrantRef("grant-1", GrantSourceKind.ConsentRequest, DateTimeOffset.UtcNow);
        var explanation = new AccessExplanation(
            Allowed: true,
            Trace: [reason],
            Columns: [colWithMask],
            Groups: groups,
            Grants: [grantRef],
            PolicyEpoch: 42L);

        explanation.Allowed.ShouldBeTrue();
        explanation.Trace.ShouldBe([reason]);
        explanation.Columns.ShouldBe([colWithMask]);
        explanation.Groups.ShouldBe(groups);
        explanation.Grants.ShouldBe([grantRef]);
        explanation.PolicyEpoch.ShouldBe(42L);

        // Immutability: with copy
        var modified = explanation with { Allowed = false };
        modified.Allowed.ShouldBeFalse();
        explanation.Allowed.ShouldBeTrue();

        // Serialization
        var expJson = JsonSerializer.Serialize(explanation);
        var expRoundtrip = JsonSerializer.Deserialize<AccessExplanation>(expJson);
        expRoundtrip.ShouldNotBeNull();
        expRoundtrip.Allowed.ShouldBeTrue();
        expRoundtrip.PolicyEpoch.ShouldBe(42L);
        expRoundtrip.Trace.Count.ShouldBe(1);
        expRoundtrip.Columns.Count.ShouldBe(1);
        expRoundtrip.Grants.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TableAccessDecision_ExtendedProperties_Defaults_AndPreservation()
    {
        var table = new TableIdentifier("sales", "crm", "orders");

        // Allowed defaults
        var allowed = TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>());
        allowed.Reasons.ShouldNotBeNull();
        allowed.Reasons.ShouldBeEmpty();
        allowed.PolicyEpoch.ShouldBe(0L);
        allowed.Grants.ShouldNotBeNull();
        allowed.Grants.ShouldBeEmpty();

        // Denied defaults
        var denied = TableAccessDecision.Denied(table, "explicit deny");
        denied.Reasons.ShouldNotBeNull();
        denied.Reasons.ShouldBeEmpty();
        denied.PolicyEpoch.ShouldBe(0L);
        denied.Grants.ShouldNotBeNull();
        denied.Grants.ShouldBeEmpty();

        // Setting extended properties via with-expression
        var reason = new DecisionReason(DecisionStep.Grant, "GRANT_FOUND", "Grant", "grant-99");
        var grantRef = new DecisionGrantRef("grant-99", GrantSourceKind.Owner, DateTimeOffset.UtcNow);
        var enriched = allowed with
        {
            Reasons = [reason],
            PolicyEpoch = 128L,
            Grants = [grantRef]
        };

        enriched.Reasons.Count.ShouldBe(1);
        enriched.Reasons[0].ShouldBe(reason);
        enriched.PolicyEpoch.ShouldBe(128L);
        enriched.Grants.Count.ShouldBe(1);
        enriched.Grants[0].ShouldBe(grantRef);
        enriched.IsAllowed.ShouldBeTrue();

        // Original remains untouched (immutability)
        allowed.Reasons.ShouldBeEmpty();
        allowed.PolicyEpoch.ShouldBe(0L);
        allowed.Grants.ShouldBeEmpty();
    }
}
