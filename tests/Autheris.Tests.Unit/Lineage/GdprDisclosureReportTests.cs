namespace Autheris.Tests.Unit.Lineage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>MISC-11: the Art. 15 report lists the readers of the subject's table, never filters by the subject as actor, and never claims completeness it lacks.</summary>
public sealed class GdprDisclosureReportTests
{
    private static readonly TableIdentifier Salary = new("finance", "dbo", "salary");
    private static readonly Sid Employee = new("S-1-5-21-employee-42");
    private static readonly Sid HrAnalyst = new("S-1-5-21-hr-analyst");

    private static AuditLogEntry Read(Sid actor, string decision = "ALLOW") => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        ActorSid = actor,
        TargetTable = Salary.ToString(),
        Decision = decision,
        EventType = "QUERY",
        TargetColumn = "salary"
    };

    private static (LineageImpactAnalyzerService Sut, IAuditLogRepository Audit) Create(IReadOnlyList<AuditLogEntry> entries, int? cap = null)
    {
        var audit = Substitute.For<IAuditLogRepository>();
        audit.QueryAuditLogsAsync(Arg.Any<string?>(), Arg.Any<Sid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<AuditLogEntry>>(entries.Take(call.ArgAt<int>(3)).ToList()));
        var sut = new LineageImpactAnalyzerService(
            Substitute.For<IConsentRepository>(),
            Substitute.For<IDataOwnershipRepository>(),
            Substitute.For<ILineageGraphStore>(),
            audit,
            Substitute.For<ITableMetadataRepository>(),
            NullLogger<LineageImpactAnalyzerService>.Instance,
            consumerAggregationRepo: null,
            gdprReportOptions: cap == null ? null : Options.Create(new GdprReportOptions { MaxAuditEvents = cap.Value }));
        return (sut, audit);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SubjectReport_DoesNotFilterBySubjectAsActor()
    {
        var (sut, audit) = Create([Read(HrAnalyst)]);

        await sut.GetGdprDataDisclosureReportAsync(Salary, Employee, 365, null, CancellationToken.None);

        await audit.Received(1).QueryAuditLogsAsync(
            Arg.Any<string?>(), Arg.Is<Sid?>(s => s == null), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SubjectReport_ExcludesSubjectFromRecipients()
    {
        var (sut, _) = Create([Read(Employee), Read(HrAnalyst)]);

        var report = await sut.GetGdprDataDisclosureReportAsync(Salary, Employee, 365, null, CancellationToken.None);

        report.DisclosedRecipients.ShouldNotContain(r => r.RecipientSid == Employee.Value);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SubjectReport_ListsOtherReaders()
    {
        var (sut, _) = Create([Read(HrAnalyst), Read(HrAnalyst), Read(Employee)]);

        var report = await sut.GetGdprDataDisclosureReportAsync(Salary, Employee, 365, null, CancellationToken.None);

        var recipient = report.DisclosedRecipients.ShouldHaveSingleItem();
        recipient.RecipientSid.ShouldBe(HrAnalyst.Value);
        recipient.TotalQueries.ShouldBe(2);
        report.EvidenceLevel.ShouldBe("TableLevel");
        report.IsComplete.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AuditCapHit_MarksIncomplete()
    {
        var (sut, _) = Create([Read(HrAnalyst), Read(HrAnalyst), Read(HrAnalyst)], cap: 2);

        var report = await sut.GetGdprDataDisclosureReportAsync(Salary, Employee, 365, null, CancellationToken.None);

        report.IsComplete.ShouldBeFalse();
        report.IncompletenessReasons.ShouldNotBeNull();
        report.IncompletenessReasons.ShouldNotBeEmpty();
        report.TotalAccessEvents.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UnresolvedSubjectScope_Throws422Exception()
    {
        var (sut, audit) = Create([Read(HrAnalyst)]);

        await Should.ThrowAsync<GdprSubjectScopeUnresolvedException>(
            () => sut.GetGdprDataDisclosureReportAsync(null, Employee, 365, null, CancellationToken.None));

        await audit.DidNotReceiveWithAnyArgs().QueryAuditLogsAsync(default, default, default, default, default, default);
    }
}
