using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Application.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// Issue #1099: relation field names are suffixed on the base name (never chained), derived from the child table when the
/// relation carries the default (parent) name, bounded in length and independent of the input order and relation ids.
/// </summary>
public sealed partial class RelationFieldNamingTests
{
    private const string Domain = "erp";
    private const string Schema = "core";

    [GeneratedRegex("^[_A-Za-z][_0-9A-Za-z]*$")]
    private static partial Regex GraphQlNameRegex();

    [Fact]
    public void SharedExplicitName_SuffixIsAppendedToTheBaseName_NeverChained()
    {
        var hub = Table("hub", "id");
        var children = Enumerable.Range(1, 12).Select(i => Table($"child_{i:D2}", "id", "hub_id")).ToArray();
        var relations = children.Select(c => Relation(hub, c, "link", ["id"], ["hub_id"])).ToArray();

        var model = Build([hub, .. children], relations);

        var names = Forward(model, hub).Select(r => r.FieldName).ToArray();
        names.ShouldBe(
        [
            "link", "link_rel", "link_rel_2", "link_rel_3", "link_rel_4", "link_rel_5", "link_rel_6",
            "link_rel_7", "link_rel_8", "link_rel_9", "link_rel_10", "link_rel_11"
        ]);
        Forward(model, hub).Select(r => r.TargetTableIdentifier.TableName).ShouldBe(children.Select(c => c.Identifier.TableName));
    }

    [Fact]
    public void UniqueExplicitName_KeepsItsFieldName()
    {
        var customers = Table("customers", "id");
        var orders = Table("orders", "id", "customer_id");

        var model = Build([customers, orders], [Relation(customers, orders, "customer_orders", ["id"], ["customer_id"])]);

        Forward(model, customers).Single().FieldName.ShouldBe("customer_orders");
        Reverse(model, orders).Single().FieldName.ShouldBe("customers_by_customer_orders");
    }

    [Fact]
    public void DefaultNamedRelations_DeriveTheParentSideFieldFromTheChild()
    {
        var client = Table("client", "id");
        var air1 = Table("air1", "id", "client_id");
        var dm1 = Table("dm1", "id", "client_id");
        var crane = Table("crane_state", "id", "client_id");

        var model = Build(
            [client, air1, dm1, crane],
            [
                Relation(client, dm1, "client", ["id"], ["client_id"]),
                Relation(client, air1, "client", ["id"], ["client_id"]),
                Relation(client, crane, "client", ["id"], ["client_id"])
            ]);

        Forward(model, client).Select(r => (r.FieldName, r.TargetTableIdentifier.TableName)).ShouldBe(
        [
            ("air1", "air1"),
            ("crane_state", "crane_state"),
            ("dm1", "dm1")
        ]);
        Forward(model, client).ShouldAllBe(r => r.RelationName == "client");

        // The child side keeps the existing {parent}_by_{relation} name.
        Reverse(model, air1).Single().FieldName.ShouldBe("client_by_client");
    }

    [Fact]
    public void DefaultNamedRelation_OnAStagingParent_IsRecognised()
    {
        // dbt strips "stg_" from the parent model name when it builds the default relation name.
        var client = Table("stg_client", "id");
        var air1 = Table("air1", "id", "client_id");

        var model = Build([client, air1], [Relation(client, air1, "client", ["id"], ["client_id"])]);

        Forward(model, client).Single().FieldName.ShouldBe("air1");
    }

    [Fact]
    public void DefaultNamedRelations_ToTheSameChild_AreDisambiguatedByTheChildKeyColumns()
    {
        var client = Table("stg_client", "id");
        var air1 = Table("air1", "id", "client_id", "billing_client_id");

        var model = Build(
            [client, air1],
            [
                Relation(client, air1, "client", ["id"], ["client_id"]),
                Relation(client, air1, "stg_client", ["id"], ["billing_client_id"])
            ]);

        Forward(model, client).Select(r => r.FieldName).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["air1_by_billing_client_id", "air1_by_client_id"]);
    }

    [Fact]
    public void DerivedName_CollidingWithAColumn_GetsTheRelSuffix()
    {
        var client = Table("client", "id", "air1");
        var air1 = Table("air1", "id", "client_id");

        var model = Build([client, air1], [Relation(client, air1, "client", ["id"], ["client_id"])]);

        Forward(model, client).Single().FieldName.ShouldBe("air1_rel");
    }

    [Fact]
    public void FieldNames_AreIndependentOfInputOrderAndRelationIds()
    {
        var hub = Table("hub", "id");
        var children = Enumerable.Range(1, 30).Select(i => Table($"c{i:D2}", "id", "hub_id", "alt_hub_id")).ToArray();
        IReadOnlyList<TableRelation> MakeRelations() =>
        [
            .. children.Select(c => Relation(hub, c, "link", ["id"], ["hub_id"])),
            .. children.Take(10).Select(c => Relation(hub, c, "link", ["id"], ["alt_hub_id"])),
            .. children.Select(c => Relation(hub, c, "hub", ["id"], ["alt_hub_id"]))
        ];

        var expected = Mapping(Build([hub, .. children], MakeRelations()), hub);
        var random = new Random(1099);
        for (var round = 0; round < 10; round++)
        {
            var shuffledRelations = MakeRelations().OrderBy(_ => random.Next()).ToArray();
            TableMetadata[] shuffledTables = [.. new[] { hub }.Concat(children).OrderBy(_ => random.Next())];

            Mapping(Build(shuffledTables, shuffledRelations), hub).ShouldBe(expected);
        }
    }

    [Fact]
    public void LongNames_AreBoundedValidAndUnique()
    {
        var hub = Table("hub", "id");
        var longName = new string('x', 400);
        var children = Enumerable.Range(1, 5).Select(i => Table($"child_{i}", "id", "hub_id")).ToArray();
        var relations = children.Select(c => Relation(hub, c, longName, ["id"], ["hub_id"])).ToArray();

        var first = Forward(Build([hub, .. children], relations), hub).Select(r => r.FieldName).ToArray();
        var second = Forward(Build([hub, .. children], relations.Reverse().ToArray()), hub).Select(r => r.FieldName).ToArray();

        first.Length.ShouldBe(5);
        first.ShouldAllBe(n => n.Length <= CatalogSchemaModel.MaxRelationFieldNameLength);
        first.ShouldAllBe(n => GraphQlNameRegex().IsMatch(n) && !n.StartsWith("__", StringComparison.Ordinal));
        first.Distinct(StringComparer.Ordinal).Count().ShouldBe(5);
        second.ShouldBe(first);
    }

    [Fact]
    public void ManyRelationsWithTheSameName_StayShortAndUnique()
    {
        var hub = Table("hub", "id");
        var children = Enumerable.Range(1, 400).Select(i => Table($"child_{i:D3}", "id", "hub_id")).ToArray();
        var relations = children.Select(c => Relation(hub, c, "link", ["id"], ["hub_id"])).ToArray();

        var names = Forward(Build([hub, .. children], relations), hub).Select(r => r.FieldName).ToArray();

        names.Length.ShouldBe(400);
        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(400);
        names.Max(n => n.Length).ShouldBeLessThanOrEqualTo("link_rel_399".Length);
        names[^1].ShouldBe("link_rel_399");
    }

    private static Dictionary<string, string> Mapping(CatalogSchemaModel model, TableMetadata parent) =>
        Forward(model, parent).ToDictionary(
            r => r.FieldName,
            r => $"{r.TargetTableIdentifier.TableName}:{string.Join(',', r.ChildColumns)}:{r.RelationName}",
            StringComparer.Ordinal);

    private static IReadOnlyList<CatalogRelationField> Forward(CatalogSchemaModel model, TableMetadata parent) =>
        model.TablesByIdentifier[parent.Identifier].Relations.Where(r => r.Cardinality == RelationCardinality.OneToMany).ToArray();

    private static IReadOnlyList<CatalogRelationField> Reverse(CatalogSchemaModel model, TableMetadata child) =>
        model.TablesByIdentifier[child.Identifier].Relations.Where(r => r.Cardinality == RelationCardinality.ManyToOne).ToArray();

    private static CatalogSchemaModel Build(IReadOnlyList<TableMetadata> tables, IReadOnlyList<TableRelation> relations) =>
        CatalogSchemaModel.BuildFromSnapshot(CatalogSnapshot.Create(new CatalogVersion(1, 1), tables, relations));

    private static TableRelation Relation(TableMetadata parent, TableMetadata child, string name, string[] parentKeys, string[] childKeys) => new()
    {
        Id = Guid.NewGuid(),
        ParentTableId = parent.Table.Id,
        ParentTableIdentifier = parent.Identifier,
        ChildTableId = child.Table.Id,
        ChildTableIdentifier = child.Identifier,
        RelationName = name,
        JoinKeysParent = parentKeys,
        JoinKeysChild = childKeys,
        Cardinality = RelationCardinality.OneToMany
    };

    private static TableMetadata Table(string name, params string[] columns) => new()
    {
        Identifier = new TableIdentifier(Domain, Schema, name),
        PrimaryKeyColumns = [columns[0]],
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = Domain,
            SchemaName = Schema,
            TableName = name,
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = [.. columns.Select(c => new TableColumn { ColumnName = c, DataType = "int" })]
    };
}
