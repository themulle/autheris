namespace Autheris.Tests.SqlCompilerGate.Databricks;

using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Tests.SqlCompilerGate.Model;
using TrinoSqlEngine.Ast.Emit;

/// <summary>A statement parameter handed to the warehouse by name (never inlined into the SQL text).</summary>
public sealed record GateParameter(string Name, string? Value, string Type);

/// <summary>The only way a statement reaches the warehouse in the G9 suite. The recording fake counts what would have been sent.</summary>
public interface IDatabricksGateTransport
{
    Task<IReadOnlyList<string?[]>> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct);
}

/// <summary>Recording transport used whenever the live warehouse is not configured (and for every denial assertion).</summary>
public sealed class RecordingGateTransport(Func<string, IReadOnlyList<string?[]>>? respond = null, Func<string, bool>? failOn = null) : IDatabricksGateTransport
{
    public List<string> Statements { get; } = [];

    public Task<IReadOnlyList<string?[]>> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
    {
        Statements.Add(sql);
        if (failOn?.Invoke(sql) == true)
        {
            throw new InvalidOperationException("Injected warehouse failure.");
        }

        return Task.FromResult(respond?.Invoke(sql) ?? []);
    }
}

/// <summary>
/// The scratch schema the live suite creates per run: <c>autheris_g9_&lt;run id&gt;</c>. The name is validated before every DDL
/// statement so a hostile or mistyped name can neither escape the prefix nor reach another schema or a catalog.
/// </summary>
public static partial class ScratchSchema
{
    public const string Prefix = "autheris_g9_";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    [GeneratedRegex(@"^autheris_g9_[0-9]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool IsScratchName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static string ValidateScratchSchemaName(string schemaName) =>
        IsScratchName(schemaName) ? schemaName : throw new ArgumentException("Not a G9 scratch schema name.", nameof(schemaName));

    public static string BuildCreateSchemaSql(string schemaName) => $"CREATE SCHEMA IF NOT EXISTS {ValidateScratchSchemaName(schemaName)}";

    /// <summary>CASCADE only ever follows a validated name; there is no code path that drops a catalog.</summary>
    public static string BuildDropSchemaSql(string schemaName) => $"DROP SCHEMA IF EXISTS {ValidateScratchSchemaName(schemaName)} CASCADE";

    public static IReadOnlyList<string> SelectStaleScratchSchemasToDrop(IEnumerable<(string Name, DateTimeOffset CreatedAtUtc)> schemas, DateTimeOffset nowUtc) =>
        [.. schemas.Where(s => IsScratchName(s.Name) && nowUtc - s.CreatedAtUtc > StaleAfter).Select(s => s.Name).Order(StringComparer.Ordinal)];

    public static Task CreateAsync(IDatabricksGateTransport transport, string schemaName, CancellationToken ct) =>
        transport.ExecuteAsync(BuildCreateSchemaSql(schemaName), [], ct);

    public static Task DropAsync(IDatabricksGateTransport transport, string schemaName, CancellationToken ct) =>
        transport.ExecuteAsync(BuildDropSchemaSql(schemaName), [], ct);

    /// <summary>Drops scratch schemas left behind by cancelled runs. Anything without the prefix is never listed for dropping.</summary>
    public static async Task<IReadOnlyList<string>> SweepAsync(IDatabricksGateTransport transport, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var rows = await transport.ExecuteAsync(
            "SELECT schema_name, created FROM information_schema.schemata WHERE schema_name LIKE 'autheris\\_g9\\_%'", [], ct);
        var schemas = rows
            .Where(r => r.Length >= 2 && r[0] is not null && DateTimeOffset.TryParse(r[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            .Select(r => (r[0]!, DateTimeOffset.Parse(r[1]!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)))
            .ToList();
        var stale = SelectStaleScratchSchemasToDrop(schemas, nowUtc);
        foreach (var name in stale)
        {
            await DropAsync(transport, name, ct);
        }

        return stale;
    }
}

/// <summary>The fixture of one live run: the <c>orders</c> table of the gate model, created and filled inside the validated scratch schema.</summary>
public static class DatabricksFixture
{
    public static string DefinitionOf(string schemaName)
    {
        var table = GateModel.Table("orders");
        var columns = table.Columns.Select(c => $"{c.Name} {c.Kind switch
        {
            ColumnKind.Whole => "BIGINT",
            ColumnKind.Fixed => "DECIMAL(18,2)",
            ColumnKind.Timestamp => "TIMESTAMP",
            _ => "STRING"
        }}");
        return $"CREATE TABLE {ScratchSchema.ValidateScratchSchemaName(schemaName)}.orders ({string.Join(", ", columns)})";
    }

    /// <summary>Fixture rows are constants of the gate model (no external input), so they are written as literals.</summary>
    public static IEnumerable<string> InsertsOf(string schemaName)
    {
        string schema = ScratchSchema.ValidateScratchSchemaName(schemaName);
        foreach (var row in GateModel.Rows.Where(r => r.Table == "orders"))
        {
            var literals = row.Values.Select(v => v switch
            {
                null => "NULL",
                string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
                DateTime d => "TIMESTAMP '" + d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'",
                decimal m => m.ToString(CultureInfo.InvariantCulture),
                _ => Convert.ToString(v, CultureInfo.InvariantCulture)!
            });
            yield return $"INSERT INTO {schema}.orders VALUES ({string.Join(", ", literals)})";
        }
    }

    public static string TypeName(SqlParameterType type) => type switch
    {
        SqlParameterType.Int32 => "INT",
        SqlParameterType.Int64 => "BIGINT",
        SqlParameterType.Decimal => "DECIMAL(38,18)",
        SqlParameterType.Double => "DOUBLE",
        SqlParameterType.Boolean => "BOOLEAN",
        SqlParameterType.Date => "DATE",
        SqlParameterType.Timestamp => "TIMESTAMP",
        _ => "STRING"
    };

    public static IReadOnlyList<GateParameter> ParametersOf(ImmutableArray<BoundParameter> bound) =>
        [.. bound.Select(p => new GateParameter(p.Marker.TrimStart(':', '@'), p.Value is null ? null : Convert.ToString(p.Value, CultureInfo.InvariantCulture), TypeName(p.Type)))];
}

/// <summary>
/// Live transport over the Databricks SQL Statement Execution REST API with OAuth M2M. It is only created when the five G9 secrets
/// are present (the nightly <c>Live Databricks suites</c> step). The token and the client secret are never logged or put in a message.
/// </summary>
public sealed class LiveDatabricksGateTransport : IDatabricksGateTransport, IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string _host;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _warehouseId;
    private readonly string? _catalog;
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    private LiveDatabricksGateTransport(string host, string clientId, string clientSecret, string warehouseId, string? catalog)
    {
        _host = host;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _warehouseId = warehouseId;
        _catalog = catalog;
    }

    public static LiveDatabricksGateTransport? TryCreateFromEnvironment()
    {
        string? host = Environment.GetEnvironmentVariable("DATABRICKS_HOST");
        string? id = Environment.GetEnvironmentVariable("DATABRICKS_CLIENT_ID");
        string? secret = Environment.GetEnvironmentVariable("DATABRICKS_CLIENT_SECRET");
        string? warehouse = Environment.GetEnvironmentVariable("DATABRICKS_WAREHOUSE_ID");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(warehouse))
        {
            return null;
        }

        string normalizedHost = host.Trim().Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        string? catalog = Environment.GetEnvironmentVariable("DATABRICKS_CATALOG");
        return new LiveDatabricksGateTransport(normalizedHost, id, secret, warehouse, string.IsNullOrWhiteSpace(catalog) ? null : catalog);
    }

    public async Task<IReadOnlyList<string?[]>> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
    {
        string token = await TokenAsync(ct);
        var body = new Dictionary<string, object?>
        {
            ["statement"] = sql,
            ["warehouse_id"] = _warehouseId,
            ["wait_timeout"] = "30s",
            ["on_wait_timeout"] = "CANCEL",
            ["disposition"] = "INLINE",
            ["format"] = "JSON_ARRAY",
            ["parameters"] = parameters.Select(p => new { name = p.Name, value = p.Value, type = p.Type }).ToArray()
        };
        if (_catalog is not null)
        {
            body["catalog"] = _catalog;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{_host}/api/2.0/sql/statements/")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Databricks statement failed (HTTP {(int)response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        string state = doc.RootElement.GetProperty("status").GetProperty("state").GetString() ?? string.Empty;
        if (state != "SUCCEEDED")
        {
            throw new InvalidOperationException($"Databricks statement ended in state {state}.");
        }

        var rows = new List<string?[]>();
        if (doc.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("data_array", out var data))
        {
            foreach (var row in data.EnumerateArray())
            {
                rows.Add([.. row.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? null : c.GetString())]);
            }
        }

        return rows;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
        {
            return _token;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{_host}/oidc/v1/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "all-apis" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}")));
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Databricks authentication failed (HTTP {(int)response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _token = doc.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Databricks authentication returned no token.");
        int seconds = doc.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 300;
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, seconds - 60));
        return _token;
    }

    public void Dispose() => _http.Dispose();
}
