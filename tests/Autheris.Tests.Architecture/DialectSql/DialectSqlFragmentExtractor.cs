using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Autheris.Tests.Architecture.DialectSql;

/// <summary>One governed persistence area whose per-dialect SQL is pinned (ARCH-MNT-22).</summary>
/// <param name="Name">Area name used in golden file names and in the <c>{Area}Sql</c> / <c>{Area}SqlCore</c> type names.</param>
/// <param name="Roots">Root methods of the dialect repository, as <c>Name</c> or <c>Name(FirstParameterTypeName)</c> (overload disambiguation); a trailing <c>?</c> marks a root that only exists after a refactoring step (its strings join the set).</param>
internal sealed record DialectSqlArea(string Name, string[] Roots);

/// <summary>Ordered string fragments of one root method plus its <c>set_Transaction</c> call count.</summary>
internal sealed record DialectSqlMethod(string Root, IReadOnlyList<string> Fragments, int SetTransactionCalls);

/// <summary>Everything the golden of one dialect and area records.</summary>
internal sealed record DialectSqlSnapshot(string Dialect, string Area, IReadOnlyList<string> Set, IReadOnlyList<DialectSqlMethod> Methods);

/// <summary>
/// ARCH-MNT-22 L1 static golden: reads the built <c>Autheris.Infrastructure</c> assembly and collects, per dialect and area, every
/// <c>ldstr</c> operand and every <c>ldsfld</c> of a non-compiler-generated static string field (rendered <c>{field:Type.Field}</c>) of
/// the area's root methods in <c>{Dialect}GovernanceRepository</c> (transitively through compiler-generated state machines and closures
/// only), plus every method and constant of the nested <c>{Area}Sql</c> type and the <c>{Area}SqlCore</c> type when present.
/// </summary>
internal static class DialectSqlFragmentExtractor
{
    public static readonly string[] Dialects = ["PostgreSql", "SqlServer", "Sqlite"];

    public static readonly DialectSqlArea[] Areas =
    [
        new("ItsmOutbox",
        [
            "EnqueueAsync(ItsmOutboxMessage)", "GetPendingMessagesAsync", "MarkCompletedAsync", "MarkFailedAsync", "GetDeadLetterMessagesAsync"
        ]),
        new("WebhookOutbox",
        [
            "EnqueueAsync(WebhookOutboxMessage)", "ClaimBatchAsync", "CompleteAsync", "RescheduleAsync", "DeadLetterAsync", "FencedUpdateAsync",
            "DeferSubscriptionAsync", "ListDeadLettersAsync", "CountPendingAsync", "PurgeAsync(DateTimeOffset)", "BindFencedUpdate?"
        ]),
        new("ConsentGrants",
        [
            "GetActiveRoleConsentsAsync", "UpdateConsentDescriptionInTransactionAsync", "RevokeConsentInTransactionAsync"
        ])
    ];

    private const string InfrastructureNamespace = "Autheris.Infrastructure.Persistence";

    public static DialectSqlSnapshot Extract(string dialect, DialectSqlArea area)
    {
        var module = CecilModuleCache.For(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly);
        var repo = module.GetType($"{InfrastructureNamespace}.{dialect}GovernanceRepository")
            ?? throw new InvalidOperationException($"{dialect}GovernanceRepository not found");

        var methods = new List<DialectSqlMethod>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in area.Roots)
        {
            var rootMethod = ResolveRoot(repo, root);
            if (rootMethod is null)
            {
                continue; // optional root (trailing '?') that does not exist in this shape of the code
            }

            var fragments = new List<string>();
            var transactionCalls = 0;
            Collect(repo, rootMethod, fragments, ref transactionCalls);
            methods.Add(new DialectSqlMethod(root, fragments, transactionCalls));
            set.UnionWith(fragments);
        }

        foreach (var type in AuxiliaryTypes(module, repo, area))
        {
            foreach (var method in AllMethods(type))
            {
                var ignored = 0;
                if (method.HasBody)
                {
                    CollectBody(method, set, ref ignored);
                }
            }

            foreach (var constant in AllFields(type).Where(f => f.HasConstant && f.Constant is string))
            {
                set.Add((string)constant.Constant);
            }
        }

        return new DialectSqlSnapshot(dialect, area.Name, set.OrderBy(s => s, StringComparer.Ordinal).ToList(), methods);
    }

    public static string Render(DialectSqlSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.Append("## set\n");
        foreach (var fragment in snapshot.Set)
        {
            sb.Append(Escape(fragment)).Append('\n');
        }

        foreach (var method in snapshot.Methods)
        {
            sb.Append("## method ").Append(method.Root).Append('\n');
            foreach (var fragment in method.Fragments)
            {
                sb.Append(Escape(fragment)).Append('\n');
            }

            sb.Append("## set_Transaction ").Append(method.SetTransactionCalls).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Parses a golden file; the exact inverse of <see cref="Render"/> for the structure (fragments stay escaped-decoded).</summary>
    public static (IReadOnlyList<string> Set, IReadOnlyDictionary<string, IReadOnlyList<string>> Methods) ParseGolden(string text)
    {
        var set = new List<string>();
        var methods = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        List<string>? current = set;
        string? currentName = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("## set_Transaction ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line == "## set")
            {
                current = set;
            }
            else if (line.StartsWith("## method ", StringComparison.Ordinal))
            {
                currentName = line["## method ".Length..];
                var list = new List<string>();
                methods[currentName] = list;
                current = list;
            }
            else if (currentName is not null || ReferenceEquals(current, set))
            {
                current!.Add(Unescape(line));
            }
        }

        // The trailing newline of the file yields one empty artefact per section end; fragments never end a section with it.
        TrimTrailingEmpty(set);
        foreach (var m in methods.Values)
        {
            TrimTrailingEmpty((List<string>)m);
        }

        return (set, methods);
    }

    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    public static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i == value.Length - 1)
            {
                sb.Append(value[i]);
                continue;
            }

            i++;
            switch (value[i])
            {
                case '\\': sb.Append('\\'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    sb.Append((char)Convert.ToInt32(value.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default: sb.Append('\\').Append(value[i]); break;
            }
        }

        return sb.ToString();
    }

    private static void TrimTrailingEmpty(List<string> list)
    {
        if (list.Count > 0 && list[^1].Length == 0)
        {
            list.RemoveAt(list.Count - 1);
        }
    }

    private static MethodDefinition? ResolveRoot(TypeDefinition repo, string root)
    {
        var optional = root.EndsWith('?');
        if (optional)
        {
            root = root[..^1];
        }

        var open = root.IndexOf('(');
        var name = open < 0 ? root : root[..open];
        var firstParameter = open < 0 ? null : root[(open + 1)..^1];
        var matches = repo.Methods
            .Where(m => m.Name == name)
            .Where(m => firstParameter is null || (m.Parameters.Count > 0 && m.Parameters[0].ParameterType.Name == firstParameter))
            .ToList();
        if (optional && matches.Count == 0)
        {
            return null;
        }

        if (matches.Count != 1)
        {
            throw new InvalidOperationException($"{repo.Name}: root '{root}' resolved to {matches.Count} methods (expected exactly 1)");
        }

        return matches[0];
    }

    private static IEnumerable<TypeDefinition> AuxiliaryTypes(ModuleDefinition module, TypeDefinition repo, DialectSqlArea area)
    {
        var nested = repo.NestedTypes.FirstOrDefault(t => t.Name == $"{area.Name}Sql");
        if (nested is not null)
        {
            yield return nested;
        }

        var core = module.GetType($"{InfrastructureNamespace}.{area.Name}SqlCore");
        if (core is not null)
        {
            yield return core;
        }
    }

    private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type) =>
        type.Methods.Concat(type.NestedTypes.SelectMany(AllMethods));

    private static IEnumerable<FieldDefinition> AllFields(TypeDefinition type) =>
        type.Fields.Concat(type.NestedTypes.SelectMany(AllFields));

    private static bool IsCompilerGenerated(TypeReference type) => type.Name.Contains('<', StringComparison.Ordinal);

    public static TypeDefinition RepositoryType(string dialect) =>
        CecilModuleCache.For(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly)
            .GetType($"{InfrastructureNamespace}.{dialect}GovernanceRepository");

    /// <summary>The root method plus every compiler-generated state machine / closure method reachable from it (the bodies a reviewer would call "the method").</summary>
    public static IReadOnlyList<MethodDefinition> Bodies(TypeDefinition repo, MethodDefinition root)
    {
        var visited = new List<MethodDefinition>();
        var queue = new Queue<MethodDefinition>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            if (visited.Contains(method) || !method.HasBody)
            {
                continue;
            }

            visited.Add(method);
            foreach (var next in Followed(repo, method))
            {
                queue.Enqueue(next);
            }
        }

        return visited;
    }

    private static void Collect(TypeDefinition repo, MethodDefinition root, List<string> fragments, ref int transactionCalls)
    {
        var visited = new HashSet<MethodDefinition>();
        var queue = new Queue<MethodDefinition>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            if (!visited.Add(method) || !method.HasBody)
            {
                continue;
            }

            CollectBody(method, fragments, ref transactionCalls);
            foreach (var next in Followed(repo, method))
            {
                queue.Enqueue(next);
            }
        }
    }

    private static IEnumerable<MethodDefinition> Followed(TypeDefinition repo, MethodDefinition method)
    {
        foreach (var variable in method.Body.Variables)
        {
            foreach (var move in StateMachineMoveNext(variable.VariableType, repo))
            {
                yield return move;
            }
        }

        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference reference)
            {
                continue;
            }

            if (reference is GenericInstanceMethod generic)
            {
                foreach (var argument in generic.GenericArguments)
                {
                    foreach (var move in StateMachineMoveNext(argument, repo))
                    {
                        yield return move;
                    }
                }
            }

            if (instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn or Code.Call or Code.Callvirt)
            {
                var resolved = reference.Resolve();
                if (resolved is not null && IsNestedIn(resolved.DeclaringType, repo) && resolved.Name.Contains('<', StringComparison.Ordinal))
                {
                    yield return resolved;
                }
            }
        }
    }

    private static IEnumerable<MethodDefinition> StateMachineMoveNext(TypeReference type, TypeDefinition repo)
    {
        if (type is ByReferenceType byRef)
        {
            type = byRef.ElementType;
        }

        var definition = type.Resolve();
        if (definition is null || !IsCompilerGenerated(definition) || !IsNestedIn(definition, repo))
        {
            yield break;
        }

        if (definition.Interfaces.Any(i => i.InterfaceType.Name == "IAsyncStateMachine"))
        {
            foreach (var move in definition.Methods.Where(m => m.Name == "MoveNext"))
            {
                yield return move;
            }
        }
    }

    private static bool IsNestedIn(TypeDefinition type, TypeDefinition repo)
    {
        for (var t = type.DeclaringType; t is not null; t = t.DeclaringType)
        {
            if (t == repo)
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectBody(MethodDefinition method, ICollection<string> fragments, ref int transactionCalls)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldstr:
                    fragments.Add((string)instruction.Operand);
                    break;
                case Code.Ldsfld when instruction.Operand is FieldReference field
                                       && field.FieldType.FullName == "System.String"
                                       && !IsCompilerGenerated(field.DeclaringType):
                    fragments.Add($"{{field:{field.DeclaringType.Name}.{field.Name}}}");
                    break;
                case Code.Call or Code.Callvirt when instruction.Operand is MethodReference { Name: "set_Transaction" }:
                    transactionCalls++;
                    break;
            }
        }
    }
}
