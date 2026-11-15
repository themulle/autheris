namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SEC-OI-508-5: no caller identity means no catalog; only the explicit auth bypass opens it.</summary>
[Trait("Category", "Security")]
public sealed class SemanticMcpCompilerTests
{
    private static ITableMetadataRepository RepoWithOneTable()
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("finance", "dbo", "customers"),
            Table = new Table { SchemaName = "dbo", TableName = "customers", Description = "Customers" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
        return Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(repo);
    }

    [Fact]
    public async Task No_principal_no_service_returns_empty()
    {
        var compiler = new SemanticMcpCompiler(RepoWithOneTable(), NullLogger<SemanticMcpCompiler>.Instance);

        (await compiler.GetSemanticResourcesAsync("finance", principal: null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task No_principal_with_consent_repository_returns_empty()
    {
        var compiler = new SemanticMcpCompiler(
            RepoWithOneTable(), NullLogger<SemanticMcpCompiler>.Instance, consentRepo: Substitute.For<IConsentRepository>());

        (await compiler.GetSemanticResourcesAsync("finance", principal: null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Explicit_auth_bypass_still_opens_the_catalog()
    {
        var options = Options.Create(new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_mcp_auth = true } });
        var compiler = new SemanticMcpCompiler(RepoWithOneTable(), NullLogger<SemanticMcpCompiler>.Instance, options: options);

        (await compiler.GetSemanticResourcesAsync("finance", principal: null)).ShouldNotBeEmpty();
    }
}
