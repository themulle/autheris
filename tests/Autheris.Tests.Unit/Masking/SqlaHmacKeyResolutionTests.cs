using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Masking;

/// <summary>SQLA-03: the master HMAC key is resolved under a lock and a failed resolve is retried after a back-off.</summary>
public sealed class SqlaHmacKeyResolutionTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly byte[] MasterKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static MaskSpec Spec(SqlDataMaskingProvider provider) =>
        provider.GetMaskSpec(new MaskingRule { RuleType = "HMAC" }, "col", "varchar", DatabaseDialect.PostgreSql, Tenant);

    [Fact]
    public async Task ConcurrentColdStart_AllCallersGetTheHmacSpec()
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(_ =>
        {
            Thread.Sleep(100);
            return MasterKey;
        });
        var provider = new SqlDataMaskingProvider(Options.Create(new GatewayOptions()), secrets);

        var specs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Spec(provider))));

        specs.ShouldAllBe(s => s.Kind == MaskKind.Hmac);
        secrets.Received(1).GetSecretBytes(Arg.Any<string>());
    }

    [Fact]
    public void TransientFailure_IsRetriedAfterBackoff_AndRedactsMeanwhile()
    {
        var calls = 0;
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("vault unavailable");
            }

            return MasterKey;
        });
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-22T10:00:00Z"));
        var provider = new SqlDataMaskingProvider(Options.Create(new GatewayOptions()), secrets, timeProvider: time);

        Spec(provider).Kind.ShouldNotBe(MaskKind.Hmac);
        Spec(provider).Kind.ShouldNotBe(MaskKind.Hmac);
        calls.ShouldBe(1);

        time.Advance(TimeSpan.FromMinutes(2));

        Spec(provider).Kind.ShouldBe(MaskKind.Hmac);
        Spec(provider).Kind.ShouldBe(MaskKind.Hmac);
        calls.ShouldBe(2);
    }
}
