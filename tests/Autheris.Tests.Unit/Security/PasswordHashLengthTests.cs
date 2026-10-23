using Autheris.Api.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>API-05: truncated hashes and short salts are rejected by the verifier and the well-formedness check.</summary>
[Trait("Category", "Security")]
public sealed class PasswordHashLengthTests
{
    [Fact]
    public void Pbkdf2_TruncatedHash_NeverVerifies_API05()
    {
        var salt = Convert.ToBase64String(new byte[16]);
        var oneByte = Convert.ToBase64String(new byte[1]);

        PasswordHasher.VerifyPassword("anything", $"$pbkdf2$600000${salt}${oneByte}", "alice", isDevelopment: false).ShouldBeFalse();
    }

    [Fact]
    public void Pbkdf2_EmptySalt_NeverVerifies_API05()
    {
        var hash = Convert.ToBase64String(new byte[32]);

        PasswordHasher.VerifyPassword("anything", $"$pbkdf2$600000$${hash}", "alice", isDevelopment: false).ShouldBeFalse();
    }

    [Fact]
    public void GeneratedHashes_AreWellFormed_API05()
    {
        PasswordHasher.HasWellFormedSaltAndHash(PasswordHasher.HashPasswordPbkdf2("pw", 600_000)).ShouldBeTrue();
        PasswordHasher.HasWellFormedSaltAndHash(PasswordHasher.HashPasswordArgon2id("pw", 8192, 1, 1)).ShouldBeTrue();
        PasswordHasher.VerifyPassword("pw", PasswordHasher.HashPasswordPbkdf2("pw", 600_000), "alice", isDevelopment: false).ShouldBeTrue();
    }

    [Theory]
    [InlineData("$pbkdf2$600000$AAAAAAAAAAAAAAAAAAAAAA==$AA==")]
    [InlineData("$pbkdf2$600000$$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("$argon2id$v=19$m=8192,t=1,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AA==")]
    [InlineData("$pbkdf2$600000$x")]
    [InlineData("plaintext")]
    public void TruncatedOrMalformedValues_AreNotWellFormed_API05(string stored) =>
        PasswordHasher.HasWellFormedSaltAndHash(stored).ShouldBeFalse();
}
