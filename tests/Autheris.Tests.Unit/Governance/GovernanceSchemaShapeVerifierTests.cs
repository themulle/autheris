using System.Text;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using NSubstitute;
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
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.Parse(Stream(""), "postgresql", "r"));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.Parse(Stream("not json"), "postgresql", "r"));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.Parse(Stream("null"), "postgresql", "r"));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.Parse(Stream("{\"engine\":\"postgresql\",\"tables\":[]}"), "postgresql", "r"));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.Parse(Stream("{\"engine\":\"postgresql\"}"), "postgresql", "r"));
        Should.Throw<InvalidOperationException>(
            () => GovernanceSchemaShapeLoader.Parse(Stream("{\"engine\":\"postgresql\",\"tables\":[{\"name\":\"T\",\"columns\":[]}]}"), "postgresql", "r"));
        Should.Throw<InvalidOperationException>(
            () => GovernanceSchemaShapeLoader.Parse(Stream("{\"engine\":\"sqlserver\",\"tables\":[{\"name\":\"T\",\"columns\":[{\"name\":\"c\",\"type\":\"TEXT\",\"family\":\"string\"}]}]}"), "postgresql", "r"));
    }

    [Fact]
    public async Task Verifier_WithMissingSnapshot_NeverPasses()
    {
        var verifier = new GovernanceSchemaShapeVerifier(
            () => throw new InvalidOperationException("snapshot missing"), new PostgreSqlSchemaCatalogReader());

        await Should.ThrowAsync<InvalidOperationException>(() => verifier.VerifyAsync(Substitute.For<System.Data.Common.DbConnection>(), CancellationToken.None));
    }

    [Fact]
    public void EmbeddedSnapshots_AreNotEmpty_ForEveryEngine()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var shape = GovernanceSchemaShapeLoader.LoadEmbedded(engine);
            shape.Tables.Count.ShouldBeGreaterThan(25);
            shape.Tables.ShouldContain(t => t.Name.Equals("TABLES", StringComparison.OrdinalIgnoreCase));
        }
    }
}
