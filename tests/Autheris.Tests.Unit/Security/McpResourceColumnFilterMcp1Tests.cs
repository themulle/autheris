namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// MCP-1: resources/list and resources/read must apply the same table and column visibility as the GraphQL catalog:
/// unconditional Deny consents hide the table, denied or not granted columns appear in no resource.
/// </summary>
public sealed class McpResourceColumnFilterMcp1Tests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Hr = new("corp", "public", "employees");
    private static readonly TableIdentifier Payroll = new("corp", "public", "payroll");

    private static TableMetadata Table(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, SourceName = id.Domain, SourceType = "PostgreSQL" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int", Description = "Key" },
            new TableColumn { ColumnName = "name", DataType = "varchar", Description = "Display name" },
            new TableColumn { ColumnName = "salary_band", DataType = "varchar", Description = "Confidential salary band" }
        ]
    };

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-MCP-USER"),
                new Claim("tenant_id", Tenant)
            ],
            "MCP"));

    private static SemanticMcpCompiler CreateCompiler(params Consent[] consents)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(new List<TableMetadata> { Table(Hr), Table(Payroll) }));

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents.ToList()));

        return new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance, null, consentRepo);
    }

    [Fact]
    public async Task DeniedColumn_AppearsInNoResource()
    {
        var compiler = CreateCompiler(
            new Consent { TableIdentifier = Hr, Effect = ConsentEffect.Allow, TenantId = new TenantId(Tenant) },
            new Consent
            {
                TableIdentifier = Hr,
                Effect = ConsentEffect.Deny,
                TenantId = new TenantId(Tenant),
                ColumnRules = [new ConsentColumnRule { ColumnName = "salary_band", AccessLevel = ColumnAccessLevel.Deny }]
            });

        var resources = await compiler.GetSemanticResourcesAsync(principal: User());

        resources.ShouldNotBeEmpty();
        resources.ShouldAllBe(r => !r.Text.Contains("salary_band") && !r.Uri.Contains("salary_band"));
        resources.ShouldContain(r => r.Uri == "dbt://models/employees/columns/name/docs");
    }

    [Fact]
    public async Task ColumnRestrictedAllow_ShowsOnlyGrantedColumns()
    {
        var compiler = CreateCompiler(
            new Consent
            {
                TableIdentifier = Hr,
                Effect = ConsentEffect.Allow,
                TenantId = new TenantId(Tenant),
                ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
            });

        var resources = await compiler.GetSemanticResourcesAsync(principal: User());

        resources.ShouldAllBe(r => !r.Text.Contains("`name`") && !r.Text.Contains("salary_band"));
    }

    [Fact]
    public async Task UnconditionalDeny_HidesTable()
    {
        var compiler = CreateCompiler(
            new Consent { TableIdentifier = Hr, Effect = ConsentEffect.Allow, TenantId = new TenantId(Tenant) },
            new Consent { TableIdentifier = Hr, Effect = ConsentEffect.Deny, TenantId = new TenantId(Tenant) },
            new Consent { TableIdentifier = Payroll, Effect = ConsentEffect.Allow, TenantId = new TenantId(Tenant) });

        var resources = await compiler.GetSemanticResourcesAsync(principal: User());

        resources.ShouldAllBe(r => !r.Uri.Contains("employees"));
        resources.ShouldContain(r => r.Uri.Contains("payroll"));
    }
}
