namespace Autheris.Tests.Unit.GraphQL;

using System.Text.Json;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using Shouldly;
using Xunit;

/// <summary>
/// GQL-06: the approve idempotency fingerprint distinguishes different restrictions.
/// </summary>
public sealed class Gql06ApproveFingerprintTests
{
    [Fact]
    public void GQL_06_DifferentRowFilters_ProduceDifferentFingerprints()
    {
        var a = new RowFilterInput { RowFilterSql = "region = 'EU'" };
        var b = new RowFilterInput { RowFilterSql = "region = 'US'" };

        var fa = Mutation.IdempotencyFingerprint("t", "r", "d", JsonSerializer.Serialize(a), string.Empty);
        var fb = Mutation.IdempotencyFingerprint("t", "r", "d", JsonSerializer.Serialize(b), string.Empty);

        fa.ShouldNotBe(fb);
    }
}
