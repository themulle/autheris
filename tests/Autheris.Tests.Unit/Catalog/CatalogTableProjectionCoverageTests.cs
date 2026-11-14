namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autheris.Application.Catalog;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>#674 (SEC-OI-674-3): a new persisted property or column without a projection entry fails the build.</summary>
[Trait("Category", "Security")]
public sealed class CatalogTableProjectionCoverageTests
{
    private static readonly Type[] ModelTypes = [typeof(Table), typeof(TableColumn), typeof(MaskingRule), typeof(TableMetadata)];

    [Fact]
    public void EveryPersistedPropertyIsProjected()
    {
        var covered = CatalogTablePersistedProjection.CoveredProperties;
        var excluded = CatalogTablePersistedProjection.NotPersistedByUpsert;
        var actual = ModelTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => $"{t.Name}.{p.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = actual.Where(p => !covered.Contains(p) && !excluded.ContainsKey(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        unclassified.ShouldBeEmpty("New model properties must be projected (CoveredProperties) or listed with a reason (NotPersistedByUpsert).");

        covered.Where(p => !actual.Contains(p)).ShouldBeEmpty("CoveredProperties lists a property that no longer exists.");
        excluded.Keys.Where(p => !actual.Contains(p)).ShouldBeEmpty("NotPersistedByUpsert lists a property that no longer exists.");
        covered.Intersect(excluded.Keys).ShouldBeEmpty("A property is either projected or excluded, not both.");
        excluded.Values.ShouldAllBe(reason => !string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void EveryWrittenSqlColumnIsMapped()
    {
        var catalogSql = typeof(GovernanceRepository).GetNestedType("CatalogSql", BindingFlags.NonPublic).ShouldNotBeNull();
        string Sql(string name) => (string)catalogSql.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;

        var written = new List<string>();
        foreach (var (constant, table) in new[]
                 {
                     ("UpsertTableSqlPg", "TABLES"), ("UpsertTableSqlSs", "TABLES"),
                     ("InsertColumnSqlPg", "TABLE_COLUMNS"), ("InsertColumnSqlSs", "TABLE_COLUMNS")
                 })
        {
            var match = Regex.Match(Sql(constant), @"INSERT\s+(?:INTO\s+)?(?:\w+\s+)?\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
            {
                match = Regex.Match(Sql(constant), @"INSERT\s+\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }

            match.Success.ShouldBeTrue($"{constant} has no INSERT column list");
            written.AddRange(match.Groups[1].Value.Split(',')
                .Select(c => c.Trim().Trim('"', '[', ']'))
                .Where(c => c.Length > 0)
                .Select(c => $"{table}.{c}"));
        }

        written.Distinct().Count().ShouldBeGreaterThan(20);
        var mapped = CatalogTablePersistedProjection.PersistedSqlColumns;
        written.Where(c => !mapped.ContainsKey(c)).Distinct().ShouldBeEmpty("A column written by the upsert is missing from PersistedSqlColumns.");
    }
}
