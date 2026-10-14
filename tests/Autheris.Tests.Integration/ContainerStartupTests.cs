namespace Autheris.Tests.Integration;

using Shouldly;

/// <summary>
/// A container that fails to start must fail the run under CI. Locally the tests skip silently (no Docker on a developer machine),
/// but a green CI run must never mean "the database tests did not run".
/// </summary>
public sealed class ContainerStartupTests
{
    // A server that cannot exist: the connection attempt fails fast, no container is started.
    private const string UnreachableServer = "Server=127.0.0.1,1;User Id=sa;Password=x;Connect Timeout=1;TrustServerCertificate=true";

    [Fact]
    public async Task SqlServerTestDatabase_StartupFailure_FailsWhenRequired()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => SqlServerTestDatabase.CreateAsync(UnreachableServer, required: true));

        ex.InnerException.ShouldNotBeNull();
    }

    [Fact]
    public async Task SqlServerTestDatabase_StartupFailure_IsASilentSkipWhenNotRequired()
    {
        var db = await SqlServerTestDatabase.CreateAsync(UnreachableServer, required: false);

        db.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public void FailIfRequired_Throws_OnlyWhenRequired()
    {
        var original = new TimeoutException("boom");

        Should.Throw<InvalidOperationException>(() => ContainerStartup.FailIfRequired(original, "PostgreSQL", required: true))
            .InnerException.ShouldBeSameAs(original);
        Should.NotThrow(() => ContainerStartup.FailIfRequired(original, "PostgreSQL", required: false));
    }

    [Theory]
    [InlineData("true", null, true)]
    [InlineData("TRUE", null, true)]
    [InlineData(null, "1", true)]
    [InlineData("false", null, false)]
    [InlineData(null, null, false)]
    public void IsRequired_ReadsCiAndTheGateSwitch(string? ci, string? gate, bool expected)
    {
        ContainerStartup.IsRequired(name => name == "CI" ? ci : name == "SQLGATE_REQUIRE_DATABASES" ? gate : null).ShouldBe(expected);
    }
}
