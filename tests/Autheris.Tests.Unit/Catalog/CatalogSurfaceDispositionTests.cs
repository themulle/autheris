namespace Autheris.Tests.Unit.Catalog;

using System.Text.RegularExpressions;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// #138 INC-1 P3 (SEC-CAT138-10, T-CAT138-15): the disposition of every surface that lists or probes catalog tables outside the discovery
/// service, pinned so that a regression (a name reaching a non-administrator) fails here.
/// <list type="table">
/// <item><term>DynamicOpenApiGenerator</term><description>out of scope, admin-gated: every OpenAPI route requires a global administrator (the spec lists all tenants, not filtered per caller).</description></item>
/// <item><term>OData service document / $metadata</term><description>consent-only by design (OQ-3): tables are kept only after the data-path decision (<c>CheckTableAccessAsync</c>), per caller; never the discovery tiers.</description></item>
/// <item><term>CatalogVersionProvider</term><description>out of scope: returns a count and a hash digest, no table name.</description></item>
/// <item><term>EffectiveAccessExplanationService</term><description>consent-only by design (S12, DataAccess surface through the engine; call-site guards).</description></item>
/// <item><term>AccessPlanningService</term><description>out of scope: governance mutation by id, identical generic not-found text.</description></item>
/// <item><term>OpenAPI gate gap</term><description>follow-up F-138-FU2: <c>IsOpenApiAdmin</c> admits a tenant-scoped GovernanceAdmin, who then receives the all-tenant spec; the catalog rule treats that class as tenant-scoped.</description></item>
/// <item><term>QueryAnalysisService, BundledConsentRequestService</term><description>follow-up (no enumeration; they resolve names the caller typed, but still tell "exists" from "unknown" for an Unmarked or ReBAC-denied table): tracked in the plan changelog.</description></item>
/// </list>
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogSurfaceDispositionTests
{
    private static string RepoFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
            {
                return Path.Combine([current.FullName, .. parts]);
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Autheris.sln not found.");
    }

    [Theory]
    [InlineData("DataOwner")]
    [InlineData("SchemaAdmin")]
    [InlineData("CatalogReader")]
    [InlineData("Subject")]
    public void SurfaceDispositionList_OpenApi_NonAdminCallers_AreNotAuthorised(string role)
    {
        // Known gap (follow-up F-138-FU2, plan changelog): a GovernanceAdmin with a tenant claim passes this gate (SEC M-11 treats the role as global).
        ODataEndpoints.IsOpenApiAdmin(Wp0.User("S-X", "tenant-a", [role])).ShouldBeFalse();
    }

    [Fact]
    public void SurfaceDispositionList_OpenApi_ClusterAdminIsAuthorised()
        => ODataEndpoints.IsOpenApiAdmin(Wp0.User("S-ROOT", "tenant-a", ["ClusterAdmin"])).ShouldBeTrue();

    [Fact]
    public void SurfaceDispositionList_OpenApi_EveryRouteChecksTheAdminGate()
    {
        var source = File.ReadAllText(RepoFile("src", "Autheris.Api", "Endpoints", "ODataEndpoints.cs"));

        var routes = Regex.Count(source, @"ConfigureOpenApiAuth\(app\.MapGet\(");
        var checks = Regex.Count(source, @"var authCheck = CheckOpenApiAuth\(context\);");

        routes.ShouldBeGreaterThan(0);
        checks.ShouldBe(routes);
        source.ShouldContain("return IsOpenApiAdmin(context.User);");
    }

    [Fact]
    public void SurfaceDispositionList_ODataMetadata_KeepsATableOnlyAfterTheDataPathDecision()
    {
        var source = File.ReadAllText(RepoFile("src", "Autheris.Extensions", "OData", "ODataHandler.cs"));
        var start = source.IndexOf("GetAuthorizedTablesCoreAsync(ClaimsPrincipal", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0);
        var body = source[start..Math.Min(source.Length, start + 4000)];

        body.ShouldContain("CheckTableAccessAsync(principal, tableMeta");
        body.ShouldContain("if (decision == null)");
        body.ShouldContain("if (!decision.IsAllowed)");
        body.IndexOf("authorizedTables.Add", StringComparison.Ordinal).ShouldBeGreaterThan(body.IndexOf("if (!decision.IsAllowed)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SurfaceDispositionList_CatalogVersion_ContainsNoTableNameOrColumnName()
    {
        var hidden = Wp0.Table("sales", "secret_hidden_t", "H", ["very_secret_col"]);
        var version = await new CatalogVersionProvider(Wp0.Tables(hidden)).GetVersionAsync();

        version.ShouldNotContain("secret_hidden_t");
        version.ShouldNotContain("very_secret_col");
        version.ShouldNotContain("sales");
    }
}
