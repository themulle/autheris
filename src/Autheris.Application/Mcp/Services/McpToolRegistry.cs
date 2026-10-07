namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Thread-safe in-memory registry of MCP tools exposed to AI agents.
/// Pre-populates default tools from GatewayOptions.Mcp.AllowedOperations or built-in standard queries.
/// </summary>
public sealed class McpToolRegistry : IMcpToolRegistry
{
    private readonly ConcurrentDictionary<string, McpToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="demoData">Without a switch (direct construction) the demo tools are registered, as before.</param>
    public McpToolRegistry(IOptions<GatewayOptions>? options = null, IDemoDataSwitch? demoData = null)
    {
        var mcpOpts = options?.Value.Mcp;
        InitializeDefaultTools(mcpOpts?.AllowedOperations, demoData?.Enabled ?? true);
    }

    public void RegisterTool(McpToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
    }

    public IReadOnlyList<McpToolDefinition> GetAvailableTools()
    {
        return _tools.Values.ToList();
    }

    public McpToolDefinition? FindTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        return _tools.TryGetValue(toolName, out var tool) ? tool : null;
    }

    private void InitializeDefaultTools(IReadOnlyList<string>? allowedOperations, bool includeDemoTools)
    {
        // 1./2. Demo tools (Query Customers, Query Invoices) on the demo catalog: only with demo data enabled
        if (includeDemoTools)
        {
        RegisterTool(new McpToolDefinition(
            Name: "query_customers",
            Description: "Queries customer records with automatic PII masking and tenant isolation.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "customerId": { "type": "string", "description": "Optional customer identifier filter." },
                "limit": { "type": "integer", "description": "Maximum number of rows to return (default 50)." }
              }
            }
            """,
            TargetGraphQLOperation: "query GetCustomers($customerId: String, $limit: Int) { customers(customerId: $customerId, limit: $limit) { id name email iban createdDate } }",
            TargetTable: new TableIdentifier("finance", "dbo", "customers")
        ));

        // 2. Built-in tool: Query Invoices
        RegisterTool(new McpToolDefinition(
            Name: "query_invoices",
            Description: "Queries enterprise financial invoices and billing lines with ABAC authorization.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "invoiceId": { "type": "string", "description": "Optional specific invoice ID." },
                "currency": { "type": "string", "description": "Filter by currency code (e.g. EUR, USD)." }
              }
            }
            """,
            TargetGraphQLOperation: "query GetInvoices($invoiceId: String, $currency: String) { invoices(invoiceId: $invoiceId, currency: $currency) { invoiceId amount currency status } }",
            TargetTable: new TableIdentifier("finance", "dbo", "invoices")
        ));
        }

        // 3. Built-in tool: Query Data Catalog Metadata
        RegisterTool(new McpToolDefinition(
            Name: "query_data_catalog",
            Description: "Inspects enterprise data catalog assets, classifications, and data stewards.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "tableName": { "type": "string", "description": "Table or asset name to inspect." }
              }
            }
            """,
            TargetGraphQLOperation: "query GetCatalogMetadata($tableName: String) { catalogAssets(tableName: $tableName) { tableName sensitivity classification owner tags } }",
            TargetTable: new TableIdentifier("governance", "catalog", "assets")
        ));

        // 4. Built-in tool: simulate_query (F-AI-04 Pre-Flight AST Simulator & Token Guard)
        RegisterTool(new McpToolDefinition(
            Name: "simulate_query",
            Description: "Simulates an enterprise GraphQL query AST to calculate estimated rows, DB scan bytes, and token volume before execution.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["query"],
              "properties": {
                "query": { "type": "string", "description": "The GraphQL query string to simulate." }
              }
            }
            """,
            TargetGraphQLOperation: "simulate_query"
        ));

        // 5. Built-in tool: get_golden_queries (F-AI-03 Dynamic Few-Shot Golden Query Injection)
        RegisterTool(new McpToolDefinition(
            Name: "get_golden_queries",
            Description: "Retrieves curated golden queries and verified few-shot query patterns to prevent LLM hallucinations on complex schemas.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "domain": { "type": "string", "description": "Optional business domain filter (e.g. 'finance', 'crm')." },
                "tableName": { "type": "string", "description": "Optional table name filter (e.g. 'customers', 'invoices')." }
              }
            }
            """,
            TargetGraphQLOperation: "get_golden_queries"
        ));

        // Register any explicitly declared operations
        if (allowedOperations != null)
        {
            foreach (var op in allowedOperations)
            {
                var cleanName = op.ToLowerInvariant().Replace(' ', '_');
                if (!_tools.ContainsKey(cleanName))
                {
                    RegisterTool(new McpToolDefinition(
                        Name: cleanName,
                        Description: $"Executes the curated enterprise GraphQL operation '{op}'.",
                        InputJsonSchema: """{"type":"object","properties":{}}""",
                        TargetGraphQLOperation: op
                    ));
                }
            }
        }
    }
}
