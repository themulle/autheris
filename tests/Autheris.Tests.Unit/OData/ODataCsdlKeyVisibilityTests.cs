namespace Autheris.Tests.Unit.OData;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Extensions.OData;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (SD-4, SEC-94-03): the CSDL key comes from the metadata filtered for the caller, as a whole.</summary>
[Trait("Category", "Security")]
public sealed class ODataCsdlKeyVisibilityTests
{
    private static readonly TableIdentifier Id = new("corp", "sales", "orders");

    private static TableColumn Col(string name, params (string K, string V)[] meta) =>
        new() { ColumnName = name, DataType = "integer", Meta = meta.ToDictionary(m => m.K, m => m.V) };

    private static async Task<(string Csdl, XElement Entity)> CsdlFor(TableMetadata table, params string[] deniedColumns)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
        var exec = Substitute.For<IGatewayExecutionService>();
        exec.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(TableAccessDecision.Allowed(
                Id,
                deniedColumns.ToDictionary(c => c, _ => ColumnAccessLevel.Deny, StringComparer.OrdinalIgnoreCase),
                hasUnconstrainedColumnAllow: true)));
        var handler = new ODataHandler(repo, exec, NullLogger<ODataHandler>.Instance);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "corp")], "TestAuth"));

        var csdl = await handler.GetMetadataCsdlAsync(user);
        var ns = XNamespace.Get("http://docs.oasis-open.org/odata/ns/edm");
        return (csdl, XDocument.Parse(csdl).Descendants(ns + "EntityType").Single());
    }

    private static string[] KeyOf(XElement entity)
    {
        var ns = XNamespace.Get("http://docs.oasis-open.org/odata/ns/edm");
        return entity.Element(ns + "Key")!.Elements(ns + "PropertyRef").Select(p => p.Attribute("Name")!.Value).ToArray();
    }

    private static TableMetadata Table(params TableColumn[] cols) => new()
    {
        Identifier = Id,
        Table = new Table { SchemaName = "sales", TableName = "orders", SourceName = "corp" },
        Columns = cols
    };

    [Fact]
    public async Task DeniedKeyOrIndexColumn_NotEmitted_CandidateDroppedWhole()
    {
        var table = Table(Col("a"), Col("hidden_b"), Col("c"), Col("d")) with
        {
            PrimaryKeyColumns = ["a", "hidden_b"],
            Indexes = [new TableIndexDefinition("ux_hidden", ["hidden_b", "c"], true), new TableIndexDefinition("ux_cd", ["c", "d"], true)]
        };

        var (csdl, entity) = await CsdlFor(table, "hidden_b");

        csdl.ShouldNotContain("hidden_b");
        KeyOf(entity).ShouldBe(new[] { "c", "d" });
    }

    [Fact]
    public async Task DeniedPrimaryKeyColumn_NoCandidate_FallsBackToAllVisibleColumns_NeverASubset()
    {
        var table = Table(Col("id"), Col("hidden_b"), Col("c")) with { PrimaryKeyColumns = ["id", "hidden_b"] };

        var (csdl, entity) = await CsdlFor(table, "hidden_b");

        csdl.ShouldNotContain("hidden_b");
        KeyOf(entity).ShouldBe(new[] { "id", "c" });
    }

    [Fact]
    public async Task PartiallyHiddenMarkerKey_IsNotEmittedAsPartialKey()
    {
        var table = Table(Col("k1", ("primary_key_ordinal", "1")), Col("k2", ("primary_key_ordinal", "2")), Col("v"));

        var (_, entity) = await CsdlFor(table, "k2");

        KeyOf(entity).ShouldBe(new[] { "k1", "v" });
    }

    [Fact]
    public async Task FullyVisibleMarkerKey_IsEmitted()
    {
        var table = Table(Col("k1", ("primary_key_ordinal", "1")), Col("k2", ("primary_key_ordinal", "2")), Col("v"));

        var (_, entity) = await CsdlFor(table);

        KeyOf(entity).ShouldBe(new[] { "k1", "k2" });
    }

    [Fact]
    public async Task ClientIdAlone_And_IdGuess_AreNotKeys()
    {
        var table = Table(Col("id"), Col("client_id"), Col("v"));

        var (_, entity) = await CsdlFor(table);

        KeyOf(entity).ShouldBe(new[] { "id", "client_id", "v" });
    }

    [Fact]
    public async Task ClientIdAndTs_AreTheConventionKey_OnlyWhenBothVisible()
    {
        var table = Table(Col("client_id"), Col("ts"), Col("v"));

        KeyOf((await CsdlFor(table)).Entity).ShouldBe(new[] { "client_id", "ts" });
        KeyOf((await CsdlFor(table, "ts")).Entity).ShouldBe(new[] { "client_id", "v" });
    }
}
