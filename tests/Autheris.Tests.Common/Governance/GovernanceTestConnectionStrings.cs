namespace Autheris.Tests.Common.Governance;

/// <summary>Governance connection strings for tests that only compose the host and never open the governance database.</summary>
public static class GovernanceTestConnectionStrings
{
    /// <summary>A PostgreSQL connection string that satisfies the startup guard, opens nothing at composition and fails fast when used.</summary>
    public const string NonConnectingPostgreSql =
        "Host=127.0.0.1;Port=1;Database=autheris_test;Username=autheris_test;Password=not-a-secret;SSL Mode=VerifyFull;Timeout=1";
}
