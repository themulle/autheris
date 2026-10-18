using System.Reflection;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.ClientListHarness;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// SEC-128-10 (M-14): the residual position check is the only guard that a list never reaches a scalar position, so its completeness
/// is a test, not an assumption. For every concrete AST node, every constructor parameter typed <c>Expression</c> (or a list of
/// Expression) is filled with a list reference; the expander must reach it. A node added without a visit makes this test fail.
/// </summary>
[Trait("Category", "Security")]
public class ClientListPositionCompletenessTests
{
    /// <summary>Nodes the rewriter deliberately never descends into: injected security nodes are created after the pass runs and are opaque to every rewriter (SecurityCoverageTests).</summary>
    private static readonly HashSet<string> OpaqueByDesign =
    [
        nameof(SecurityPredicateExpression), nameof(MaskExpression), nameof(PolicyParameterExpression), nameof(NamedBindMarkerExpression)
    ];

    private static readonly ParameterReference Marker = new("l", null, true);

    private static IEnumerable<(Type Node, ConstructorInfo Constructor, ParameterInfo[] Slots)> Positions()
    {
        foreach (var type in typeof(SqlNode).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(SqlNode).IsAssignableFrom(t) && t.Namespace == typeof(SqlNode).Namespace))
        {
            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (constructor is null)
            {
                continue;
            }

            var slots = constructor.GetParameters().Where(p => IsExpressionSlot(p.ParameterType)).ToArray();
            if (slots.Length > 0)
            {
                yield return (type, constructor, slots);
            }
        }
    }

    private static bool IsExpressionSlot(Type type) =>
        type == typeof(Expression) || type == typeof(IReadOnlyList<Expression>) || type == typeof(IReadOnlyList<Expression?>);

    public static IEnumerable<object[]> Cases() =>
        Positions().SelectMany(p => p.Slots.Select(s => new object[] { p.Node.Name, s.Name! }));

    [Fact]
    public void EveryExpressionPositionIsEnumerated()
    {
        Assert.True(Positions().SelectMany(p => p.Slots).Count() > 40);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryExpressionProperty_IsVisited_AndAListThereIsRejected(string nodeName, string slotName)
    {
        if (OpaqueByDesign.Contains(nodeName))
        {
            return;
        }

        var (type, constructor, slots) = Positions().Single(p => p.Node.Name == nodeName);
        var parameters = constructor.GetParameters();
        var arguments = new object?[parameters.Length];
        var nullability = new NullabilityInfoContext();
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            arguments[i] = p.Name == slotName ? Fill(p.ParameterType) : DefaultFor(p.ParameterType, nullability.Create(p).WriteState != NullabilityState.Nullable);
        }

        var node = (SqlNode)constructor.Invoke(arguments);
        var expander = new ClientListExpander(Shapes(("l", 3)), DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql));

        var ex = Assert.Throws<ClientListException>(() => expander.Visit(node));
        Assert.Equal(GovernedSqlErrorCodes.ListParamPosition, ex.Code);
        _ = type;
        _ = slots;
    }

    // A list slot gets two items, so the position is never the legal sole item of an IN list.
    private static object Fill(Type slotType) => slotType == typeof(Expression) ? Marker : new Expression[] { new LiteralExpression(1L, LiteralType.Integer), Marker };

    private static object? DefaultFor(Type type, bool required, int depth = 0)
    {
        if (type == typeof(Expression) && required)
        {
            return new LiteralExpression(1L, LiteralType.Integer);
        }

        if (type.IsValueType)
        {
            return Activator.CreateInstance(type);
        }

        if (type == typeof(string))
        {
            return "x";
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        }

        if (!required || depth > 6 || !typeof(SqlNode).IsAssignableFrom(type))
        {
            return null;
        }

        // A required child node that is not an Expression (a target table, a name): build the simplest concrete node of that type.
        var concrete = type.IsAbstract || type.IsInterface
            ? typeof(SqlNode).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && type.IsAssignableFrom(t) && t.GetConstructors().Length > 0)
                .OrderBy(t => t.GetConstructors().Min(c => c.GetParameters().Length)).First()
            : type;
        var constructor = concrete.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        var nullability = new NullabilityInfoContext();
        var arguments = constructor.GetParameters()
            .Select(p => DefaultFor(p.ParameterType, nullability.Create(p).WriteState != NullabilityState.Nullable, depth + 1)).ToArray();
        return constructor.Invoke(arguments);
    }
}
