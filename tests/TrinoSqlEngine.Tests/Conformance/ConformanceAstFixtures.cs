using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Tests.Conformance;

/// <summary>
/// In-memory AST inputs for nodes that client text can never produce: the gateway injects them (policy parameters, masks, security
/// predicates, trusted fragments, client parameter references). They need a conformance fixture like every other node (G2).
/// </summary>
internal static class ConformanceAstFixtures
{
    public sealed record AstCase(SqlStatement Statement, IReadOnlyDictionary<string, PolicyValue> PolicyValues, IReadOnlyDictionary<string, object?> ClientValues);

    private static readonly IReadOnlyDictionary<string, PolicyValue> NoPolicy = new Dictionary<string, PolicyValue>();
    private static readonly IReadOnlyDictionary<string, object?> NoClient = new Dictionary<string, object?>();

    private static SqlIdentifier Id(string n) => new(n, true);
    private static ColumnReference Col(string n) => new(new SqlQualifiedName(new[] { Id(n) }));
    private static PolicyParameterExpression P(string n, SqlParameterType t, ParameterOrigin o = ParameterOrigin.Mask) => new(n, t, o);

    private static SelectStatement Select(Expression item, Expression? where = null) => new(null, new QuerySpecification(false,
        new SelectItem[] { new ColumnSelectItem(item, Id("r")) },
        new NamedTableSource(new SqlQualifiedName(new[] { Id("s"), Id("t") }), null), where, null, null), null, null);

    private static AstCase Case(SqlStatement s, IReadOnlyDictionary<string, PolicyValue>? policy = null, IReadOnlyDictionary<string, object?>? client = null) =>
        new(s, policy ?? NoPolicy, client ?? NoClient);

    private static BinaryExpression Eq(Expression l, Expression r) => new(l, BinaryOperator.Equal, r);

    public static readonly IReadOnlyDictionary<string, Func<AstCase>> ByName = new Dictionary<string, Func<AstCase>>
    {
        ["policy-param-where"] = () => Case(Select(Col("a"), Eq(Col("tenant_id"), P("tenant", SqlParameterType.String, ParameterOrigin.Tenant))),
            new Dictionary<string, PolicyValue> { ["tenant"] = new("acme", SqlParameterType.String) }),
        ["policy-param-like"] = () => Case(Select(Col("a"), new LikeExpression(Col("b"), new PolicyParameterExpression("pat", SqlParameterType.String, ParameterOrigin.Policy, IsLikePattern: true), null, false)),
            new Dictionary<string, PolicyValue> { ["pat"] = new("x%", SqlParameterType.String) }),
        ["policy-param-int"] = () => Case(Select(Col("a"), Eq(Col("level"), P("lvl", SqlParameterType.Int32, ParameterOrigin.Policy))),
            new Dictionary<string, PolicyValue> { ["lvl"] = new(3, SqlParameterType.Int32) }),
        ["security-predicate"] = () => Case(Select(Col("a"), new SecurityPredicateExpression(
                Eq(Col("tenant_id"), P("tenant", SqlParameterType.String, ParameterOrigin.Tenant)), new SecurityPredicateId("s.t", 0), SecurityScope.Root)),
            new Dictionary<string, PolicyValue> { ["tenant"] = new("acme", SqlParameterType.String) }),
        ["parenthesized"] = () => Case(Select(Col("a"), new BinaryExpression(new ParenthesizedExpression(Eq(Col("b"), new LiteralExpression(1, LiteralType.Integer))), BinaryOperator.And,
            new ParenthesizedExpression(Eq(Col("c"), new LiteralExpression("x", LiteralType.String)))))),
        ["trusted-sql"] = () => Case(Select(new TrustedSqlExpression("UPPER(email)"))),
        ["param-ref-named"] = () => Case(Select(Col("a"), Eq(Col("b"), new ParameterReference("p", IsSynthetic: true))),
            client: new Dictionary<string, object?> { ["p"] = 5 }),
        ["param-ref-positional"] = () => Case(Select(Col("a"), Eq(Col("b"), new ParameterReference("p0", PositionalIndex: 0)))),
        ["mask-nullify"] = () => Case(Select(new MaskExpression(MaskKind.Nullify, Col("email"), new MaskArguments(), "varchar(200)"))),
        ["mask-redact-text"] = () => Case(Select(new MaskExpression(MaskKind.Redact, Col("email"), new MaskArguments(Constant: P("m", SqlParameterType.String)), "varchar(200)")),
            new Dictionary<string, PolicyValue> { ["m"] = new("[REDACTED]", SqlParameterType.String) }),
        ["mask-redact-numeric"] = () => Case(Select(new MaskExpression(MaskKind.Redact, Col("amount"), new MaskArguments(), "decimal(18,2)"))),
        ["mask-partial"] = () => Case(Select(new MaskExpression(MaskKind.PartialMask, Col("email"),
                new MaskArguments(KeepPrefix: P("p", SqlParameterType.Int32), KeepSuffix: P("s", SqlParameterType.Int32), MaskChar: P("c", SqlParameterType.String)), "varchar(200)")),
            new Dictionary<string, PolicyValue> { ["p"] = new(2, SqlParameterType.Int32), ["s"] = new(4, SqlParameterType.Int32), ["c"] = new("*", SqlParameterType.String) }),
        ["mask-hmac"] = () => Case(Select(new MaskExpression(MaskKind.Hmac, Col("email"),
                new MaskArguments(HmacKey: P("k", SqlParameterType.Binary), HmacKeyOuter: P("o", SqlParameterType.Binary)), "varchar(200)")),
            new Dictionary<string, PolicyValue> { ["k"] = new(new byte[] { 1, 2 }, SqlParameterType.Binary), ["o"] = new(new byte[] { 3, 4 }, SqlParameterType.Binary) }),
        ["mask-constant"] = () => Case(Select(new MaskExpression(MaskKind.Constant, Col("email"), new MaskArguments(Constant: P("m", SqlParameterType.String)), "varchar(200)")),
            new Dictionary<string, PolicyValue> { ["m"] = new("const", SqlParameterType.String) }),
        ["mask-geojitter"] = () => Case(Select(new MaskExpression(MaskKind.GeoJitter, Col("lat"), new MaskArguments(Decimals: 2), "double"))),
    };
}
