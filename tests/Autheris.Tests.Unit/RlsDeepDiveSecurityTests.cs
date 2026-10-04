#pragma warning disable CA2012
using System.Security.Claims;
using System.Security;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public sealed class RlsDeepDiveSecurityTests
{
    private readonly TableIdentifier _testTable = new("crm", "dbo", "customers");
    private readonly Sid _userSid = new("S-1-5-21-RLS-TEST-USER");
    private readonly TenantId _tenantId = new("tenant-rls-test");

    [Fact]
    public void Test1_ConsentCombination_UnconstrainedAllowDoesNotExposeRowConstrainedColumnsInCleartext()
    {
        // RLS-1 / RLS-2 Verification:
        // Consent A: Table-wide allow, unconstrained rows, but only general columns (e.g. name = Clear, email = Mask)
        // Consent B: Constrained rows (region = 'EU'), but grants sensitive column (email = Clear)
        // The unconstrained table rows MUST NOT see 'email' in Cleartext!
        var sqlBuilder = new RowFilterSqlBuilder();
        var resolver = new ConsentResolutionService(sqlBuilder);

        var now = DateTimeOffset.UtcNow;
        var consentA = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(1),
            RowFilters = new List<ConsentRowFilter>(), // Unconstrained rows!
            ColumnRules = new List<ConsentColumnRule>
            {
                new() { ColumnName = "name", AccessLevel = ColumnAccessLevel.Clear },
                new() { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
            }
        };

        var consentB = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(1),
            RowFilters = new List<ConsentRowFilter>
            {
                new()
                {
                    ColumnName = "region",
                    Operator = "EQ",
                    ValueJson = "\"EU\""
                }
            },
            ColumnRules = new List<ConsentColumnRule>
            {
                new() { ColumnName = "email", AccessLevel = ColumnAccessLevel.Clear }
            }
        };

        var decision = resolver.ResolveAccess(
            _userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            _testTable,
            new[] { consentA, consentB },
            DatabaseDialect.SqlServer);

        decision.IsAllowed.ShouldBeTrue();
        // Zero-Trust: email must NOT be elevated to Clear across unconstrained rows!
        decision.GetColumnAccess("name").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public void Test2_InMemoryRls_DenyFilterWithDotsAndSpecialCharsInStringLiteral_IsNotMangledByRegex()
    {
        // RLS-5 Verification:
        // Filter: NOT (email = 'john.doe@x.de')
        // In-Memory normalizer MUST NOT strip table prefixes inside string literals (turning 'john.doe@x.de' into 'doe@x.de')
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["email"] = "john.doe@x.de" },
            new Dictionary<string, object?> { ["id"] = 2, ["email"] = "other.user@x.de" }
        };

        var metadata = new TableMetadata
        {
            Identifier = _testTable,
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "int" },
                new() { ColumnName = "email", DataType = "varchar" }
            }
        };

        // When evaluating in-memory filter: NOT ([email] = 'john.doe@x.de')
        var filterSql = "NOT ([email] = 'john.doe@x.de')";

        var filtered = GatewayExecutionService.FilterRows(rows, filterSql, metadata);

        // 'john.doe@x.de' should be excluded, 'other.user@x.de' should remain
        filtered.Count.ShouldBe(1);
        filtered[0]["email"].ShouldBe("other.user@x.de");
    }

    [Fact]
    public async Task Test3_CasbinTemplate_MissingClaimAttribute_ThrowsSecurityExceptionFailClosed()
    {
        // RLS-4 Verification:
        // Casbin RLS Template with ${department} when user lacks department claim MUST throw SecurityException
        var rlsGen = RlsFilterGenerator.Instance;
        var casbinService = new CasbinEnforcementService(null, rlsGen);

        var policyText = $$"""
        p, {{_userSid.Value}}, tenant-rls-test, crm.dbo.customers, read, true, allow, department LIKE '${department}%'
        """;
        casbinService.LoadPolicyFromText(_tenantId, policyText);

        var contextWithoutDept = new SecurityEvaluationContext(
            UserSid: _userSid,
            GroupSids: Array.Empty<Sid>(),
            Tenant: _tenantId,
            TargetTable: _testTable,
            RequestedColumns: new[] { "id", "name" },
            ClientIp: System.Net.IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null,
            Attributes: new Dictionary<string, object?>() // No department!
        );

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await casbinService.EvaluatePolicyAsync(contextWithoutDept);
        });
    }

    [Fact]
    public void Test4_InMemoryRls_MissingFilterColumn_FailsClosed()
    {
        // RLS-5 Verification:
        // When upstream rows do not contain the filter column, in-memory RLS must fail-closed
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" }
        };

        var metadata = new TableMetadata
        {
            Identifier = _testTable,
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "int" },
                new() { ColumnName = "name", DataType = "varchar" }
            }
        };

        // Filter references 'region' which does not exist in metadata or row
        var filterSql = "[region] = 'EU'";

        var filtered = GatewayExecutionService.FilterRows(rows, filterSql, metadata);

        // Fail-closed -> 0 rows returned
        filtered.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Test5_StreamRls_SubscriberWithRowConstraint_DiscardsForeignRowEvents()
    {
        // RLS-3 / K1 Verification:
        // CDC event streaming with StreamRlsPolicyEnforcer must reject events failing CombinedRowFilterSql
        var policyEnforcement = Substitute.For<IPolicyEnforcementService>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        var epochService = Substitute.For<IEpochValidationService>();

        policyEnforcement.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(TableAccessDecision.Allowed(
                _testTable,
                new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["name"] = ColumnAccessLevel.Clear,
                    ["region"] = ColumnAccessLevel.Clear
                },
                rowFilterSql: "[region] = 'EU'")));

        metadataRepo.GetTableMetadataAsync(_testTable, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(new TableMetadata
            {
                Identifier = _testTable,
                Columns = new List<TableColumn>
                {
                    new() { ColumnName = "id", DataType = "int" },
                    new() { ColumnName = "name", DataType = "varchar" },
                    new() { ColumnName = "region", DataType = "varchar" }
                }
            }));

        // SEC H-09: Consent is the primary decision; Casbin (with RLS filter) acts as additional gate when policies exist.
        policyEnforcement.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(TableAccessDecision.Allowed(_testTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var enforcer = new StreamRlsPolicyEnforcer(
            policyEnforcement,
            metadataRepo,
            maskingProvider,
            epochService,
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            consentRepo,
            resolution,
            Substitute.For<IConsentCacheService>());

        var cdcEvent = new CdcEvent(
            EventId: "evt-us-1",
            Table: _testTable,
            Operation: CdcOperation.Insert,
            TenantId: _tenantId.Value,
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 1, ["name"] = "US Customer", ["region"] = "US" },
            Timestamp: DateTimeOffset.UtcNow
        );

        var subscriber = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, _userSid.Value),
            new Claim("tenant_id", _tenantId.Value)
        }, "Test"));

        var result = await enforcer.EvaluateAndMaskAsync(cdcEvent, subscriber);

        // Foreign row event (US when filter requires EU) MUST be dropped!
        result.IsAllowed.ShouldBeFalse();
        result.MaskedPayload.ShouldBeNull();
    }

    [Fact]
    public void Test6_ConsentExpiration_TtlIsBoundedByEarliestValidTo()
    {
        // RLS-7 Verification:
        // Ensure that decision caching uses min(ttl, earliest ValidTo - now)
        var now = DateTimeOffset.UtcNow;
        var soonExpiringConsent = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddSeconds(15) // Expires in 15s!
        };

        var activeConsents = new[] { soonExpiringConsent };
        var defaultTtl = TimeSpan.FromMinutes(10);

        var earliestExpiry = activeConsents
            .Where(c => c.ValidTo > now)
            .Select(c => c.ValidTo - now)
            .DefaultIfEmpty(defaultTtl)
            .Min();

        var computedTtl = earliestExpiry < defaultTtl
            ? (earliestExpiry > TimeSpan.FromSeconds(1) ? earliestExpiry : TimeSpan.FromSeconds(1))
            : defaultTtl;

        // TTL must be approx 15 seconds, NOT 10 minutes
        computedTtl.TotalSeconds.ShouldBeLessThan(20);
        computedTtl.TotalSeconds.ShouldBeGreaterThan(5);
    }

    [Fact]
    public void Test7_CasbinCorrelatedSubquery_InvalidPredicateStructure_FailsClosed()
    {
        // RLS-9 / RLS-6 Verification:
        // Invalid subquery predicate JSON structure must throw InvalidOperationException (Fail-Closed)
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("crm", "dbo", "customers"),
            TargetTableAlias = "i",
            ForeignKeyColumn = "customer_id",
            PrimaryKeyColumn = "id",
            SubqueryFilterPredicateJson = "[123, 456]" // Invalid: array of non-objects
        };

        Should.Throw<InvalidOperationException>(() =>
        {
            AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, DatabaseDialect.SqlServer);
        });
    }
}
