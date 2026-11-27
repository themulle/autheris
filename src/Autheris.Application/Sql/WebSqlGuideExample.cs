namespace Autheris.Application.Sql;

/// <summary>
/// The one WebSQL example shown by the agent query guide, the OpenAPI document and the feature documentation, and
/// replayed unchanged by an integration test. Constants only: the statement uses <c>@</c> parameters and no literals.
/// </summary>
public static class WebSqlGuideExample
{
    /// <summary>The statement text; it stays fixed while the parameter values change.</summary>
    public const string Sql =
        "SELECT id, status, amount, created_at FROM finance.public.invoices WHERE status IN (@statuses) AND amount >= @minAmount ORDER BY id";

    /// <summary>The <c>parameters</c> object of the example request, including one list parameter.</summary>
    public const string ParametersJson = """{ "statuses": ["PAID", "PENDING"], "minAmount": 100 }""";

    /// <summary>The <c>dataSource</c> of the example request.</summary>
    public const string DataSource = "finance";

    /// <summary>The <c>page.size</c> of the example request.</summary>
    public const int PageSize = 50;

    /// <summary>The complete example request body as indented JSON.</summary>
    public const string RequestJson =
        "{\n" +
        "  \"sql\": \"" + Sql + "\",\n" +
        "  \"parameters\": " + ParametersJson + ",\n" +
        "  \"dataSource\": \"" + DataSource + "\",\n" +
        "  \"page\": { \"size\": 50 }\n" +
        "}";
}
