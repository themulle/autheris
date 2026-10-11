namespace Autheris.Infrastructure.Databricks;

using System.Collections.Immutable;

/// <summary>One named, typed statement parameter (the value is never inlined into the SQL text).</summary>
public readonly record struct DatabricksParameter(string Name, string? Value, string Type);

/// <summary>A statement for the Statement Execution API (design §6.3). <paramref name="DataSource"/> null = the only configured Databricks data source.</summary>
public sealed record DatabricksStatementRequest(
    string Statement,
    ImmutableArray<DatabricksParameter> Parameters,
    string Catalog,
    string Schema,
    TimeSpan WaitTimeout,
    string? DataSource = null);

public readonly record struct DatabricksColumn(string Name, string TypeName);

/// <summary>Inline JSON_ARRAY result: every cell is the textual value or null.</summary>
public sealed record DatabricksStatementResult(
    string StatementId,
    ImmutableArray<DatabricksColumn> Columns,
    IReadOnlyList<string?[]> Rows);

/// <summary>Sanitised provider failure: carries only a stable code and the Databricks error code, never driver text or secrets.</summary>
public class DatabricksProviderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The result exceeded the configured byte limit (fail closed, no partial data returned).</summary>
public sealed class DatabricksResultTooLargeException()
    : DatabricksProviderException(DatabricksErrorCodes.ResultTooLarge, "The Databricks result exceeds the configured byte limit.");

public static class DatabricksErrorCodes
{
    public const string ResultTooLarge = "DATABRICKS_RESULT_TOO_LARGE";
    public const string StatementFailed = "DATABRICKS_STATEMENT_FAILED";
    public const string ProtocolViolation = "DATABRICKS_PROTOCOL_VIOLATION";
    public const string InvalidRequest = "DATABRICKS_INVALID_REQUEST";
    public const string Configuration = "DATABRICKS_CONFIGURATION";
    public const string Http = "DATABRICKS_HTTP_ERROR";
    public const string Timeout = "DATABRICKS_TIMEOUT";
    public const string AuthFailed = "DATABRICKS_AUTH_FAILED";
}
