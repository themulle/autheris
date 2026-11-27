namespace Autheris.Infrastructure.Databricks.Provisioning;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Provisioning;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Unity Catalog permissions and workspace SCIM client of the provisioning path. Security controls: every URL is built from
/// the configured workspace host and fixed paths; securable names, principals, privileges and SCIM filter values are validated
/// before use; every securable must lie inside the data source's allowed schemas; only the one addable privilege per securable
/// kind may be added; the token is the dedicated provisioning principal's and is never logged; redirects are never followed;
/// responses are bounded; failures carry only stable codes.
/// </summary>
public sealed partial class UnityCatalogPermissionsClient(
    HttpClient http,
    IOptionsMonitor<GatewayOptions> options,
    IDatabricksCredentialProvider credentials,
    ILogger<UnityCatalogPermissionsClient>? logger = null) : IUnityCatalogPermissionsClient
{
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public const int MaxChangesPerUpdate = 100;
    public const int MaxPages = 100;
    private const int MaxPrincipalLength = 255;
    private const int MaxScimResults = 10;

    [GeneratedRegex(@"^[A-Z][A-Z_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrivilegeRegex();

    [GeneratedRegex(@"^[A-Za-z0-9+/=_.\-]{1,2048}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PageTokenRegex();

    private sealed record Settings(DatabricksDataSourceOptions Credentials, string Host, IReadOnlySet<string> AllowedSchemas);

    public async Task<IReadOnlyList<UcPrivilegeAssignment>> GetPermissionsAsync(
        string dataSource, UcSecurableType type, string fullName, CancellationToken ct)
    {
        var settings = ResolveSettings(dataSource);
        var path = PermissionsPath(settings, type, fullName);
        var result = new List<UcPrivilegeAssignment>();
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = pageToken is null ? path : path + "?page_token=" + Uri.EscapeDataString(pageToken);
            using var doc = await SendAsync(settings, HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            result.AddRange(ReadAssignments(doc.RootElement));
            pageToken = ReadPageToken(doc.RootElement);
            if (pageToken is null)
            {
                return result;
            }
        }

        throw new UcProvisioningException(UcErrorCodes.ResponseTooLarge);
    }

    public async Task<IReadOnlyList<UcPrivilegeAssignment>> UpdatePermissionsAsync(
        string dataSource, UcSecurableType type, string fullName, IReadOnlyList<UcPermissionChange> changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var settings = ResolveSettings(dataSource);
        var path = PermissionsPath(settings, type, fullName);
        if (changes.Count is 0 or > MaxChangesPerUpdate)
        {
            throw new UcProvisioningException(UcErrorCodes.InvalidRequest);
        }

        var body = BuildUpdateBody(type, changes);
        using var doc = await SendAsync(settings, HttpMethod.Patch, path, body, ct).ConfigureAwait(false);
        return ReadAssignments(doc.RootElement);
    }

    public async Task<bool> HasEffectivePrivilegeAsync(
        string dataSource, UcSecurableType type, string fullName, string principal, IReadOnlyCollection<string> privileges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(privileges);
        var settings = ResolveSettings(dataSource);
        if (!Enum.IsDefined(type) || !UcSecurableNames.IsInsideAllowedSchemas(type, fullName, settings.AllowedSchemas))
        {
            throw new UcProvisioningException(UcErrorCodes.SecurableNotAllowed);
        }

        if (!IsValidPrincipal(principal) || privileges.Count == 0)
        {
            throw new UcProvisioningException(UcErrorCodes.InvalidRequest);
        }

        var path = "/api/2.1/unity-catalog/effective-permissions/" + UcPrivileges.ApiSegment(type) + "/" + Uri.EscapeDataString(fullName) +
                   "?principal=" + Uri.EscapeDataString(principal);
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = pageToken is null ? path : path + "&page_token=" + Uri.EscapeDataString(pageToken);
            using var doc = await SendAsync(settings, HttpMethod.Get, url, null, ct, forbiddenIsExpected: true).ConfigureAwait(false);
            if (ReadEffectivePrivileges(doc.RootElement).Overlaps(privileges))
            {
                return true;
            }

            pageToken = ReadPageToken(doc.RootElement);
            if (pageToken is null)
            {
                return false;
            }
        }

        throw new UcProvisioningException(UcErrorCodes.ResponseTooLarge);
    }

    public async Task<UcPrincipalResolution> ResolvePrincipalAsync(
        string dataSource, GranteeType type, string externalId, CancellationToken ct)
    {
        var settings = ResolveSettings(dataSource);
        if (!Guid.TryParse(externalId?.Trim(), out var id))
        {
            return UcPrincipalResolution.NotFound;
        }

        var key = id.ToString("D");
        return type switch
        {
            GranteeType.Group => await ResolveGroupAsync(settings, key, ct).ConfigureAwait(false),
            GranteeType.User => await ResolveUserAsync(settings, key, ct).ConfigureAwait(false),
            GranteeType.ServicePrincipal => await ResolveServicePrincipalAsync(settings, key, ct).ConfigureAwait(false),
            _ => UcPrincipalResolution.NotFound
        };
    }

    // ---- principal resolution ----

    private async Task<UcPrincipalResolution> ResolveGroupAsync(Settings settings, string externalId, CancellationToken ct)
    {
        var matches = await ScimQueryAsync(settings, "Groups", "externalId eq \"" + externalId + "\"", "id,displayName,externalId", ct).ConfigureAwait(false);
        if (matches.NotSingle is { } notSingle)
        {
            return notSingle;
        }

        var group = matches.Items[0];
        var displayName = ReadString(group, "displayName");
        if (!SameId(ReadString(group, "externalId"), externalId) || !IsValidPrincipal(displayName))
        {
            return UcPrincipalResolution.NotFound;
        }

        // Unity Catalog names a group by its display name, which is neither unique nor immutable: it must name exactly this group.
        var sameName = await ScimQueryAsync(settings, "Groups", "displayName eq \"" + EscapeScim(displayName!) + "\"", "id,displayName,externalId", ct).ConfigureAwait(false);
        if (sameName.NotSingle is not null || !SameId(ReadString(sameName.Items[0], "externalId"), externalId))
        {
            return UcPrincipalResolution.Ambiguous;
        }

        return UcPrincipalResolution.Resolved(new UcPrincipal(GranteeType.Group, displayName!, externalId));
    }

    private async Task<UcPrincipalResolution> ResolveUserAsync(Settings settings, string externalId, CancellationToken ct)
    {
        var matches = await ScimQueryAsync(settings, "Users", "externalId eq \"" + externalId + "\"", "id,userName,externalId", ct).ConfigureAwait(false);
        if (matches.NotSingle is { } notSingle)
        {
            return notSingle;
        }

        var userName = ReadString(matches.Items[0], "userName");
        return SameId(ReadString(matches.Items[0], "externalId"), externalId) && IsValidPrincipal(userName)
            ? UcPrincipalResolution.Resolved(new UcPrincipal(GranteeType.User, userName!, externalId))
            : UcPrincipalResolution.NotFound;
    }

    private async Task<UcPrincipalResolution> ResolveServicePrincipalAsync(Settings settings, string applicationId, CancellationToken ct)
    {
        var matches = await ScimQueryAsync(settings, "ServicePrincipals", "applicationId eq \"" + applicationId + "\"", "id,applicationId", ct).ConfigureAwait(false);
        if (matches.NotSingle is { } notSingle)
        {
            return notSingle;
        }

        return SameId(ReadString(matches.Items[0], "applicationId"), applicationId)
            ? UcPrincipalResolution.Resolved(new UcPrincipal(GranteeType.ServicePrincipal, applicationId, applicationId))
            : UcPrincipalResolution.NotFound;
    }

    private async Task<ScimMatches> ScimQueryAsync(Settings settings, string resource, string filter, string attributes, CancellationToken ct)
    {
        var path = "/api/2.0/preview/scim/v2/" + resource +
                   "?filter=" + Uri.EscapeDataString(filter) +
                   "&attributes=" + Uri.EscapeDataString(attributes) +
                   "&count=" + MaxScimResults.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var doc = await SendAsync(settings, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UcProvisioningException(UcErrorCodes.Protocol);
        }

        var result = new List<JsonElement>();
        if (root.TryGetProperty("Resources", out var resources))
        {
            if (resources.ValueKind != JsonValueKind.Array)
            {
                throw new UcProvisioningException(UcErrorCodes.Protocol);
            }

            foreach (var item in resources.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new UcProvisioningException(UcErrorCodes.Protocol);
                }

                result.Add(item.Clone());
            }
        }

        // A total above the listed page means more matches exist than were returned: never pick one of them.
        var truncated = root.TryGetProperty("totalResults", out var total) && total.TryGetInt32(out var totalResults) && totalResults > result.Count;
        return new ScimMatches(result, truncated);
    }

    private sealed record ScimMatches(List<JsonElement> Items, bool Truncated)
    {
        /// <summary>Null when exactly one match exists; otherwise the not-found or ambiguous outcome.</summary>
        public UcPrincipalResolution? NotSingle =>
            Truncated || Items.Count > 1 ? UcPrincipalResolution.Ambiguous
            : Items.Count == 0 ? UcPrincipalResolution.NotFound
            : null;
    }

    private static string EscapeScim(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static bool SameId(string? actual, string expected) =>
        Guid.TryParse(actual, out var a) && Guid.TryParse(expected, out var e) && a == e;

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ---- configuration and guards ----

    private Settings ResolveSettings(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource) ||
            !options.CurrentValue.DataSources.Connections.TryGetValue(dataSource, out var connection) ||
            connection is null ||
            !DataSourceProvider.Is(connection.Provider, DatabaseDialect.Databricks) ||
            connection.Databricks is not { Provisioning: { } provisioning } db ||
            !DatabricksHostPolicy.IsValidHost(db.Host) ||
            provisioning.Auth is not { Mode: DatabricksAuthMode.OAuthM2M } auth ||
            string.IsNullOrWhiteSpace(auth.ClientId))
        {
            throw new UcProvisioningException(UcErrorCodes.Configuration);
        }

        var allowed = UcSecurableNames.AllowedSchemaSet(provisioning.AllowedSchemas);
        if (allowed.Count == 0)
        {
            throw new UcProvisioningException(UcErrorCodes.Configuration);
        }

        // The provisioning principal gets its own credential set (and token cache entry): host and auth only, never the query principal.
        var credentialOptions = new DatabricksDataSourceOptions
        {
            Host = db.Host,
            WarehouseId = db.WarehouseId,
            Catalog = db.Catalog,
            Schema = db.Schema,
            Auth = auth
        };
        return new Settings(credentialOptions, db.Host, allowed);
    }

    private static string PermissionsPath(Settings settings, UcSecurableType type, string fullName)
    {
        if (!Enum.IsDefined(type) || !UcSecurableNames.IsInsideAllowedSchemas(type, fullName, settings.AllowedSchemas))
        {
            throw new UcProvisioningException(UcErrorCodes.SecurableNotAllowed);
        }

        return "/api/2.1/unity-catalog/permissions/" + UcPrivileges.ApiSegment(type) + "/" + Uri.EscapeDataString(fullName);
    }

    private static string BuildUpdateBody(UcSecurableType type, IReadOnlyList<UcPermissionChange> changes)
    {
        // Catalogs are never changed; on a schema only USE_SCHEMA moves; on a table SELECT is added and any privilege may be removed.
        var addable = UcPrivileges.AddableOn(type) ?? throw new UcProvisioningException(UcErrorCodes.PrivilegeNotAllowed);
        Func<string, bool> removable = type == UcSecurableType.Table
            ? static p => PrivilegeRegex().IsMatch(p)
            : p => string.Equals(p, addable, StringComparison.Ordinal);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartArray("changes");
            foreach (var change in changes)
            {
                if (change is null || !IsValidPrincipal(change.Principal) || (change.Add.Count == 0 && change.Remove.Count == 0))
                {
                    throw new UcProvisioningException(UcErrorCodes.InvalidRequest);
                }

                w.WriteStartObject();
                w.WriteString("principal", change.Principal);
                WritePrivileges(w, "add", change.Add, p => string.Equals(p, addable, StringComparison.Ordinal));
                WritePrivileges(w, "remove", change.Remove, removable);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WritePrivileges(Utf8JsonWriter w, string name, IReadOnlyList<string> privileges, Func<string, bool> allowed)
    {
        w.WriteStartArray(name);
        foreach (var privilege in privileges)
        {
            if (string.IsNullOrEmpty(privilege) || !PrivilegeRegex().IsMatch(privilege))
            {
                throw new UcProvisioningException(UcErrorCodes.InvalidRequest);
            }

            if (!allowed(privilege))
            {
                throw new UcProvisioningException(UcErrorCodes.PrivilegeNotAllowed);
            }

            w.WriteStringValue(privilege);
        }

        w.WriteEndArray();
    }

    private static bool IsValidPrincipal(string? principal)
    {
        if (string.IsNullOrWhiteSpace(principal) || principal.Length > MaxPrincipalLength || principal.Trim().Length != principal.Length)
        {
            return false;
        }

        foreach (var ch in principal)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    // ---- response mapping ----

    private static List<UcPrivilegeAssignment> ReadAssignments(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UcProvisioningException(UcErrorCodes.Protocol);
        }

        var result = new List<UcPrivilegeAssignment>();
        if (!root.TryGetProperty("privilege_assignments", out var assignments) || assignments.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (assignments.ValueKind != JsonValueKind.Array)
        {
            throw new UcProvisioningException(UcErrorCodes.Protocol);
        }

        foreach (var item in assignments.EnumerateArray())
        {
            var principal = ReadString(item, "principal");
            if (!IsValidPrincipal(principal))
            {
                throw new UcProvisioningException(UcErrorCodes.Protocol);
            }

            var privileges = new List<string>();
            if (item.TryGetProperty("privileges", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in list.EnumerateArray())
                {
                    if (p.ValueKind == JsonValueKind.String && p.GetString() is { } privilege && PrivilegeRegex().IsMatch(privilege))
                    {
                        privileges.Add(privilege);
                    }
                    else
                    {
                        throw new UcProvisioningException(UcErrorCodes.Protocol);
                    }
                }
            }

            result.Add(new UcPrivilegeAssignment(principal!, privileges));
        }

        return result;
    }

    private static HashSet<string> ReadEffectivePrivileges(JsonElement root)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UcProvisioningException(UcErrorCodes.Protocol);
        }

        if (!root.TryGetProperty("privilege_assignments", out var assignments) || assignments.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (assignments.ValueKind != JsonValueKind.Array)
        {
            throw new UcProvisioningException(UcErrorCodes.Protocol);
        }

        foreach (var item in assignments.EnumerateArray())
        {
            // The response is already scoped to the asked principal; privileges it holds through a group are listed under the group's
            // name (for example "account users"), so every returned assignment counts.
            if (!item.TryGetProperty("privileges", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in list.EnumerateArray())
            {
                var privilege = entry.ValueKind == JsonValueKind.Object ? ReadString(entry, "privilege") : null;
                if (privilege is not null && PrivilegeRegex().IsMatch(privilege))
                {
                    result.Add(privilege);
                }
            }
        }

        return result;
    }

    private static string? ReadPageToken(JsonElement root)
    {
        var token = ReadString(root, "next_page_token");
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return PageTokenRegex().IsMatch(token) ? token : throw new UcProvisioningException(UcErrorCodes.Protocol);
    }

    // ---- HTTP ----

    private async Task<JsonDocument> SendAsync(
        Settings settings, HttpMethod method, string pathAndQuery, string? body, CancellationToken ct, bool forbiddenIsExpected = false)
    {
        var token = await AcquireTokenAsync(settings, ct).ConfigureAwait(false);
        using var response = await SendOnceAsync(settings, token, method, pathAndQuery, body, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // An expired or rotated credential: drop only this principal's cached token (the secret is re-read on acquire), retry once.
            credentials.EvictToken(settings.Credentials, null);
            var refreshed = await AcquireTokenAsync(settings, ct).ConfigureAwait(false);
            using var retry = await SendOnceAsync(settings, refreshed, method, pathAndQuery, body, ct).ConfigureAwait(false);
            return await ReadResponseAsync(settings, retry, forbiddenIsExpected, ct).ConfigureAwait(false);
        }

        return await ReadResponseAsync(settings, response, forbiddenIsExpected, ct).ConfigureAwait(false);
    }

    private async Task<string> AcquireTokenAsync(Settings settings, CancellationToken ct)
    {
        try
        {
            return await credentials.AcquireTokenAsync(settings.Credentials, null, ct).ConfigureAwait(false);
        }
        catch (DatabricksProviderException ex)
        {
            throw new UcProvisioningException(
                ex.Code == DatabricksErrorCodes.Configuration ? UcErrorCodes.Configuration : UcErrorCodes.AuthFailed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UcProvisioningException(UcErrorCodes.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            throw new UcProvisioningException(UcErrorCodes.AuthFailed);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Settings settings, string token, HttpMethod method, string pathAndQuery, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri("https://" + settings.Host + pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UcProvisioningException(UcErrorCodes.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UcProvisioningException(UcErrorCodes.Unavailable);
        }
    }

    private async Task<JsonDocument> ReadResponseAsync(Settings settings, HttpResponseMessage response, bool forbiddenIsExpected, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            throw new UcProvisioningException(UcErrorCodes.Http);
        }

        ReadOnlyMemory<byte> bytes;
        try
        {
            bytes = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UcProvisioningException(UcErrorCodes.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UcProvisioningException(UcErrorCodes.Unavailable);
        }

        if (response.IsSuccessStatusCode)
        {
            try
            {
                return JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                throw new UcProvisioningException(UcErrorCodes.Protocol);
            }
        }

        var remoteCode = ReadRemoteErrorCode(bytes);
        if (!(forbiddenIsExpected && response.StatusCode == HttpStatusCode.Forbidden))
        {
            logger?.LogWarning("Unity Catalog call failed with HTTP {StatusCode} ({ErrorCode}).", status, remoteCode ?? "none");
        }
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => UcErrorCodes.AuthFailed,
            HttpStatusCode.Forbidden => UcErrorCodes.Forbidden,
            HttpStatusCode.NotFound => UcErrorCodes.NotFound,
            HttpStatusCode.TooManyRequests => UcErrorCodes.RateLimited,
            _ when status >= 500 => UcErrorCodes.Unavailable,
            _ => UcErrorCodes.Rejected
        };
        // A refused read of another principal's effective permissions is expected (it needs rights the provisioning principal lacks)
        // and says nothing about the token.
        if (code == UcErrorCodes.AuthFailed || (code == UcErrorCodes.Forbidden && !forbiddenIsExpected))
        {
            credentials.EvictToken(settings.Credentials, null);
        }

        throw new UcProvisioningException(code, remoteCode);
    }

    private static string? ReadRemoteErrorCode(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var code = ReadString(doc.RootElement, "error_code");
            return code is not null && PrivilegeRegex().IsMatch(code) ? code : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } length && length > MaxResponseBytes)
        {
            throw new UcProvisioningException(UcErrorCodes.ResponseTooLarge);
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
            if (buffer.WrittenCount > MaxResponseBytes)
            {
                throw new UcProvisioningException(UcErrorCodes.ResponseTooLarge);
            }
        }
    }
}
