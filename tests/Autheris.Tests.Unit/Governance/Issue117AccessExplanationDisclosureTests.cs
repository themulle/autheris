using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #117: the access explanation must not disclose hidden tables, their columns or row filter SQL.</summary>
public sealed class Issue117AccessExplanationDisclosureTests
{
    private static readonly TableIdentifier Denied = new("default", "public", "secret");
    private static readonly TableIdentifier Unknown = new("default", "public", "missing");
    private const string FilterSql = "cost_center IN ('4711','4712')";

    private sealed record Fixture(
        EffectiveAccessExplanationService Service,
        IDataOwnershipRepository Ownership,
        IConsentResolutionService Resolution);

    private static ClaimsPrincipal User(params Claim[] extra) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"), .. extra],
            "test"));

    private static TableMetadata Meta(DatasetVisibility visibility = DatasetVisibility.Hidden) => new()
    {
        Identifier = Denied,
        Table = new Table { Visibility = visibility },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "salary", DataType = "int" }
        ]
    };

    private static Fixture Create(TableAccessDecision decision, bool owner = false)
    {
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Denied, Arg.Any<CancellationToken>()).Returns(Meta());
        tableRepo.GetTableMetadataAsync(Unknown, Arg.Any<CancellationToken>()).Returns((TableMetadata?)null);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        consentRepo.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(), Arg.Any<IReadOnlySet<Sid>?>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(decision);

        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(owner);

        return new Fixture(new EffectiveAccessExplanationService(tableRepo, consentRepo, resolution, ownership, Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions { Tenancy = new Autheris.Domain.Options.TenancyOptions { Mode = "Single" } })), ownership, resolution);
    }

    private static TableAccessDecision DeniedDecision() => TableAccessDecision.Denied(Denied, "No active allow consent.");

    private static TableAccessDecision AllowedWithFilter() => TableAccessDecision.Allowed(
        Denied,
        new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["salary"] = ColumnAccessLevel.Deny
        },
        rowFilterSql: FilterSql,
        hasUnconstrainedColumnAllow: false);

    [Fact]
    public async Task UnknownAndHiddenDeniedTable_AreIndistinguishable_WithoutColumns()
    {
        var f = Create(DeniedDecision());
        var user = User();

        var unknown = await f.Service.ExplainMyAccessAsync(user, Unknown);
        var hidden = await f.Service.ExplainMyAccessAsync(user, Denied);

        hidden.Decision.ShouldBe(unknown.Decision);
        hidden.ConsentDescription.ShouldBe(unknown.ConsentDescription);
        hidden.ConsentDescription.ShouldBe("Table not found in catalog.");
        hidden.AllowedColumns.ShouldBeEmpty();
        hidden.MaskedColumns.ShouldBeEmpty();
        hidden.DeniedColumns.ShouldBeEmpty();
        hidden.ActiveRowFilters.ShouldBeEmpty();
        hidden.ValidTo.ShouldBeNull();
    }

    [Fact]
    public async Task DeniedTable_VisibleInCatalog_StillListsColumnsAlreadyVisibleThere()
    {
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Denied, Arg.Any<CancellationToken>()).Returns(Meta(DatasetVisibility.Full));
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        consentRepo.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(), Arg.Any<IReadOnlySet<Sid>?>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(DeniedDecision());

        var service = new EffectiveAccessExplanationService(tableRepo, consentRepo, resolution);
        var result = await service.ExplainMyAccessAsync(User(), Denied);

        result.Decision.ShouldBe("DENIED");
        result.ConsentDescription.ShouldNotBe("Table not found in catalog.");
        result.DeniedColumns.ShouldBe(["id", "salary"]);
    }

    [Fact]
    public async Task NormalUser_GetsNoSql_InActiveRowFilters()
    {
        var f = Create(AllowedWithFilter());

        var result = await f.Service.ExplainMyAccessAsync(User(), Denied);

        result.Decision.ShouldBe("ALLOWED");
        result.ActiveRowFilters.ShouldNotBeEmpty();
        result.ActiveRowFilters.ShouldNotContain(x => x.Contains("4711") || x.Contains("cost_center"));
        result.AllowedColumns.ShouldBe(["id"]);
    }

    [Fact]
    public async Task OwnerOrDelegate_SeesFilterDefinition()
    {
        // IsAuthorizedApproverForTableAsync covers both the data owner and an active delegate.
        var f = Create(AllowedWithFilter(), owner: true);

        var result = await f.Service.ExplainMyAccessAsync(User(), Denied);

        result.ActiveRowFilters.ShouldBe([FilterSql]);
        await f.Ownership.Received(1).IsAuthorizedApproverForTableAsync(Denied, Arg.Any<Sid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GovernanceAdmin_SeesFilterDefinition()
    {
        var f = Create(AllowedWithFilter());

        var result = await f.Service.ExplainMyAccessAsync(User(new Claim(ClaimTypes.Role, "GovernanceAdmin")), Denied);

        result.ActiveRowFilters.ShouldBe([FilterSql]);
    }

    [Fact]
    public async Task OwnershipLookupFailure_FailsClosed_NoSql()
    {
        var f = Create(AllowedWithFilter());
        f.Ownership.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("db down"));

        var result = await f.Service.ExplainMyAccessAsync(User(), Denied);

        result.ActiveRowFilters.ShouldNotContain(FilterSql);
    }

    [Fact]
    public async Task GovernanceAdmin_ForDeniedTable_StillSeesTable()
    {
        var f = Create(DeniedDecision());

        var result = await f.Service.ExplainMyAccessAsync(User(new Claim(ClaimTypes.Role, "GovernanceAdmin")), Denied);

        result.ConsentDescription.ShouldNotBe("Table not found in catalog.");
        result.DeniedColumns.ShouldBe(["id", "salary"]);
    }
}
