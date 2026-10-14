using System.Collections.Immutable;
using FsCheck;
using FsCheck.Fluent;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>
/// FsCheck generators of Trino statements over the fixture catalog (Orders and Entitlements carry a tenant column, Lookup does
/// not). They cover joins, set operations, CTEs (also recursive), subqueries in every position (FROM, WHERE, IN, EXISTS, scalar,
/// correlated), window functions, pagination and hostile string literals. Generators build token lists so that one statement can
/// be rendered in several styles (G3 identity).
/// </summary>
internal static class QueryGen
{
    private static readonly (string Name, ColKind Kind)[] Orders =
        [("id", ColKind.Int), ("tenantid", ColKind.Str), ("region", ColKind.Str), ("status", ColKind.Str), ("amount", ColKind.Int), ("email", ColKind.Str)];
    private static readonly (string Name, ColKind Kind)[] Entitlements = [("id", ColKind.Int), ("tenantid", ColKind.Str), ("orderid", ColKind.Int)];
    private static readonly (string Name, ColKind Kind)[] Lookup = [("code", ColKind.Str)];
    private static readonly (string Table, (string Name, ColKind Kind)[] Cols)[] Tables =
        [("orders", Orders), ("entitlements", Entitlements), ("lookup", Lookup)];

    /// <summary>Hostile fragments for user literals, policy values and tenant values (plan 9.2).</summary>
    public static readonly string[] Hostile =
    [
        "", "'", "''", "\"", "`", "]", "[", "\\", ";", "--", "/*", "*/", "$$", "E'", "аdmin", "‮", "\0", " OR 1=1", "'; DROP TABLE orders;--",
        "${x}", "%", "_", ":p1", "@p0", "$1", "?", "\n", "é", "\U0001F600", "\\'", "'' OR ''1''=''1"
    ];

    private static readonly string[] HostileIdentifiers =
        ["a'b", "a\"b", "a]b", "a`b", "a;b", "a--b", "a b", "a\\b", "a$$b", "a/*b", "été", "select", "dbo\".\"orders", "[x]"];

    /// <summary>Builds the generator only when a value is drawn, so recursive generators do not expand their whole tree eagerly.</summary>
    private static Gen<T> Defer<T>(Func<Gen<T>> factory) => Gen.Constant(0).SelectMany(_ => factory());

    // ---- literals ----

    private static Gen<string> RawString(string prefix) =>
        from n in Gen.Choose(0, 9999)
        from kind in Gen.Frequency(
            (6, Gen.Constant(-1)),
            (5, Gen.Choose(0, Hostile.Length - 1)),
            (1, Gen.Constant(-2)))
        from extra in Gen.Choose(0, Hostile.Length - 1)
        select kind switch
        {
            -1 => $"{prefix}{n}_v",
            -2 => $"{prefix}{n}_" + new string('x', 10_000),
            _ => $"{prefix}{n}_" + Hostile[kind] + Hostile[extra]
        };

    internal static string Quote(string raw) => "'" + raw.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static Gen<Frag> StrLiteral() =>
        RawString("LIT").Select(raw => new Frag(ImmutableArray.Create(Quote(raw)), 0, ImmutableArray.Create(raw), false));

    private static Gen<Frag> IntLiteral() => Gen.Choose(0, 5000).Select(n => Frag.Of(n.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static Gen<string> AliasName(string prefix) => Gen.Choose(0, 9999).Select(n => $"{prefix}{n}");

    // ---- sources ----

    private sealed record Source(Frag Frag, ImmutableArray<ScopeCol> Cols);

    private static Gen<Source> TableSource() =>
        from t in Gen.Elements(Tables)
        from alias in AliasName("t")
        from useAs in Gen.Elements(true, false)
        select new Source(
            (useAs ? Frag.Of(t.Table, "as", alias) : Frag.Of(t.Table, alias)) with { PhysicalRefs = 1 },
            t.Cols.Select(c => new ScopeCol(alias, c.Name, c.Kind)).ToImmutableArray());

    private static Gen<Source> CteSource(ImmutableArray<string> ctes) =>
        from cte in Gen.Elements(ctes.ToArray())
        from alias in AliasName("x")
        select new Source(Frag.Of(cte, "as", alias), ImmutableArray.Create(new ScopeCol(alias, "c0", ColKind.Int)));

    private static Gen<Source> ValuesSource() =>
        from alias in AliasName("v")
        from a in IntLiteral()
        from b in IntLiteral()
        select new Source(
            Frag.Of("(", "values", "(").Add(a).Add(",").Add(b).Add(")", ")", "as", alias, "(", "c0", ",", "c1", ")"),
            ImmutableArray.Create(new ScopeCol(alias, "c0", ColKind.Int), new ScopeCol(alias, "c1", ColKind.Int)));

    private static Gen<Source> DerivedSource(int size, ImmutableArray<string> ctes, ImmutableArray<ScopeCol> outer) => Defer(() => DerivedSourceCore(size, ctes, outer));

    private static Gen<Source> DerivedSourceCore(int size, ImmutableArray<string> ctes, ImmutableArray<ScopeCol> outer) =>
        from alias in AliasName("s")
        from kinds in Gen.Elements(
            new[] { ColKind.Int }, new[] { ColKind.Int, ColKind.Str }, new[] { ColKind.Str, ColKind.Int, ColKind.Int })
        from inner in SelectOf(size / 2, ctes, outer, kinds)
        select new Source(
            Frag.Of("(").Add(inner).Add(")", "as", alias),
            kinds.Select((k, i) => new ScopeCol(alias, "c" + i, k)).ToImmutableArray());

    private static Gen<Source> SourceGen(int size, ImmutableArray<string> ctes, ImmutableArray<ScopeCol> outer) => Defer(() => SourceGenCore(size, ctes, outer));

    private static Gen<Source> SourceGenCore(int size, ImmutableArray<string> ctes, ImmutableArray<ScopeCol> outer)
    {
        var leaf = new List<(int, Gen<Source>)> { (6, TableSource()), (1, ValuesSource()) };
        if (!ctes.IsDefaultOrEmpty)
        {
            leaf.Add((3, CteSource(ctes)));
        }

        if (size <= 1)
        {
            return Gen.Frequency(leaf);
        }

        var all = new List<(int, Gen<Source>)>(leaf) { (2, DerivedSource(size, ctes, outer)), (4, JoinSource(size, ctes, outer)) };
        return Gen.Frequency(all);
    }

    private static Gen<Source> JoinSource(int size, ImmutableArray<string> ctes, ImmutableArray<ScopeCol> outer) => Defer(() => JoinSourceCore(size, ctes, outer));

    private static Gen<Source> JoinSourceCore(int size, ImmutableArray<string> ctes, ImmutableArray<ScopeCol> outer) =>
        from left in SourceGen(size / 2, ctes, outer)
        from right in SourceGen(size / 2, ctes, outer)
        from kind in Gen.Elements("inner join", "left join", "cross join", "join")
        from onPred in OnPredicate(left.Cols, right.Cols)
        select new Source(
            kind == "cross join"
                ? left.Frag.Add(kind).Add(right.Frag)
                : left.Frag.Add(kind).Add(right.Frag).Add("on").Add(onPred),
            left.Cols.AddRange(right.Cols));

    private static Gen<Frag> OnPredicate(ImmutableArray<ScopeCol> left, ImmutableArray<ScopeCol> right)
    {
        var pairs = (from l in left from r in right where l.Kind == r.Kind select (l, r)).ToArray();
        if (pairs.Length == 0)
        {
            return Gen.Constant(Frag.Of("true"));
        }

        return Gen.Elements(pairs).Select(p => Frag.Of(p.l.Ref, "=", p.r.Ref));
    }

    // ---- expressions ----

    private static Gen<Frag> ColOfKind(ImmutableArray<ScopeCol> scope, ColKind kind)
    {
        var cols = scope.Where(c => c.Kind == kind).ToArray();
        return cols.Length == 0 ? (kind == ColKind.Int ? IntLiteral() : StrLiteral()) : Gen.Elements(cols).Select(c => Frag.Of(c.Ref));
    }

    private static Gen<Frag> IntExpr(int size, ImmutableArray<ScopeCol> scope, ImmutableArray<string> ctes) => Defer(() => IntExprCore(size, scope, ctes));

    private static Gen<Frag> IntExprCore(int size, ImmutableArray<ScopeCol> scope, ImmutableArray<string> ctes)
    {
        var options = new List<(int, Gen<Frag>)>
        {
            (5, ColOfKind(scope, ColKind.Int)),
            (2, IntLiteral()),
            (1, ColOfKind(scope, ColKind.Str).Select(c => Frag.Of("length", "(").Add(c).Add(")"))),
            (1, ColOfKind(scope, ColKind.Int).Select(c => Frag.Of("abs", "(").Add(c).Add(")"))),
            (1, from a in ColOfKind(scope, ColKind.Int) from b in IntLiteral() select Frag.Of("coalesce", "(").Add(a).Add(",").Add(b).Add(")")),
            (1, from a in ColOfKind(scope, ColKind.Int) from b in IntLiteral() select a.Add("+").Add(b)),
            (1, ColOfKind(scope, ColKind.Int).Select(c => Frag.Of("row_number", "(", ")", "over", "(", "order", "by").Add(c).Add(")"))),
            (1, ColOfKind(scope, ColKind.Int).Select(c => Frag.Of("sum", "(").Add(c).Add(")", "over", "(", ")"))),
            (1, from p in ColOfKind(scope, ColKind.Str) from o in ColOfKind(scope, ColKind.Int)
                select Frag.Of("rank", "(", ")", "over", "(", "partition", "by").Add(p).Add("order", "by").Add(o).Add(")"))
        };
        if (size > 1)
        {
            options.Add((2, from pred in Predicate(size / 2, scope, ctes) from a in IntLiteral() from b in IntLiteral()
                            select Frag.Of("case", "when").Add(pred).Add("then").Add(a).Add("else").Add(b).Add("end")));
            options.Add((2, from sub in IntSelect(size / 2, ctes, scope, aggregate: true) select Frag.Of("(").Add(sub).Add(")")));
        }

        return Gen.Frequency(options);
    }

    private static readonly string[] AllowedStringFunctions = ["lower", "upper", "trim"];

    private static Gen<Frag> StrExpr(int size, ImmutableArray<ScopeCol> scope)
    {
        var options = new List<(int, Gen<Frag>)>
        {
            (5, ColOfKind(scope, ColKind.Str)),
            (3, StrLiteral()),
            (1, from f in Gen.Elements(AllowedStringFunctions) from c in ColOfKind(scope, ColKind.Str) select Frag.Of(f, "(").Add(c).Add(")")),
            (1, from c in ColOfKind(scope, ColKind.Str) from l in StrLiteral() select c.Add("||").Add(l)),
            (1, from c in ColOfKind(scope, ColKind.Str) from l in StrLiteral() select Frag.Of("coalesce", "(").Add(c).Add(",").Add(l).Add(")")),
            // An unknown function must be rejected (INV-1), never emitted.
            (1, from c in ColOfKind(scope, ColKind.Str) from f in Gen.Elements("pg_read_file", "xp_cmdshell", "load_extension", "sleep_ms", "utl_http_request")
                select Frag.Of(f, "(").Add(c).Add(")") with { UnknownFunction = true })
        };
        return Gen.Frequency(options);
    }

    private static Gen<Frag> ItemOf(int size, ImmutableArray<ScopeCol> scope, ImmutableArray<string> ctes, ColKind kind) =>
        kind == ColKind.Int ? IntExpr(size, scope, ctes) : StrExpr(size, scope);

    private static Gen<Frag> Predicate(int size, ImmutableArray<ScopeCol> scope, ImmutableArray<string> ctes) => Defer(() => PredicateCore(size, scope, ctes));

    private static Gen<Frag> PredicateCore(int size, ImmutableArray<ScopeCol> scope, ImmutableArray<string> ctes)
    {
        var options = new List<(int, Gen<Frag>)>
        {
            (4, from c in ColOfKind(scope, ColKind.Int) from op in Gen.Elements("=", "<>", "<", ">=") from l in IntLiteral() select c.Add(op).Add(l)),
            (4, from c in ColOfKind(scope, ColKind.Str) from op in Gen.Elements("=", "<>") from l in StrLiteral() select c.Add(op).Add(l)),
            (2, from c in ColOfKind(scope, ColKind.Str) from l in StrLiteral() select c.Add("like").Add(l)),
            (2, from c in ColOfKind(scope, ColKind.Str) from a in StrLiteral() from b in StrLiteral() select c.Add("in", "(").Add(a).Add(",").Add(b).Add(")")),
            (1, from c in ColOfKind(scope, ColKind.Int) from n in Gen.Choose(1, 5) select c.Add("in", "(").Add(string.Join(" , ", Enumerable.Range(1, n))).Add(")")),
            (1, from c in ColOfKind(scope, ColKind.Str) from neg in Gen.Elements("is null", "is not null") select c.Add(neg.Split(' '))),
            (1, from c in ColOfKind(scope, ColKind.Int) from a in IntLiteral() from b in IntLiteral() select c.Add("between").Add(a).Add("and").Add(b)),
            (1, from a in ColOfKind(scope, ColKind.Int) from b in ColOfKind(scope, ColKind.Int) select a.Add("=").Add(b)),
            (1, Gen.Constant(Frag.Of("true"))),
            (1, Gen.Constant(Frag.Of("1", "=", "1")))
        };
        if (size > 1)
        {
            options.Add((2, from a in Predicate(size / 2, scope, ctes) from b in Predicate(size / 2, scope, ctes) from op in Gen.Elements("and", "or") select a.Add(op).Add(b)));
            options.Add((1, Predicate(size - 1, scope, ctes).Select(p => Frag.Of("not").Add(p.Paren()))));
            options.Add((1, Predicate(size - 1, scope, ctes).Select(p => p.Paren())));
            options.Add((2, from sub in IntSelect(size / 2, ctes, scope, aggregate: false) select Frag.Of("exists", "(").Add(sub).Add(")")));
            options.Add((2, from c in ColOfKind(scope, ColKind.Int) from sub in IntSelect(size / 2, ctes, scope, aggregate: false)
                            select c.Add("in", "(").Add(sub).Add(")")));
            options.Add((1, from c in ColOfKind(scope, ColKind.Int) from sub in IntSelect(size / 2, ctes, scope, aggregate: true)
                            select c.Add("=", "(").Add(sub).Add(")")));
        }

        return Gen.Frequency(options);
    }

    // ---- selects ----

    /// <summary>A SELECT with one expression per requested kind, projected as <c>c0..cn</c>, optionally correlated to <paramref name="outer"/>.</summary>
    private static Gen<Frag> SelectOf(int size, ImmutableArray<string> ctes, ImmutableArray<ScopeCol> outer, ColKind[] kinds) => Defer(() => SelectOfCore(size, ctes, outer, kinds));

    private static Gen<Frag> SelectOfCore(int size, ImmutableArray<string> ctes, ImmutableArray<ScopeCol> outer, ColKind[] kinds) =>
        from source in SourceGen(Math.Max(1, size), ctes, outer)
        let scope = source.Cols.AddRange(outer.IsDefault ? ImmutableArray<ScopeCol>.Empty : outer)
        from distinct in Gen.Elements(false, false, true)
        from items in Gen.CollectToList(kinds.Select(k => ItemOf(size, scope, ctes, k)))
        from hasWhere in Gen.Elements(true, true, false)
        from pred in Predicate(Math.Max(1, size - 1), scope, ctes)
        select SelectFrag(source, items, distinct, hasWhere ? pred : null);

    private static Frag SelectFrag(Source source, IReadOnlyList<Frag> items, bool distinct, Frag? where)
    {
        var f = Frag.Of("select");
        if (distinct)
        {
            f = f.Add("distinct");
        }

        for (int i = 0; i < items.Count; i++)
        {
            f = f.Add(i == 0 ? Frag.Empty : Frag.Of(",")).Add(items[i]).Add("as", "c" + i);
        }

        f = f.Add("from").Add(source.Frag);
        return where is null ? f : f.Add("where").Add(where);
    }

    private static Gen<Frag> IntSelect(int size, ImmutableArray<string> ctes, ImmutableArray<ScopeCol> outer, bool aggregate) => Defer(() => IntSelectCore(size, ctes, outer, aggregate));

    private static Gen<Frag> IntSelectCore(int size, ImmutableArray<string> ctes, ImmutableArray<ScopeCol> outer, bool aggregate)
    {
        if (!aggregate)
        {
            return SelectOf(size, ctes, outer, [ColKind.Int]);
        }

        // Scalar subquery shape: an aggregate over one source, correlated to the outer scope.
        return from source in SourceGen(Math.Max(1, size - 1), ctes, outer)
               let scope = source.Cols.AddRange(outer)
               from fn in Gen.Elements("count(*)", "max", "min")
               from col in ColOfKind(source.Cols, ColKind.Int)
               from pred in Predicate(Math.Max(1, size - 1), scope, ctes)
               select (fn == "count(*)" ? Frag.Of("select", "count", "(", "*", ")", "as", "c0") : Frag.Of("select", fn, "(").Add(col).Add(")", "as", "c0"))
                   .Add("from").Add(source.Frag).Add("where").Add(pred);
    }

    // ---- statements ----

    private static Gen<Frag> Cte(int size, ImmutableArray<string> prior, int index) =>
        from body in IntSelect(size, prior, ImmutableArray<ScopeCol>.Empty, aggregate: false)
        select Frag.Of("c" + index, "as", "(").Add(body).Add(")");

    private static Gen<Frag> RecursiveCte(int size) =>
        from anchor in IntSelect(Math.Max(1, size / 2), ImmutableArray<string>.Empty, ImmutableArray<ScopeCol>.Empty, aggregate: false)
        from limit in Gen.Choose(1, 4)
        select Frag.Of("recursive", "c0", "(", "c0", ")", "as", "(").Add(anchor).Add("union", "all", "select", "r.c0", "+", "1", "from", "c0", "as", "r", "where", "r.c0", "<", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), ")");

    private static Gen<Frag> Tail(ImmutableArray<ScopeCol> topScope) =>
        from kind in Gen.Elements(0, 0, 1, 2, 3)
        from n in Gen.Choose(0, 50)
        from m in Gen.Choose(1, 50)
        select kind switch
        {
            1 => Frag.Of("limit", m.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            2 => Frag.Of("order", "by", "1", "limit", m.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            3 => Frag.Of("order", "by", "1", "offset", n.ToString(System.Globalization.CultureInfo.InvariantCulture), "rows", "fetch", "next", m.ToString(System.Globalization.CultureInfo.InvariantCulture), "rows", "only"),
            _ => Frag.Empty
        };

    /// <summary>A governed read statement. Size bounds nesting; the top level may use a wildcard and a hostile output alias.</summary>
    public static Gen<Frag> Query() => Gen.Sized(size => Query(Math.Max(1, size)));

    private static Gen<Frag> Query(int size) => Gen.Frequency(
        (6, TopSelect(size, ImmutableArray<string>.Empty)),
        (2, SetOperation(size)),
        (3, WithQuery(size)),
        (1, RecursiveWith(size)));

    private static Gen<Frag> TopSelect(int size, ImmutableArray<string> ctes) =>
        from source in SourceGen(size, ctes, ImmutableArray<ScopeCol>.Empty)
        from shape in Gen.Elements(0, 1, 1, 2)
        from kinds in Gen.Elements(new[] { ColKind.Int }, new[] { ColKind.Str, ColKind.Int }, new[] { ColKind.Int, ColKind.Int, ColKind.Str })
        from items in Gen.CollectToList(kinds.Select(k => ItemOf(size, source.Cols, ctes, k)))
        from hostile in Gen.Elements(HostileIdentifiers)
        from useHostileAlias in Gen.Elements(false, false, true)
        from hasWhere in Gen.Elements(true, true, false)
        from pred in Predicate(Math.Max(1, size - 1), source.Cols, ctes)
        from tail in Tail(source.Cols)
        select TopSelectFrag(source, shape, items, useHostileAlias ? hostile : null, hasWhere ? pred : null, tail);

    private static Frag TopSelectFrag(Source source, int shape, IReadOnlyList<Frag> items, string? hostileAlias, Frag? where, Frag tail)
    {
        var f = Frag.Of("select");
        if (shape == 0)
        {
            f = f.Add("*");
        }
        else if (shape == 2 && source.Cols.Length > 0)
        {
            f = f.Add(source.Cols[0].Alias + ".*");
        }
        else
        {
            for (int i = 0; i < items.Count; i++)
            {
                string alias = hostileAlias is not null && i == 0 ? "\"" + hostileAlias.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : "c" + i;
                f = f.Add(i == 0 ? Frag.Empty : Frag.Of(",")).Add(items[i]).Add("as", alias);
            }
        }

        f = f.Add("from").Add(source.Frag);
        if (where is not null)
        {
            f = f.Add("where").Add(where);
        }

        return f.Add(tail);
    }

    private static Gen<Frag> SetOperation(int size) =>
        from left in IntSelect(size / 2, ImmutableArray<string>.Empty, ImmutableArray<ScopeCol>.Empty, aggregate: false)
        from right in IntSelect(size / 2, ImmutableArray<string>.Empty, ImmutableArray<ScopeCol>.Empty, aggregate: false)
        from op in Gen.Elements("union", "union all", "except", "intersect")
        // Parenthesized operands are rejected by the builder (KD-1, see KnownDefectTests) and are therefore not generated.
        select left.Add(op.Split(' ')).Add(right);

    private static Gen<Frag> WithQuery(int size) =>
        from n in Gen.Choose(1, 2)
        from c0 in Cte(size / 2, ImmutableArray<string>.Empty, 0)
        from c1 in Cte(size / 2, ImmutableArray.Create("c0"), 1)
        let names = n == 1 ? ImmutableArray.Create("c0") : ImmutableArray.Create("c0", "c1")
        from main in TopSelect(size / 2, names)
        select Frag.Of("with").Add(c0).Add(n == 2 ? Frag.Of(",").Add(c1) : Frag.Empty).Add(main);

    private static Gen<Frag> RecursiveWith(int size) =>
        from cte in RecursiveCte(size)
        from main in TopSelect(Math.Max(1, size / 2), ImmutableArray.Create("c0"))
        select Frag.Of("with").Add(cte).Add(main);

    // ---- DML ----

    /// <summary>Kinds of generated DML; the oracle derives its expectation from this.</summary>
    internal enum DmlKind { Update, Delete, DeleteUnfiltered, Insert, InsertOtherTenant, InsertSelect, UpdateTenantColumn, Merge }

    internal sealed record DmlFrag(Frag Frag, DmlKind Kind);

    public static Gen<DmlFrag> Dml(string tenantValue) =>
        from kind in Gen.Elements(DmlKind.Update, DmlKind.Update, DmlKind.Delete, DmlKind.Delete, DmlKind.DeleteUnfiltered, DmlKind.Insert, DmlKind.Insert,
            DmlKind.InsertOtherTenant, DmlKind.InsertSelect, DmlKind.UpdateTenantColumn, DmlKind.Merge)
        from frag in DmlBody(kind, tenantValue)
        select new DmlFrag(frag, kind);

    private static readonly ImmutableArray<ScopeCol> OrdersScope = Orders.Select(c => new ScopeCol("orders", c.Name, c.Kind)).ToImmutableArray();

    private static Gen<Frag> DmlBody(DmlKind kind, string tenantValue)
    {
        var bare = OrdersScope.Select(c => new ScopeCol(string.Empty, c.Name, c.Kind)).ToImmutableArray();
        Gen<Frag> wh = Predicate(2, bare.Select(c => c with { Alias = "orders" }).ToImmutableArray(), ImmutableArray<string>.Empty)
            .Select(p => Strip(p));
        return kind switch
        {
            DmlKind.Update => from l in StrLiteral() from w in wh select Frag.Of("update", "orders", "set", "status", "=").Add(l).Add("where").Add(w) with { PhysicalRefs = 0 },
            DmlKind.UpdateTenantColumn => from l in StrLiteral() select Frag.Of("update", "orders", "set", "tenantid", "=").Add(l).Add("where", "id", "=", "1"),
            DmlKind.Delete => from w in wh select Frag.Of("delete", "from", "orders", "where").Add(w),
            DmlKind.DeleteUnfiltered => Gen.Constant(Frag.Of("delete", "from", "orders")),
            DmlKind.Insert => from r in StrLiteral() from s in StrLiteral() select InsertFrag(Quote(tenantValue), tenantValue, r, s),
            DmlKind.InsertOtherTenant => from r in StrLiteral() from s in StrLiteral() from other in RawString("OTH")
                                         select InsertFrag(Quote(other), other, r, s),
            DmlKind.InsertSelect => from w in wh select Frag.Of("insert", "into", "orders", "(", "id", ",", "tenantid", ",", "region", ",", "status", ")",
                "select", "id", ",", "tenantid", ",", "region", ",", "status", "from", "orders", "where").Add(w) with { PhysicalRefs = 1 },
            _ => from l in StrLiteral() select Frag.Of("merge", "into", "orders", "as", "t", "using", "entitlements", "as", "s", "on", "t.id", "=", "s.orderid",
                "when", "matched", "then", "update", "set", "status", "=").Add(l)
        };
    }

    private static Frag InsertFrag(string tenantToken, string tenantRaw, Frag region, Frag status) =>
        Frag.Of("insert", "into", "orders", "(", "id", ",", "tenantid", ",", "region", ",", "status", ")", "values", "(", "1", ",", tenantToken, ",")
            .Add(region).Add(",").Add(status).Add(")") with { Literals = ImmutableArray.Create(tenantRaw).AddRange(region.Literals).AddRange(status.Literals) };

    // Predicate columns are qualified with the alias "orders"; DML statements reference the table columns bare, so drop the qualifier.
    private static Frag Strip(Frag f) => f with { Tokens = f.Tokens.Select(t => t.StartsWith("orders.", StringComparison.Ordinal) ? t["orders.".Length..] : t).ToImmutableArray() };
}
