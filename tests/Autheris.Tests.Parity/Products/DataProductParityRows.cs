using Autheris.Tests.Parity.Infrastructure;

namespace Autheris.Tests.Parity.Products;

/// <summary>
/// Planned cross-transport parity rows P1..P13 of the data products work package. The parity project has no data products fixture yet
/// (a seeded product with loaded results needs a governance database and a registered second source), so every row is a skipped stub
/// that names its scenario. Each row runs the same product read through REST, GraphQL, OData, WebSQL, Trino and MCP (procedures through
/// REST and MCP) and compares the decision, the columns and the masking state, like the matrix cells. They are compiled here and are
/// executed on GitHub Actions only once the fixture exists.
/// </summary>
[Trait("Category", "Parity")]
public sealed class DataProductParityRows : ParityTestBase
{
    private const string NoFixture = "planned: the parity host has no data products fixture (seeded product, loaded result, second source) yet";

    [Fact(Skip = NoFixture)]
    public void P1_view_schedule_single_source()
    {
        // P1: view, schedule, single source
    }

    [Fact(Skip = NoFixture)]
    public void P2_view_over_two_sources()
    {
        // P2: view over two sources
    }

    [Fact(Skip = NoFixture)]
    public void P3_no_product_grant()
    {
        // P3: no product grant (default deny, uniform 404)
    }

    [Fact(Skip = NoFixture)]
    public void P4_caller_denied_on_a_base_object_but_granted_on_the_product()
    {
        // P4: caller denied on a base object but granted on the product (allowed)
    }

    [Fact(Skip = NoFixture)]
    public void P5_PII_class_masked_column()
    {
        // P5: PII class masked column
    }

    [Fact(Skip = NoFixture)]
    public void P6_clear_eligible_column_with_a_Clear_rule_on_the_grant()
    {
        // P6: clear-eligible column with a Clear rule on the grant
    }

    [Fact(Skip = NoFixture)]
    public void P7_row_policy_on_the_product()
    {
        // P7: row policy on the product
    }

    [Fact(Skip = NoFixture)]
    public void P8_stale_beyond_MaxStaleness()
    {
        // P8: stale beyond MaxStaleness (PRODUCT_STALE)
    }

    [Fact(Skip = NoFixture)]
    public void P9_product_over_a_product()
    {
        // P9: product over a product (rejected, fail closed)
    }

    [Fact(Skip = NoFixture)]
    public void P10_procedure_declared_read_with_an_execute_grant()
    {
        // P10: procedure declared read with an execute grant
    }

    [Fact(Skip = NoFixture)]
    public void P11_procedure_declared_write_with_a_write_execute_grant()
    {
        // P11: procedure declared write with a write-execute grant (REST and MCP; every other transport rejects)
    }

    [Fact(Skip = NoFixture)]
    public void P12_retired_product()
    {
        // P12: retired product (uniform 404)
    }

    [Fact(Skip = NoFixture)]
    public void P13_onDemand_expiry_and_reload()
    {
        // P13: onDemand expiry and reload
    }
}
