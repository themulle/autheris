using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Identity.Attributes.CallerAttributeTestSupport;

namespace Autheris.Tests.Integration.Governance;

[Trait("Category", "Security")]
public sealed class ConsentRowFilterAttributeValidatorGovernanceTests
{
    private static readonly TableIdentifier Orders = new("hr_db", "main", "orders");

    private static ConsentRowFilter Attr(string op = "EQ", string name = "cost_center", string json = "[]", string source = "USER_ATTRIBUTE", RowFilterType type = RowFilterType.SimpleColumnPredicate) => new()
    {
        ColumnName = "cost_center", Operator = op, UserAttribute = name, ValueJson = json, ValueSource = source, FilterType = type
    };

    private static Consent ConsentOf(params ConsentRowFilter[] filters) => new()
    {
        TableIdentifier = Orders, TenantId = Tenant, GranteeType = GranteeType.Role, RoleName = "analyst", Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1), RowFilters = filters
    };

    [Fact]
    public async Task ST_129_24_DirectRepository_Create_RefusesTheShapeViolations_OnPostgreSql()
    {
        using var store = GovernanceTestDatabase.CreateRepository(
            Substitute.For<IEpochValidationService>(),
            Microsoft.Extensions.Options.Options.Create(new GatewayOptions()));
        IConsentRepository repo = store;
        await Should.ThrowAsync<InvalidOperationException>(() => repo.CreateConsentAsync(ConsentOf(Attr(source: "STATIC")), default));
        await Should.ThrowAsync<InvalidOperationException>(() => repo.CreateConsentAsync(ConsentOf(Attr(json: "\"x\"")), default));
        await Should.ThrowAsync<InvalidOperationException>(() => repo.ReplaceConsentsAsync([], [ConsentOf(Attr("LIKE"))], new Sid("S-1"), "r"));
    }
}
