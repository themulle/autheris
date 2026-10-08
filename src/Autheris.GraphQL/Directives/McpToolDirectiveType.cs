namespace Autheris.GraphQL.Directives;

using HotChocolate.Types;

/// <summary>
/// HotChocolate directive exposing a GraphQL query field as a typed Model Context Protocol (MCP) tool
/// for autonomous AI agents.
/// Example:
///   customerOrders(customerId: ID!): [Order!]! @mcpTool(name: "lookup_customer_orders", description: "Fetches recent customer orders")
/// </summary>
public sealed class McpToolDirectiveType : DirectiveType
{
    protected override void Configure(IDirectiveTypeDescriptor descriptor)
    {
        descriptor.Name("mcpTool");
        descriptor.Description("Exposes a GraphQL query operation as an MCP AI tool with zero-trust guardrails.");
        descriptor.Location(DirectiveLocation.FieldDefinition);
        descriptor.Argument("name").Type<NonNullType<StringType>>().Description("Name of the MCP tool.");
        descriptor.Argument("description").Type<StringType>().Description("Human/LLM-readable description of the tool.");
        descriptor.Argument("targetTable").Type<StringType>().Description("Explicit target table identifier in 'domain.schema.table' format.");
        descriptor.Argument("table").Type<StringType>().Description("Alias for targetTable.");
    }
}
