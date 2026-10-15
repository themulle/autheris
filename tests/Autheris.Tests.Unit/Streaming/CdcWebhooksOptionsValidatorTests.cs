namespace Autheris.Tests.Unit.Streaming;

using Autheris.Application.Events.Services;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public sealed class CdcWebhooksOptionsValidatorTests
{
    private readonly CdcWebhooksOptionsValidator _validator = new();

    [Fact]
    public void Defaults_AreValid_AndDocumented()
    {
        var o = new CdcWebhooksOptions();
        _validator.Validate(null, o).Succeeded.ShouldBeTrue();
        o.Enabled.ShouldBeTrue(); // G-2: enabled by default
        o.MaxSubscriptionsPerTenant.ShouldBe(100);
        o.CreatorSnapshotMaxAgeDays.ShouldBe(90);
        o.MaxSnapshotGroups.ShouldBe(256);
    }

    [Theory]
    [InlineData(0, 90, 256, 30)]
    [InlineData(100, -1, 256, 30)]
    [InlineData(100, 90, 0, 30)]
    [InlineData(100, 90, 256, 0)]
    public void OutOfRange_Fails(int max, int age, int groups, int audit) =>
        _validator.Validate(null, new CdcWebhooksOptions
        {
            MaxSubscriptionsPerTenant = max, CreatorSnapshotMaxAgeDays = age, MaxSnapshotGroups = groups, AuditBatchSeconds = audit
        }).Failed.ShouldBeTrue();
}
