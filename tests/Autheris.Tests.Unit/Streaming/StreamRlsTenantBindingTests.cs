namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class StreamRlsTenantBindingTests : IDisposable
{
    private readonly IPolicyEnforcementService _policyEnforcement = Substitute.For<IPolicyEnforcementService>();
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IColumnMaskingProvider _maskingProvider = Substitute.For<IColumnMaskingProvider>();
    private readonly IEpochValidationService _epochService = Substitute.For<IEpochValidationService>();
    private readonly IConsentRepository _consentRepo = Substitute.For<IConsentRepository>();
    private readonly IConsentResolutionService _resolution = Substitute.For<IConsentResolutionService>();
    private readonly IConsentCacheService _cache = Substitute.For<IConsentCacheService>();
    private readonly StreamRlsPolicyEnforcer _sut;

    private static readonly TableIdentifier TestTable = new("crm", "public", "customers");

    public StreamRlsTenantBindingTests()
    {
        _consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var meta = new TableMetadata
        {
            Identifier = TestTable,
            Table = new Table { SourceName = "crm", SchemaName = "public", TableName = "customers" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        };
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(meta));

        _resolution.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(),
                Arg.Any<IReadOnlySet<Sid>?>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(TableAccessDecision.Allowed(TestTable, new Dictionary<string, ColumnAccessLevel>()));

        _sut = new StreamRlsPolicyEnforcer(
            _policyEnforcement,
            _metadataRepo,
            _maskingProvider,
            _epochService,
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            _consentRepo,
            _resolution,
            _cache);
    }

    public void Dispose()
    {
        Sid.RealmLookup = null;
    }

    [Fact]
    public async Task FixedRealmBinding_IdpTenantClaimForOtherTenant_EventDenied()
    {
        var boundTenant = new TenantId("tenant-corp-bound");
        var realm = new IdentityRealm(
            "corp-realm",
            IdentityRealmKind.Customer,
            tenantBinding: TenantBindingSpecification.Fixed(boundTenant));

        Sid.RealmLookup = r => r == "corp-realm" ? realm : null;

        // Subscriber token has conflicting raw tenant_id claim "other-tenant" but autheris:realm "corp-realm"
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("autheris:realm", "corp-realm"),
            new Claim("tenant_id", "other-tenant"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")
        ], "TestAuth"));

        var cdcEvent = new CdcEvent(
            Guid.NewGuid().ToString(),
            TestTable,
            CdcOperation.Insert,
            boundTenant.Value,
            null,
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
            DateTimeOffset.UtcNow);

        var decision = await _sut.EvaluateAndMaskAsync(cdcEvent, principal);

        decision.IsAllowed.ShouldBeFalse();
        decision.FilterReason.ShouldBe("Tenant mismatch");
    }

    [Fact]
    public async Task FixedRealmBinding_EventOfBoundTenant_Allowed()
    {
        var boundTenant = new TenantId("tenant-corp-bound");
        var realm = new IdentityRealm(
            "corp-realm",
            IdentityRealmKind.Customer,
            tenantBinding: TenantBindingSpecification.Fixed(boundTenant));

        Sid.RealmLookup = r => r == "corp-realm" ? realm : null;

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("autheris:realm", "corp-realm"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")
        ], "TestAuth"));

        var cdcEvent = new CdcEvent(
            Guid.NewGuid().ToString(),
            TestTable,
            CdcOperation.Insert,
            boundTenant.Value,
            null,
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
            DateTimeOffset.UtcNow);

        var decision = await _sut.EvaluateAndMaskAsync(cdcEvent, principal);

        decision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task ClaimMappedBinding_TidGuid_ResolvesBoundTenant()
    {
        var mappedTenant = new TenantId("tenant-mapped-finance");
        var realm = new IdentityRealm(
            "finance-realm",
            IdentityRealmKind.Customer,
            tenantBinding: TenantBindingSpecification.ClaimMapped(
                "finance_tid",
                new Dictionary<string, TenantId> { ["org-12345"] = mappedTenant }));

        Sid.RealmLookup = r => r == "finance-realm" ? realm : null;

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("autheris:realm", "finance-realm"),
            new Claim("finance_tid", "org-12345"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")
        ], "TestAuth"));

        var cdcEvent = new CdcEvent(
            Guid.NewGuid().ToString(),
            TestTable,
            CdcOperation.Insert,
            mappedTenant.Value,
            null,
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
            DateTimeOffset.UtcNow);

        var decision = await _sut.EvaluateAndMaskAsync(cdcEvent, principal);

        decision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task RawTenantClaimConflictsWithBinding_Denied()
    {
        var boundTenant = new TenantId("tenant-corp-bound");
        var realm = new IdentityRealm(
            "corp-realm",
            IdentityRealmKind.Customer,
            tenantBinding: TenantBindingSpecification.Fixed(boundTenant));

        Sid.RealmLookup = r => r == "corp-realm" ? realm : null;

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("autheris:realm", "corp-realm"),
            new Claim("tid", "conflicting-tenant-id"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")
        ], "TestAuth"));

        var cdcEvent = new CdcEvent(
            Guid.NewGuid().ToString(),
            TestTable,
            CdcOperation.Insert,
            boundTenant.Value,
            null,
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
            DateTimeOffset.UtcNow);

        var decision = await _sut.EvaluateAndMaskAsync(cdcEvent, principal);

        decision.IsAllowed.ShouldBeFalse();
        decision.FilterReason.ShouldBe("Tenant mismatch");
    }
}
