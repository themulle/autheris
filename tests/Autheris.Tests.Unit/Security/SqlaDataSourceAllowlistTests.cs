namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Analysis;
using Xunit;

/// <summary>SQLA-07: the allowlist is checked on the data source a read really uses (catalog SourceName, mapped), not only on the identifier domain.</summary>
public sealed class SqlaDataSourceAllowlistTests
{
    private const string TenantA = "tenant_a";

    private static TableMetadata Table(string domain, string sourceName) => new()
    {
        Identifier = new TableIdentifier(domain, "public", "a"),
        Table = new Table
        {
            TableName = "a",
            SchemaName = "public",
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            SourceName = sourceName,
            IsActive = true
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static WebSqlOptions Web(params string[] tenantAllowed) => new()
    {
        DefaultDataSourceName = "default",
        AllowedDataSources = ["ds1", "ds2", "default"],
        TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [TenantA] = [.. tenantAllowed] }
    };

    private static CrossSourceQueryRouter Router(WebSqlOptions web, params TableMetadata[] metas)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        foreach (var m in metas)
        {
            repo.GetTableMetadataAsync(m.Identifier, Arg.Any<CancellationToken>()).Returns(m);
        }

        return new CrossSourceQueryRouter(repo, new CrossSourceOptions { Enabled = true }, web);
    }

    private static SqlQueryMetadata Query(string catalog) =>
        new(SqlStatementType.Select, [new TableAccessTarget(catalog, "public", "a", null, $"{catalog}.public.a")], ["id"], 0, 0, false, null);

    [Fact]
    public async Task Router_SourceNameNotInTenantAllowlist_Denied()
    {
        // The catalog entry is addressed through the allowed domain ds1 but reads from ds2.
        var router = Router(Web("ds1"), Table("ds1", "ds2"));

        await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await router.RouteAsync(Query("ds1"), null, new TenantId(TenantA), isDml: false));
    }

    [Fact]
    public async Task Router_DefaultFallbackToUnlistedSourceName_Denied()
    {
        // ds1.public.a does not exist; the default.public.a fallback reads from ds2, which is not the requested domain.
        var router = Router(Web("ds1", "default", "ds2"), Table("default", "ds2"));

        await Should.ThrowAsync<WebSqlTableDeniedException>(async () =>
            await router.RouteAsync(Query("ds1"), null, new TenantId(TenantA), isDml: false));
    }

    [Fact]
    public async Task Router_MappedSourceAllowed_Passes()
    {
        var web = Web("ds1");
        web.DataSourceMappings["ds1"] = "ds1";
        var router = Router(web, Table("ds1", "ds1"));

        var decision = await router.RouteAsync(Query("ds1"), null, new TenantId(TenantA), isDml: false);

        decision.ShouldNotBeNull();
    }

    [Fact]
    public void EffectiveDataSource_PrefersSourceName_ThenDomain_AndMaps()
    {
        var web = Web("ds1");
        web.DataSourceMappings["alias"] = "ds2";

        DataSourceAllowlistGuard.EffectiveDataSource(web, Table("ds1", "alias")).ShouldBe("ds2");
        DataSourceAllowlistGuard.EffectiveDataSource(web, Table("ds1", "")).ShouldBe("ds1");
    }

    [Fact]
    public void EffectiveDataSource_EmptyEverywhere_IsDeniedByTheGuard()
    {
        // A TableIdentifier cannot be constructed with an empty domain; a default value is the only way to reach "empty everywhere".
        var meta = Table("ds1", "") with { Identifier = default };

        var effective = DataSourceAllowlistGuard.EffectiveDataSource(Web("ds1"), meta);

        Should.Throw<WebSqlPolicyException>(() => DataSourceAllowlistGuard.EnsureAllowed(Web("ds1"), new TenantId(TenantA), effective));
    }
}
