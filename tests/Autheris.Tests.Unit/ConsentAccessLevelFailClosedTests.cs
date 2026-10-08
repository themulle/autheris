using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class ConsentAccessLevelFailClosedTests
{
    private readonly TableIdentifier _testTable = new("finance", "dbo", "invoices");
    private readonly Sid _userSid = new("S-1-5-21-1001");
    private readonly ConsentResolutionService _service = new();

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(999)]
    public void ConsentColumnRule_NormalizesInvalidAccessLevel_ToDeny(int invalidInt)
    {
        var rule = new ConsentColumnRule
        {
            ColumnName = "salary",
            AccessLevel = (ColumnAccessLevel)invalidInt
        };

        rule.AccessLevel.ShouldBe(ColumnAccessLevel.Deny);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void CachedConsentEnvelope_ToDecision_NormalizesInvalidAccessLevel_ToDeny(int invalidInt)
    {
        var env = new CachedConsentEnvelope
        {
            Domain = "finance",
            Schema = "dbo",
            TableName = "invoices",
            IsAllowed = true,
            ColumnAccess = new Dictionary<string, int>
            {
                ["salary"] = invalidInt,
                ["email"] = (int)ColumnAccessLevel.Clear
            }
        };

        var decision = env.ToDecision();
        decision.ColumnAccess["salary"].ShouldBe(ColumnAccessLevel.Deny);
        decision.ColumnAccess["email"].ShouldBe(ColumnAccessLevel.Clear);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(10)]
    public void ResolveAccess_WhenRowFilterConsentHasInvalidColumnAccessLevel_FailsClosedToDeny(int invalidInt)
    {
        var consent = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            IsRevoked = false,
            RowFilters =
            [
                new ConsentRowFilter
                {
                    FilterGroup = 1,
                    ColumnName = "department",
                    Operator = "EQ",
                    ValueJson = "\"Finance\""
                }
            ],
            ColumnRules =
            [
                new ConsentColumnRule
                {
                    ColumnName = "salary",
                    AccessLevel = (ColumnAccessLevel)invalidInt
                },
                new ConsentColumnRule
                {
                    ColumnName = "amount",
                    AccessLevel = ColumnAccessLevel.Clear
                }
            ]
        };

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            _testTable,
            [consent]);

        decision.IsAllowed.ShouldBeTrue();
        decision.ColumnAccess["salary"].ShouldBe(ColumnAccessLevel.Deny);
        decision.ColumnAccess["amount"].ShouldBe(ColumnAccessLevel.Clear);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void ResolveAccess_WhenUnconstrainedConsentHasInvalidColumnAccessLevel_FailsClosedToDeny(int invalidInt)
    {
        var consent = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            IsRevoked = false,
            RowFilters = [],
            ColumnRules =
            [
                new ConsentColumnRule
                {
                    ColumnName = "salary",
                    AccessLevel = (ColumnAccessLevel)invalidInt
                },
                new ConsentColumnRule
                {
                    ColumnName = "amount",
                    AccessLevel = ColumnAccessLevel.Clear
                }
            ]
        };

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            _testTable,
            [consent]);

        decision.IsAllowed.ShouldBeTrue();
        decision.ColumnAccess["salary"].ShouldBe(ColumnAccessLevel.Deny);
        decision.ColumnAccess["amount"].ShouldBe(ColumnAccessLevel.Clear);
    }
}
