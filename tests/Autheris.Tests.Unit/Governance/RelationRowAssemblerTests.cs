using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #151 (decision 5, SEC-REL151-08/09): engine-neutral assembly of relations from the single read statement.</summary>
public sealed class RelationRowAssemblerTests
{
    private static readonly TableIdentifier Parent = new("sales", "dbo", "orders");
    private static readonly TableIdentifier Child = new("sales", "dbo", "lines");

    private static RelationRowData Row(Guid id, int? ordinal, string? parent, string? child, string cardinality = "OneToMany") =>
        new(id.ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "items", cardinality, Parent, Child, ordinal, parent, child);

    [Fact]
    public void Assemble_GroupsRowsInOrdinalOrder_AndNeverAllowsCrossDataSource()
    {
        var id = Guid.NewGuid();

        var result = RelationRowAssembler.Assemble([Row(id, 1, "a", "x"), Row(id, 2, "b", "y"), Row(id, 3, "c", "z")]);

        var relation = result.ShouldHaveSingleItem();
        relation.Id.ShouldBe(id);
        relation.JoinKeysParent.ShouldBe(["a", "b", "c"]);
        relation.JoinKeysChild.ShouldBe(["x", "y", "z"]);
        relation.AllowCrossDataSource.ShouldBeFalse();
    }

    [Fact]
    public void Assemble_UnknownCardinality_FallsBackToOneToMany()
    {
        var result = RelationRowAssembler.Assemble([Row(Guid.NewGuid(), 1, "a", "x", "Bogus")]);

        result.ShouldHaveSingleItem().Cardinality.ShouldBe(RelationCardinality.OneToMany);
    }

    [Fact]
    public void Assemble_SkipsNoKeysGapAndRuleViolations_KeepsTheRest()
    {
        var noKeys = Guid.NewGuid();
        var gap = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        var good = Guid.NewGuid();
        var rows = new[]
        {
            Row(noKeys, null, null, null),
            Row(gap, 1, "a", "x"), Row(gap, 3, "b", "y"),
            Row(duplicate, 1, "a", "x"), Row(duplicate, 2, "A", "y"),
            Row(good, 1, "a", "x")
        };

        var result = RelationRowAssembler.Assemble(rows);

        result.ShouldHaveSingleItem().Id.ShouldBe(good);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GetRelations_ManyBrokenRelations_LogsBoundedLines()
    {
        // T-REL151-13 (SEC-REL151-09): one summary plus at most 10 detail lines per load, relation id only.
        var logger = new CapturingLogger();
        var rows = Enumerable.Range(0, 500).Select(_ => Row(Guid.NewGuid(), null, null, null)).ToList();
        var ids = rows.Select(r => r.Id).ToList();

        var result = RelationRowAssembler.Assemble(rows, logger);

        result.ShouldBeEmpty();
        logger.Messages.Count.ShouldBe(1 + RelationRowAssembler.MaxDetailLogLines);
        logger.Messages[0].ShouldContain("500");
        logger.Messages.Skip(1).ShouldAllBe(m => ids.Any(id => m.Contains(id, StringComparison.Ordinal)));
        logger.Messages.ShouldAllBe(m => !m.Contains("items", StringComparison.Ordinal), "relation names must not be logged");
    }

    [Fact]
    public void Assemble_NoSkippedRelations_LogsNothing()
    {
        var logger = new CapturingLogger();

        RelationRowAssembler.Assemble([Row(Guid.NewGuid(), 1, "a", "x")], logger);

        logger.Messages.ShouldBeEmpty();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
