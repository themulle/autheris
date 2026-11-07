namespace Autheris.Tests.Unit.VirtualFilters;

using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Shared virtual-filter fixtures for the unit and the PostgreSQL integration tests.</summary>
internal static class VirtualFilterTestData
{
    private static readonly TenantId Tenant = new("tenant_lwe");

    internal static VirtualFilter DavidFilter(string name = "nicht_ausgelieferte_krane") => new()
    {
        TenantId = Tenant,
        Name = name,
        Source = "lwetem_prod",
        KeyColumns = ["client.client_id"],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("lwetem_prod", "conf", "client"),
            FromAlias = "client",
            Joins =
            [
                new FilterJoin(new TableIdentifier("lwetem_prod", "md", "crane"), "crane", "crane.serial_number", "client.crane_serial_number")
            ],
            Where = [new FilterCondition("crane.is_delivered", FilterConditionOperator.IsNull)]
        }
    };

    internal static VirtualFilterAccessProfile DavidProfile(string name = "david", params string[] filters) => new()
    {
        TenantId = Tenant,
        Name = name,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("S-1-5-21-LWE-DAVID"),
        Scope = "lwetem_prod.*.*",
        Uncovered = UncoveredPolicy.Deny,
        Bindings = (filters.Length == 0 ? ["nicht_ausgelieferte_krane"] : filters)
            .Select(f => new FilterBinding { FilterName = f, TargetPattern = "lwetem_prod.*.*.client_id" }).ToList()
    };

    internal static TableMetadata Table(string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", schema, table),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = schema, TableName = table, SourceType = "SqlServer" },
        Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList()
    };
}
