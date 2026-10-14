namespace Autheris.Tests.Unit.Databricks;

using System.Net;
using System.Net.Http;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, string? Authorization);

internal sealed class FakeDatabricksHandler(Func<RecordedRequest, HttpResponseMessage> router) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString());
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        return router(recorded);
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    /// <summary>The same lines with their level (to assert that something was logged as Critical).</summary>
    public List<(LogLevel Level, string Text)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = formatter(state, exception) + (exception is null ? string.Empty : exception.ToString());
        Lines.Add(line);
        Entries.Add((logLevel, line));
    }
}

internal static class DatabricksTestSupport
{
    public const string Host = "adb-123.azuredatabricks.net";
    public const string StatementId = "01ef1234-5678-9abc-def0-123456789abc";
    public const string OtherStatementId = "01ef9999-5678-9abc-def0-123456789abc";
    public const string AccessToken = "tok-ABC-super-secret";
    public const string ClientSecret = "sp-secret-value-xyz";
    public const string Pat = "dapi-pat-secret-123";

    public static string StatementsUrl => $"https://{Host}/api/2.0/sql/statements/";

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Ok(string json) => Json(HttpStatusCode.OK, json);

    public static HttpResponseMessage TokenOk() => Ok("{\"access_token\":\"" + AccessToken + "\",\"token_type\":\"Bearer\",\"expires_in\":3600}");

    public static string Succeeded(string id, string rowsJson, string? nextLink = null, bool truncated = false, string? extra = null) =>
        "{\"statement_id\":\"" + id + "\",\"status\":{\"state\":\"SUCCEEDED\"}," +
        "\"manifest\":{\"format\":\"JSON_ARRAY\",\"truncated\":" + (truncated ? "true" : "false") + "," +
        "\"schema\":{\"column_count\":2,\"columns\":[{\"name\":\"id\",\"type_name\":\"INT\",\"position\":0},{\"name\":\"name\",\"type_name\":\"STRING\",\"position\":1}]}}," +
        "\"result\":{\"chunk_index\":0,\"row_offset\":0,\"data_array\":" + rowsJson +
        (nextLink is null ? string.Empty : ",\"next_chunk_internal_link\":\"" + nextLink + "\"") + extra + "}}";

    public static string State(string id, string state) =>
        "{\"statement_id\":\"" + id + "\",\"status\":{\"state\":\"" + state + "\"}}";

    public static GatewayOptions Options(
        DatabricksAuthMode mode = DatabricksAuthMode.OAuthM2M,
        string host = Host,
        long byteLimit = 26214400,
        Action<Dictionary<string, DataSourceConnectionOptions>>? extra = null)
    {
        var connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbx"] = new()
            {
                Provider = "databricks",
                CommandTimeoutSeconds = 30,
                Databricks = new DatabricksDataSourceOptions
                {
                    Host = host,
                    WarehouseId = "wh-1",
                    Catalog = "main",
                    Schema = "default",
                    ByteLimit = byteLimit,
                    PollIntervalMilliseconds = 10,
                    Auth = mode == DatabricksAuthMode.Pat
                        ? new DatabricksAuthOptions { Mode = DatabricksAuthMode.Pat, PatRef = "kv://dbx-pat" }
                        : new DatabricksAuthOptions { ClientId = "sp-id", ClientSecretRef = "kv://dbx-sp-secret" }
                }
            }
        };
        extra?.Invoke(connections);
        return new GatewayOptions { DataSources = new SqlDataSourceOptions { Connections = connections } };
    }

    public static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    /// <summary>Key Vault that resolves the known references to distinct secrets.</summary>
    public static IKeyVaultSecretProvider Vault()
    {
        var vault = Substitute.For<IKeyVaultSecretProvider>();
        vault.GetSecretBytes("kv://dbx-sp-secret").Returns(Encoding.UTF8.GetBytes(ClientSecret));
        vault.GetSecretBytes("kv://dbx-pat").Returns(Encoding.UTF8.GetBytes(Pat));
        return vault;
    }

    public static DatabricksStatementExecutionClient Client(
        FakeDatabricksHandler handler,
        GatewayOptions? options = null,
        string environment = "Production",
        IKeyVaultSecretProvider? vault = null,
        ILogger<DatabricksStatementExecutionClient>? logger = null,
        HttpMessageHandler? outer = null)
    {
        var http = new HttpClient(outer ?? handler);
        return new DatabricksStatementExecutionClient(
            http,
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            vault ?? Vault(),
            Env(environment),
            new DatabricksTokenCache(),
            logger);
    }

    public static DatabricksStatementRequest Request(
        string statement = "SELECT id, name FROM t WHERE a = :p0",
        IEnumerable<DatabricksParameter>? parameters = null,
        TimeSpan? wait = null) =>
        new(statement, [.. parameters ?? [new DatabricksParameter("p0", "1", "INT")]], "main", "default", wait ?? TimeSpan.FromSeconds(30));

    /// <summary>Standard router: token endpoint, POST statement, optional extra routes.</summary>
    public static Func<RecordedRequest, HttpResponseMessage> Router(
        Func<RecordedRequest, HttpResponseMessage?> statementRoutes) =>
        r => r.Uri.AbsolutePath == "/oidc/v1/token" ? TokenOk() : statementRoutes(r) ?? Json(HttpStatusCode.NotFound, "{}");

    public static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new Xunit.Sdk.XunitException("Expected an exception but none was thrown.");
    }
}
