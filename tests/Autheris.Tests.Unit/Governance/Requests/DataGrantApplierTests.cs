using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class DataGrantApplierTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    private static ConsentRequest CreateRequest(IReadOnlyList<string>? targetColumns) => new()
    {
        Id = Guid.NewGuid(),
        TableId = Guid.NewGuid(),
        TableIdentifier = OrdersTable,
        RequesterSid = new Sid("user:alice"),
        RequestedGranteeType = GranteeType.User,
        RequestedGranteeRef = "user:alice",
        BusinessJustification = "Column-scoped data access request.",
        TenantId = TenantA,
        RequestKind = ConsentRequestKind.Data,
        GrantKind = PermissionGrantKind.Data,
        TargetColumns = targetColumns
    };

    private static ApprovedPermission Approve(ConsentRequest req) =>
        new(req, new Sid("user:owner"), ApproverCapacity.DataOwner, SelfGrant: false, Now, Now, Now.AddDays(30));

    private static IGovernanceTransaction DummyTx(DbTransaction rawTx) =>
        NullFake.Create<IGovernanceTransaction>((m, _) =>
            m.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? rawTx : NullFake.UseDefault);

    [Fact]
    public async Task Target_columns_with_missing_or_empty_metadata_fails_closed()
    {
        var writerCalled = false;
        var writer = NullFake.Create<IGrantWriter>((m, _) =>
        {
            if (m.Name == nameof(IGrantWriter.InsertAndGrantAsync))
            {
                writerCalled = true;
            }

            return NullFake.UseDefault;
        });

        using var rawTx = new DummyDbTransaction();
        var tx = DummyTx(rawTx);

        var applierNoRepo = new DataGrantApplier(writer, metadataRepository: null);
        var noRepoEx = await Should.ThrowAsync<ConsentDecisionException>(() =>
            applierNoRepo.ApplyAsync(tx, Approve(CreateRequest(["email"]))));
        noRepoEx.ErrorCode.ShouldBe("COLUMN_METADATA_UNAVAILABLE");

        var nullMetaRepo = NullFake.Create<ITableMetadataRepository>((m, _) =>
            m.Name == nameof(ITableMetadataRepository.GetTableMetadataAsync)
                ? Task.FromResult<TableMetadata?>(null)
                : NullFake.UseDefault);
        var applierNullMeta = new DataGrantApplier(writer, nullMetaRepo);
        var nullMetaEx = await Should.ThrowAsync<ConsentDecisionException>(() =>
            applierNullMeta.ApplyAsync(tx, Approve(CreateRequest(["email"]))));
        nullMetaEx.ErrorCode.ShouldBe("COLUMN_METADATA_UNAVAILABLE");

        var emptyColsMetaRepo = NullFake.Create<ITableMetadataRepository>((m, _) =>
            m.Name == nameof(ITableMetadataRepository.GetTableMetadataAsync)
                ? Task.FromResult<TableMetadata?>(new TableMetadata
                {
                    Identifier = OrdersTable,
                    Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
                    Columns = []
                })
                : NullFake.UseDefault);
        var applierEmptyCols = new DataGrantApplier(writer, emptyColsMetaRepo);
        var emptyColsEx = await Should.ThrowAsync<ConsentDecisionException>(() =>
            applierEmptyCols.ApplyAsync(tx, Approve(CreateRequest(["email"]))));
        emptyColsEx.ErrorCode.ShouldBe("COLUMN_METADATA_UNAVAILABLE");

        var partialMetaRepo = NullFake.Create<ITableMetadataRepository>((m, _) =>
            m.Name == nameof(ITableMetadataRepository.GetTableMetadataAsync)
                ? Task.FromResult<TableMetadata?>(new TableMetadata
                {
                    Identifier = OrdersTable,
                    Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
                    Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "email", DataType = "VARCHAR" }]
                })
                : NullFake.UseDefault);
        var applierUnknownCol = new DataGrantApplier(writer, partialMetaRepo);
        var unknownColEx = await Should.ThrowAsync<ConsentDecisionException>(() =>
            applierUnknownCol.ApplyAsync(tx, Approve(CreateRequest(["missing_col"]))));
        unknownColEx.ErrorCode.ShouldBe("TARGET_NOT_FOUND");

        var blankColEx = await Should.ThrowAsync<ConsentDecisionException>(() =>
            applierUnknownCol.ApplyAsync(tx, Approve(CreateRequest(["   "]))));
        blankColEx.ErrorCode.ShouldBe("VALIDATION_ERROR");

        writerCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Target_columns_with_valid_metadata_denies_only_non_requested_columns()
    {
        Consent? persisted = null;
        var writer = NullFake.Create<IGrantWriter>((m, args) =>
        {
            if (m.Name == nameof(IGrantWriter.InsertAndGrantAsync))
            {
                persisted = (Consent)args![1]!;
                return Task.FromResult(new GrantRecord(
                    persisted.TenantId.Value, 7, Guid.NewGuid(), GrantRecordType.Issued, persisted.Id, persisted.ConsentRequestId,
                    persisted.GrantSource, null, "User", "user:alice", OrdersTable.ToString(), "{}",
                    persisted.ValidFrom, persisted.ValidTo, "user:alice", "{}", Now, Now, GrantRecordChain.GenesisHash));
            }

            return NullFake.UseDefault;
        });

        var tableId = Guid.NewGuid();
        var metaRepo = NullFake.Create<ITableMetadataRepository>((m, _) =>
            m.Name == nameof(ITableMetadataRepository.GetTableMetadataAsync)
                ? Task.FromResult<TableMetadata?>(new TableMetadata
                {
                    Identifier = OrdersTable,
                    Table = new Table { Id = tableId, SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
                    Columns =
                    [
                        new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "email", DataType = "VARCHAR" },
                        new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "amount", DataType = "DECIMAL" }
                    ]
                })
                : NullFake.UseDefault);

        using var rawTx = new DummyDbTransaction();
        var extrasWritten = false;
        var extrasWriter = NullFake.Create<IConsentExtrasWriter>((m, _) =>
        {
            if (m.Name == nameof(IConsentExtrasWriter.PersistDataGrantExtrasAsync))
            {
                extrasWritten = true;
                return Task.CompletedTask;
            }

            return NullFake.UseDefault;
        });

        var applier = new DataGrantApplier(writer, metaRepo, extrasWriter);
        await applier.ApplyAsync(DummyTx(rawTx), Approve(CreateRequest(["email"])));

        persisted.ShouldNotBeNull();
        persisted.TargetColumns.ShouldBe(["email"]);
        persisted.ColumnRules.ShouldHaveSingleItem();
        persisted.ColumnRules[0].ColumnName.ShouldBe("amount");
        persisted.ColumnRules[0].AccessLevel.ShouldBe(ColumnAccessLevel.Deny);
        extrasWritten.ShouldBeTrue();
    }

    private sealed class DummyDbTransaction : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
