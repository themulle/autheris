using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture.DialectSql;

/// <summary>
/// ARCH-MNT-22 (SC-01, SC-08): the SQL text, parameter names, lock hints, audit literals and exception messages of the ITSM outbox, the
/// webhook outbox and the consent grants of the three governance repositories are pinned by static goldens generated from the pre-refactor
/// code. Updating a golden requires <c>AUTHERIS_APPROVE_DIALECT_SQL=1</c> and explicit reviewer sign-off in the PR description.
/// </summary>
public sealed class DialectSqlGoldenArchitectureTests
{
    private const string ApproveVariable = "AUTHERIS_APPROVE_DIALECT_SQL";

    public static TheoryData<string, string> DialectAreaPairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var dialect in DialectSqlFragmentExtractor.Dialects)
        {
            foreach (var area in DialectSqlFragmentExtractor.Areas)
            {
                data.Add(dialect, area.Name);
            }
        }

        return data;
    }

    public static TheoryData<string> DialectNames()
    {
        var data = new TheoryData<string>();
        foreach (var dialect in DialectSqlFragmentExtractor.Dialects)
        {
            data.Add(dialect);
        }

        return data;
    }

    internal static string GoldenPath(string dialect, string area) =>
        Path.Combine(RepositoryScan.RepoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "DialectSql", $"{dialect}.{area}.txt");

    private static DialectSqlArea Area(string name) => DialectSqlFragmentExtractor.Areas.Single(a => a.Name == name);

    private static (IReadOnlyList<string> Set, IReadOnlyDictionary<string, IReadOnlyList<string>> Methods) LoadGolden(string dialect, string area)
    {
        var path = GoldenPath(dialect, area);
        File.Exists(path).ShouldBeTrue($"missing golden {path}: generating it requires {ApproveVariable}=1 and reviewer sign-off (fail-closed)");
        return DialectSqlFragmentExtractor.ParseGolden(File.ReadAllText(path));
    }

    [Theory]
    [MemberData(nameof(DialectAreaPairs))]
    public void GoldenMatchesCurrentCode(string dialect, string area)
    {
        var snapshot = DialectSqlFragmentExtractor.Extract(dialect, Area(area));
        if (Environment.GetEnvironmentVariable(ApproveVariable) == "1")
        {
            var path = GoldenPath(dialect, area);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DialectSqlFragmentExtractor.Render(snapshot), new System.Text.UTF8Encoding(false));
        }

        var golden = LoadGolden(dialect, area);
        AssertSameSet(snapshot, golden.Set, $"{dialect}.{area}");
    }

    private static void AssertSameSet(DialectSqlSnapshot snapshot, IReadOnlyList<string> golden, string label)
    {
        var removed = golden.Except(snapshot.Set, StringComparer.Ordinal).Select(DialectSqlFragmentExtractor.Escape).ToList();
        var added = snapshot.Set.Except(golden, StringComparer.Ordinal).Select(DialectSqlFragmentExtractor.Escape).ToList();
        (removed.Count + added.Count).ShouldBe(
            0,
            $"{label}: SQL, parameter names, lock hints or literals changed; behaviour-preserving refactors must keep the golden byte-identical. Missing: [{string.Join(" | ", removed)}] Unexpected: [{string.Join(" | ", added)}]");
    }

    [Theory]
    [MemberData(nameof(DialectAreaPairs))]
    public void EveryManifestMethod_HasFragments(string dialect, string area)
    {
        var golden = LoadGolden(dialect, area);
        foreach (var root in Area(area).Roots.Where(r => !r.EndsWith('?')))
        {
            golden.Methods.ContainsKey(root).ShouldBeTrue($"{dialect}.{area}: golden lacks method '{root}'");
            golden.Methods[root].ShouldNotBeEmpty($"{dialect}.{area}: method '{root}' has no fragments, the extractor would not notice a change");
        }
    }

    [Theory]
    [MemberData(nameof(DialectNames))]
    public void TenantScopedStatements_KeepTheirTenantPredicate(string dialect)
    {
        // Documented exemptions (ARCH-MNT-22-SC-04): (a) revoke select/update by consent id under the row lock (the tenant is read from the
        // locked row), (b) the ITSM outbox, a system queue without tenant filter. The webhook claim/fence statements are id-keyed system queue
        // statements as before. Everything else asserted below must keep its tenant predicate.
        var consent = LoadGolden(dialect, "ConsentGrants").Set;
        consent.ShouldContain(f => f.Contains("c.tenant_id = @tenantId", StringComparison.Ordinal));
        consent.Count(f => f.Contains("tenant_id = @t AND is_revoked = 0", StringComparison.Ordinal)).ShouldBe(2);

        var webhook = LoadGolden(dialect, "WebhookOutbox").Set;
        webhook.ShouldContain(f => f.Contains("owner_tenant_id = @tenant", StringComparison.Ordinal));
        webhook.ShouldContain(f => f.Contains("owner_tenant_id IS NULL", StringComparison.Ordinal));
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(DialectNames))]
    public void DialectGolden_ConsentGrants_Sql_Is_Unchanged(string dialect)
    {
        // ARCH-MNT-22-T-01: the committed golden equals what the compiled code contains (static L1 proof, all three dialects).
        var snapshot = DialectSqlFragmentExtractor.Extract(dialect, Area("ConsentGrants"));
        AssertSameSet(snapshot, LoadGolden(dialect, "ConsentGrants").Set, $"{dialect}.ConsentGrants");
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(DialectNames))]
    public void Itsm_Outbox_SystemQueue_Is_Only_TenantFilter_Exemption(string dialect)
    {
        // ARCH-MNT-22-T-06 / SC-04: the ITSM outbox statements are the only statements of the three areas on a tenant-bearing table that are
        // allowed to carry no tenant predicate besides the revoke-by-id pair. Explicit allow-list; a new tenant-less statement breaks it.
        var itsm = LoadGolden(dialect, "ItsmOutbox").Set.Where(f => f.Contains("ITSM_OUTBOX", StringComparison.Ordinal)).ToList();
        itsm.ShouldNotBeEmpty();
        itsm.ShouldAllBe(f => !f.Contains("tenant_id =", StringComparison.Ordinal));

        var consentStatements = LoadGolden(dialect, "ConsentGrants").Set
            .Where(f => f.Contains("CONSENTS", StringComparison.Ordinal) && (f.Contains("SELECT", StringComparison.Ordinal) || f.Contains("UPDATE", StringComparison.Ordinal) || f.Contains("DELETE", StringComparison.Ordinal)))
            .ToList();
        var withoutTenant = consentStatements.Where(f => !f.Contains("tenant_id = @", StringComparison.Ordinal)).ToList();

        // Allow-list (a): the revoke select under the row lock and the revoke update, both id-keyed. The active-role query takes its
        // tenant predicate as a separate optional fragment (" AND c.tenant_id = @tenantId", asserted by TenantScopedStatements_...).
        withoutTenant.Count(f => f.Contains("c.id = @id", StringComparison.Ordinal)).ShouldBe(1);
        withoutTenant.Count(f => f.Contains("UPDATE CONSENTS SET is_revoked = 1", StringComparison.Ordinal)).ShouldBe(1);
        withoutTenant.Count(f => f.Contains("c.is_revoked = 0", StringComparison.Ordinal)).ShouldBe(1);
        withoutTenant.Count.ShouldBe(3, "a new CONSENTS statement without a tenant predicate needs a documented exemption (ARCH-MNT-22-SC-04)");
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(DialectNames))]
    public void Webhook_And_Itsm_Outbox_Claim_Is_Exclusive_And_Tenant_Scoped_Unchanged(string dialect)
    {
        // ARCH-MNT-22-T-08: the claim keeps its exclusive-lock hint per dialect; list dead letters keeps its owner scope; ITSM reads stay plain.
        var webhook = LoadGolden(dialect, "WebhookOutbox").Set;
        var hint = dialect switch
        {
            "PostgreSql" => "FOR UPDATE SKIP LOCKED",
            "SqlServer" => "UPDLOCK, READPAST, ROWLOCK",
            _ => "WHERE id IN ("
        };
        webhook.ShouldContain(f => f.Contains(hint, StringComparison.Ordinal));
        webhook.ShouldContain(f => f.Contains("owner_tenant_id = @tenant", StringComparison.Ordinal));
        webhook.ShouldContain(f => f.Contains("owner_tenant_id IS NULL", StringComparison.Ordinal));
        webhook.ShouldContain(f => f.Contains("state = 1 AND lease_owner = @owner", StringComparison.Ordinal));

        var itsm = LoadGolden(dialect, "ItsmOutbox").Set;
        itsm.ShouldContain(f => f.Contains("WHERE status IN (0, 1)", StringComparison.Ordinal));
        itsm.ShouldNotContain(f => f.Contains("SKIP LOCKED", StringComparison.Ordinal) || f.Contains("UPDLOCK", StringComparison.Ordinal) || f.Contains("READPAST", StringComparison.Ordinal));
    }

    [Trait("Category", "Security")]
    [Fact]
    public void CoreClasses_Do_Not_Contain_GovernanceRepository_In_Name()
    {
        // ARCH-MNT-22-T-07 / SC-05: ConsentQueryTenantArchitectureTests skips every type whose name contains "GovernanceRepository"; the
        // extracted cores and SQL holders must stay visible to it.
        var module = CecilModuleCache.For(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly);
        var candidates = module.GetTypes()
            .Where(t => t.Name.EndsWith("SqlCore", StringComparison.Ordinal) || t.Name.EndsWith("Sql", StringComparison.Ordinal))
            .Where(t => DialectSqlFragmentExtractor.Areas.Any(a => t.Name.StartsWith(a.TypeName, StringComparison.Ordinal)))
            .ToList();
        foreach (var type in candidates)
        {
            type.Name.ShouldNotContain("GovernanceRepository", Case.Sensitive);
            if (type.Name.EndsWith("SqlCore", StringComparison.Ordinal))
            {
                // The cores are top-level types: a nested core would inherit the repository name and be skipped by the tenant guard.
                type.IsNested.ShouldBeFalse($"{type.FullName} must be top-level");
                type.FullName.ShouldNotContain("GovernanceRepository", Case.Sensitive, $"{type.FullName} would be skipped by ConsentQueryTenantArchitectureTests");
            }
        }
    }

    private static MethodDefinition Method(TypeDefinition repo, string name, int? parameterCount = null) =>
        repo.Methods.Single(m => m.Name == name && (parameterCount is null || m.Parameters.Count == parameterCount));

    private static IEnumerable<Instruction> Instructions(string dialect, string name, int? parameterCount = null)
    {
        var repo = DialectSqlFragmentExtractor.RepositoryType(dialect);
        return DialectSqlFragmentExtractor.Bodies(repo, Method(repo, name, parameterCount)).SelectMany(m => m.Body.Instructions);
    }

    private static readonly string[] ConsentRevokeMethods = ["RevokeConsentInTransactionAsync", "RevokeSystemConsentAsync", "RevokeConsentAsync"];

    private static List<MethodReference> Calls(IEnumerable<Instruction> instructions) =>
        instructions.Where(i => i.Operand is MethodReference).Select(i => (MethodReference)i.Operand).ToList();

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(DialectNames))]
    public void Revoke_Is_Unified_Across_Dialects(string dialect)
    {
        // DEF-02/03/04 (replaces ARCH-MNT-22-T-03): one decision literal, one fail-closed tenant resolution, approver check on the transaction.
        foreach (var method in new[] { "RevokeConsentInTransactionAsync", "RevokeSystemConsentAsync" })
        {
            var instructions = Instructions(dialect, method).ToList();
            var literals = instructions.Where(i => i.OpCode.Code == Code.Ldstr).Select(i => (string)i.Operand).ToList();
            var calls = Calls(instructions);

            // DEF-03 / DEF-SC-07: "REVOKED" (the const RevokedDecision), never "REVOKE".
            literals.ShouldContain("REVOKED", $"{dialect}.{method}");
            literals.ShouldNotContain("REVOKE", $"{dialect}.{method}");

            // DEF-02 / DEF-SC-01: ResolveConsentTenant is the only tenant parse; no TryParse, no constructor, no default tenant.
            calls.Count(c => c.Name == "ResolveConsentTenant").ShouldBe(1, $"{dialect}.{method}");
            calls.ShouldNotContain(c => c.Name == "TryParse" && c.DeclaringType.Name == "TenantId", $"{dialect}.{method}");
            calls.ShouldNotContain(c => c.Name == ".ctor" && c.DeclaringType.Name == "TenantId", $"{dialect}.{method}");
            calls.ShouldNotContain(c => c.Name == "get_LegacySingleTenant", $"{dialect}.{method}");
        }

        // DEF-04 / DEF-SC-06: the approver check runs on the revoke connection and transaction; the connection-opening overload is not used.
        var approver = Calls(Instructions(dialect, "RevokeConsentInTransactionAsync")).Single(c => c.Name == "IsAuthorizedApproverForTableInternalAsync");
        approver.Parameters.Any(p => p.ParameterType.Name.EndsWith("Connection", StringComparison.Ordinal)).ShouldBeTrue();
        approver.Parameters.Any(p => p.ParameterType.Name.Contains("Transaction", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(DialectNames))]
    public void Revoke_Entry_Points_Share_One_Core_Per_Dialect(string dialect)
    {
        // DEF-T-09 / DEF-SC-05: RevokeConsentAsync is the single-id use of the shared transactional core (no second copy of the revoke logic).
        var revoke = Instructions(dialect, "RevokeConsentAsync").ToList();
        Calls(revoke).ShouldContain(c => c.Name == "RevokeConsentInTransactionAsync");
        revoke.Where(i => i.OpCode.Code == Code.Ldstr).Select(i => (string)i.Operand).ShouldNotContain("CONSENT_REVOKED");
        Calls(revoke).ShouldNotContain(c => c.Name == "IsAuthorizedApproverForTableInternalAsync");
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(DialectNames))]
    public void Revoke_Audit_Decision_Literal_Is_Identical_Across_Dialects(string dialect)
    {
        // DEF-T-02 / DEF-SC-07 (IL part; runtime part in ConsentRevokeUnificationTests): no consent revoke method contains "REVOKE" as a literal.
        foreach (var method in ConsentRevokeMethods)
        {
            Instructions(dialect, method).Where(i => i.OpCode.Code == Code.Ldstr).Select(i => (string)i.Operand).ShouldNotContain("REVOKE", $"{dialect}.{method}");
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public void IntegrityAuditTenant_Is_Referenced_Only_By_The_Refusal_Audit()
    {
        // DEF-SC-04: one named bucket, used by exactly one method (the single replacement point for the default-tenant lane).
        var module = CecilModuleCache.For(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly);
        var users = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody)
            .Where(m => m.Body.Instructions.Any(i => i.Operand is MethodReference { Name: "get_IntegrityAuditTenant" }))
            .Select(m => m.DeclaringType.Name + "." + m.Name).ToList();
        users.ShouldBe(["ConsentTenantRefusalAudit.Build"]);
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(DialectNames))]
    public void ReplaceConsents_Invalidates_Epoch_And_Metadata_Cache_All_Dialects(string dialect)
    {
        // DEF-01 (replaces ARCH-MNT-22-T-09): every dialect invalidates the epoch and evicts the cached table metadata. PG/MSSQL touch
        // _metadataCache directly; SQLite queues the eviction in the epoch bump (QueueMetadataInvalidation, applied on commit).
        var instructions = Instructions(dialect, "ReplaceConsentsAsync", parameterCount: 7).ToList();
        instructions.Any(i => i.Operand is MethodReference { Name: "InvalidateEpochAsync" }).ShouldBeTrue();
        if (dialect == "Sqlite")
        {
            Calls(instructions).ShouldContain(c => c.Name == "IncrementTableEpochInternalAsync");
            var repo = DialectSqlFragmentExtractor.RepositoryType(dialect);
            var bump = repo.Methods.Single(m => m.Name == "IncrementTableEpochInternalAsync" && m.Parameters.Count == 4);
            DialectSqlFragmentExtractor.Bodies(repo, bump).SelectMany(m => m.Body.Instructions)
                .Any(i => i.Operand is MethodReference { Name: "QueueMetadataInvalidation" }).ShouldBeTrue();
        }
        else
        {
            instructions.Any(i => i.Operand is FieldReference { Name: "_metadataCache" }).ShouldBeTrue();
        }
    }
}
