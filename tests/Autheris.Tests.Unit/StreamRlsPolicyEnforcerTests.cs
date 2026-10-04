#pragma warning disable CA2012

namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class StreamRlsPolicyEnforcerTests
{
    private readonly IPolicyEnforcementService _policyEnforcement = Substitute.For<IPolicyEnforcementService>();
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IColumnMaskingProvider _maskingProvider = Substitute.For<IColumnMaskingProvider>();
    private readonly IEpochValidationService _epochService = Substitute.For<IEpochValidationService>();
    private readonly IConsentRepository _consentRepo = Substitute.For<IConsentRepository>();
    private readonly IConsentResolutionService _resolution = Substitute.For<IConsentResolutionService>();
    private readonly IConsentCacheService _cache = Substitute.For<IConsentCacheService>();
    private readonly StreamRlsPolicyEnforcer _sut;

    public StreamRlsPolicyEnforcerTests()
    {
        _consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

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

    // SEC H-09: The consent decision (IConsentResolutionService) is the primary source; Casbin is an additional gate.
    private void SetupConsentDecision(TableAccessDecision decision)
    {
        _resolution.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(decision);
    }

    private void SetupMetadata(TableIdentifier table, string[] columns, IReadOnlyDictionary<string, MaskingRule>? maskingRules = null)
    {
        var cols = new List<TableColumn>();
        foreach (var c in columns)
        {
            cols.Add(new TableColumn { ColumnName = c });
        }

        _metadataRepo.GetTableMetadataAsync(table, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(new TableMetadata
            {
                Identifier = table,
                Table = new Table { SourceName = table.Domain, SchemaName = table.Schema, TableName = table.TableName },
                Columns = cols,
                ColumnMaskingRules = maskingRules ?? new Dictionary<string, MaskingRule>()
            }));
    }

    [Fact]
    public async Task EvaluateAndMaskAsync_TenantMismatch_ReturnsDenied()
    {
        var table = new TableIdentifier("sales", "crm", "customers");
        var cdcEvent = new CdcEvent(
            EventId: "evt-1",
            Table: table,
            Operation: CdcOperation.Insert,
            TenantId: "tenant-a",
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
            Timestamp: DateTimeOffset.UtcNow
        );

        var subscriber = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-b"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-ALICE")
        }));

        var result = await _sut.EvaluateAndMaskAsync(cdcEvent, subscriber);

        result.IsAllowed.ShouldBeFalse();
        result.FilterReason.ShouldBe("Tenant mismatch");
        result.MaskedPayload.ShouldBeNull();
    }

    [Fact]
    public async Task EvaluateAndMaskAsync_AbacPolicyDenied_ReturnsDenied()
    {
        var table = new TableIdentifier("sales", "crm", "customers");
        var cdcEvent = new CdcEvent(
            EventId: "evt-2",
            Table: table,
            Operation: CdcOperation.Update,
            TenantId: "tenant-a",
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
            Timestamp: DateTimeOffset.UtcNow
        );

        var subscriber = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-BOB")
        }));

        SetupMetadata(table, ["id", "name"]);
        SetupConsentDecision(TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        _policyEnforcement.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        _policyEnforcement.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(new TableAccessDecision(
                Table: table,
                IsAllowed: false,
                ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
                CombinedRowFilterSql: null,
                DeniedReasons: new[] { "Casbin deny" }
            )));

        var result = await _sut.EvaluateAndMaskAsync(cdcEvent, subscriber);

        result.IsAllowed.ShouldBeFalse();
        result.FilterReason.ShouldBe("Access denied by ABAC policy");
    }

    [Fact]
    public async Task EvaluateAndMaskAsync_AllowedWithMaskingAndDeny_MasksAndStripsColumns()
    {
        var table = new TableIdentifier("sales", "crm", "customers");
        var cdcEvent = new CdcEvent(
            EventId: "evt-3",
            Table: table,
            Operation: CdcOperation.Insert,
            TenantId: "tenant-a",
            Before: null,
            After: new Dictionary<string, object?>
            {
                ["id"] = 100,
                ["name"] = "Charlie",
                ["email"] = "charlie@enterprise.com",
                ["ssn"] = "123-45-6789"
            },
            Timestamp: DateTimeOffset.UtcNow
        );

        var subscriber = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-CHARLIE")
        }));

        // Column permissions: id (Clear), name (Clear), email (Mask), ssn (Deny)
        var columnAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask,
            ["ssn"] = ColumnAccessLevel.Deny
        };

        SetupConsentDecision(new TableAccessDecision(
            Table: table,
            IsAllowed: true,
            ColumnAccess: columnAccess,
            CombinedRowFilterSql: null,
            DeniedReasons: Array.Empty<string>()
        ));

        SetupMetadata(
            table,
            ["id", "name", "email", "ssn"],
            new Dictionary<string, MaskingRule>
            {
                ["email"] = new MaskingRule { RuleType = "MASK_EMAIL" }
            });

        _maskingProvider.MaskValue("email", "charlie@enterprise.com", Arg.Any<MaskingRule>())
            .Returns("c***@enterprise.com");

        var result = await _sut.EvaluateAndMaskAsync(cdcEvent, subscriber);

        result.IsAllowed.ShouldBeTrue();
        result.MaskedPayload.ShouldNotBeNull();
        result.MaskedPayload["id"].ShouldBe(100);
        result.MaskedPayload["name"].ShouldBe("Charlie");
        result.MaskedPayload["email"].ShouldBe("c***@enterprise.com");
        result.MaskedPayload.ContainsKey("ssn").ShouldBeFalse(); // Denied column stripped
    }

    [Fact]
    public async Task EvaluateAndMaskAsync_DeleteOperation_UsesBeforePayload()
    {
        var table = new TableIdentifier("finance", "accounting", "invoices");
        var cdcEvent = new CdcEvent(
            EventId: "evt-4",
            Table: table,
            Operation: CdcOperation.Delete,
            TenantId: "tenant-a",
            Before: new Dictionary<string, object?> { ["invoice_id"] = 999, ["amount"] = 5000.0 },
            After: null,
            Timestamp: DateTimeOffset.UtcNow
        );

        var subscriber = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-AUDITOR")
        }));

        SetupMetadata(table, ["invoice_id", "amount"]);
        SetupConsentDecision(new TableAccessDecision(
            Table: table,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: null,
            DeniedReasons: Array.Empty<string>(),
            HasUnconstrainedColumnAllow: true
        ));

        var result = await _sut.EvaluateAndMaskAsync(cdcEvent, subscriber);

        result.IsAllowed.ShouldBeTrue();
        result.MaskedPayload.ShouldNotBeNull();
        result.MaskedPayload["invoice_id"].ShouldBe(999);
        result.MaskedPayload["amount"].ShouldBe(5000.0);
    }

    [Fact]
    public async Task EvaluateAndMaskAsync_InListFilter_MatchesAndAllows()
    {
        var table = new TableIdentifier("sales", "crm", "orders");
        var cdcEvent = new CdcEvent(
            EventId: "evt-5",
            Table: table,
            Operation: CdcOperation.Insert,
            TenantId: "tenant-a",
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 10, ["status"] = "PENDING", ["amount"] = 250 },
            Timestamp: DateTimeOffset.UtcNow
        );

        var subscriber = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-CLERK")
        }));

        SetupMetadata(table, ["id", "status", "amount"]);
        SetupConsentDecision(new TableAccessDecision(
            Table: table,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: "status IN ('PENDING', 'PROCESSING') AND amount >= 100",
            DeniedReasons: Array.Empty<string>(),
            HasUnconstrainedColumnAllow: true
        ));

        var result = await _sut.EvaluateAndMaskAsync(cdcEvent, subscriber);

        result.IsAllowed.ShouldBeTrue();
        result.MaskedPayload.ShouldNotBeNull();
        result.MaskedPayload["id"].ShouldBe(10);
    }

    [Fact]
    public async Task EvaluateAndMaskAsync_GreaterThanFilter_FiltersOutWhenNotMet()
    {
        var table = new TableIdentifier("sales", "crm", "orders");
        var cdcEvent = new CdcEvent(
            EventId: "evt-6",
            Table: table,
            Operation: CdcOperation.Insert,
            TenantId: "tenant-a",
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 11, ["status"] = "PENDING", ["amount"] = 50 },
            Timestamp: DateTimeOffset.UtcNow
        );

        var subscriber = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-CLERK")
        }));

        SetupMetadata(table, ["id", "status", "amount"]);
        SetupConsentDecision(new TableAccessDecision(
            Table: table,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: "amount >= 100",
            DeniedReasons: Array.Empty<string>(),
            HasUnconstrainedColumnAllow: true
        ));

        var result = await _sut.EvaluateAndMaskAsync(cdcEvent, subscriber);

        result.IsAllowed.ShouldBeFalse();
        result.FilterReason.ShouldBe("Filtered by row-level security");
    }

    [Fact]
    public async Task EvaluateAndMaskAsync_ComplexBooleanAndLikeFilter_EvaluatesAccurately()
    {
        var table = new TableIdentifier("sales", "crm", "customers");
        var cdcEvent = new CdcEvent(
            EventId: "evt-7",
            Table: table,
            Operation: CdcOperation.Insert,
            TenantId: "tenant-a",
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 12, ["email"] = "vip.client@enterprise.com", ["level"] = "GOLD" },
            Timestamp: DateTimeOffset.UtcNow
        );

        var subscriber = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-MANAGER")
        }));

        SetupMetadata(table, ["id", "email", "level"]);
        SetupConsentDecision(new TableAccessDecision(
            Table: table,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: "(level = 'GOLD' OR level = 'PLATINUM') AND email LIKE '%.client@%'",
            DeniedReasons: Array.Empty<string>(),
            HasUnconstrainedColumnAllow: true
        ));

        var result = await _sut.EvaluateAndMaskAsync(cdcEvent, subscriber);

        result.IsAllowed.ShouldBeTrue();
        result.MaskedPayload.ShouldNotBeNull();
        result.MaskedPayload["id"].ShouldBe(12);
    }

    [Fact]
    public void StreamingRowFilterAstEvaluator_HighThroughputEvaluation_MatchesAccurately()
    {
        var payload = new Dictionary<string, object?>
        {
            ["level"] = "GOLD",
            ["email"] = "alice.smith@enterprise.com",
            ["age"] = 35,
            ["country"] = "DE"
        };

        const string filterSql = "(level = 'GOLD' OR level = 'PLATINUM') AND age >= 18 AND country IN ('DE', 'AT', 'CH') AND email LIKE '%.smith@%'";

        for (int i = 0; i < 500; i++)
        {
            bool isMatch = StreamingRowFilterAstEvaluator.Matches(payload, filterSql);
            isMatch.ShouldBeTrue();
        }

        var nonMatchingPayload = new Dictionary<string, object?>
        {
            ["level"] = "SILVER",
            ["email"] = "bob@other.com",
            ["age"] = 16,
            ["country"] = "US"
        };

        StreamingRowFilterAstEvaluator.Matches(nonMatchingPayload, filterSql).ShouldBeFalse();
    }
}
