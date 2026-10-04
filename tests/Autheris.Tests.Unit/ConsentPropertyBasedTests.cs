using FsCheck;
using FsCheck.Xunit;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Xunit;

namespace Autheris.Tests.Unit;

public class ConsentPropertyBasedTests
{
    private static readonly TableIdentifier TestTable = new("testdom", "public", "customers");
    private static readonly Sid UserSid = new("S-1-5-21-USER1");
    private static readonly Sid GroupSid = new("S-1-5-21-GRP1");
    private readonly ConsentResolutionService _service = new();

    [Property(MaxTest = 100)]
    public bool Invariant_ZeroTrust_WhenNoAllowConsents_AccessAlwaysDenied(PositiveInt count)
    {
        var denyCount = Math.Min(count.Item, 20);
        var denies = Enumerable.Range(0, denyCount).Select(_ => new Consent
        {
            TableIdentifier = TestTable,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        }).ToList();

        var decision = _service.ResolveAccess(
            UserSid,
            new HashSet<Sid> { GroupSid },
            new HashSet<string>(),
            TestTable,
            denies
        );

        return !decision.IsAllowed;
    }

    [Property(MaxTest = 100)]
    public bool Invariant_HardTableDeny_AlwaysOverridesAnyNumberOfAllows(PositiveInt count)
    {
        var allowCount = Math.Min(count.Item, 30);
        var allows = Enumerable.Range(0, allowCount).Select(i => new Consent
        {
            TableIdentifier = TestTable,
            Effect = ConsentEffect.Allow,
            GranteeType = (i % 2 == 0) ? GranteeType.User : GranteeType.Group,
            GranteeSid = (i % 2 == 0) ? UserSid : GroupSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        }).ToList();

        var hardDeny = new Consent
        {
            TableIdentifier = TestTable,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };

        var allConsents = allows.Append(hardDeny).ToList();

        var decision = _service.ResolveAccess(
            UserSid,
            new HashSet<Sid> { GroupSid },
            new HashSet<string>(),
            TestTable,
            allConsents
        );

        return !decision.IsAllowed;
    }

    [Property(MaxTest = 100)]
    public bool Invariant_HardColumnDeny_NeverAllowsClearAccess(PositiveInt count)
    {
        var allowCount = Math.Min(count.Item, 20);
        var allows = Enumerable.Range(0, allowCount).Select(_ => new Consent
        {
            TableIdentifier = TestTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = new[]
            {
                new ConsentColumnRule { ColumnName = "secret_col", AccessLevel = ColumnAccessLevel.Clear }
            }
        }).ToList();

        var columnDeny = new Consent
        {
            TableIdentifier = TestTable,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.Group,
            GranteeSid = GroupSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = new[]
            {
                new ConsentColumnRule { ColumnName = "secret_col", AccessLevel = ColumnAccessLevel.Deny }
            }
        };

        var allConsents = allows.Append(columnDeny).ToList();

        var decision = _service.ResolveAccess(
            UserSid,
            new HashSet<Sid> { GroupSid },
            new HashSet<string>(),
            TestTable,
            allConsents
        );

        // secret_col MUST NOT be Clear!
        return decision.ColumnAccess.ContainsKey("secret_col")
            && decision.ColumnAccess["secret_col"] == ColumnAccessLevel.Deny;
    }
}
