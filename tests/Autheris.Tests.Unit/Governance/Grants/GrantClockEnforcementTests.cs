namespace Autheris.Tests.Unit.Governance.Grants;

using System;
using System.Collections.Generic;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>Grant validity is enforced by the decision itself, so an expired grant never depends on the expiry sweep having run.</summary>
[Trait("Category", "Security")]
public sealed class GrantClockEnforcementTests
{
    [Fact]
    public void Expired_grant_denied_without_sweep()
    {
        var table = new TableIdentifier("sales", "dbo", "orders");
        var user = new Sid("user-alice");
        var now = DateTimeOffset.UtcNow;

        // Stored as still active (IsRevoked = false): the sweep never ran, but the validity ended an hour ago.
        var expired = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = user,
            ValidFrom = now.AddDays(-5),
            ValidTo = now.AddHours(-1),
            IsRevoked = false
        };

        var decision = new ConsentResolutionService().ResolveAccess(user, new HashSet<Sid>(), new HashSet<string>(), table, [expired]);

        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Unexpired_grant_is_allowed_by_the_same_path()
    {
        var table = new TableIdentifier("sales", "dbo", "orders");
        var user = new Sid("user-alice");
        var now = DateTimeOffset.UtcNow;
        var live = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = user,
            ValidFrom = now.AddDays(-5),
            ValidTo = now.AddDays(5)
        };

        new ConsentResolutionService().ResolveAccess(user, new HashSet<Sid>(), new HashSet<string>(), table, [live]).IsAllowed.ShouldBeTrue();
    }
}
