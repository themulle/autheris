namespace Autheris.Domain.Options;

using System;

/// <summary>
/// Single switch for everything Autheris ships as sample content: the seeded demo catalog (finance/hr/... tables and data
/// owners), the demo GraphQL types (<c>finance</c>, <c>hr</c>, <c>InvoiceRecord</c>), the demo MCP tools
/// (<c>query_customers</c>, <c>query_invoices</c>) and the built-in golden queries.
/// </summary>
public interface IDemoDataSwitch
{
    bool Enabled { get; }
}

public sealed class DemoDataSwitch(bool enabled) : IDemoDataSwitch
{
    public bool Enabled { get; } = enabled;

    /// <summary>
    /// <c>GovernanceDb:SeedDemoData</c> decides when it is set (true/false). Otherwise sample content is available in
    /// Development (as before) and never in other environments.
    /// </summary>
    public static bool Resolve(GatewayOptions options, string? environmentName)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.GovernanceDb.SeedDemoData.HasValue)
        {
            return options.GovernanceDb.SeedDemoData.Value;
        }

        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
