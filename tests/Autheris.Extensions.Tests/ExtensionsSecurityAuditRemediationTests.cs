namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.Dbt;
using Autheris.Extensions.Lakehouse.Services;
using Autheris.Application.OData;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ExtensionsSecurityAuditRemediationTests
{
    [Fact]
    public async Task ODataHandler_Unauthenticated_ReturnsEmptyMetadataAndDocument()
    {
        // CRIT-05: Unauthenticated access to OData metadata or service doc must return empty/restricted tables
        var metaMock = Substitute.For<ITableMetadataRepository>();
        metaMock.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([
                new TableMetadata
                {
                    Identifier = new TableIdentifier("corp", "sales", "invoices"),
                    Table = new Table { SchemaName = "sales", TableName = "invoices" },
                    Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
                },
                new TableMetadata
                {
                    Identifier = new TableIdentifier("secret", "hr", "salaries"),
                    Table = new Table { SchemaName = "hr", TableName = "salaries" },
                    Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
                }
            ]));

        var execMock = Substitute.For<IGatewayExecutionService>();
        // SR-M-16: a missing decision is a deny, so the access check must answer explicitly.
        execMock.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(TableAccessDecision.Allowed(
                ci.Arg<TableMetadata>().Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));
        var handler = new ODataHandler(metaMock, execMock, NullLogger<ODataHandler>.Instance);

        // 1. Unauthenticated principal
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity());
        var csdl = await handler.GetMetadataCsdlAsync(unauthenticated);
        csdl.ShouldNotContain("invoices");
        csdl.ShouldNotContain("salaries");

        // 2. Authenticated principal for 'corp' tenant
        var corpUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "corp")], "TestAuth"));
        var corpCsdl = await handler.GetMetadataCsdlAsync(corpUser);
        corpCsdl.ShouldContain("invoices");
        corpCsdl.ShouldNotContain("salaries"); // 'secret' domain not visible to 'corp' tenant
    }

    [Fact]
    public async Task Dbt_ValidateContractsStreamAsync_RejectsNullOrMalformedManifest()
    {
        var metaMock = Substitute.For<ITableMetadataRepository>();
        var validator = new DbtContractValidator(metaMock, NullLogger<DbtContractValidator>.Instance);

        await Should.ThrowAsync<ArgumentNullException>(() => validator.ValidateContractsStreamAsync(null!));

        using var badStream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("{not-json"));
        var result = await validator.ValidateContractsStreamAsync(badStream);
        result.IsCompatible.ShouldBeFalse();
        result.Warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public void Iceberg_ValidateManifestLocation_RejectsTraversalAndRestrictedPaths()
    {
        // CRIT-07: Iceberg manifest location must reject null bytes, restricted paths, and directory traversal
        Should.Throw<SecurityException>(() =>
            IcebergMetadataReader.ValidateManifestLocation("metadata.json\0", null));

        Should.Throw<SecurityException>(() =>
            IcebergMetadataReader.ValidateManifestLocation("/etc/passwd", null));

        Should.Throw<SecurityException>(() =>
            IcebergMetadataReader.ValidateManifestLocation("/var/run/secrets/token", null));

        // When tableLocation is specified, manifest must reside within table directory
        Should.Throw<SecurityException>(() =>
            IcebergMetadataReader.ValidateManifestLocation("/data/other_table/metadata.json", "/data/my_table"));

        // Valid inside table location
        IcebergMetadataReader.ValidateManifestLocation("/data/my_table/metadata.json", "/data/my_table");
    }
}
