namespace Autheris.Tests.Unit.OData;

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Extensions.OData;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #94: @odata.nextLink escaping (SEC-94-14), unordered paging refusal (D-10) and no ordering rule in responses.</summary>
[Trait("Category", "Security")]
public sealed class ODataNextLinkTests
{
    private static readonly TableIdentifier Plain = new("erp", "main", "invoices");
    private static readonly Dictionary<string, ColumnAccessLevel> Access = new(System.StringComparer.OrdinalIgnoreCase) { ["id"] = ColumnAccessLevel.Clear };

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ANALYST"), new Claim("tenant_id", "erp")], "Test"));

    private static List<IReadOnlyDictionary<string, object?>> Rows(int n) =>
        Enumerable.Range(1, n).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i }).ToList();

    private static ODataHandler Handler(TableIdentifier table, int rows, bool unordered)
    {
        var execution = Substitute.For<IGatewayExecutionService>();
        execution.ExecuteTablePageAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<TablePageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TableQueryPage(Rows(rows), TableAccessDecision.Allowed(table, Access), null, unordered));
        return new ODataHandler(Substitute.For<ITableMetadataRepository>(), execution, NullLogger<ODataHandler>.Instance);
    }

    [Fact]
    public async Task PathSegmentsEscaped()
    {
        var table = new TableIdentifier("a b", "s?x", "t/u#v");

        var result = await Handler(table, rows: 3, unordered: false).ExecuteEntitySetQueryAsync(
            User(), "https://gw/odata/v4", table, top: 2, skip: null, select: null, includeCount: false, headers: null);

        var link = ((IReadOnlyDictionary<string, object?>)result.Payload)["@odata.nextLink"]!.ToString()!;
        link.ShouldStartWith("https://gw/odata/v4/a%20b/s%3Fx/t%2Fu%23v?$skip=2");
    }

    [Fact]
    public async Task DerivedTiebreakerNotInLink()
    {
        var result = await Handler(Plain, rows: 3, unordered: false).ExecuteEntitySetQueryAsync(
            User(), "https://gw/odata/v4", Plain, top: 2, skip: null, select: null, includeCount: false, headers: null, orderBy: "amount desc");

        var link = ((IReadOnlyDictionary<string, object?>)result.Payload)["@odata.nextLink"]!.ToString()!;
        link.ShouldContain("$orderby=amount%20desc");
        link.ShouldNotContain("%2C");
        link.ShouldNotContain("id");
    }

    [Fact]
    public async Task Unordered_MoreRowsThanThePage_Is400WithoutColumnNames()
    {
        var result = await Handler(Plain, rows: 3, unordered: true).ExecuteEntitySetQueryAsync(
            User(), "https://gw/odata/v4", Plain, top: 2, skip: null, select: null, includeCount: false, headers: null);

        result.StatusCode.ShouldBe(400);
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("no orderable column readable in clear text");
    }

    [Fact]
    public async Task Unordered_PageThatFits_Succeeds_WithoutNextLink()
    {
        var result = await Handler(Plain, rows: 2, unordered: true).ExecuteEntitySetQueryAsync(
            User(), "https://gw/odata/v4", Plain, top: 2, skip: null, select: null, includeCount: false, headers: null);

        result.StatusCode.ShouldBe(200);
        ((IReadOnlyDictionary<string, object?>)result.Payload).ShouldNotContainKey("@odata.nextLink");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseHasNoOrderingKind(bool unordered)
    {
        var result = await Handler(Plain, rows: 2, unordered: unordered).ExecuteEntitySetQueryAsync(
            User(), "https://gw/odata/v4", Plain, top: 2, skip: null, select: null, includeCount: false, headers: null);

        var json = JsonSerializer.Serialize(result.Payload);
        foreach (var marker in new[] { "ordering", "PrimaryKey", "UniqueIndex", "ClearColumnComposite", "ConventionKey", "MetaMarkerKey" })
        {
            json.ShouldNotContain(marker, Case.Insensitive);
        }
    }
}
