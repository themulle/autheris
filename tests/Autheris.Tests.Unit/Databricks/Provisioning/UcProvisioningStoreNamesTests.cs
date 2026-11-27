namespace Autheris.Tests.Unit.Databricks.Provisioning;

using Autheris.Application.Governance.Provisioning;
using Shouldly;
using Xunit;

/// <summary>Issue #1122: stored provisioning states round-trip, and unknown stored values read fail closed.</summary>
[Trait("Category", "Security")]
public sealed class UcProvisioningStoreNamesTests
{
    [Theory]
    [InlineData(UcLedgerState.Pending)]
    [InlineData(UcLedgerState.Applied)]
    [InlineData(UcLedgerState.RevokePending)]
    [InlineData(UcLedgerState.Failed)]
    public void Ledger_states_round_trip(UcLedgerState state)
    {
        UcProvisioningStoreNames.ParseLedgerState(state.ToStored()).ShouldBe(state);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("applied")]
    [InlineData("GRANTED")]
    public void Unknown_ledger_state_reads_as_revoke_pending(string? stored)
    {
        // A row in an unknown state is removed, never kept granting.
        UcProvisioningStoreNames.ParseLedgerState(stored).ShouldBe(UcLedgerState.RevokePending);
    }

    [Theory]
    [InlineData(UcGrantState.Pending)]
    [InlineData(UcGrantState.Provisioned)]
    [InlineData(UcGrantState.Unsupported)]
    [InlineData(UcGrantState.Failed)]
    [InlineData(UcGrantState.Revoked)]
    public void Grant_states_round_trip(UcGrantState state)
    {
        UcProvisioningStoreNames.ParseGrantState(state.ToStored()).ShouldBe(state);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("provisioned")]
    [InlineData("EFFECTIVE")]
    public void Unknown_grant_state_never_reads_as_provisioned(string? stored)
    {
        UcProvisioningStoreNames.ParseGrantState(stored).ShouldBe(UcGrantState.Pending);
    }

    [Theory]
    [InlineData(UcSecurableType.Catalog)]
    [InlineData(UcSecurableType.Schema)]
    [InlineData(UcSecurableType.Table)]
    public void Securable_types_round_trip(UcSecurableType type)
    {
        UcProvisioningStoreNames.TryParseSecurableType(type.ToStored(), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("table")]
    [InlineData("VOLUME")]
    public void Unknown_securable_type_is_not_parsed(string? stored)
    {
        UcProvisioningStoreNames.TryParseSecurableType(stored, out _).ShouldBeFalse();
    }

    [Fact]
    public void Undefined_enum_values_cannot_be_stored()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ((UcLedgerState)9).ToStored());
        Should.Throw<ArgumentOutOfRangeException>(() => ((UcGrantState)9).ToStored());
        Should.Throw<ArgumentOutOfRangeException>(() => ((UcSecurableType)9).ToStored());
    }
}
