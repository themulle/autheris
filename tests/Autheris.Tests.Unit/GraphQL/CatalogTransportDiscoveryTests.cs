using Autheris.Tests.Common;

namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Autheris.GraphQL.Catalog;
using Autheris.Tests.Unit.DataSources;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #447 WP-4 (REQ-ATR-08, SEC-ATR-08): the GraphQL catalog schema omits tables whose source does not allow GraphQL.</summary>
[Trait("Category", "Security")]
public sealed class CatalogTransportDiscoveryTests
{
    public sealed class PingQuery
    {
        public string Ping() => "pong";
    }

    private static TableMetadata Table(string name, string source) => new()
    {
        Identifier = new TableIdentifier("sales", "dbo", name),
        Table = new Table { SourceName = source, SchemaName = "dbo", TableName = name, IsActive = true },
        Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }],
        PrimaryKeyColumns = ["id"]
    };

    private static (CatalogGraphQlTypeModule Module, ITableMetadataRepository Meta) NewModule(IDataSourceOptionsResolver resolver)
    {
        var meta = Substitute.For<ITableMetadataRepository>();
        var rel = Substitute.For<ITableRelationRepository>();
        rel.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);
        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new List<TableMetadata> { Table("visible_t", "open_src"), Table("hidden_t", "secret_src") });
        return (new CatalogGraphQlTypeModule(meta, rel, transportResolver: resolver), meta);
    }

    private static HierarchicalDataSourceOptionsResolver Resolver(DataSourceTransportFlags secretFlags)
    {
        var options = new DataSourceConfigurationOptions();
        options.Profiles["secret_src"] = new DataSourceProfileOptions { AllowedTransports = secretFlags };
        return new HierarchicalDataSourceOptionsResolver(new DataSourceTransportGateTests.FixedMonitor<DataSourceConfigurationOptions>(options));
    }

    [Fact]
    public async Task Discovery_GraphQlSchemaOmitsDeniedSource()
    {
        using var resolver = Resolver(DataSourceTransportFlags.WebSql);
        var (module, _) = NewModule(resolver);
        using var _ = module;

        var executor = await new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).BuildRequestExecutorAsync();

        module.GetTableMetadataForField("sales_dbo_visible_t").ShouldNotBeNull();
        module.GetTableMetadataForField("sales_dbo_hidden_t").ShouldBeNull();
        executor.Schema.ToString().ShouldContain("visible_t");
        executor.Schema.ToString().ShouldNotContain("hidden_t");
    }

    [Fact]
    public async Task Discovery_GraphQlSchemaKeepsSourceAllowingGraphQl()
    {
        using var resolver = Resolver(DataSourceTransportFlags.GraphQL);
        var (module, _) = NewModule(resolver);
        using var _ = module;

        var executor = await new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).BuildRequestExecutorAsync();

        module.GetTableMetadataForField("sales_dbo_hidden_t").ShouldNotBeNull();
        executor.Schema.ToString().ShouldContain("hidden_t");
    }

    private sealed class ReloadableMonitor(DataSourceConfigurationOptions initial) : Microsoft.Extensions.Options.IOptionsMonitor<DataSourceConfigurationOptions>
    {
        private DataSourceConfigurationOptions _value = initial;
        private Action<DataSourceConfigurationOptions, string?>? _listener;

        public DataSourceConfigurationOptions CurrentValue => _value;

        public DataSourceConfigurationOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<DataSourceConfigurationOptions, string?> listener)
        {
            _listener = listener;
            return null;
        }

        public void Reload(DataSourceConfigurationOptions next)
        {
            _value = next;
            _listener?.Invoke(next, null);
        }
    }

    private static DataSourceConfigurationOptions Config(DataSourceTransportFlags secretFlags)
    {
        var options = new DataSourceConfigurationOptions();
        options.Profiles["secret_src"] = new DataSourceProfileOptions { AllowedTransports = secretFlags };
        return options;
    }

    [Fact]
    public async Task Discovery_ProfileChangeTriggersSchemaRebuild()
    {
        var monitor = new ReloadableMonitor(Config(DataSourceTransportFlags.GraphQL));
        using var resolver = new HierarchicalDataSourceOptionsResolver(monitor);
        var (module, _) = NewModule(resolver);
        using var _ = module;
        var raised = 0;
        module.TypesChanged += (_, _) => raised++;
        await new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).BuildRequestExecutorAsync();

        (await module.CheckForCatalogChangesAsync()).ShouldBeFalse();
        monitor.Reload(Config(DataSourceTransportFlags.WebSql));
        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        raised.ShouldBe(1);
    }
}
