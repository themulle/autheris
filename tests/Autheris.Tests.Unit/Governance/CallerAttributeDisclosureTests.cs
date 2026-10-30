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

/// <summary>REQ-GOV-129 AC-129-7, V-129-10: the caller views say only that an attribute filter applies.</summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeDisclosureTests
{
    private static readonly TableIdentifier Orders = new("default", "public", "orders");

    private static EffectiveAccessExplanationService Service(TableAccessDecision decision, bool owner)
    {
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { Visibility = DatasetVisibility.Full },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "cost_center", DataType = "varchar" }]
        });
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>([]));
        consentRepo.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>([]));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(),
            Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(), Arg.Any<IReadOnlySet<Sid>?>(), Arg.Any<IReadOnlySet<string>?>()).Returns(decision);
        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(owner);
        return new EffectiveAccessExplanationService(tableRepo, consentRepo, resolution, ownership,
            Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions { Tenancy = new Autheris.Domain.Options.TenancyOptions { DefaultTenantId = "default-tenant" } }));
    }

    private static TableAccessDecision WithAttribute(string text = "1 = 0") =>
        TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["cost_center"] = ColumnAccessLevel.Clear }, rowFilterSql: text, hasUnconstrainedColumnAllow: false)
        with
        {
            ConsentFilterRecipe = new ConsentRowFilterRecipe(
                [[new ConsentRowFilter { ColumnName = "cost_center", Operator = "IN", ValueSource = "USER_ATTRIBUTE", UserAttribute = "secret_attribute_name" }]], [])
        };

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER")], "test"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerAndOwner_SeeOnlyThatAnAttributeFilterApplies(bool owner)
    {
        var dto = await Service(WithAttribute(), owner).ExplainMyAccessAsync(User(), Orders);

        dto.ActiveRowFilters.ShouldBe([EffectiveAccessExplanationService.AttributeRowFilterDescription]);
        string.Join(' ', dto.ActiveRowFilters).ShouldNotContain("secret_attribute_name");
        string.Join(' ', dto.ActiveRowFilters).ShouldNotContain("1 = 0");
    }

    [Fact]
    public async Task ALiteralFilter_IsDescribedAsBefore()
    {
        var literal = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }, rowFilterSql: "cost_center = 'x'", hasUnconstrainedColumnAllow: false);
        (await Service(literal, owner: false).ExplainMyAccessAsync(User(), Orders)).ActiveRowFilters.ShouldBe([EffectiveAccessExplanationService.RowFilterDescription]);
        (await Service(literal, owner: true).ExplainMyAccessAsync(User(), Orders)).ActiveRowFilters.ShouldBe(["cost_center = 'x'"]);
    }
}
