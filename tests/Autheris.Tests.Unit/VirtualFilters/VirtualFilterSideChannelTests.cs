namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 3: the two paths that resolve consents without <c>TableAccessPolicy</c>. CDC events cannot
/// evaluate a subquery and are dropped when a virtual filter applies; raw Iceberg metadata cannot be filtered and is
/// refused.
/// </summary>
public sealed class VirtualFilterSideChannelTests
{
    private static readonly TableIdentifier Orders = new("tenant-1", "raw", "orders");

    private sealed class FixedOutcome(MandatoryFilterOutcome outcome) : IMandatoryRowFilterResolver
    {
        public ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default) => ValueTask.FromResult(outcome);
    }

    public static TheoryData<string, bool> Outcomes() => new()
    {
        { "none", true },
        { "predicate", false },
        { "deny", false }
    };

    private static MandatoryFilterOutcome Outcome(string kind) => kind switch
    {
        "predicate" => new MandatoryFilterOutcome(false, null, "P_filter_a(client_id)", ["filter_a"]),
        "deny" => MandatoryFilterOutcome.Deny("uncovered"),
        _ => MandatoryFilterOutcome.None
    };

    private static TableMetadata Meta(DataSourceType type = DataSourceType.Sql) => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "tenant-1", SchemaName = "raw", TableName = "orders", DataSourceType = type },
        Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "client_id" }]
    };

    private static IConsentResolutionService AllowAll()
    {
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        return resolution;
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task CdcEvent_IsDropped_WhenAVirtualFilterApplies(string outcome, bool delivered)
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(Meta());
        var enforcer = new StreamRlsPolicyEnforcer(
            Substitute.For<IPolicyEnforcementService>(), metadata, Substitute.For<IColumnMaskingProvider>(), Substitute.For<IEpochValidationService>(),
            NullLogger<StreamRlsPolicyEnforcer>.Instance, Substitute.For<IConsentRepository>(), AllowAll(), Substitute.For<IConsentCacheService>(),
            mandatoryFilters: new FixedOutcome(Outcome(outcome)));
        var subscriber = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "tenant-1"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-STREAM-USER")], "Test"));
        var cdcEvent = new CdcEvent("ev-1", Orders, CdcOperation.Insert, "tenant-1", null, new Dictionary<string, object?> { ["id"] = 1, ["client_id"] = 7 }, DateTimeOffset.UtcNow);

        var result = await enforcer.EvaluateAndMaskAsync(cdcEvent, subscriber);

        result.IsAllowed.ShouldBe(delivered);
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task IcebergRawAccess_IsRefused_WhenAVirtualFilterApplies(string outcome, bool delivered)
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Meta(DataSourceType.LakehouseIceberg));
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        var service = new IcebergRestCatalogFederationService(
            Substitute.For<IIcebergMetadataReader>(), metadata, Options.Create(new GatewayOptions()), NullLogger<IcebergRestCatalogFederationService>.Instance,
            AllowAll(), consents, mandatoryFilters: new FixedOutcome(Outcome(outcome)));
        var analyst = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "analyst"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ANALYST")], "Bearer"));

        if (delivered)
        {
            await service.LoadTableAsync("tenant-1", "raw", "orders", analyst);
        }
        else
        {
            await Should.ThrowAsync<SecurityException>(() => service.LoadTableAsync("tenant-1", "raw", "orders", analyst).AsTask());
        }
    }
}
