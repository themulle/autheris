namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TableWritePermissionTests
{
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly Sid AliceSid = new("S-1-5-21-ALICE");
    private static readonly TenantId TenantA = new("tenant-a");

    [Fact]
    public async Task TableAccessPolicy_CanWriteTableAsync_Stays_Denied_With_Allow_Consent()
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var allowConsent = new Consent
        {
            TableIdentifier = OrdersTable,
            TenantId = TenantA,
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Consent>>(new[] { allowConsent }.Where(c => c.TableIdentifier == ci.ArgAt<TableIdentifier>(1)).ToList()));

        var tableAccessPolicy = new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            cacheService: Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            options: new GatewayOptions(),
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, AliceSid.Value),
            new Claim("sub", AliceSid.Value)
        }, "TestAuth");
        var user = new ClaimsPrincipal(identity);

        var tableMeta = new TableMetadata
        {
            Identifier = OrdersTable,
            Columns = new[]
            {
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            }
        };

        (await tableAccessPolicy.CanWriteTableAsync(user, TenantA, tableMeta)).ShouldBeFalse();
    }

    [Fact]
    public async Task TableAccessPolicy_CanWriteTableAsync_WithContract_Stays_Denied()
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var allowConsent = new Consent
        {
            TableIdentifier = OrdersTable,
            TenantId = TenantA,
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Consent>>(new[] { allowConsent }));

        var tableAccessPolicy = new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            cacheService: Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            options: new GatewayOptions(),
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, AliceSid.Value),
            new Claim("sub", AliceSid.Value)
        }, "TestAuth");
        var user = new ClaimsPrincipal(identity);

        var tableMeta = new TableMetadata
        {
            Identifier = OrdersTable,
            Table = new Table { TableName = "orders", SchemaName = "dbo" },
            Columns = new[]
            {
                new TableColumn { ColumnName = "id", DataType = "int" }
            }
        };

        // When contract allows only "customers"
        var contractExcluding = new Autheris.Application.Governance.Contracts.SchemaContractDefinition("partner", allowedTables: ["customers"]);
        var canWriteDisallowed = await tableAccessPolicy.CanWriteTableAsync(user, TenantA, tableMeta, contractExcluding, default);
        canWriteDisallowed.ShouldBeFalse();

        // When contract allows "orders"
        var contractAllowing = new Autheris.Application.Governance.Contracts.SchemaContractDefinition("partner", allowedTables: ["orders"]);
        var canWriteAllowed = await tableAccessPolicy.CanWriteTableAsync(user, TenantA, tableMeta, contractAllowing, default);
        canWriteAllowed.ShouldBeFalse();
    }
}
