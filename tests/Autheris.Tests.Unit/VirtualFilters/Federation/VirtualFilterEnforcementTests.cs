namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.VirtualFilterEnforcementFixture;

/// <summary>
/// F-GOV-14 WI-4 (M3): the gateway semi-join. A row is kept only if, for every applied (filter, binding) pair, its
/// typed key tuple is in that pair's authorized key set. No row multiplication, NULL never matches.
/// </summary>
public sealed class VirtualFilterEnforcementTests
{
    [Fact]
    public void Apply_RowOutsideKeySet_Dropped()
    {
        var enforcement = Enforcement(Customers("varchar"), (PairById(), Keys([VirtualFilterKeyType.Text], ["1"], ["3"])));

        var kept = enforcement.Apply([Customer("1"), Customer("2"), Customer("3")]);

        kept.Select(r => r["id"]).ShouldBe(["1", "3"]);
    }

    [Fact]
    public void Apply_DuplicateKeyTuples_NoRowMultiplication()
    {
        var enforcement = Enforcement(Customers("varchar"), (PairById(), Keys([VirtualFilterKeyType.Text], ["1"], ["1"], ["1"])));

        enforcement.Apply([Customer("1"), Customer("2")]).ShouldHaveSingleItem()["id"].ShouldBe("1");
    }

    [Fact]
    public void Apply_NullOrMissingKeyColumn_RowDropped()
    {
        var enforcement = Enforcement(Customers("varchar"), (PairById(), Keys([VirtualFilterKeyType.Text], ["1"])));
        var noId = FederatedVfPipelineHarness.Row(("name", "x"));

        enforcement.Apply([Customer(null), noId, Customer("1")]).ShouldHaveSingleItem()["id"].ShouldBe("1");
    }

    [Fact]
    public void Apply_CompositeKey_RequiresAllComponents()
    {
        var table = FederatedVfPipelineHarness.HttpTable("crm_api", "public", "contracts", "client_id", "region", "name");
        var filter = KeyFilter("vf_composite", "client_id", "region");
        var pair = new AppliedVirtualFilterBinding(filter, new FilterBinding { FilterName = "vf_composite", TargetPattern = "crm_api.public.contracts" }, 1);
        var enforcement = Enforcement(table, (pair, Keys([VirtualFilterKeyType.Text, VirtualFilterKeyType.Text], ["1", "EU"], ["2", "US"])));

        var kept = enforcement.Apply(
        [
            FederatedVfPipelineHarness.Row(("client_id", "1"), ("region", "EU")),
            FederatedVfPipelineHarness.Row(("client_id", "1"), ("region", "US")),
            FederatedVfPipelineHarness.Row(("client_id", "2"), ("region", "US")),
            FederatedVfPipelineHarness.Row(("client_id", "2"), ("region", null))
        ]);

        kept.Select(r => $"{r["client_id"]}/{r["region"]}").ShouldBe(["1/EU", "2/US"]);
    }

    [Fact]
    public void Apply_TwoPairs_RowMustMatchBoth()
    {
        var table = Customers("varchar");
        var byAccount = new AppliedVirtualFilterBinding(
            KeyFilter("vf_account", "account_id"),
            new FilterBinding { FilterName = "vf_account", TargetPattern = "crm_api.public.customers" },
            1);
        var enforcement = Enforcement(table,
            (PairById(), Keys([VirtualFilterKeyType.Text], ["1"], ["2"])),
            (byAccount, Keys([VirtualFilterKeyType.Text], ["A"])));

        var kept = enforcement.Apply(
        [
            FederatedVfPipelineHarness.Row(("id", "1"), ("account_id", "A")),
            FederatedVfPipelineHarness.Row(("id", "2"), ("account_id", "B")),
            FederatedVfPipelineHarness.Row(("id", "3"), ("account_id", "A"))
        ]);

        kept.ShouldHaveSingleItem()["id"].ShouldBe("1");
        enforcement.Bindings.Count.ShouldBe(2);
    }

    [Fact]
    public void Apply_UpstreamStringVsSourceInteger_Matches()
    {
        // Key source delivers integers, the HTTP API delivers strings; the catalog type (integer) decides.
        var enforcement = Enforcement(Customers("integer"), (PairById(), Keys([VirtualFilterKeyType.IntegerNumber], [47L], [99L])));

        enforcement.Apply([Customer("47"), Customer("048"), Customer("99")]).Select(r => r["id"]).ShouldBe(["47", "99"]);
    }

    [Fact]
    public void Apply_UpstreamPrecisionCollisionsAndNonScalarIds_Dropped()
    {
        // ST-12: 9007199254740993.0 arrives as the double 2^53 and must not match the authorized key 2^53; JSON objects,
        // arrays (raw text) and booleans never match.
        var enforcement = Enforcement(Customers("bigint"),
            (PairById(), Keys([VirtualFilterKeyType.IntegerNumber], [9007199254740992L], [1L])));

        var kept = enforcement.Apply(
        [
            Customer(9007199254740993.0d),
            Customer("{\"id\":1}"),
            Customer("[1]"),
            Customer(true),
            Customer(1L)
        ]);

        kept.ShouldHaveSingleItem()["id"].ShouldBe(1L);
    }

    [Fact]
    public void Apply_TextKeyIsCaseSensitive()
    {
        var enforcement = Enforcement(Customers("varchar"), (PairById(), Keys([VirtualFilterKeyType.Text], ["abc"])));

        enforcement.Apply([Customer("ABC"), Customer("abc")]).ShouldHaveSingleItem()["id"].ShouldBe("abc");
    }

    [Fact]
    public void Create_KeyTypesDifferFromCatalog_ThrowsKeyTypeMismatch()
    {
        var ex = Should.Throw<GatewaySecurityException>(() =>
            Enforcement(Customers("integer"), (PairById(), Keys([VirtualFilterKeyType.Text], ["1"]))));

        ex.ErrorCode.ShouldBe("VF_KEY_TYPE_MISMATCH");
    }

    [Fact]
    public void Create_UnsupportedCatalogType_ThrowsKeyTypeMismatch()
    {
        Should.Throw<GatewaySecurityException>(() => Enforcement(Customers("date"), (PairById(), Keys([VirtualFilterKeyType.Text], ["1"]))))
            .ErrorCode.ShouldBe("VF_KEY_TYPE_MISMATCH");
    }

    [Fact]
    public void Create_MappedColumnMissingOnTable_ThrowsBindingMisconfigured()
    {
        var pair = new AppliedVirtualFilterBinding(
            KeyFilter("vf_customers", "client_id"),
            new FilterBinding { FilterName = "vf_customers", TargetPattern = "crm_api.public.customers", ColumnMap = new Dictionary<string, string> { ["client_id"] = "nope" } },
            1);

        Should.Throw<GatewaySecurityException>(() => Enforcement(Customers("varchar"), (pair, Keys([VirtualFilterKeyType.Text], ["1"]))))
            .ErrorCode.ShouldBe("VF_BINDING_MISCONFIGURED");
    }

    [Fact]
    public void Create_SqlFilterOrValidityWindow_ThrowsKeySourceUnsupported()
    {
        var sqlFilter = new VirtualFilter { TenantId = FederatedVfPipelineHarness.Tenant, Name = "vf_sql", Source = "erp", Sql = "from x where target.id = x.id", SqlTargetColumns = ["id"] };
        var windowed = KeyFilter("vf_window", "client_id") with { ValidFromColumn = "client.valid_from" };
        var mapToId = new Dictionary<string, string> { ["client_id"] = "id" };

        Should.Throw<GatewaySecurityException>(() => Enforcement(Customers("varchar"),
                (new AppliedVirtualFilterBinding(sqlFilter, new FilterBinding { FilterName = "vf_sql" }, 1), Keys([VirtualFilterKeyType.Text], ["1"]))))
            .ErrorCode.ShouldBe("VF_KEY_SOURCE_UNSUPPORTED");
        Should.Throw<GatewaySecurityException>(() => Enforcement(Customers("varchar"),
                (new AppliedVirtualFilterBinding(windowed, new FilterBinding { FilterName = "vf_window", ColumnMap = mapToId, TimeColumn = "name" }, 1), Keys([VirtualFilterKeyType.Text], ["1"]))))
            .ErrorCode.ShouldBe("VF_KEY_SOURCE_UNSUPPORTED");
    }

    [Fact]
    public void Create_WithoutPairs_ThrowsEnforcementIncomplete()
    {
        Should.Throw<GatewaySecurityException>(() => Enforcement(Customers("varchar")))
            .ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
    }

    [Fact]
    public void KeySet_UnconvertibleKeys_DroppedAndCounted()
    {
        var set = Keys([VirtualFilterKeyType.IntegerNumber], [1L], ["x"], [null], [2.5d], ["2"]);

        set.Count.ShouldBe(2);
        set.DroppedKeyCount.ShouldBe(3);
        set.KeyTypes.ShouldBe([VirtualFilterKeyType.IntegerNumber]);
    }
}
