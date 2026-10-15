using System.Text;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>SEC-94-08a/c: the snapshot resource fails closed; catalog reads bind names as parameters.</summary>
[Trait("Category", "Security")]
public sealed class GovernanceSchemaShapeVerifierTests
{
    private static MemoryStream Stream(string json) => new(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void MissingOrEmptySnapshotResource_ThrowsAtStartup()
    {
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.LoadEmbedded("nonexistent-engine"));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.Parse(Stream(""), "sqlite", "r"));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.Parse(Stream("not json"), "sqlite", "r"));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.Parse(Stream("null"), "sqlite", "r"));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.Parse(Stream("{\"engine\":\"sqlite\",\"tables\":[]}"), "sqlite", "r"));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.Parse(Stream("{\"engine\":\"sqlite\"}"), "sqlite", "r"));
        Should.Throw<InvalidOperationException>(
            () => GovernanceSchemaShapeLoader.Parse(Stream("{\"engine\":\"sqlite\",\"tables\":[{\"name\":\"T\",\"columns\":[]}]}"), "sqlite", "r"));
        Should.Throw<InvalidOperationException>(
            () => GovernanceSchemaShapeLoader.Parse(Stream("{\"engine\":\"postgresql\",\"tables\":[{\"name\":\"T\",\"columns\":[{\"name\":\"c\",\"type\":\"TEXT\",\"family\":\"string\"}]}]}"), "sqlite", "r"));
    }

    [Fact]
    public async Task Verifier_WithMissingSnapshot_NeverPasses()
    {
        var verifier = new GovernanceSchemaShapeVerifier(
            () => throw new InvalidOperationException("snapshot missing"), new SqliteSchemaCatalogReader());
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => verifier.VerifyAsync(connection, CancellationToken.None));
    }

    [Fact]
    public void EmbeddedSnapshots_AreNotEmpty_ForEveryEngine()
    {
        foreach (var engine in new[] { "sqlite", "postgresql", "sqlserver" })
        {
            var shape = GovernanceSchemaShapeLoader.LoadEmbedded(engine);
            shape.Tables.Count.ShouldBeGreaterThan(25);
            shape.Tables.ShouldContain(t => t.Name.Equals("TABLES", StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>L2: catalog reader commands enlist the active step transaction (SQL Server and PostgreSQL require it).</summary>
public sealed class SchemaCatalogReaderTransactionTests
{
    [Fact]
    public async Task Command_EnlistsTheActiveTransaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        using var withTx = SchemaCatalogReaderSupport.Command(new CatalogSession(connection, tx), "SELECT 1");

        withTx.Transaction.ShouldBe(tx);
    }

    [Fact]
    public async Task Verifier_InsideATransaction_ReadsThroughIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();
        var verifier = new GovernanceSchemaShapeVerifier("sqlite", new SqliteSchemaCatalogReader());

        var result = await verifier.VerifyAsync(connection, CancellationToken.None, tx);

        result.Deviations.ShouldNotBeEmpty(); // empty database: every table missing, read without error inside the transaction
    }
}
