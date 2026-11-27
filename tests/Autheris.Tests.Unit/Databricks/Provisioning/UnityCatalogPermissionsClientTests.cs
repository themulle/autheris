namespace Autheris.Tests.Unit.Databricks.Provisioning;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Provisioning;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Autheris.Infrastructure.Databricks.Provisioning;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.DatabricksTestSupport;

/// <summary>
/// Issue #1122: the Unity Catalog client authenticates as the dedicated provisioning principal, stays inside the allowed schemas,
/// adds only the one privilege per securable kind, resolves principals never by display name alone and reports failures as codes.
/// </summary>
[Trait("Category", "Security")]
public sealed class UnityCatalogPermissionsClientTests
{
    private const string ProvisioningSecret = "prov-secret-value";
    private const string GroupOid = "8b0c4b5e-6f0e-4a63-9a5e-1b2a4f9d7c11";
    private const string TablePath = "/api/2.1/unity-catalog/permissions/table/main.sales.orders";

    [Fact]
    public async Task Get_uses_the_provisioning_principal_and_maps_assignments()
    {
        var handler = Handler(r => r.Uri.AbsolutePath == TablePath
            ? Ok("{\"privilege_assignments\":[{\"principal\":\"analysts\",\"privileges\":[\"SELECT\"]},{\"principal\":\"a@x.com\",\"privileges\":[]}]}")
            : null);

        var result = await Client(handler).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].Principal.ShouldBe("analysts");
        result[0].Privileges.ShouldBe(["SELECT"]);
        var token = handler.Requests.Single(r => r.Uri.AbsolutePath == "/oidc/v1/token");
        Encoding.UTF8.GetString(Convert.FromBase64String(token.Authorization!["Basic ".Length..])).ShouldBe("prov-sp:" + ProvisioningSecret);
        handler.Requests.Single(r => r.Uri.AbsolutePath == TablePath).Authorization.ShouldBe("Bearer " + AccessToken);
    }

    [Fact]
    public async Task Get_follows_page_tokens()
    {
        var handler = Handler(r => r.Uri.AbsolutePath != TablePath ? null
            : r.Uri.Query.Contains("page_token=p2", StringComparison.Ordinal)
                ? Ok("{\"privilege_assignments\":[{\"principal\":\"b\",\"privileges\":[\"SELECT\"]}]}")
                : Ok("{\"privilege_assignments\":[{\"principal\":\"a\",\"privileges\":[\"SELECT\"]}],\"next_page_token\":\"p2\"}"));

        var result = await Client(handler).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None);

        result.Select(a => a.Principal).ShouldBe(["a", "b"]);
    }

    [Theory]
    [InlineData(UcSecurableType.Table, "main.finance.ledger")]
    [InlineData(UcSecurableType.Table, "other.sales.orders")]
    [InlineData(UcSecurableType.Table, "main.sales.Orders")]
    [InlineData(UcSecurableType.Table, "main.sales.orders;drop")]
    [InlineData(UcSecurableType.Table, "main.sales")]
    [InlineData(UcSecurableType.Table, "main.sales.orders.extra")]
    [InlineData(UcSecurableType.Schema, "main.finance")]
    [InlineData(UcSecurableType.Schema, "main")]
    [InlineData(UcSecurableType.Catalog, "other")]
    [InlineData(UcSecurableType.Catalog, "main.sales")]
    [InlineData((UcSecurableType)9, "main.sales.orders")]
    public async Task Securables_outside_the_allowed_schemas_are_refused_before_any_call(UcSecurableType type, string fullName)
    {
        var handler = Handler(_ => Ok("{}"));
        var client = Client(handler);

        (await Should.ThrowAsync<UcProvisioningException>(() => client.GetPermissionsAsync("dbx", type, fullName, CancellationToken.None)))
            .Code.ShouldBe(UcErrorCodes.SecurableNotAllowed);
        (await Should.ThrowAsync<UcProvisioningException>(() => client.UpdatePermissionsAsync("dbx", type, fullName, [Grant("analysts", "SELECT")], CancellationToken.None)))
            .Code.ShouldBe(UcErrorCodes.SecurableNotAllowed);
        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(UcSecurableType.Schema, "main.sales", "USE_SCHEMA", "USE_SCHEMA")]
    [InlineData(UcSecurableType.Table, "main.sales.orders", "SELECT", "MODIFY")]
    public async Task Update_sends_one_patch_with_the_addable_privilege(UcSecurableType type, string fullName, string privilege, string removed)
    {
        var handler = Handler(r => r.Method == HttpMethod.Patch ? Ok("{\"privilege_assignments\":[{\"principal\":\"analysts\",\"privileges\":[\"" + privilege + "\"]}]}") : null);

        var result = await Client(handler).UpdatePermissionsAsync(
            "dbx", type, fullName, [new UcPermissionChange("analysts", [privilege], [removed])], CancellationToken.None);

        result.Single().Privileges.ShouldBe([privilege]);
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        patch.Uri.AbsolutePath.ShouldBe("/api/2.1/unity-catalog/permissions/" + UcPrivileges.ApiSegment(type) + "/" + fullName);
        using var body = JsonDocument.Parse(patch.Body);
        var change = body.RootElement.GetProperty("changes")[0];
        change.GetProperty("principal").GetString().ShouldBe("analysts");
        change.GetProperty("add")[0].GetString().ShouldBe(privilege);
        change.GetProperty("remove")[0].GetString().ShouldBe(removed);
    }

    [Theory]
    [InlineData(UcSecurableType.Catalog, "main", "USE_CATALOG", false)]
    [InlineData(UcSecurableType.Catalog, "main", "USE_CATALOG", true)]
    [InlineData(UcSecurableType.Schema, "main.sales", "MODIFY", true)]
    [InlineData(UcSecurableType.Schema, "main.sales", "ALL_PRIVILEGES", true)]
    public async Task Catalogs_are_never_changed_and_schemas_only_lose_use_schema(UcSecurableType type, string fullName, string privilege, bool remove)
    {
        var handler = Handler(_ => Ok("{}"));
        var change = remove ? new UcPermissionChange("analysts", [], [privilege]) : Grant("analysts", privilege);

        (await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).UpdatePermissionsAsync(
            "dbx", type, fullName, [change], CancellationToken.None))).Code.ShouldBe(UcErrorCodes.PrivilegeNotAllowed);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cached_query_principal_token_is_never_used_for_provisioning()
    {
        var cache = new DatabricksTokenCache();
        cache.Set(Host, null, DatabricksAuthMode.OAuthM2M, "sp-id", string.Empty, "query-principal-token", DateTimeOffset.UtcNow.AddHours(1));
        var handler = Handler(r => r.Uri.AbsolutePath == TablePath ? Ok("{\"privilege_assignments\":[]}") : null);

        await Client(handler, cache).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None);

        handler.Requests.Single(r => r.Uri.AbsolutePath == TablePath).Authorization.ShouldBe("Bearer " + AccessToken);
        handler.Requests.ShouldContain(r => r.Uri.AbsolutePath == "/oidc/v1/token");
    }

    [Fact]
    public async Task Transport_failures_become_codes()
    {
        var handler = new FakeDatabricksHandler(r => r.Uri.AbsolutePath == "/oidc/v1/token" ? TokenOk() : throw new HttpRequestException("connection reset by 10.1.2.3"));

        var ex = await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None));

        ex.Code.ShouldBe(UcErrorCodes.Unavailable);
        ex.Message.ShouldNotContain("10.1.2.3");
    }

    [Fact]
    public async Task Token_endpoint_failure_becomes_an_auth_code()
    {
        var handler = new FakeDatabricksHandler(r => r.Uri.AbsolutePath == "/oidc/v1/token" ? Ok("not json") : Ok("{}"));

        (await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None)))
            .Code.ShouldBe(UcErrorCodes.AuthFailed);
    }

    [Theory]
    [InlineData(UcSecurableType.Table, "main.sales.orders", "MODIFY")]
    [InlineData(UcSecurableType.Table, "main.sales.orders", "ALL_PRIVILEGES")]
    [InlineData(UcSecurableType.Table, "main.sales.orders", "MANAGE")]
    [InlineData(UcSecurableType.Schema, "main.sales", "SELECT")]
    [InlineData(UcSecurableType.Schema, "main.sales", "CREATE_TABLE")]
    [InlineData(UcSecurableType.Catalog, "main", "USE_SCHEMA")]
    [InlineData(UcSecurableType.Catalog, "main", "USE_CATALOG")]
    public async Task Update_never_adds_any_other_privilege(UcSecurableType type, string fullName, string privilege)
    {
        var handler = Handler(_ => Ok("{}"));

        (await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).UpdatePermissionsAsync(
            "dbx", type, fullName, [Grant("analysts", privilege)], CancellationToken.None))).Code.ShouldBe(UcErrorCodes.PrivilegeNotAllowed);
        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" analysts")]
    [InlineData("ana\nlysts")]
    public async Task Update_rejects_malformed_principals_and_privileges(string principal)
    {
        var handler = Handler(_ => Ok("{}"));
        var client = Client(handler);

        (await Should.ThrowAsync<UcProvisioningException>(() => client.UpdatePermissionsAsync(
            "dbx", UcSecurableType.Table, "main.sales.orders", [Grant(principal, "SELECT")], CancellationToken.None))).Code.ShouldBe(UcErrorCodes.InvalidRequest);
        (await Should.ThrowAsync<UcProvisioningException>(() => client.UpdatePermissionsAsync(
            "dbx", UcSecurableType.Table, "main.sales.orders", [new UcPermissionChange("analysts", [], ["select;"])], CancellationToken.None))).Code.ShouldBe(UcErrorCodes.InvalidRequest);
        (await Should.ThrowAsync<UcProvisioningException>(() => client.UpdatePermissionsAsync(
            "dbx", UcSecurableType.Table, "main.sales.orders", [], CancellationToken.None))).Code.ShouldBe(UcErrorCodes.InvalidRequest);
        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, UcErrorCodes.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, UcErrorCodes.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, UcErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UcErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, UcErrorCodes.Rejected)]
    [InlineData(HttpStatusCode.Found, UcErrorCodes.Http)]
    public async Task Failures_carry_only_codes_never_response_text(HttpStatusCode status, string code)
    {
        var handler = Handler(r => r.Uri.AbsolutePath == TablePath
            ? Json(status, "{\"error_code\":\"PERMISSION_DENIED\",\"message\":\"User analysts@corp has no MANAGE on main.sales\"}")
            : null);

        var ex = await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None));

        ex.Code.ShouldBe(code);
        ex.Message.ShouldNotContain("analysts");
        ex.Message.ShouldNotContain("MANAGE");
        ex.ToString().ShouldNotContain(AccessToken);
        if (status != HttpStatusCode.Found)
        {
            ex.RemoteErrorCode.ShouldBe("PERMISSION_DENIED");
        }
    }

    [Fact]
    public async Task Remote_error_code_outside_the_token_rule_is_dropped()
    {
        var handler = Handler(r => r.Uri.AbsolutePath == TablePath ? Json(HttpStatusCode.BadRequest, "{\"error_code\":\"bad code <script>\"}") : null);

        var ex = await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None));

        ex.RemoteErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Unauthorized_refreshes_the_token_exactly_once()
    {
        var calls = 0;
        var handler = Handler(r => r.Uri.AbsolutePath == TablePath
            ? Interlocked.Increment(ref calls) == 1 ? Json(HttpStatusCode.Unauthorized, "{}") : Ok("{\"privilege_assignments\":[]}")
            : null);

        (await Client(handler).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None)).ShouldBeEmpty();

        calls.ShouldBe(2);
        handler.Requests.Count(r => r.Uri.AbsolutePath == "/oidc/v1/token").ShouldBe(2);
    }

    [Fact]
    public async Task Repeated_unauthorized_fails_with_auth_code()
    {
        var handler = Handler(r => r.Uri.AbsolutePath == TablePath ? Json(HttpStatusCode.Unauthorized, "{}") : null);

        (await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None)))
            .Code.ShouldBe(UcErrorCodes.AuthFailed);
        handler.Requests.Count(r => r.Uri.AbsolutePath == TablePath).ShouldBe(2);
    }

    [Theory]
    [InlineData("{\"privilege_assignments\":[{\"principal\":\"a\",\"privileges\":[\"select me\"]}]}")]
    [InlineData("{\"privilege_assignments\":[{\"principal\":\"\",\"privileges\":[]}]}")]
    [InlineData("{\"privilege_assignments\":{}}")]
    [InlineData("not json")]
    [InlineData("{\"privilege_assignments\":[],\"next_page_token\":\"a b\"}")]
    public async Task Malformed_responses_are_protocol_violations(string body)
    {
        var handler = Handler(r => r.Uri.AbsolutePath == TablePath ? Ok(body) : null);

        (await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None)))
            .Code.ShouldBe(UcErrorCodes.Protocol);
    }

    [Fact]
    public async Task Oversized_response_is_refused()
    {
        var handler = Handler(r => r.Uri.AbsolutePath == TablePath ? Ok(new string(' ', UnityCatalogPermissionsClient.MaxResponseBytes + 1)) : null);

        (await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).GetPermissionsAsync("dbx", UcSecurableType.Table, "main.sales.orders", CancellationToken.None)))
            .Code.ShouldBe(UcErrorCodes.ResponseTooLarge);
    }

    [Theory]
    [InlineData("pg")]
    [InlineData("plain")]
    [InlineData("missing")]
    [InlineData("")]
    public async Task Data_sources_without_provisioning_are_refused(string dataSource)
    {
        var handler = Handler(_ => Ok("{}"));

        (await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).GetPermissionsAsync(dataSource, UcSecurableType.Table, "main.sales.orders", CancellationToken.None)))
            .Code.ShouldBe(UcErrorCodes.Configuration);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Group_resolves_by_external_id_and_unique_display_name()
    {
        var handler = Handler(r => r.Uri.AbsolutePath.EndsWith("/scim/v2/Groups", StringComparison.Ordinal)
            ? Ok(Scim(Group("Data Analysts", GroupOid)))
            : null);

        var result = await Client(handler).ResolvePrincipalAsync("dbx", GranteeType.Group, GroupOid.ToUpperInvariant(), CancellationToken.None);

        result.Status.ShouldBe(UcPrincipalResolutionStatus.Resolved);
        result.Principal.ShouldBe(new UcPrincipal(GranteeType.Group, "Data Analysts", GroupOid));
        var queries = handler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/Groups", StringComparison.Ordinal)).Select(r => Uri.UnescapeDataString(r.Uri.Query)).ToList();
        queries[0].ShouldContain("filter=externalId eq \"" + GroupOid + "\"");
        queries[1].ShouldContain("filter=displayName eq \"Data Analysts\"");
    }

    [Fact]
    public async Task Group_whose_display_name_is_shared_is_ambiguous()
    {
        var handler = Handler(r => !r.Uri.AbsolutePath.EndsWith("/Groups", StringComparison.Ordinal) ? null
            : Uri.UnescapeDataString(r.Uri.Query).Contains("displayName eq", StringComparison.Ordinal)
                ? Ok(Scim(Group("Data Analysts", GroupOid), Group("Data Analysts", Guid.NewGuid().ToString())))
                : Ok(Scim(Group("Data Analysts", GroupOid))));

        (await Client(handler).ResolvePrincipalAsync("dbx", GranteeType.Group, GroupOid, CancellationToken.None))
            .Status.ShouldBe(UcPrincipalResolutionStatus.Ambiguous);
    }

    [Theory]
    [InlineData(0, UcPrincipalResolutionStatus.NotFound)]
    [InlineData(2, UcPrincipalResolutionStatus.Ambiguous)]
    public async Task Group_external_id_with_zero_or_several_matches_is_not_resolved(int count, UcPrincipalResolutionStatus status)
    {
        var groups = Enumerable.Range(0, count).Select(i => Group("g" + i, GroupOid)).ToArray();
        var handler = Handler(r => r.Uri.AbsolutePath.EndsWith("/Groups", StringComparison.Ordinal) ? Ok(Scim(groups)) : null);

        (await Client(handler).ResolvePrincipalAsync("dbx", GranteeType.Group, GroupOid, CancellationToken.None)).Status.ShouldBe(status);
    }

    [Fact]
    public async Task Truncated_scim_page_is_ambiguous()
    {
        var handler = Handler(r => r.Uri.AbsolutePath.EndsWith("/Groups", StringComparison.Ordinal)
            ? Ok("{\"totalResults\":3,\"Resources\":[" + Group("g", GroupOid) + "]}")
            : null);

        (await Client(handler).ResolvePrincipalAsync("dbx", GranteeType.Group, GroupOid, CancellationToken.None))
            .Status.ShouldBe(UcPrincipalResolutionStatus.Ambiguous);
    }

    [Fact]
    public async Task Scim_answer_for_another_external_id_is_not_trusted()
    {
        var handler = Handler(r => r.Uri.AbsolutePath.EndsWith("/Groups", StringComparison.Ordinal) ? Ok(Scim(Group("Admins", Guid.NewGuid().ToString()))) : null);

        (await Client(handler).ResolvePrincipalAsync("dbx", GranteeType.Group, GroupOid, CancellationToken.None))
            .Status.ShouldBe(UcPrincipalResolutionStatus.NotFound);
    }

    [Theory]
    [InlineData("Data Analysts")]
    [InlineData("not-a-guid\" or displayName pr \"")]
    [InlineData("")]
    public async Task Non_guid_identifiers_are_never_sent_to_scim(string externalId)
    {
        var handler = Handler(_ => Ok(Scim(Group("Data Analysts", GroupOid))));

        (await Client(handler).ResolvePrincipalAsync("dbx", GranteeType.Group, externalId, CancellationToken.None))
            .Status.ShouldBe(UcPrincipalResolutionStatus.NotFound);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task User_resolves_by_external_id_to_the_user_name()
    {
        var handler = Handler(r => r.Uri.AbsolutePath.EndsWith("/scim/v2/Users", StringComparison.Ordinal)
            ? Ok(Scim("{\"id\":\"1\",\"userName\":\"jane@example.com\",\"externalId\":\"" + GroupOid + "\"}"))
            : null);

        var result = await Client(handler).ResolvePrincipalAsync("dbx", GranteeType.User, GroupOid, CancellationToken.None);

        result.Principal.ShouldBe(new UcPrincipal(GranteeType.User, "jane@example.com", GroupOid));
        Uri.UnescapeDataString(handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal)).Uri.Query)
            .ShouldContain("filter=externalId eq \"" + GroupOid + "\"");
    }

    [Fact]
    public async Task Service_principal_resolves_by_application_id()
    {
        var appId = "0f9d7c11-1b2a-4f9d-9a5e-8b0c4b5e6f0e";
        var handler = Handler(r => r.Uri.AbsolutePath.EndsWith("/scim/v2/ServicePrincipals", StringComparison.Ordinal)
            ? Ok(Scim("{\"id\":\"7\",\"applicationId\":\"" + appId + "\"}"))
            : null);

        var result = await Client(handler).ResolvePrincipalAsync("dbx", GranteeType.ServicePrincipal, appId, CancellationToken.None);

        result.Principal.ShouldBe(new UcPrincipal(GranteeType.ServicePrincipal, appId, appId));
        Uri.UnescapeDataString(handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/ServicePrincipals", StringComparison.Ordinal)).Uri.Query)
            .ShouldContain("filter=applicationId eq \"" + appId + "\"");
    }

    [Theory]
    [InlineData("{\"privilege_assignments\":[{\"principal\":\"jane@example.com\",\"privileges\":[{\"privilege\":\"USE_CATALOG\",\"inherited_from_type\":\"METASTORE\"}]}]}", true)]
    [InlineData("{\"privilege_assignments\":[{\"principal\":\"other@example.com\",\"privileges\":[{\"privilege\":\"USE_CATALOG\"}]}]}", false)]
    [InlineData("{\"privilege_assignments\":[{\"principal\":\"jane@example.com\",\"privileges\":[{\"privilege\":\"BROWSE\"}]}]}", false)]
    [InlineData("{}", false)]
    public async Task Effective_catalog_access_counts_only_the_asked_principal(string body, bool expected)
    {
        var handler = Handler(r => r.Uri.AbsolutePath == "/api/2.1/unity-catalog/effective-permissions/catalog/main" ? Ok(body) : null);

        var result = await Client(handler).HasEffectivePrivilegeAsync("dbx", UcSecurableType.Catalog, "main", "jane@example.com", ["USE_CATALOG", "ALL_PRIVILEGES"], CancellationToken.None);

        result.ShouldBe(expected);
        Uri.UnescapeDataString(handler.Requests.Single(r => r.Uri.AbsolutePath.Contains("effective-permissions", StringComparison.Ordinal)).Uri.Query)
            .ShouldBe("?principal=jane@example.com");
    }

    [Fact]
    public async Task Effective_check_outside_the_allowed_schemas_is_refused()
    {
        var handler = Handler(_ => Ok("{}"));

        (await Should.ThrowAsync<UcProvisioningException>(() => Client(handler).HasEffectivePrivilegeAsync(
            "dbx", UcSecurableType.Catalog, "other", "jane@example.com", ["USE_CATALOG"], CancellationToken.None))).Code.ShouldBe(UcErrorCodes.SecurableNotAllowed);
        handler.Requests.ShouldBeEmpty();
    }

    // ---- support ----

    private static UcPermissionChange Grant(string principal, string privilege) => new(principal, [privilege], []);

    private static string Group(string displayName, string externalId) =>
        "{\"id\":\"" + Guid.NewGuid().ToString("N") + "\",\"displayName\":\"" + displayName + "\",\"externalId\":\"" + externalId + "\"}";

    private static string Scim(params string[] resources) =>
        "{\"totalResults\":" + resources.Length + ",\"Resources\":[" + string.Join(",", resources) + "]}";

    private static FakeDatabricksHandler Handler(Func<RecordedRequest, HttpResponseMessage?> routes) =>
        new(r => r.Uri.AbsolutePath == "/oidc/v1/token" ? TokenOk() : routes(r) ?? Json(HttpStatusCode.NotFound, "{}"));

    private static UnityCatalogPermissionsClient Client(FakeDatabricksHandler handler, DatabricksTokenCache? cache = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(ProvisioningOptions());
        var credentials = new DatabricksOboTokenProvider(new HttpClient(handler), options, new ProvisioningVault(), Env("Production"), cache ?? new DatabricksTokenCache());
        return new UnityCatalogPermissionsClient(new HttpClient(handler), options, credentials);
    }

    private static GatewayOptions ProvisioningOptions() => Options(extra: connections =>
    {
        var dbx = connections["dbx"];
        connections["dbx"] = new DataSourceConnectionOptions
        {
            Provider = dbx.Provider,
            CommandTimeoutSeconds = dbx.CommandTimeoutSeconds,
            Databricks = new DatabricksDataSourceOptions
            {
                Host = Host,
                WarehouseId = "wh-1",
                Catalog = "main",
                Auth = dbx.Databricks!.Auth,
                Provisioning = new DatabricksProvisioningOptions
                {
                    Mode = DatabricksProvisioningMode.Enforce,
                    Tenant = "acme",
                    AllowedSchemas = ["main.sales"],
                    Auth = new DatabricksAuthOptions { Mode = DatabricksAuthMode.OAuthM2M, ClientId = "prov-sp", ClientSecretRef = "kv://dbx-prov-secret" }
                }
            }
        };
        connections["plain"] = new DataSourceConnectionOptions { Provider = "databricks", Databricks = new DatabricksDataSourceOptions { Host = Host, WarehouseId = "wh-2" } };
        connections["pg"] = new DataSourceConnectionOptions { Provider = "postgresql", ConnectionString = "Host=x" };
    });

    private sealed class ProvisioningVault : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) => secretRef switch
        {
            "kv://dbx-prov-secret" => Encoding.UTF8.GetBytes(ProvisioningSecret),
            "kv://dbx-sp-secret" => Encoding.UTF8.GetBytes(ClientSecret),
            _ => throw new KeyNotFoundException(secretRef)
        };
    }
}
