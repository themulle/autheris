using System.Data;
using System.Data.Common;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common.Fakes;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence;

/// <summary>The profile row, the level row and the item rows of a profile are read in one repeatable-read transaction, so a concurrent upsert is never seen half applied.</summary>
[Trait("Category", "Security")]
public sealed class AccessProfileStoreSnapshotReadTests
{
    [Fact]
    public async Task GetProfile_ReadsProfileLevelAndItemsInOneRepeatableReadTransaction()
    {
        var (store, begun, commands) = Harness(joined: false);

        var profile = await store.GetProfileAsync(new TenantId("tenant-a"), "p1", default);

        profile.ShouldNotBeNull();
        profile.MaskingMode.ShouldBe(MaskingPolicyMode.Default);
        profile.TargetTables.ShouldBe(["sales.*"]);
        begun.ShouldBe([IsolationLevel.RepeatableRead]);
        commands.Count.ShouldBeGreaterThanOrEqualTo(4);
        commands.ShouldAllBe(c => c.Tx != null);
        commands.Select(c => c.Tx).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task GetProfilesForSubjects_ReadsJoinLevelAndItemsInOneRepeatableReadTransaction()
    {
        var (store, begun, commands) = Harness(joined: true);

        var profiles = await store.GetProfilesForSubjectsAsync(new TenantId("tenant-a"), [], ["grp"], [], default);

        profiles.Count.ShouldBe(1);
        profiles[0].TargetTables.ShouldBe(["sales.*"]);
        begun.ShouldBe([IsolationLevel.RepeatableRead]);
        commands.Count.ShouldBe(3);
        commands.ShouldAllBe(c => c.Tx != null);
        commands.Select(c => c.Tx).Distinct().Count().ShouldBe(1);
    }

    private static readonly string[] ProfileColumns = ["profile_id", "tenant_id", "name", "row_scope_json", "justification", "created_by", "created_at", "valid_to"];

    private static (AccessProfileStore Store, List<IsolationLevel> Begun, List<(string Sql, DbTransaction? Tx)> Commands) Harness(bool joined)
    {
        var begun = new List<IsolationLevel>();
        var commands = new List<(string, DbTransaction?)>();
        var conn = new CallbackDbConnection((sql, _) =>
        {
            if (sql.Contains("FROM ACCESS_PROFILE_CLASSIFICATION_ACCESS", StringComparison.Ordinal))
            {
                return new StubDbDataReader(["profile_id", "code", "access_level"], [["p1", "PII", (short)1]]);
            }

            if (sql.Contains("FROM ACCESS_PROFILE_ITEMS", StringComparison.Ordinal))
            {
                return new StubDbDataReader(["profile_id", "item_no", "object_ref"], [["p1", 1, "sales.*"]]);
            }

            if (sql.Contains("JOIN ACCESS_PROFILE_ASSIGNMENTS", StringComparison.Ordinal))
            {
                string[] columns = [.. ProfileColumns, "subject", "subject_type", "assigned_at", "expires_at", "created_by", "approved_by", "approved_at", "justification", "kind", "renewal_count"];
                return new StubDbDataReader(columns,
                [
                    ["p1", "tenant-a", "n", null, null, null, "2026-01-01T00:00:00+00:00", null,
                     "grp", "Group", "2026-01-01T00:00:00+00:00", null, null, null, null, null, "Standard", 0]
                ]);
            }

            return sql.Contains("FROM ACCESS_PROFILE_ASSIGNMENTS", StringComparison.Ordinal)
                ? new StubDbDataReader(["profile_id"], [])
                : new StubDbDataReader(ProfileColumns, [["p1", "tenant-a", "n", null, null, null, "2026-01-01T00:00:00+00:00", null]]);
        })
        {
            OnBeginTransaction = begun.Add,
            OnCommandExecuting = (sql, tx) => commands.Add((sql, tx))
        };
        var store = new AccessProfileStore(AccessProfileDialect.PostgreSql, (_, _) => Task.FromResult(new AccessProfileStoreScope(conn, () => ValueTask.CompletedTask)));
        return (store, begun, commands);
    }
}
