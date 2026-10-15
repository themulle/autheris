namespace Autheris.Tests.Integration;

using Xunit;

/// <summary>
/// WP-TS-05: Isolates integration tests that mutate process-wide state (such as environment variables
/// or global connection pools) into a non-parallel xUnit collection so other test collections can safely
/// execute in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialIntegrationCollection
{
    public const string Name = "SerialIntegration";
}
