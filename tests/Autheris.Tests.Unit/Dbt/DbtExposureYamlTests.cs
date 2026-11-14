namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using YamlDotNet.RepresentationModel;

public sealed class DbtExposureYamlTests
{
    private sealed class FakeMetadataRepository(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(tables.FirstOrDefault(t => t.Identifier == table));

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult(tables);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class HostileTelemetryProvider(string hostileConsumer, string hostileTier) : ITelemetryMetricsProvider
    {
        public ValueTask<TableTelemetryMetrics> GetTableMetricsAsync(TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(new TableTelemetryMetrics(
                Table: table,
                MonthlyQueries: 42,
                P99LatencyMs: 12.3,
                TopConsumers: [hostileConsumer, "consumer: #comment\n---"],
                GovernanceTier: hostileTier));

        public void RecordQueryExecution(TableIdentifier table, double latencyMs, string? consumerName = null) { }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Quote_and_newline_in_name_cannot_inject_yaml()
    {
        var hostileNames = new[]
        {
            "orders\"injected: true #comment",
            "tbl: value --- - name: evil",
            "\"*alias &anchor !tag",
            "- ? @ `backtick` 'single' \"double\"",
            "orders \U0001F600 emoji_and_quotes\"colon: test"
        };

        var tables = hostileNames
            .Select((name, idx) => new TableMetadata
            {
                Identifier = new TableIdentifier("sales", $"sch_{idx}: #x *anchor &alias", name),
                Table = new Table
                {
                    SourceName = "sales",
                    SchemaName = $"sch_{idx}: #x *anchor &alias",
                    TableName = name
                }
            })
            .ToList();

        var hostileConsumer = "bi-tool\"\ninjected_actor: true\n  - rogue";
        var hostileTier = "Tier: \"Gold\"\ninjected_meta: 99\n---";

        var publisher = new DbtExposurePublisher(
            new FakeMetadataRepository(tables),
            NullLogger<DbtExposurePublisher>.Instance,
            new HostileTelemetryProvider(hostileConsumer, hostileTier));

        var yaml = await publisher.GenerateExposuresYamlAsync();

        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        stream.Documents.Count.ShouldBe(1);
        var root = stream.Documents[0].RootNode.ShouldBeOfType<YamlMappingNode>();
        var rootKeys = root.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).ToList();
        rootKeys.ShouldBe(["version", "exposures"]);
        ((YamlScalarNode)root.Children[new YamlScalarNode("version")]).Value.ShouldBe("2");

        var exposures = root.Children[new YamlScalarNode("exposures")].ShouldBeOfType<YamlSequenceNode>();
        exposures.Children.Count.ShouldBe(tables.Count);

        for (var i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            var entry = exposures.Children[i].ShouldBeOfType<YamlMappingNode>();
            var entryKeys = entry.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!).ToList();
            entryKeys.ShouldBe(["name", "type", "maturity", "url", "description", "depends_on", "owner", "meta"]);
            entryKeys.ShouldNotContain("injected");

            var parsedName = ((YamlScalarNode)entry.Children[new YamlScalarNode("name")]).Value;
            parsedName.ShouldBe($"autheris_gateway_{table.Identifier.Schema}_{table.Identifier.TableName}".ToLowerInvariant());

            var parsedDesc = ((YamlScalarNode)entry.Children[new YamlScalarNode("description")]).Value;
            parsedDesc.ShouldBe($"Managed table '{table.Identifier}' governed by Enterprise GraphQL Gateway.");

            var dependsOn = entry.Children[new YamlScalarNode("depends_on")].ShouldBeOfType<YamlSequenceNode>();
            dependsOn.Children.Count.ShouldBe(1);
            var depVal = ((YamlScalarNode)dependsOn.Children[0]).Value;
            depVal.ShouldNotBeNull();
            depVal.ShouldContain(table.Identifier.TableName);

            var owner = entry.Children[new YamlScalarNode("owner")].ShouldBeOfType<YamlMappingNode>();
            owner.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!).ShouldBe(["name", "email"]);

            var meta = entry.Children[new YamlScalarNode("meta")].ShouldBeOfType<YamlMappingNode>();
            meta.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!)
                .ShouldBe(["monthly_queries", "p99_latency_ms", "top_consumers", "governance_tier"]);
            meta.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!).ShouldNotContain("injected_meta");
            ((YamlScalarNode)meta.Children[new YamlScalarNode("governance_tier")]).Value.ShouldBe(hostileTier);

            var consumers = meta.Children[new YamlScalarNode("top_consumers")].ShouldBeOfType<YamlSequenceNode>();
            consumers.Children.Count.ShouldBe(2);
            ((YamlScalarNode)consumers.Children[0]).Value.ShouldBe(hostileConsumer);
            ((YamlScalarNode)consumers.Children[1]).Value.ShouldBe("consumer: #comment\n---");
        }
    }
}
