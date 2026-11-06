namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DatasetCatalogPrivacyEditTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private readonly ITableMetadataRepository _tables = Substitute.For<ITableMetadataRepository>();
    private readonly IDataOwnershipRepository _owners = Substitute.For<IDataOwnershipRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private DatasetCatalogSettings _state = new([], DatasetVisibility.Hidden, MinGroupSize: null, NonPersonal: null);

    public DatasetCatalogPrivacyEditTests()
    {
        _tables.GetActiveTableIdentifierAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<TableIdentifier>().Equals(Orders) ? Orders : (TableIdentifier?)null);
        _tables.GetDatasetCatalogSettingsAsync(Orders, true, Arg.Any<CancellationToken>()).Returns(_ => _state);
        _tables.SetDatasetPrivacyAsync(Orders, Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var before = _state;
                _state = before with { MinGroupSize = ci.ArgAt<int?>(1), NonPersonal = ci.ArgAt<bool?>(2) };
                return new DatasetCatalogChange(Orders, before, _state);
            });
    }

    private DatasetCatalogAdminService Create()
        => new(_tables, _owners, _audit,
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
            {
                Tenancy = new TenancyOptions { DefaultTenantId = "tenant-a" },
                Catalog = new DataCatalogOptions { MaxTagsPerDataset = 32 }
            }),
            TimeProvider.System);

    private static ClaimsPrincipal Admin() => Wp0.User("S-1-5-21-root", null, ["ClusterAdmin"]);
    private static ClaimsPrincipal Stranger() => Wp0.User("S-1-5-21-stranger", "tenant-b");

    [Fact]
    public async Task Apply_SetNonPersonalAndMinGroupSize_AuditsBeforeAfter()
    {
        var svc = Create();
        var edit = new DatasetCatalogEdit(null, null, null, null, MinGroupSize: "5", NonPersonal: true);

        var result = await svc.ApplyAsync(Admin(), "sales.dbo.orders", edit, "admin", CancellationToken.None);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
        result.Settings.ShouldNotBeNull();
        result.Settings.MinGroupSize.ShouldBe(5);
        result.Settings.NonPersonal.ShouldBe(true);

        await _audit.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.CatalogMinGroupSizeChanged &&
            e.TargetTable == Orders.ToString()), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_InvalidMinGroupSize_InvalidRequest()
    {
        var svc = Create();
        var edit = new DatasetCatalogEdit(null, null, null, null, MinGroupSize: "invalid_number", NonPersonal: true);

        var result = await svc.ApplyAsync(Admin(), "sales.dbo.orders", edit, "admin", CancellationToken.None);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.InvalidRequest);
        await _audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Fact]
    public async Task Apply_Unchanged_NoAudit()
    {
        _state = new DatasetCatalogSettings([], DatasetVisibility.Hidden, MinGroupSize: 5, NonPersonal: true);
        var svc = Create();
        var edit = new DatasetCatalogEdit(null, null, null, null, MinGroupSize: "5", NonPersonal: true);

        var result = await svc.ApplyAsync(Admin(), "sales.dbo.orders", edit, "admin", CancellationToken.None);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.Unchanged);
        await _audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Fact]
    public async Task Apply_Unauthorized_Denied()
    {
        var svc = Create();
        var edit = new DatasetCatalogEdit(null, null, null, null, MinGroupSize: "5", NonPersonal: true);

        // Stranger without ownership or cluster admin gets NotFound (fail-closed, does not leak existence)
        var result = await svc.ApplyAsync(Stranger(), "sales.dbo.orders", edit, "admin", CancellationToken.None);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);
        await _tables.DidNotReceiveWithAnyArgs().SetDatasetPrivacyAsync(default, default, default, default, default);
        await _audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Fact]
    public async Task Apply_NonPersonalWithSensitiveColumn_Warning()
    {
        var meta = new TableMetadata
        {
            Identifier = Orders,
            Columns =
            [
                new TableColumn { ColumnName = "secret", IsSensitive = true }
            ],
            Table = new Table { Id = Guid.NewGuid(), Sensitivity = "NORMAL", TableName = "orders", SchemaName = "dbo", SourceName = "sales", SourceType = "sql" }
        };
        _tables.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(meta);

        var svc = Create();
        var edit = new DatasetCatalogEdit(null, null, null, null, MinGroupSize: "1", NonPersonal: true);

        var result = await svc.ApplyAsync(Admin(), "sales.dbo.orders", edit, "admin", CancellationToken.None);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
        result.Warning.ShouldNotBeNull();
        result.Warning.ShouldContain("catalog-sensitive columns");
    }

    [Fact]
    public async Task Apply_AuditWriteFails_ChangeAborted()
    {
        _audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Audit store failed"));

        var svc = Create();
        var edit = new DatasetCatalogEdit(null, null, null, null, MinGroupSize: "1", NonPersonal: true);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await svc.ApplyAsync(Admin(), "sales.dbo.orders", edit, "admin", CancellationToken.None));
    }
}
