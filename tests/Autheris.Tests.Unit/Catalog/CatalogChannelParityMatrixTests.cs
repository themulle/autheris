namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Common;
using System.Security.Claims;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// #138 INC-1 P3 (T-CAT138-09, F-C7, SEC-CAT138-04): REST, MCP and GraphQL answer the same tables with the same column sets for the same
/// caller, and a table the ReBAC data-path rule denies (<c>can_query</c>) never shows up on any channel. Matrix: caller class (subject,
/// ClusterAdmin, tenant-scoped GovernanceAdmin in Multi, Unrestricted in Single) x tier (hidden without consent, hidden with consent,
/// Summary, Full) x ReBAC on/off, with explicit expectations of presence and absence.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogChannelParityMatrixTests
{
    private static readonly string[] AllColumns = ["id", "secret_col"];

    public enum CallerClass { Subject, ClusterAdmin, TenantGovernanceAdmin, UnrestrictedSingle }

    public enum Tier { HiddenNoConsent, HiddenWithConsent, Summary, Full }

    private sealed record ChannelColumns(
        Dictionary<string, string[]> Rest, Dictionary<string, string[]> Mcp, Dictionary<string, string[]> GraphQl);

    private static TableMetadata Row(string name, Tier tier)
    {
        var t = Wp0.Table("sales", name, "Short " + name, AllColumns);
        var visibility = tier switch
        {
            Tier.Summary => DatasetVisibility.Summary,
            Tier.Full => DatasetVisibility.Full,
            _ => DatasetVisibility.Hidden
        };
        return t with { Table = t.Table with { Visibility = visibility } };
    }

    private static GatewayOptions Opts(bool rebac, CallerClass caller, params TableMetadata[] rows) => new()
    {
        Tenancy = (caller == CallerClass.UnrestrictedSingle ? TenancyShape.Single : TenancyShape.Multi).Options(),
        Catalog = new DataCatalogOptions { SharedTables = rows.Select(r => r.Identifier.ToString()).ToArray() },
        Rebac = new RebacOptions { Enabled = rebac }
    };

    private static IRebacEvaluator Evaluator(params TableMetadata[] denied)
    {
        var deniedObjects = denied.Select(t => RebacTableGate.ObjectId(t.Identifier)).ToHashSet();
        var evaluator = Substitute.For<IRebacEvaluator>();
        evaluator.IsEnabled.Returns(true);
        evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => deniedObjects.Contains(ci.Arg<RebacCheckRequest>().Object) ? RebacCheckResult.Denied : RebacCheckResult.Permitted);
        return evaluator.WithBatchFromCheck();
    }

    private static ClaimsPrincipal Principal(CallerClass caller) => caller switch
    {
        CallerClass.ClusterAdmin => Wp0.User("S-USER", "tenant-a", ["ClusterAdmin"]),
        CallerClass.TenantGovernanceAdmin => Wp0.User("S-USER", "tenant-a", ["GovernanceAdmin"]),
        CallerClass.UnrestrictedSingle => Wp0.User("S-USER", Wp0.SingleTenantClaim, ["GovernanceAdmin"]),
        _ => Wp0.User("S-USER", "tenant-a")
    };

    private static async Task<ChannelColumns> ListAsync(
        CallerClass callerClass, TableMetadata[] rows, bool consentHidden, GatewayOptions options, IRebacEvaluator? rebac)
    {
        var user = Principal(callerClass);
        var tables = Wp0.Tables(rows);
        var consentRows = consentHidden ? rows : [];
        var consents = Wp0.Consents(consentRows.Select(r => Wp0.Allow(r.Identifier, "S-USER", Wp0.TenantA, "id")).ToArray());
        var service = CatalogVisibilityTestFactory.Create(tables, consents, options, rebac);
        var mcp = new McpDatasetCatalog(
            tables, consents, Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(options), visibilityService: service);
        var rest = new CatalogDiscoveryService(tables, Substitute.For<IAuditLogRepository>(), Options.Create(options), service);
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = user });
        var context = Wp0.Context(user);

        var restColumns = new Dictionary<string, string[]>();
        foreach (var summary in await rest.ListDatasetsAsync(context))
        {
            var detail = await rest.GetDatasetDetailAsync(new TableIdentifier(summary.Domain, summary.Schema, summary.Table), context);
            detail.ShouldNotBeNull();
            restColumns[summary.Table] = detail.Columns.Select(c => c.Name).Order().ToArray();
        }

        var mcpColumns = new Dictionary<string, string[]>();
        foreach (var dataset in (await mcp.ListDatasetsAsync(user)).Datasets)
        {
            var description = await mcp.DescribeDatasetAsync(user, dataset.Id);
            mcpColumns[dataset.Id.Split('.')[^1]] = description.Columns.Select(c => c.Name).Order().ToArray();
        }

        var graphQlColumns = (await new Query().GetCatalogAsync(
                null, null, null, null, null, tables, consents, null, null, accessor, Options.Create(options), service))
            .ToDictionary(t => t.TableName, t => t.Columns.Order().ToArray());
        return new ChannelColumns(restColumns, mcpColumns, graphQlColumns);
    }

    private static void ShouldAgree(ChannelColumns result)
    {
        result.Mcp.Keys.Order().ShouldBe(result.Rest.Keys.Order());
        result.GraphQl.Keys.Order().ShouldBe(result.Rest.Keys.Order());
        foreach (var (name, columns) in result.Rest)
        {
            result.Mcp[name].ShouldBe(columns, $"MCP columns of {name}");
            result.GraphQl[name].ShouldBe(columns, $"GraphQL columns of {name}");
        }
    }

    /// <summary>Expected columns of a visible table, or null when the caller must not see it at all.</summary>
    private static string[]? ExpectedColumns(CallerClass caller, Tier tier)
    {
        if (caller != CallerClass.Subject)
        {
            return [.. AllColumns.Order()];
        }

        return tier switch
        {
            Tier.HiddenNoConsent => null,
            Tier.HiddenWithConsent => ["id"],
            Tier.Summary => [],
            _ => [.. AllColumns.Order()]
        };
    }

    public static TheoryData<CallerClass, Tier> Matrix()
    {
        var data = new TheoryData<CallerClass, Tier>();
        foreach (var caller in Enum.GetValues<CallerClass>())
        {
            foreach (var tier in Enum.GetValues<Tier>())
            {
                data.Add(caller, tier);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task CatalogNeverShowsWhatDataPathDeniesByRebac_Theory(CallerClass caller, Tier tier)
    {
        var allowed = Row("allowed_t", tier);
        var denied = Row("denied_t", tier);
        var rows = new[] { allowed, denied };
        var consentHidden = tier == Tier.HiddenWithConsent;
        var expected = ExpectedColumns(caller, tier);

        var on = await ListAsync(caller, rows, consentHidden, Opts(rebac: true, caller, rows), Evaluator(denied));
        var off = await ListAsync(caller, rows, consentHidden, Opts(rebac: false, caller, rows), null);

        ShouldAgree(on);
        ShouldAgree(off);
        on.Rest.ShouldNotContainKey("denied_t");
        if (expected == null)
        {
            on.Rest.ShouldBeEmpty();
            off.Rest.ShouldBeEmpty();
            return;
        }

        on.Rest.Keys.ShouldBe(["allowed_t"]);
        on.Rest["allowed_t"].ShouldBe(expected);
        off.Rest.Keys.Order().ShouldBe(["allowed_t", "denied_t"]);
        off.Rest["denied_t"].ShouldBe(expected);
    }

    [Theory]
    [InlineData(CallerClass.Subject)]
    [InlineData(CallerClass.ClusterAdmin)]
    [InlineData(CallerClass.TenantGovernanceAdmin)]
    [InlineData(CallerClass.UnrestrictedSingle)]
    public async Task ReBacEnabledWithoutTuples_ConsentedCaller_SeesNothing_OnAllThreeChannels(CallerClass caller)
    {
        var a = Row("a_t", Tier.Full);
        var b = Row("b_t", Tier.Full);
        var rows = new[] { a, b };

        var result = await ListAsync(caller, rows, consentHidden: true, Opts(rebac: true, caller, rows), Evaluator(a, b));

        result.Rest.ShouldBeEmpty();
        result.Mcp.ShouldBeEmpty();
        result.GraphQl.ShouldBeEmpty();
    }
}
