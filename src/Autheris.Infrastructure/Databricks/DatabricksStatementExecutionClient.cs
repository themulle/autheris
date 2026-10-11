namespace Autheris.Infrastructure.Databricks;

using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Databricks SQL Statement Execution API 2.0 client (design §6.3, WP-X0i). Security controls (SEC-ADG-09): every URL is built
/// from the configured host and fixed paths; server-supplied ids and chunk links are validated and never used as URLs;
/// disposition is always INLINE (<c>external_links</c> are rejected); redirects are never followed; response buffers are
/// bounded; tokens are held in memory only and never logged or put into exception messages; errors carry no driver text.
/// </summary>
public sealed partial class DatabricksStatementExecutionClient(
    HttpClient http,
    IOptions<GatewayOptions> options,
    IKeyVaultSecretProvider? secrets = null,
    IHostEnvironment? environment = null,
    DatabricksTokenCache? tokenCache = null,
    ILogger<DatabricksStatementExecutionClient>? logger = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>Allowance for the manifest and JSON framing on top of <c>ByteLimit</c> (also used for the HttpClient buffer).</summary>
    public const int ResponseOverheadBytes = 256 * 1024;

    private const int MaxParameters = 1000;
    private const int MaxChunks = 10_000;
    private const int MaxTokenResponseBytes = 64 * 1024;
    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    private readonly DatabricksTokenCache _tokenCache = tokenCache ?? new DatabricksTokenCache();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    [GeneratedRegex("^[0-9a-fA-F-]{36}$", RegexOptions.CultureInvariant)]
    private static partial Regex StatementIdRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterNameRegex();

    [GeneratedRegex(@"^(STRING|BOOLEAN|TINYINT|SMALLINT|INT|BIGINT|FLOAT|DOUBLE|DATE|TIMESTAMP|TIMESTAMP_NTZ|BINARY|DECIMAL\(\d{1,2},\d{1,2}\))$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterTypeRegex();

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeRegex();

    private sealed record Settings(DatabricksDataSourceOptions Databricks, int CommandTimeoutSeconds);

    public async Task<DatabricksStatementResult> ExecuteAsync(DatabricksStatementRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = ResolveSettings(request.DataSource);
        ValidateRequest(request);
        var token = await AcquireTokenAsync(settings.Databricks, ct).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.CommandTimeoutSeconds));
        string? statementId = null;
        try
        {
            var db = settings.Databricks;
            using var firstDoc = await SendAsync(db, token, HttpMethod.Post, "/api/2.0/sql/statements/", BuildBody(request, db), deadline.Token).ConfigureAwait(false);
            statementId = ReadStatementId(firstDoc.RootElement);
            var current = firstDoc;

            JsonDocument? polled = null;
            try
            {
                while (IsInProgress(current.RootElement))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(db.PollIntervalMilliseconds), _time, deadline.Token).ConfigureAwait(false);
                    polled?.Dispose();
                    polled = await SendAsync(db, token, HttpMethod.Get, $"/api/2.0/sql/statements/{statementId}", null, deadline.Token).ConfigureAwait(false);
                    if (!string.Equals(ReadStatementId(polled.RootElement), statementId, StringComparison.Ordinal))
                    {
                        throw Protocol("The statement id changed between calls.");
                    }

                    current = polled;
                }

                return await ReadResultAsync(db, token, statementId, current.RootElement, deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                polled?.Dispose();
            }
        }
        catch (OperationCanceledException) when (statementId is not null)
        {
            await TryCancelAsync(settings.Databricks, token, statementId).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new DatabricksProviderException(DatabricksErrorCodes.Timeout, "The Databricks statement exceeded the command timeout and was cancelled.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DatabricksProviderException(DatabricksErrorCodes.Timeout, "The Databricks statement exceeded the command timeout.");
        }
    }

    public async Task CancelAsync(string statementId, CancellationToken ct) => await CancelAsync(statementId, null, ct).ConfigureAwait(false);

    public async Task CancelAsync(string statementId, string? dataSource, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(statementId) || !StatementIdRegex().IsMatch(statementId))
        {
            throw new DatabricksProviderException(DatabricksErrorCodes.InvalidRequest, "The statement id has an invalid format.");
        }

        var settings = ResolveSettings(dataSource);
        var token = await AcquireTokenAsync(settings.Databricks, ct).ConfigureAwait(false);
        using var _ = await SendAsync(settings.Databricks, token, HttpMethod.Post, $"/api/2.0/sql/statements/{statementId}/cancel", null, ct).ConfigureAwait(false);
    }

    private async Task TryCancelAsync(DatabricksDataSourceOptions db, string token, string statementId)
    {
        try
        {
            using var cts = new CancellationTokenSource(CancelTimeout);
            using var _ = await SendAsync(db, token, HttpMethod.Post, $"/api/2.0/sql/statements/{statementId}/cancel", null, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger?.LogWarning("Databricks cancel call failed ({ExceptionType}).", ex.GetType().Name);
        }
    }

    // ---- configuration ----

    private Settings ResolveSettings(string? dataSource)
    {
        var connections = options.Value.DataSources.Connections;
        DataSourceConnectionOptions? match = null;
        if (!string.IsNullOrWhiteSpace(dataSource))
        {
            if (!connections.TryGetValue(dataSource, out match) || !DataSourceProvider.Is(match.Provider, DatabaseDialect.Databricks))
            {
                throw Config("The requested data source is not a configured Databricks data source.");
            }
        }
        else
        {
            var all = connections.Values.Where(c => DataSourceProvider.Is(c.Provider, DatabaseDialect.Databricks)).ToList();
            if (all.Count != 1)
            {
                throw Config("Exactly one Databricks data source must be configured unless the data source is named.");
            }

            match = all[0];
        }

        var db = match.Databricks ?? throw Config("The Databricks section of the data source is missing.");
        if (!DatabricksHostPolicy.IsValidHost(db.Host))
        {
            throw Config("The Databricks host must be one exact DNS name without scheme, port, path or wildcard.");
        }

        if (string.IsNullOrWhiteSpace(db.WarehouseId))
        {
            throw Config("The Databricks warehouse id is missing.");
        }

        if (db.Auth.Mode == DatabricksAuthMode.Pat && !(environment?.IsDevelopment() ?? false))
        {
            throw Config("Auth.Mode Pat is allowed in Development only.");
        }

        return new Settings(db, match.CommandTimeoutSeconds);
    }

    private static void ValidateRequest(DatabricksStatementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Statement))
        {
            throw Invalid("The statement is empty.");
        }

        if (request.Parameters.IsDefault)
        {
            return;
        }

        if (request.Parameters.Length > MaxParameters)
        {
            throw Invalid("Too many statement parameters.");
        }

        foreach (var p in request.Parameters)
        {
            if (p.Name is null || !ParameterNameRegex().IsMatch(p.Name))
            {
                throw Invalid("A statement parameter name is invalid.");
            }

            if (p.Type is null || !ParameterTypeRegex().IsMatch(p.Type))
            {
                throw Invalid("A statement parameter type is not supported.");
            }
        }
    }

    // ---- authentication ----

    private async Task<string> AcquireTokenAsync(DatabricksDataSourceOptions db, CancellationToken ct)
    {
        var auth = db.Auth;
        if (auth.Mode == DatabricksAuthMode.Pat)
        {
            return SecretReferenceResolver.Resolve(secrets, auth.PatRef, environment, true, logger, "Databricks PAT")
                   ?? throw Config("The Databricks PAT reference is missing.");
        }

        if (string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecretRef))
        {
            throw Config("OAuthM2M requires Auth.ClientId and Auth.ClientSecretRef.");
        }

        var now = _time.GetUtcNow();
        if (_tokenCache.TryGet(db.Host, auth.ClientId, now, out var cached))
        {
            return cached;
        }

        var secret = SecretReferenceResolver.Resolve(secrets, auth.ClientSecretRef, environment, true, logger, "Databricks client secret")
                     ?? throw Config("The Databricks client secret could not be resolved.");

        // SEC-ADG-09 item 3: the token endpoint is derived from the host, never configurable.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{db.Host}/oidc/v1/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "all-apis" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.ClientId}:{secret}")));

        using var response = await SendRawAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DatabricksProviderException(DatabricksErrorCodes.AuthFailed, $"Databricks authentication failed (HTTP {(int)response.StatusCode}).");
        }

        using var doc = ParseJson(await ReadBoundedAsync(response, MaxTokenResponseBytes, ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(tokenEl.GetString()))
        {
            throw new DatabricksProviderException(DatabricksErrorCodes.AuthFailed, "Databricks authentication returned no access token.");
        }

        var token = tokenEl.GetString()!;
        var lifetime = doc.RootElement.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) ? seconds : 300;
        _tokenCache.Set(db.Host, auth.ClientId, token, now.AddSeconds(Math.Max(30, lifetime - 60)));
        return token;
    }

    // ---- HTTP ----

    private async Task<JsonDocument> SendAsync(DatabricksDataSourceOptions db, string token, HttpMethod method, string path, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri($"https://{db.Host}{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await SendRawAsync(request, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            throw new DatabricksProviderException(DatabricksErrorCodes.Http, "Databricks answered with a redirect, which is never followed.");
        }

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _tokenCache.Remove(db.Host, db.Auth.ClientId ?? string.Empty);
                throw new DatabricksProviderException(DatabricksErrorCodes.AuthFailed, $"Databricks rejected the credentials (HTTP {status}).");
            }

            logger?.LogWarning("Databricks call failed with HTTP {StatusCode}.", status);
            throw new DatabricksProviderException(DatabricksErrorCodes.Http, $"Databricks answered with HTTP {status}.");
        }

        return ParseJson(await ReadBoundedAsync(response, checked((int)Math.Min(int.MaxValue - 1, db.ByteLimit + ResponseOverheadBytes)), ct).ConfigureAwait(false));
    }

    private Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct) =>
        http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } length && length > limit)
        {
            throw new DatabricksResultTooLargeException();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new ArrayBufferWriter<byte>(8192);
        while (true)
        {
            var memory = buffer.GetMemory(8192);
            var read = await stream.ReadAsync(memory, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.WrittenMemory;
            }

            buffer.Advance(read);
            if (buffer.WrittenCount > limit)
            {
                throw new DatabricksResultTooLargeException();
            }
        }
    }

    private static JsonDocument ParseJson(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw Protocol("The response is not valid JSON.");
        }
    }

    // ---- request / response mapping ----

    private static string BuildBody(DatabricksStatementRequest request, DatabricksDataSourceOptions db)
    {
        var wait = request.WaitTimeout < MinWait ? MinWait : request.WaitTimeout > MaxWait ? MaxWait : request.WaitTimeout;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("statement", request.Statement);
            w.WriteString("warehouse_id", db.WarehouseId);
            w.WriteString("catalog", string.IsNullOrWhiteSpace(request.Catalog) ? db.Catalog : request.Catalog);
            w.WriteString("schema", string.IsNullOrWhiteSpace(request.Schema) ? db.Schema : request.Schema);
            w.WriteStartArray("parameters");
            if (!request.Parameters.IsDefault)
            {
                foreach (var p in request.Parameters)
                {
                    w.WriteStartObject();
                    w.WriteString("name", p.Name);
                    if (p.Value is null)
                    {
                        w.WriteNull("value");
                    }
                    else
                    {
                        w.WriteString("value", p.Value);
                    }

                    w.WriteString("type", p.Type);
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
            w.WriteString("wait_timeout", $"{(int)wait.TotalSeconds}s");
            w.WriteString("on_wait_timeout", "CANCEL");
            w.WriteString("disposition", "INLINE");
            w.WriteString("format", "JSON_ARRAY");
            w.WriteNumber("byte_limit", db.ByteLimit);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ReadStatementId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("statement_id", out var el) || el.ValueKind != JsonValueKind.String ||
            el.GetString() is not { } id || !StatementIdRegex().IsMatch(id))
        {
            throw Protocol("The statement id has an invalid format.");
        }

        return id;
    }

    private static string ReadState(JsonElement root)
    {
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object &&
            status.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
        {
            return state.GetString()!;
        }

        throw Protocol("The response has no statement state.");
    }

    private static bool IsInProgress(JsonElement root) => ReadState(root) is "PENDING" or "RUNNING";

    private async Task<DatabricksStatementResult> ReadResultAsync(DatabricksDataSourceOptions db, string token, string statementId, JsonElement root, CancellationToken ct)
    {
        var state = ReadState(root);
        switch (state)
        {
            case "SUCCEEDED":
                break;
            case "FAILED":
                var code = root.GetProperty("status").TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object &&
                           err.TryGetProperty("error_code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String &&
                           codeEl.GetString() is { } c && ErrorCodeRegex().IsMatch(c)
                    ? c
                    : "UNKNOWN";
                logger?.LogWarning("Databricks statement failed with error code {ErrorCode}.", code);
                throw new DatabricksProviderException(DatabricksErrorCodes.StatementFailed, $"The Databricks statement failed (error code {code}).");
            case "CANCELED" or "CLOSED":
                throw new DatabricksProviderException(DatabricksErrorCodes.StatementFailed, $"The Databricks statement ended in state {state}.");
            default:
                throw Protocol("The statement state is not recognised.");
        }

        var columns = ReadColumns(root);
        var rows = new List<string?[]>();
        long dataBytes = 0;
        JsonElement? chunk = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? result : null;
        JsonDocument? chunkDoc = null;
        var lastIndex = 0;
        var chunkCount = 0;
        try
        {
            while (chunk is { } current)
            {
                if (current.TryGetProperty("external_links", out var links) && links.ValueKind != JsonValueKind.Null)
                {
                    throw Protocol("External links are not supported.");
                }

                AppendRows(current, rows, ref dataBytes, db.ByteLimit);

                if (!current.TryGetProperty("next_chunk_internal_link", out var next) || next.ValueKind == JsonValueKind.Null)
                {
                    break;
                }

                var index = ParseChunkLink(next, statementId);
                if (index <= lastIndex || ++chunkCount > MaxChunks)
                {
                    throw Protocol("The chunk sequence is invalid.");
                }

                lastIndex = index;
                var nextDoc = await SendAsync(db, token, HttpMethod.Get, $"/api/2.0/sql/statements/{statementId}/result/chunks/{index.ToString(CultureInfo.InvariantCulture)}", null, ct).ConfigureAwait(false);
                chunkDoc?.Dispose();
                chunkDoc = nextDoc;
                chunk = chunkDoc.RootElement;
            }
        }
        finally
        {
            chunkDoc?.Dispose();
        }

        return new DatabricksStatementResult(statementId, columns, rows);
    }

    private static int ParseChunkLink(JsonElement link, string statementId)
    {
        // SEC-ADG-09 item 1: only a relative link into this statement's own chunk path is followed; the request path is rebuilt from the index.
        var prefix = $"/api/2.0/sql/statements/{statementId}/result/chunks/";
        if (link.ValueKind != JsonValueKind.String || link.GetString() is not { } text ||
            !text.StartsWith(prefix, StringComparison.Ordinal) ||
            text.Length == prefix.Length || text.Length - prefix.Length > 6 ||
            text.AsSpan(prefix.Length).ContainsAnyExcept("0123456789"))
        {
            throw Protocol("The next chunk link is not an allowed relative link.");
        }

        return int.Parse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static ImmutableArray<DatabricksColumn> ReadColumns(JsonElement root)
    {
        if (!root.TryGetProperty("manifest", out var manifest) || manifest.ValueKind != JsonValueKind.Object)
        {
            return ImmutableArray<DatabricksColumn>.Empty;
        }

        if (manifest.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
        {
            throw new DatabricksResultTooLargeException();
        }

        var builder = ImmutableArray.CreateBuilder<DatabricksColumn>();
        if (manifest.TryGetProperty("schema", out var schema) && schema.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
        {
            foreach (var col in cols.EnumerateArray())
            {
                var name = col.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : string.Empty;
                var type = col.TryGetProperty("type_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "STRING";
                builder.Add(new DatabricksColumn(name, type));
            }
        }

        return builder.ToImmutable();
    }

    private static void AppendRows(JsonElement chunk, List<string?[]> rows, ref long dataBytes, long byteLimit)
    {
        if (!chunk.TryGetProperty("data_array", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                throw Protocol("A result row is not an array.");
            }

            var cells = new string?[row.GetArrayLength()];
            var i = 0;
            foreach (var cell in row.EnumerateArray())
            {
                var text = cell.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => cell.GetString(),
                    _ => cell.GetRawText()
                };
                dataBytes += text is null ? 0 : Encoding.UTF8.GetByteCount(text);
                if (dataBytes > byteLimit)
                {
                    throw new DatabricksResultTooLargeException();
                }

                cells[i++] = text;
            }

            rows.Add(cells);
        }
    }

    private static DatabricksProviderException Config(string message) => new(DatabricksErrorCodes.Configuration, message);

    private static DatabricksProviderException Invalid(string message) => new(DatabricksErrorCodes.InvalidRequest, message);

    private static DatabricksProviderException Protocol(string message) => new(DatabricksErrorCodes.ProtocolViolation, message);
}
