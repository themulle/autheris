using System.Collections.Frozen;
using System.Collections.Generic;
using System.Security;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>WP-X0f: typed column masks on <see cref="TableAccessDecision"/> (SEC-ADG-02, fail closed like RequireTypedRowPolicy).</summary>
public sealed class TypedColumnMaskDecisionTests
{
    private static readonly TableIdentifier Orders = new("d", "main", "orders");

    private static MaskSpec Redact(string name = "__gql_mask_a_c", string value = "***") => new(
        MaskKind.Redact,
        new MaskArguments(Constant: new PolicyParameterExpression(name, SqlParameterType.String, ParameterOrigin.Mask)),
        new Dictionary<string, PolicyValue> { [name] = new PolicyValue(value, SqlParameterType.String) }.ToFrozenDictionary());

    private static ColumnMaskPolicy Masks(params string[] columns)
    {
        var map = new Dictionary<string, MaskSpec>();
        foreach (var column in columns)
        {
            map[column] = Redact("__gql_mask_" + column + "_c");
        }

        return new ColumnMaskPolicy(map);
    }

    private static TableAccessDecision Decision(ColumnAccessLevel email = ColumnAccessLevel.Mask, IColumnMaskPolicy? masks = null)
    {
        var d = TableAccessDecision.Allowed(Orders,
            new Dictionary<string, ColumnAccessLevel> { ["email"] = email, ["id"] = ColumnAccessLevel.Clear });
        return masks is null ? d : d with { ColumnMasks = masks };
    }

    [Fact]
    public void StringOnlyMask_IsDenied()
    {
        var result = Decision().RequireTypedColumnMasks();

        result.IsAllowed.ShouldBeFalse();
        result.DeniedReasons.ShouldContain(TableAccessDecision.TypedColumnMaskRequiredReason);
    }

    [Fact]
    public void TypedMask_CoveringEveryMaskedColumn_IsAllowedUnchanged()
    {
        var decision = Decision(masks: Masks("email"));

        decision.RequireTypedColumnMasks().ShouldBeSameAs(decision);
    }

    [Fact]
    public void TypedMask_MissingOneMaskedColumn_IsDenied()
    {
        var decision = Decision(masks: Masks("other"));

        decision.RequireTypedColumnMasks().IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void NoMaskedColumn_IsUnchanged()
    {
        var decision = Decision(email: ColumnAccessLevel.Clear);

        decision.RequireTypedColumnMasks().ShouldBeSameAs(decision);
    }

    [Fact]
    public void DeniedDecision_IsUnchanged()
    {
        var denied = TableAccessDecision.Denied(Orders, "no");

        denied.RequireTypedColumnMasks().ShouldBeSameAs(denied);
    }

    [Fact]
    public void CatalogSensitiveColumn_WithoutTypedMask_IsDenied()
    {
        // The decision lists no column but allows all; the catalog marks the column sensitive, so it is effectively masked.
        var decision = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var metadata = new TableMetadata
        {
            Identifier = Orders,
            Columns = [new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true }]
        };

        decision.RequireTypedColumnMasks(metadata).IsAllowed.ShouldBeFalse();
        (decision with { ColumnMasks = Masks("ssn") }).RequireTypedColumnMasks(metadata).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void ColumnMaskPolicy_ParameterWithoutValue_FailsClosed()
    {
        var spec = new MaskSpec(MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("missing", SqlParameterType.String, ParameterOrigin.Mask)),
            FrozenDictionary<string, PolicyValue>.Empty);

        Should.Throw<SecurityException>(() => new ColumnMaskPolicy(new Dictionary<string, MaskSpec> { ["email"] = spec }));
    }

    [Fact]
    public void ColumnMaskPolicy_NonMaskOriginParameter_FailsClosed()
    {
        var spec = new MaskSpec(MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("p", SqlParameterType.String, ParameterOrigin.Policy)),
            new Dictionary<string, PolicyValue> { ["p"] = new PolicyValue("x", SqlParameterType.String) }.ToFrozenDictionary());

        Should.Throw<SecurityException>(() => new ColumnMaskPolicy(new Dictionary<string, MaskSpec> { ["email"] = spec }));
    }

    [Fact]
    public void ColumnMaskPolicy_Fingerprint_IgnoresValues_ButNotShape()
    {
        var a = new ColumnMaskPolicy(new Dictionary<string, MaskSpec> { ["email"] = Redact(value: "one") });
        var b = new ColumnMaskPolicy(new Dictionary<string, MaskSpec> { ["EMAIL"] = Redact(value: "two") });
        var other = new ColumnMaskPolicy(new Dictionary<string, MaskSpec>
        {
            ["email"] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty)
        });

        a.Fingerprint.ShouldNotBeNullOrEmpty();
        a.Fingerprint.ShouldBe(b.Fingerprint);
        a.Fingerprint.ShouldNotBe(other.Fingerprint);
        a.HasMask("EMAIL").ShouldBeTrue();
    }

    [Fact]
    public void DecisionColumnMaskProvider_ServesTheTypedSpec()
    {
        var table = new TableIdentity("main", "orders");
        var policy = Masks("email");
        var provider = new DecisionColumnMaskProvider(new Dictionary<TableIdentity, ColumnMaskPolicy> { [table] = policy });

        provider.HasMask(table, "email").ShouldBeTrue();
        provider.HasMask(table, "id").ShouldBeFalse();
        provider.HasMask(new TableIdentity("main", "other"), "email").ShouldBeFalse();
        provider.GetMask(table, "email").Kind.ShouldBe(MaskKind.Redact);
        Should.Throw<SecurityException>(() => provider.GetMask(table, "id"));
    }
}
