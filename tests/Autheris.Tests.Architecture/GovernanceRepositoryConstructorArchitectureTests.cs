using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-ARC-B-12 (ARC-I-13): a governance repository constructor performs no database, file or thread-pool I/O, so that building the
/// dependency graph never touches the network or the disk. Schema creation and initialization belong to the migration runner and
/// <c>InitializeAsync</c>. The scan reads IL: every constructor plus its private/same-type callees up to depth 3.
/// Key Vault access (<c>IKeyVaultSecretProvider.GetSecretBytes</c>) is a documented residual of the constructors.
/// </summary>
public sealed class GovernanceRepositoryConstructorArchitectureTests
{
    private const int MaxCallDepth = 3;

    private static readonly string[] RepositoryTypes =
    [
        "Autheris.Infrastructure.Persistence.SqliteGovernanceRepository",
        "Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository",
        "Autheris.Infrastructure.Persistence.SqlServerGovernanceRepository"
    ];

    /// <summary>Types that still violate the rule. Each entry is removed together with its violation (see the allow-list test).</summary>
    private static readonly string[] ConstructorIoAllowList = [];

    private static readonly (string Type, string MethodPrefix)[] ForbiddenCalls =
    [
        ("System.Data.Common.DbConnection", "Open"),
        ("Microsoft.Data.Sqlite.SqliteConnection", "Open"),
        ("Npgsql.NpgsqlDataSource", "OpenConnection"),
        ("Microsoft.Data.SqlClient.SqlConnection", "Open"),
        ("System.Data.Common.DbCommand", "Execute"),
        ("Microsoft.Data.Sqlite.SqliteCommand", "Execute"),
        ("System.Threading.Tasks.Task", "Run"),
        ("System.IO.Directory", "CreateDirectory"),
        ("System.IO.File", "")
    ];

    private static ModuleDefinition InfrastructureModule() =>
        CecilModuleCache.For(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly);

    private static ModuleDefinition TestModule() => CecilModuleCache.For(typeof(GovernanceRepositoryConstructorArchitectureTests).Assembly);

    private static bool IsForbidden(MethodReference method) =>
        ForbiddenCalls.Any(f => method.DeclaringType.FullName == f.Type && method.Name.StartsWith(f.MethodPrefix, StringComparison.Ordinal));

    private static List<string> Violations(string typeName) => Violations(InfrastructureModule(), typeName);

    private static List<string> Violations(ModuleDefinition module, string typeName)
    {
        var type = module.GetType(typeName) ?? throw new InvalidOperationException($"{typeName} not found");
        var violations = new List<string>();
        var visited = new HashSet<MethodDefinition>();

        void Scan(MethodDefinition method, int depth, string path)
        {
            if (!method.HasBody || !visited.Add(method))
            {
                return;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference callee ||
                    instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj))
                {
                    continue;
                }

                if (IsForbidden(callee))
                {
                    violations.Add($"{path} -> {callee.DeclaringType.Name}::{callee.Name}");
                    continue;
                }

                var definition = callee.Resolve();
                if (definition != null && depth < MaxCallDepth && IsSameTypeCallee(definition, type))
                {
                    Scan(definition, depth + 1, path + " -> " + definition.Name);
                }
            }
        }

        foreach (var ctor in type.Methods.Where(m => m.IsConstructor && !m.IsStatic))
        {
            Scan(ctor, 1, type.Name + "::" + ctor.Name);
        }

        return violations.Distinct().ToList();
    }

    private static bool IsSameTypeCallee(MethodDefinition method, TypeDefinition repository) =>
        method.DeclaringType == repository || method.DeclaringType.DeclaringType == repository;

    [Theory]
    [InlineData("Autheris.Infrastructure.Persistence.SqliteGovernanceRepository")]
    [InlineData("Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository")]
    [InlineData("Autheris.Infrastructure.Persistence.SqlServerGovernanceRepository")]
    public void GovernanceRepositoryConstructor_PerformsNoIo(string typeName)
    {
        if (ConstructorIoAllowList.Contains(typeName))
        {
            return;
        }

        var violations = Violations(typeName);

        violations.ShouldBeEmpty("Constructor I/O is forbidden (WP-ARC-B-12): " + string.Join("; ", violations));
    }

    // Positive control: without it a rename of a forbidden API would make the scan pass vacuously.
    [Fact]
    public void ConstructorIoAllowList_EntriesStillViolate()
    {
        foreach (var typeName in ConstructorIoAllowList)
        {
            Violations(typeName).ShouldNotBeEmpty($"{typeName} no longer violates the rule: remove it from the allow-list.");
        }
    }

    // Positive controls for the scanner itself: the allow-list is empty, so without them a broken scan would pass vacuously.
    [Fact]
    public void Scanner_DetectsAnOpenInTheConstructor()
    {
        var violations = Violations(TestModule(), typeof(DirectOpenFixture).FullName!.Replace('+', '/'));

        // The C# compiler binds Open() to the DbConnection base declaration.
        violations.ShouldContain(v => v.EndsWith("DbConnection::Open", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_DetectsFileSystemAndThreadPoolWorkThroughAPrivateCallee()
    {
        var violations = Violations(TestModule(), typeof(IndirectIoFixture).FullName!.Replace('+', '/'));

        violations.ShouldContain(v => v.EndsWith("Directory::CreateDirectory", StringComparison.Ordinal));
        violations.ShouldContain(v => v.EndsWith("Task::Run", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_AcceptsAConstructorWithoutIo()
    {
        Violations(TestModule(), typeof(CleanFixture).FullName!.Replace('+', '/')).ShouldBeEmpty();
    }

    internal sealed class DirectOpenFixture
    {
        public DirectOpenFixture()
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            connection.Open();
        }
    }

    internal sealed class IndirectIoFixture
    {
        public IndirectIoFixture() => Prepare();

        private void Prepare()
        {
            Directory.CreateDirectory(Path.GetTempPath());
            _ = Task.Run(() => { });
        }
    }

    internal sealed class CleanFixture
    {
        public CleanFixture() => Name = "clean";

        public string Name { get; }
    }

    // WP-ARC-B-13: the shared connection is gone; every operation opens its own connection through the provider.
    [Fact]
    public void SqliteGovernanceRepository_HasNoSqliteConnectionField()
    {
        var module = InfrastructureModule();
        var repository = module.GetType("Autheris.Infrastructure.Persistence.SqliteGovernanceRepository")!;

        var connectionFields = repository.Fields
            .Where(f => f.FieldType.FullName == "Microsoft.Data.Sqlite.SqliteConnection")
            .Select(f => f.Name)
            .ToList();
        var connectionProperties = repository.Properties
            .Where(p => p.PropertyType.FullName == "Microsoft.Data.Sqlite.SqliteConnection")
            .Select(p => p.Name)
            .ToList();

        connectionFields.ShouldBeEmpty("a shared SqliteConnection field would bypass the write gate: " + string.Join(", ", connectionFields));
        connectionProperties.ShouldBeEmpty("a shared SqliteConnection property would bypass the write gate: " + string.Join(", ", connectionProperties));
    }

    [Fact]
    public void ConstructorIoAllowList_IsEmpty()
    {
        ConstructorIoAllowList.ShouldBeEmpty();
    }

    [Fact]
    public void ConstructorIoAllowList_ContainsOnlyKnownRepositories()
    {
        ConstructorIoAllowList.ShouldBeSubsetOf(RepositoryTypes);
    }
}
