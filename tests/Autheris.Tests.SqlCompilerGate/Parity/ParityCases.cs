namespace Autheris.Tests.SqlCompilerGate.Parity;

using System.Globalization;
using System.Text.Json;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.SqlCompilerGate.Model;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;

/// <summary>
/// The producer cases of gate G10 (plan 16.5 M-9). Every case is evaluated against the fixture table <c>orders</c> (tenant A rows 1..4
/// and 2001..2004, tenant B rows 1001..1004) and carries an independent reference of the rows it must admit.
/// </summary>
public static class ParityCases
{
    private static readonly TableDef Orders = GateModel.Table("orders");
    private static readonly TableDef Customers = GateModel.Table("customers");

    public static IReadOnlyList<ParityCase> All { get; } = [.. ConsentCases(), .. SubqueryCases(), .. VirtualFilterCases()];

    // ---- reference helpers (independent of both producers) ----

    private static object? Cell(RowDef row, string column) => row.Values[GateModel.Table(row.Table).IndexOf(column)];

    private static string Text(RowDef row, string column) => (string)Cell(row, column)!;

    private static decimal Number(RowDef row, string column) => Convert.ToDecimal(Cell(row, column), CultureInfo.InvariantCulture);

    private static IEnumerable<RowDef> Of(string table) => GateModel.Rows.Where(r => r.Table == table);

    private static bool ReferenceCondition(ConsentRowFilter filter, RowDef row, bool isDeny = false)
    {
        // Zero Trust polarity (RR-L4-01): an unresolvable user attribute never admits a row. As an allow condition it is false, as a deny
        // condition it is true (so that NOT (...) is false).
        if (string.Equals(filter.ValueSource, "USER_ATTRIBUTE", StringComparison.OrdinalIgnoreCase)) return isDeny;
        var cell = Cell(row, filter.ColumnName);
        using var json = JsonDocument.Parse(filter.ValueJson);
        var value = json.RootElement;
        string op = filter.Operator.ToUpperInvariant();
        if (op == "IN") return value.EnumerateArray().Any(e => Equal(cell, e));
        if (value.ValueKind == JsonValueKind.Null) return op == "EQ" ? cell is null : cell is not null;
        if (cell is null) return false;
        return op switch
        {
            "EQ" => Equal(cell, value),
            "NEQ" => !Equal(cell, value),
            "LT" => Compare(cell, value) < 0,
            "GT" => Compare(cell, value) > 0,
            "LTE" => Compare(cell, value) <= 0,
            "GTE" => Compare(cell, value) >= 0,
            "LIKE" => Like((string)cell, value.GetString()!),
            _ => throw new InvalidOperationException("No reference for operator " + op)
        };
    }

    private static bool Equal(object? cell, JsonElement value) => cell is not null && Compare(cell, value) == 0;

    private static int Compare(object cell, JsonElement value) => cell is string s
        ? string.CompareOrdinal(s, value.GetString())
        : Convert.ToDecimal(cell, CultureInfo.InvariantCulture).CompareTo(value.GetDecimal());

    private static bool Like(string text, string pattern)
    {
        // only the shapes the cases use: prefix%, %suffix, %infix%
        bool start = pattern.StartsWith('%'), end = pattern.EndsWith('%');
        string core = pattern.Trim('%');
        return (start, end) switch
        {
            (true, true) => text.Contains(core, StringComparison.Ordinal),
            (false, true) => text.StartsWith(core, StringComparison.Ordinal),
            (true, false) => text.EndsWith(core, StringComparison.Ordinal),
            _ => text == core
        };
    }

    /// <summary>The §7.3 semantics: groups OR, conditions AND, allow consents OR (an unconstrained allow admits all), deny consents NOT (...).</summary>
    private static bool ReferenceCombined(IReadOnlyList<Consent> allow, IReadOnlyList<Consent> deny, RowDef row)
    {
        bool ConsentMatches(Consent c, bool isDeny) => c.RowFilters.GroupBy(f => f.FilterGroup).Any(g => g.All(f => ReferenceCondition(f, row, isDeny)));
        bool allowed = allow.Count == 0 || allow.Any(c => c.RowFilters.Count == 0) || allow.Any(c => ConsentMatches(c, false));
        bool denied = deny.Where(c => c.RowFilters.Count > 0).Any(c => ConsentMatches(c, true));
        return allowed && !denied;
    }

    // ---- consent filter cases ----

    private static ConsentRowFilter F(string column, string op, string valueJson, int group = 0) =>
        new() { ColumnName = column, Operator = op, ValueJson = valueJson, FilterGroup = group };

    private static Consent Allow(params ConsentRowFilter[] filters) => new() { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Effect = ConsentEffect.Allow, RowFilters = filters };

    private static Consent Deny(params ConsentRowFilter[] filters) => new() { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Effect = ConsentEffect.Deny, RowFilters = filters };

    private static ParityCase Consent(string id, string description, Consent[] allow, Consent[] deny, DatabaseDialect? only = null) =>
        new(id, ProducerKind.ConsentFilter, description, ctx =>
        {
            var builder = new RowFilterSqlBuilder();
            var legacy = builder.BuildCombinedRowFilter(allow, deny, ctx.Database);
            var ir = builder.BuildCombinedRowFilterParameterized(allow, deny, ctx.Database);
            return new ProducedPair(
                legacy is null ? FilterText.Plain(string.Empty) : FilterText.Plain(legacy),
                ir.Sql is null ? FilterText.Plain(string.Empty) : new FilterText(ir.Sql, ir.Parameters ?? FilterText.NoParameters)
                {
                    Typed = new TypedRowFilterBuilder().BuildCombined(allow, deny, new TrinoSqlEngine.Governance.TableIdentity(ctx.Schema, "orders"),
                        PolicyParameterNamer.ToTargetDialect(ctx.Database))
                });
        }, row => ReferenceCombined(allow, deny, row));

    private static IEnumerable<ParityCase> ConsentCases()
    {
        yield return Consent("consent.eq-string", "EQ string", [Allow(F("status", "EQ", "\"open\""))], []);
        yield return Consent("consent.neq-string", "NEQ string", [Allow(F("status", "NEQ", "\"closed\""))], []);
        yield return Consent("consent.lt-number", "LT number", [Allow(F("amount", "LT", "35"))], []);
        yield return Consent("consent.gt-number", "GT number", [Allow(F("amount", "GT", "35"))], []);
        yield return Consent("consent.lte-number", "LTE number", [Allow(F("amount", "LTE", "30.5"))], []);
        yield return Consent("consent.gte-number", "GTE number", [Allow(F("amount", "GTE", "40.5"))], []);
        yield return Consent("consent.eq-int-id", "EQ integer", [Allow(F("id", "EQ", "2"))], []);
        yield return Consent("consent.like-prefix", "LIKE prefix", [Allow(F("note", "LIKE", "\"orders-A-%\""))], []);
        yield return Consent("consent.in-strings", "IN strings", [Allow(F("dept", "IN", "[\"sales\",\"hr\"]"))], []);
        yield return Consent("consent.in-numbers", "IN numbers", [Allow(F("id", "IN", "[1,3,2001]"))], []);
        yield return Consent("consent.in-empty", "IN empty list denies all", [Allow(F("dept", "IN", "[]"))], []);
        yield return Consent("consent.eq-null", "EQ NULL", [Allow(F("status", "EQ", "null"))], []);
        yield return Consent("consent.neq-null", "NEQ NULL", [Allow(F("status", "NEQ", "null"))], []);
        yield return Consent("consent.hostile-quote", "hostile quote value", [Allow(F("status", "EQ", "\"open' OR '1'='1\""))], []);
        yield return Consent("consent.hostile-comment", "hostile comment value", [Allow(F("status", "EQ", "\"x'; DROP TABLE orders; --\""))], []);
        yield return Consent("consent.hostile-in", "hostile IN value", [Allow(F("status", "IN", "[\"open\",\"x' OR 'a'='a\"]"))], []);
        yield return Consent("consent.group-or-and", "groups OR, conditions AND",
            [Allow(F("status", "EQ", "\"open\"", 0), F("dept", "EQ", "\"sales\"", 0), F("status", "EQ", "\"pending\"", 1))], []);
        yield return Consent("consent.three-groups", "three groups",
            [Allow(F("dept", "EQ", "\"sales\"", 0), F("dept", "EQ", "\"ops\"", 1), F("id", "EQ", "3", 2))], []);
        yield return Consent("consent.two-allows", "two allow consents OR-ed",
            [Allow(F("status", "EQ", "\"open\"")), Allow(F("dept", "EQ", "\"hr\""))], []);
        yield return Consent("consent.allow-and-deny", "allow minus deny",
            [Allow(F("status", "EQ", "\"open\""))], [Deny(F("dept", "EQ", "\"sales\""))]);
        yield return Consent("consent.two-denies", "two deny consents",
            [Allow(F("amount", "GT", "10"))], [Deny(F("dept", "EQ", "\"sales\"")), Deny(F("dept", "EQ", "\"ops\""))]);
        yield return Consent("consent.deny-only", "deny only", [], [Deny(F("status", "EQ", "\"closed\""))]);
        yield return Consent("consent.deny-groups", "deny with groups",
            [Allow(F("amount", "GT", "10"))], [Deny(F("dept", "EQ", "\"sales\"", 0), F("status", "EQ", "\"open\"", 0), F("dept", "EQ", "\"it\"", 1))]);
        yield return Consent("consent.unconstrained-allow", "an unconstrained allow admits all",
            [Allow(F("status", "EQ", "\"open\"")), Allow()], []);
        yield return Consent("consent.unconstrained-allow-deny", "unconstrained allow still honours deny",
            [Allow()], [Deny(F("status", "EQ", "\"closed\""))]);
        yield return Consent("consent.user-attribute-allow", "USER_ATTRIBUTE allow fails closed",
            [Allow(new ConsentRowFilter { ColumnName = "status", Operator = "EQ", ValueJson = "\"x\"", ValueSource = "USER_ATTRIBUTE" })], []);
        yield return Consent("consent.user-attribute-deny", "USER_ATTRIBUTE deny fails closed",
            [Allow(F("amount", "GT", "10"))],
            [Deny(new ConsentRowFilter { ColumnName = "status", Operator = "EQ", ValueJson = "\"x\"", ValueSource = "USER_ATTRIBUTE" })]);
        yield return Consent("consent.precedence-or-in-and", "(a OR b) AND NOT (c OR d)",
            [Allow(F("status", "EQ", "\"open\"", 0), F("dept", "EQ", "\"hr\"", 1))],
            [Deny(F("amount", "GT", "45", 0), F("amount", "LT", "25", 1))]);
    }

    // ---- correlated and cross-source cases ----

    private static ConsentRowFilter Correlated(ParityContext ctx, string predicateJson, string foreignKey = "customer_id") => new()
    {
        FilterType = RowFilterType.SubqueryCorrelated,
        DependentTable = new TableIdentifier("gate", ctx.Schema, "customers"),
        DependentTableAlias = "c",
        ForeignKeyColumn = foreignKey,
        PrimaryKeyColumn = "id",
        SubqueryFilterPredicateJson = predicateJson
    };

    private static ParityCase Subquery(string id, string description, Func<ParityContext, ConsentRowFilter> filter, Func<RowDef, bool> reference,
        RowFilterSubqueryStrategy strategy = RowFilterSubqueryStrategy.Exists, bool deny = false) =>
        new(id, id.StartsWith("correlated.", StringComparison.Ordinal) ? ProducerKind.CorrelatedFilter : ProducerKind.CrossSourceSetFilter,
            description, ctx =>
            {
                var consent = deny ? Deny(filter(ctx)) : Allow(filter(ctx));
                Consent[] allow = deny ? [Allow(F("amount", "GT", "10"))] : [consent];
                Consent[] denied = deny ? [consent] : [];
                var builder = new RowFilterSqlBuilder(null, Options.Create(new GatewayOptions { RowFilters = new RowFilterOptions { SubqueryStrategy = strategy } }));
                var legacy = builder.BuildCombinedRowFilter(allow, denied, ctx.Database)!;
                var ir = builder.BuildCombinedRowFilterParameterized(allow, denied, ctx.Database);
                return new ProducedPair(FilterText.Plain(legacy), new FilterText(ir.Sql!, ir.Parameters ?? FilterText.NoParameters));
            }, deny ? row => Number(row, "amount") > 10 && !reference(row) : reference);

    private static bool CustomerOf(RowDef order, Func<RowDef, bool> customerPredicate) =>
        Of("customers").Any(c => Convert.ToInt64(Cell(c, "id"), CultureInfo.InvariantCulture) == Convert.ToInt64(Cell(order, "customer_id"), CultureInfo.InvariantCulture)
                                 && customerPredicate(c));

    private static IEnumerable<ParityCase> SubqueryCases()
    {
        yield return Subquery("correlated.object-eq", "correlated EXISTS, object predicate",
            ctx => Correlated(ctx, "{\"c.name\": \"customers-A-1\"}"),
            row => CustomerOf(row, c => Text(c, "name") == "customers-A-1"));
        yield return Subquery("correlated.object-hostile", "correlated EXISTS, hostile value",
            ctx => Correlated(ctx, "{\"c.name\": \"x' OR '1'='1\"}"),
            _ => false);
        yield return Subquery("correlated.object-null", "correlated EXISTS, NULL value",
            ctx => Correlated(ctx, "{\"c.region\": \"EU\", \"c.name\": null}"),
            _ => false);
        yield return Subquery("correlated.array-ops", "correlated EXISTS, operator array",
            ctx => Correlated(ctx, "[{\"column\":\"c.id\",\"op\":\"GTE\",\"value\":2},{\"column\":\"c.region\",\"op\":\"EQ\",\"value\":\"EU\"}]"),
            row => CustomerOf(row, c => Number(c, "id") >= 2 && Text(c, "region") == "EU"));
        yield return Subquery("correlated.region-eu", "correlated EXISTS, region",
            ctx => Correlated(ctx, "{\"c.region\": \"EU\"}"),
            row => CustomerOf(row, c => Text(c, "region") == "EU"));
        yield return Subquery("correlated.deny", "correlated EXISTS as a deny consent",
            ctx => Correlated(ctx, "{\"c.name\": \"customers-A-1\"}"),
            row => CustomerOf(row, c => Text(c, "name") == "customers-A-1"), deny: true);
        yield return Subquery("correlated.in-strategy", "legacy IN strategy against the IR EXISTS",
            ctx => Correlated(ctx, "{\"c.region\": \"EU\"}"),
            row => CustomerOf(row, c => Text(c, "region") == "EU"), RowFilterSubqueryStrategy.In);
        yield return Subquery("correlated.in-correlated-strategy", "legacy correlated IN strategy against the IR EXISTS",
            ctx => Correlated(ctx, "{\"c.region\": \"EU\"}"),
            row => CustomerOf(row, c => Text(c, "region") == "EU"), RowFilterSubqueryStrategy.InCorrelated);
        yield return Subquery("crosssource.set", "cross-source value set", _ => new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter, ColumnName = "customer_id", ValueJson = "[1, 2]"
        }, row => new[] { 1L, 2L }.Contains(Convert.ToInt64(Cell(row, "customer_id"), CultureInfo.InvariantCulture)));
        yield return Subquery("crosssource.hostile", "cross-source set with hostile strings", _ => new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter, ColumnName = "status", ValueJson = "[\"open\", \"a' OR '1'='1\"]"
        }, row => Text(row, "status") == "open");
        yield return Subquery("crosssource.empty", "empty cross-source set denies all", _ => new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter, ColumnName = "customer_id", ValueJson = "[]"
        }, _ => false);
    }

    // ---- virtual filter cases ----

    private static TableMetadata Catalogued(ParityContext ctx, TableDef table) => new()
    {
        Identifier = new TableIdentifier("gate", ctx.Schema, table.Name),
        Table = new Table { SourceName = "gate", SchemaName = ctx.Schema, TableName = table.Name, SourceType = ctx.Database.ToString(), IsActive = true },
        Columns = [.. table.Columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Kind == ColumnKind.Whole ? "integer" : "text" })]
    };

    private static ParityCase SqlVirtualFilter(string id, string description, Func<ParityContext, string> sql, Func<RowDef, bool> reference) =>
        new(id, ProducerKind.VirtualFilterSql, description, ctx =>
        {
            var filter = SqlFilterCompiler.Validate(new VirtualFilter
            {
                TenantId = new TenantId("tenant_gate"), Name = "gate_filter", Source = "gate", Sql = sql(ctx)
            }, [Catalogued(ctx, Customers)]);
            // SR-L-11: every tenanted source table is bound to the caller's tenant through the reserved value.
            var tenant = new VirtualFilterTenantContext(GateModel.TenantA, _ => GateModel.TenantColumn);
            var legacy = SqlFilterCompiler.Compile(filter, ctx.Database, tenant);
            var typed = SqlFilterCompiler.CompileTyped(filter, ctx.Database, new FastSqlEngine(), tenant: tenant);
            var rendered = new PolicyPredicateRenderer().Render(typed, PolicyParameterNamer.ToTargetDialect(ctx.Database), RowFilterAliases.Target);
            return new ProducedPair(FilterText.Plain(legacy), new FilterText(rendered.Sql, rendered.Parameters) { Typed = typed });
        }, reference);

    private static ParityCase StructuredVirtualFilter(string id, string description, Func<ParityContext, StructuredFilterDefinition> definition,
        Func<RowDef, bool> reference) =>
        new(id, ProducerKind.VirtualFilterStructured, description, ctx =>
        {
            var filter = new VirtualFilter
            {
                TenantId = new TenantId("tenant_gate"), Name = "gate_structured", Source = "gate", KeyColumns = ["c.id"], Structured = definition(ctx)
            };
            var binding = new FilterBinding { FilterName = "gate_structured", ColumnMap = new Dictionary<string, string> { ["id"] = "customer_id" } };
            var builder = new StructuredFilterSqlBuilder();
            var target = Catalogued(ctx, Orders);
            var legacy = builder.Build(filter, binding, target, ctx.Database);
            var typed = builder.BuildTyped(filter, binding, target, ctx.Database, "__gql_vf_")
                ?? throw new InvalidOperationException("The structured filter has no typed form.");
            var rendered = new PolicyPredicateRenderer().Render(typed, PolicyParameterNamer.ToTargetDialect(ctx.Database), RowFilterAliases.Target);
            return new ProducedPair(FilterText.Plain(legacy), new FilterText(rendered.Sql, rendered.Parameters) { Typed = typed });
        }, reference);

    private static StructuredFilterDefinition Structured(ParityContext ctx, params FilterCondition[] where) => new()
    {
        From = new TableIdentifier("gate", ctx.Schema, "customers"),
        FromAlias = "c",
        Joins = [],
        Where = where
    };

    private static IEnumerable<ParityCase> VirtualFilterCases()
    {
        yield return SqlVirtualFilter("vf-sql.eq-literal", "sql filter, string literal",
            ctx => $"from {ctx.Schema}.customers c where target.customer_id = c.id and c.tenant_id = '{{{{tenant}}}}' and c.region = 'EU'",
            row => CustomerOf(row, c => Text(c, "region") == "EU"));
        yield return SqlVirtualFilter("vf-sql.hostile-literal", "sql filter, hostile literal",
            ctx => $"from {ctx.Schema}.customers c where target.customer_id = c.id and c.tenant_id = '{{{{tenant}}}}' and c.name = 'x'' OR ''1''=''1'",
            _ => false);
        yield return SqlVirtualFilter("vf-sql.number-in", "sql filter, number and IN list",
            ctx => $"from {ctx.Schema}.customers c where target.customer_id = c.id and c.tenant_id = '{{{{tenant}}}}' and c.id >= 2 and c.region in ('EU', 'XX')",
            row => CustomerOf(row, c => Number(c, "id") >= 2 && Text(c, "region") == "EU"));
        yield return SqlVirtualFilter("vf-sql.or-precedence", "sql filter, OR inside AND",
            ctx => $"from {ctx.Schema}.customers c where c.tenant_id = '{{{{tenant}}}}' and ((target.customer_id = c.id and c.id = 1) or (target.customer_id = c.id and c.id = 2)) and c.region = 'EU'",
            row => CustomerOf(row, c => (Number(c, "id") == 1 || Number(c, "id") == 2) && Text(c, "region") == "EU"));
        yield return SqlVirtualFilter("vf-sql.like", "sql filter, LIKE",
            ctx => $"from {ctx.Schema}.customers c where target.customer_id = c.id and c.tenant_id = '{{{{tenant}}}}' and c.name like 'customers-A-%'",
            row => CustomerOf(row, c => Text(c, "name").StartsWith("customers-A-", StringComparison.Ordinal)));
        yield return StructuredVirtualFilter("vf-structured.eq", "structured filter, equality",
            ctx => Structured(ctx, new FilterCondition("c.region", FilterConditionOperator.Eq, "EU")),
            row => CustomerOf(row, c => Text(c, "region") == "EU"));
        yield return StructuredVirtualFilter("vf-structured.hostile", "structured filter, hostile value",
            ctx => Structured(ctx, new FilterCondition("c.name", FilterConditionOperator.Eq, "x' OR '1'='1")),
            _ => false);
        yield return StructuredVirtualFilter("vf-structured.number-notnull", "structured filter, number and IS NOT NULL",
            ctx => Structured(ctx, new FilterCondition("c.id", FilterConditionOperator.NotEq, "1"), new FilterCondition("c.region", FilterConditionOperator.IsNotNull)),
            row => CustomerOf(row, c => Number(c, "id") != 1));
    }
}
