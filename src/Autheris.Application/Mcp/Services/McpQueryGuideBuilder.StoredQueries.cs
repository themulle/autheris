namespace Autheris.Application.Mcp.Services;

using System.Text;

internal static partial class McpQueryGuideBuilder
{
    internal const string ListExample = """
        query OrdersPage($where: orders_filter, $orderBy: [orders_order_by!], $first: Int = 50, $offset: Int = 0) {
          orders(where: $where, orderBy: $orderBy, first: $first, offset: $offset) { id status amount }
        }
        """;

    internal const string ConnectionExample = """
        query PaidOrdersConnection($filter: orders_filter! = {}, $dir: AutherisSortDirection! = ASC, $first: Int, $after: String) {
          ordersConnection(where: { and: [{ status: { eq: "PAID" } }, $filter] }, orderBy: [{ id: $dir }], first: $first, after: $after) {
            edges { node { id status amount } }
            pageInfo { hasNextPage endCursor }
          }
        }
        """;

    internal const string AggregateExample = """
        query OrdersByStatus($groupBy: [orders_column!], $aggregates: [orders_aggregate_input!]!, $where: orders_filter, $orderBy: [orders_aggregate_order_by!], $limit: Int) {
          orders_aggregate(groupBy: $groupBy, aggregates: $aggregates, where: $where, orderBy: $orderBy, limit: $limit) {
            groups { key { status } rowCount aggregates { alias intValue } }
            suppressed truncated
          }
        }
        """;

    private const string StoredQueriesIntro = """
        ## 7. Stored Queries with Variables

        A stored query (a trusted document, a saved report, a script) has a fixed text, so optional filters, the sort order and paging come in as variables. Variable types are named after the table's GraphQL field: `<tableField>_filter`, `<tableField>_order_by`, `<tableField>_column`, `<tableField>_aggregate_input`, `<tableField>_aggregate_order_by`, `<tableField>_time_bucket_input`. Variables follow the same rules as literals (section 6: no masked column in `where` or `orderBy`).

        - **Optional filter:** Pass the whole `where` as one variable (`$where: orders_filter`). `null`, `{}` or an omitted variable means no filter.
        - **Operand variables:** A variable used as an operand binds its value. A `null` operand on `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `contains`, `startsWith` or `endsWith` matches no row; `null` on `in`, `nin`, `isNull` or on a whole column filter is rejected. To make a condition optional, put the whole `where` or a whole `and` member in a variable, never only the operand.
        - **Fixed condition plus optional filter:** `where: { and: [{ status: { eq: "PAID" } }, $filter] }` with `$filter: orders_filter! = {}`. Pass `{}` or omit `$filter` for no extra filter; a `null` member inside `and` or `or` is rejected.
        - **Selectable sort order:** Pass `orderBy` as one variable (`$orderBy: [orders_order_by!]`; `null` or omitted means the default key order), or only the direction (`orderBy: [{ id: $dir }]` with `$dir: AutherisSortDirection! = ASC`). A connection accepts only key columns (section 3), so there the caller chooses the direction; on the root list also the column.
        - **Paging:** On the root list use `$first: Int` and `$offset: Int`. On a connection use `$first: Int` and `$after: String`; `null` means the default page size and the first page. Pass `pageInfo.endCursor` as `$after` and keep the other variables unchanged (section 2).
        - **Aggregates:** `groupBy`, `timeBuckets`, `aggregates`, `where`, `orderBy` and `limit` all take variables. `null` means no grouping or time buckets, the default order, the default `limit` of `100`, or no filter; `aggregates` is required.

        Root list with optional filter, selectable order and offset paging:
        """;

    private const string ListVariables = """
        Variables: `{}`, `{ "where": null }` or `{ "where": {} }` return all rows in key order; `{ "where": { "amount": { "gte": 100 } }, "orderBy": [{ "amount": "DESC" }], "first": 20, "offset": 20 }` returns the second page of 20.

        Connection with a fixed condition, an optional extra filter, a selectable direction and cursor paging:
        """;

    private const string ConnectionVariables = """
        Variables: `{}` returns the first page of paid orders; `{ "filter": { "amount": { "gte": 100 } }, "dir": "DESC", "first": 20 }`, then the same with `"after": "<endCursor>"` until `hasNextPage` is `false`.

        Aggregate with every argument as a variable:
        """;

    private const string AggregateVariables = """
        Variables: `{ "aggregates": [{ "fn": "COUNT", "alias": "n" }] }` returns one total; `{ "groupBy": ["status"], "aggregates": [{ "fn": "COUNT", "alias": "n" }], "where": { "amount": { "gte": 100 } }, "orderBy": [{ "alias": "n", "direction": "DESC" }], "limit": 10 }` returns one group per status.
        """;

    private static void AppendStoredQueriesWithVariables(StringBuilder sb)
    {
        AppendLines(sb, StoredQueriesIntro);
        sb.AppendLine();
        AppendExample(sb, ListExample);
        AppendLines(sb, ListVariables);
        sb.AppendLine();
        AppendExample(sb, ConnectionExample);
        AppendLines(sb, ConnectionVariables);
        sb.AppendLine();
        AppendExample(sb, AggregateExample);
        AppendLines(sb, AggregateVariables);
        sb.AppendLine();
    }

    private static void AppendExample(StringBuilder sb, string example)
    {
        sb.AppendLine("```graphql");
        AppendLines(sb, example);
        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static void AppendLines(StringBuilder sb, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            sb.AppendLine(line.TrimEnd('\r'));
        }
    }
}
