namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Builders for Virtual Filter enforcement tests (F-GOV-14 WI-4): protected HTTP table, filters, key sets.</summary>
internal static class VirtualFilterEnforcementFixture
{
    internal static readonly TableIdentifier CustomersId = new("crm_api", "public", "customers");

    /// <summary><c>crm_api.public.customers(id, account_id, name, secret)</c>; <paramref name="idType"/> is the catalog type of <c>id</c>.</summary>
    internal static TableMetadata Customers(string idType)
    {
        var table = FederatedVfPipelineHarness.HttpTable("crm_api", "public", "customers", "id", "account_id", "name", "secret");
        return table with
        {
            Columns = table.Columns.Select(c => c.ColumnName == "id" ? new TableColumn { ColumnName = "id", DataType = idType } : c).ToList()
        };
    }

    /// <summary>Structured filter on <c>erp.auth.client AS client</c> with the given key columns.</summary>
    internal static VirtualFilter KeyFilter(string name, params string[] keys) => new()
    {
        TenantId = FederatedVfPipelineHarness.Tenant,
        Name = name,
        Source = "erp",
        KeyColumns = keys.Select(k => "client." + k).ToList(),
        Structured = new StructuredFilterDefinition { From = new TableIdentifier("erp", "auth", "client"), FromAlias = "client" }
    };

    internal static readonly VirtualFilter CustomersFilter = KeyFilter("vf_customers", "client_id");

    internal static AppliedVirtualFilterBinding PairById(long generation = 1) => new(
        CustomersFilter,
        new FilterBinding
        {
            FilterName = "vf_customers",
            TargetPattern = "crm_api.public.customers",
            ColumnMap = new Dictionary<string, string> { ["client_id"] = "id" }
        },
        generation);

    internal static VirtualFilterKeySet Keys(IReadOnlyList<VirtualFilterKeyType> types, params object?[][] tuples) =>
        VirtualFilterKeySet.Create(types, tuples);

    internal static VirtualFilterEnforcement Enforcement(TableMetadata table, params (AppliedVirtualFilterBinding Pair, VirtualFilterKeySet Keys)[] pairs) =>
        VirtualFilterEnforcement.Create(table, pairs);

    internal static IReadOnlyDictionary<string, object?> Customer(object? id) =>
        FederatedVfPipelineHarness.Row(("id", id), ("account_id", "A"), ("name", "n"), ("secret", "s"));
}
