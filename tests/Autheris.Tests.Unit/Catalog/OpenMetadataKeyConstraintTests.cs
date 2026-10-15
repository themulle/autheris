namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (D-8, SEC-94-11): OpenMetadata tableConstraints become the declared definition.</summary>
public sealed class OpenMetadataKeyConstraintTests
{
    private readonly IOpenMetadataClient _client = Substitute.For<IOpenMetadataClient>();
    private readonly ITableMetadataRepository _repo = Substitute.For<ITableMetadataRepository>();
    private readonly List<TableMetadata> _upserts = new();

    private OpenMetadataSyncService Service()
    {
        var options = new GatewayOptions { OpenMetadata = new OpenMetadataOptions { Enabled = true } };
        _client.GetPoliciesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([]));
        _client.GetRolesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([]));
        _client.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        _client.GetUsersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]));
        _repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(c => { _upserts.Add(c.Arg<TableMetadata>()); return Task.FromResult(c.Arg<TableMetadata>()); });
        return new OpenMetadataSyncService(_client, _repo, Substitute.For<IConsentRepository>(), Substitute.For<IPolicyEpochRepository>(),
            Options.Create(options), NullLogger<OpenMetadataSyncService>.Instance);
    }

    private static OpenMetadataTable Table(params OpenMetadataTableConstraint[] constraints) => new()
    {
        Id = Guid.NewGuid(),
        Name = "orders",
        FullyQualifiedName = "svc.corp.dbo.orders",
        Service = new OpenMetadataEntityReference { Name = "svc" },
        DatabaseSchema = new OpenMetadataEntityReference { Name = "dbo" },
        Columns =
        [
            new OpenMetadataColumn { Name = "a", DataType = "INT" },
            new OpenMetadataColumn { Name = "b", DataType = "INT" },
            new OpenMetadataColumn { Name = "c", DataType = "VARCHAR" }
        ],
        TableConstraints = constraints.Length == 0 ? null : constraints.ToList()
    };

    private async Task<TableMetadata> SyncAsync(OpenMetadataTable table)
    {
        _client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>([table]));
        await Service().SyncPermissionsAsync(dryRun: false);
        return _upserts.Single();
    }

    private static OpenMetadataTableConstraint Constraint(string type, params string[] columns) =>
        new() { ConstraintType = type, Columns = columns.ToList() };

    [Fact]
    public async Task PrimaryKeyAndUnique_BecomeDeclaredDefinition_AndReplaceIsRequested()
    {
        var meta = await SyncAsync(Table(Constraint("PRIMARY_KEY", "a", "b"), Constraint("UNIQUE", "c"), Constraint("UNIQUE", "b", "c")));

        meta.ReplaceKeyDefinition.ShouldBeTrue();
        meta.PrimaryKeyColumns.ShouldBe(["a", "b"]);
        meta.Indexes.Select(i => (i.Name, i.IsUnique)).ShouldBe([("uq_1", true), ("uq_2", true)]);
        meta.Indexes[1].Columns.ShouldBe(["b", "c"]);
    }

    [Fact]
    public async Task NoConstraints_DoesNotReplaceTheStoredDefinition()
    {
        var meta = await SyncAsync(Table());
        meta.ReplaceKeyDefinition.ShouldBeFalse();
        meta.PrimaryKeyColumns.ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyForeignKeyConstraints_DoNotReplace()
    {
        var meta = await SyncAsync(Table(Constraint("FOREIGN_KEY", "a")));
        meta.ReplaceKeyDefinition.ShouldBeFalse();
    }

    [Fact]
    public async Task UnknownColumn_DefinitionDropped_TableIngested()
    {
        var meta = await SyncAsync(Table(Constraint("PRIMARY_KEY", "a", "ghost"), Constraint("UNIQUE", "c")));

        meta.ReplaceKeyDefinition.ShouldBeFalse();
        meta.PrimaryKeyColumns.ShouldBeEmpty();
        meta.Indexes.ShouldBeEmpty();
        meta.Columns.Count.ShouldBe(3);
    }

    [Fact]
    public async Task TwoPrimaryKeys_AreAmbiguous_DefinitionDropped()
    {
        var meta = await SyncAsync(Table(Constraint("PRIMARY_KEY", "a"), Constraint("PRIMARY_KEY", "b")));
        meta.ReplaceKeyDefinition.ShouldBeFalse();
    }

    [Fact]
    public async Task OversizedConstraintColumns_DefinitionDropped()
    {
        var meta = await SyncAsync(Table(Constraint("PRIMARY_KEY", Enumerable.Range(0, 40).Select(i => $"c{i}").ToArray())));
        meta.ReplaceKeyDefinition.ShouldBeFalse();
    }

    [Fact]
    public async Task TooManyUniqueConstraints_DefinitionDropped()
    {
        var constraints = Enumerable.Range(0, 70).Select(_ => Constraint("UNIQUE", "a")).ToArray();
        var meta = await SyncAsync(Table(constraints));
        meta.ReplaceKeyDefinition.ShouldBeFalse();
        meta.Indexes.ShouldBeEmpty();
    }

    [Fact]
    public async Task MalformedConstraint_NullColumns_DefinitionDropped_NoThrow()
    {
        var meta = await SyncAsync(Table(new OpenMetadataTableConstraint { ConstraintType = "PRIMARY_KEY", Columns = null }));
        meta.ReplaceKeyDefinition.ShouldBeFalse();
    }
}
