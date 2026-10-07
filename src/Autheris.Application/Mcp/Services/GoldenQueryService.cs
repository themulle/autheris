namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class GoldenQueryService : IGoldenQueryService
{
    private readonly ConcurrentDictionary<string, GoldenQuery> _queries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<GoldenQueryService> _logger;
    private readonly int _maxResults;
    private readonly bool _enabled;

    /// <param name="demoData">Without a switch (direct construction) the built-in demo queries are registered, as before.</param>
    public GoldenQueryService(IOptions<GatewayOptions>? options, ILogger<GoldenQueryService> logger, IDemoDataSwitch? demoData = null)
    {
        _logger = logger;
        var goldenOpts = options?.Value.GoldenQueries ?? new GoldenQueryOptions();
        _enabled = goldenOpts.Enabled;
        _maxResults = goldenOpts.MaxResultsPerRequest > 0 ? goldenOpts.MaxResultsPerRequest : 20;

        InitializeQueries(goldenOpts.InitialQueries, demoData?.Enabled ?? true);
    }

    private const int MaxQueryStoreCapacity = 5000;
    private const int MaxQueryTextLength = 65536;

    public void RegisterGoldenQuery(GoldenQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.TableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.QueryText);

        if (query.QueryText.Length > MaxQueryTextLength)
        {
            throw new ArgumentException($"Query text exceeds maximum allowed size of {MaxQueryTextLength} characters.", nameof(query));
        }

        if (_queries.Count >= MaxQueryStoreCapacity && !_queries.ContainsKey(query.Id))
        {
            throw new InvalidOperationException($"Golden query registry capacity limit ({MaxQueryStoreCapacity}) reached.");
        }

        _queries[query.Id] = query;
        _logger.LogDebug("Registered golden query '{Id}' for domain '{Domain}', table '{TableName}'",
            query.Id, query.Domain, query.TableName);
    }

    public ValueTask<IReadOnlyList<GoldenQuery>> GetGoldenQueriesAsync(
        string? domain = null,
        string? tableName = null,
        CancellationToken ct = default)
    {
        if (!_enabled)
        {
            return ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]);
        }

        IEnumerable<GoldenQuery> query = _queries.Values;

        if (!string.IsNullOrWhiteSpace(domain))
        {
            query = query.Where(q => string.Equals(q.Domain, domain, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tableName))
        {
            query = query.Where(q => string.Equals(q.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        }

        var results = query.Take(_maxResults).ToList();
        return ValueTask.FromResult<IReadOnlyList<GoldenQuery>>(results);
    }

    public ValueTask<GoldenQuery?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(id))
        {
            return ValueTask.FromResult<GoldenQuery?>(null);
        }

        _queries.TryGetValue(id, out var query);
        return ValueTask.FromResult(query);
    }

    private void InitializeQueries(IReadOnlyList<GoldenQueryDefinition>? initialQueries, bool includeDemoQueries)
    {
        if (initialQueries != null && initialQueries.Count > 0)
        {
            foreach (var def in initialQueries)
            {
                RegisterGoldenQuery(new GoldenQuery(
                    Id: def.Id,
                    Domain: def.Domain,
                    TableName: def.TableName,
                    Title: def.Title,
                    Description: def.Description,
                    QueryText: def.QueryText,
                    VariablesJson: def.VariablesJson,
                    Tags: def.Tags,
                    CreatedAt: DateTimeOffset.UtcNow
                ));
            }
            return;
        }

        // Built-in Golden Queries (finance demo catalog) only with demo data enabled
        if (!includeDemoQueries)
        {
            return;
        }

        RegisterGoldenQuery(new GoldenQuery(
            Id: "golden_customers_active",
            Domain: "finance",
            TableName: "customers",
            Title: "Get Active Customers with Account Details",
            Description: "Returns customer identifier, masked IBAN, and account status with standard pagination.",
            QueryText: """
            query GetActiveCustomers($limit: Int) {
              customers(filter: { status: "ACTIVE" }, limit: $limit) {
                id
                name
                email
                iban
                accountStatus
              }
            }
            """,
            VariablesJson: """{ "limit": 25 }""",
            Tags: ["finance", "customers", "active", "recommended"],
            CreatedAt: DateTimeOffset.UtcNow
        ));

        RegisterGoldenQuery(new GoldenQuery(
            Id: "golden_invoices_unpaid",
            Domain: "finance",
            TableName: "invoices",
            Title: "Retrieve Overdue and Unpaid Invoices",
            Description: "Extracts unpaid customer invoices filtered by dueDate with currency formatting.",
            QueryText: """
            query GetUnpaidInvoices($dueDateBefore: String) {
              invoices(filter: { status: "UNPAID", dueDateBefore: $dueDateBefore }) {
                invoiceNumber
                customerId
                amount
                currency
                dueDate
              }
            }
            """,
            VariablesJson: """{ "dueDateBefore": "2026-12-31" }""",
            Tags: ["finance", "invoices", "overdue"],
            CreatedAt: DateTimeOffset.UtcNow
        ));
    }
}
