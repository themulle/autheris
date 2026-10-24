namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>#320 (SEC-RG-01/02): a source the caller may not use answers exactly like an unknown table (no existence oracle).</summary>
public sealed class CrossSourceQueryRouterTests
{
    private static readonly TenantId Tenant = new("tenant_test");

    private static TableMetadata Orders(string source = "crm") => new()
    {
        Identifier = new TableIdentifier(source, "public", "orders"),
        Table = new Table
        {
            TableName = "orders",
            SchemaName = "public",
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            SourceName = source,
            IsActive = true
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static (CrossSourceQueryRouter Router, ITableMetadataRepository Repo) CreateRouter(
        Dictionary<string, List<string>>? tenantAllowlist = null)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.Arg<TableIdentifier>();
                if (string.Equals(id.Domain, "down", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SqlSourceUnavailableException("down");
                }

                return string.Equals(id.TableName, "orders", StringComparison.OrdinalIgnoreCase) &&
                       (string.Equals(id.Domain, "crm", StringComparison.OrdinalIgnoreCase) || string.Equals(id.Domain, "other", StringComparison.OrdinalIgnoreCase))
                    ? Orders(id.Domain)
                    : null;
            });
        var webSql = new WebSqlOptions
        {
            Enabled = true,
            DefaultDataSourceName = "crm",
            AllowedDataSources = ["crm", "down"],
            TenantDataSourceAllowlist = tenantAllowlist ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
        return (new CrossSourceQueryRouter(repo, new CrossSourceOptions { Enabled = true }, webSql), repo);
    }

    private static Task<QueryRoutingDecision> Route(CrossSourceQueryRouter router, string sql) =>
        router.RouteAsync(FastSqlEngine.Default.Analyze(sql.AsMemory()), null, Tenant, isDml: false, CancellationToken.None);

    [Theory]
    [InlineData("SELECT id FROM nosuch.public.orders")]
    [InlineData("SELECT o.id FROM crm.public.orders o JOIN nosuch.public.orders p ON o.id = p.id")]
    public async Task UnknownSource_ThrowsTableDenied(string sql)
    {
        var (router, _) = CreateRouter();

        await Should.ThrowAsync<WebSqlTableDeniedException>(() => Route(router, sql));
    }

    [Fact]
    public async Task SourceNotEnabledForTenant_ThrowsTableDenied()
    {
        var (router, _) = CreateRouter(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Tenant.Value] = ["down"]
        });

        await Should.ThrowAsync<WebSqlTableDeniedException>(() => Route(router, "SELECT id FROM crm.public.orders"));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task DeniedSource_NoMetadataLookup()
    {
        var (router, repo) = CreateRouter();

        await Should.ThrowAsync<WebSqlTableDeniedException>(() => Route(router, "SELECT id FROM nosuch.public.orders"));

        repo.ReceivedCalls().ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task AllowedDownSource_Returns503()
    {
        var (router, _) = CreateRouter();

        var ex = await Should.ThrowAsync<SqlSourceUnavailableException>(() => Route(router, "SELECT id FROM down.public.orders"));

        SqlCompilerErrorMapper.TryMap(ex, out var mapping).ShouldBeTrue();
        mapping.Status.ShouldBe(503);
        mapping.Code.ShouldBe("SQL_SOURCE_UNAVAILABLE");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task UnknownSourceUnknownTableDisabledSource_ByteIdenticalBody()
    {
        var (router, _) = CreateRouter(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Tenant.Value] = ["crm"]
        });

        var unknownSource = await Should.ThrowAsync<Exception>(() => Route(router, "SELECT id FROM nosuch.public.orders"));
        var unknownTable = await Should.ThrowAsync<Exception>(() => Route(router, "SELECT id FROM crm.public.nosuch"));
        var disabledForTenant = await Should.ThrowAsync<Exception>(() => Route(router, "SELECT id FROM other.public.orders"));

        SqlCompilerErrorMapper.TryMap(unknownSource, out var a).ShouldBeTrue();
        SqlCompilerErrorMapper.TryMap(unknownTable, out var b).ShouldBeTrue();
        SqlCompilerErrorMapper.TryMap(disabledForTenant, out var c).ShouldBeTrue();
        a.ShouldBe(b);
        a.ShouldBe(c);
        a.Status.ShouldBe(403);
        a.Code.ShouldBe("SQL_POLICY_DENIED");
        unknownSource.GetType().ShouldBe(unknownTable.GetType());
        disabledForTenant.GetType().ShouldBe(unknownTable.GetType());
    }
}
