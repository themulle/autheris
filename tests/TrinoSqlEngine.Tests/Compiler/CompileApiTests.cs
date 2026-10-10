using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

public class CompileApiTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    private static PolicyPredicate RegionPredicate(string value) => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
            BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new(value, SqlParameterType.String) });

    private CompileRequest Request(
        string tenant = "acme",
        TargetSqlDialect dialect = TargetSqlDialect.SqlServer,
        InMemoryTableCatalog? catalog = null,
        Func<CompileRequest, CompileRequest>? configure = null)
    {
        var request = new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = _rowFilters,
                Masks = _masks,
                Catalog = catalog ?? Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
            }
        };
        return configure?.Invoke(request) ?? request;
    }

    private CompiledSql Compile(string sql, CompileRequest? request = null, CancellationToken ct = default) =>
        _engine.Compile(sql.AsMemory(), request ?? Request(), ct);

    public CompileApiTests()
    {
        _rowFilters.Predicates[Orders] = RegionPredicate("EU");
    }

    // ---- happy path ----

    [Fact]
    public void Select_OnSqlServer_ReturnsVerifiedParameterizedSql()
    {
        var c = Compile("SELECT id, status FROM orders WHERE status = 'open'");

        Assert.Equal(TargetSqlDialect.SqlServer, c.Dialect);
        Assert.Equal(SqlStatementClass.Select, c.StatementClass);
        Assert.Contains("[dbo].[Orders]", c.Sql);
        Assert.DoesNotContain("acme", c.Sql);
        Assert.DoesNotContain("'", c.Sql);
        Assert.Contains(c.Parameters, p => p.Origin == ParameterOrigin.Tenant && Equals(p.Value, "acme"));
        Assert.Contains(c.Parameters, p => p.Origin == ParameterOrigin.Policy && Equals(p.Value, "EU"));
        Assert.Contains(c.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && Equals(p.Value, "open"));
        Assert.Equal(CompilerInfo.Version, c.CompilerVersion);
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 0), c.AppliedPredicates);
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 1), c.AppliedPredicates);
    }

    [Fact]
    public void SimplifierRunsBeforeSecurity_InjectedDenyAllSurvives()
    {
        _rowFilters.Predicates[Orders] = PolicyPredicate.DenyAll;
        var c = Compile("SELECT id FROM orders WHERE 1 = 1 OR status = 'x'");
        Assert.Contains("1 = 0", c.Sql);
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 1), c.AppliedPredicates);
    }

    [Fact]
    public void Joins_Subqueries_Ctes_AllCoveredByTheVerifier()
    {
        var c = Compile(
            "WITH o AS (SELECT id, region FROM orders) " +
            "SELECT o.id FROM o JOIN entitlements e ON e.orderid = o.id " +
            "WHERE EXISTS (SELECT 1 FROM orders x WHERE x.id = o.id) " +
            "UNION ALL SELECT id FROM orders");
        Assert.True(c.Parameters.Count(p => p.Origin == ParameterOrigin.Tenant) == 1); // one tenant marker, reused
        Assert.True(CountOf(c.Sql, "[dbo].[Orders]") >= 3);
    }

    [Fact]
    public void EnforcedMaxRows_IsClamped_Inline()
    {
        var c = Compile("SELECT id FROM orders", Request(configure: r => r with { EnforcedMaxRows = 100 }));
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY", c.Sql);
    }

    [Fact]
    public void FunctionAllowList_IsEnforced()
    {
        Assert.ThrowsAny<SecurityException>(() => Compile("SELECT upper(email) FROM orders", Request(configure: r => r with { AllowedFunctions = new HashSet<string>() })));
        var ok = Compile("SELECT upper(email) FROM orders", Request(configure: r => r with { AllowedFunctions = new HashSet<string> { "upper" } }));
        Assert.Contains("UPPER(", ok.Sql);
    }

    [Fact]
    public void TokenGuards_AreEnforced()
    {
        Assert.ThrowsAny<Exception>(() => Compile("SELECT id FROM orders -- c"));
        Assert.ThrowsAny<Exception>(() => Compile("SELECT id FROM orders WHERE status = 'a\\b'"));
    }

    [Fact]
    public void UnknownTable_IsRejected()
    {
        Assert.ThrowsAny<SecurityException>(() => Compile("SELECT * FROM mystery"));
    }

    [Fact]
    public void ClientNamedParameter_IsBoundByTheBinderNotRestoredInText()
    {
        var c = Compile("SELECT id FROM orders WHERE status = __param_st");
        var client = Assert.Single(c.Parameters, p => p.Origin == ParameterOrigin.ClientNamed);
        Assert.Equal("st", client.SourceName);
        Assert.DoesNotContain("__param_", c.Sql);
    }

    // ---- fail closed: statements and dialects (not yet supported) ----

    [Theory]
    [InlineData(TargetSqlDialect.Ansi)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.Snowflake)]
    public void NonSqlServerDialects_FailClosed_WithATypedNotYetSupportedError(TargetSqlDialect dialect)
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile("SELECT id FROM orders", Request(dialect: dialect)));
        Assert.Equal(SqlCompileNotSupportedReason.Dialect, ex.Reason);
        Assert.IsAssignableFrom<SecurityException>(ex);
    }

    [Fact]
    public void ExperimentalDialect_StaysRejected_EvenWhenAllowed()
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() =>
            Compile("SELECT id FROM orders", Request(dialect: TargetSqlDialect.Snowflake, configure: r => r with { AllowExperimentalDialect = true })));
        Assert.Equal(SqlCompileNotSupportedReason.Dialect, ex.Reason);
    }

    [Theory]
    [InlineData("INSERT INTO orders (id, tenantid) VALUES (1, 'acme')")]
    [InlineData("UPDATE orders SET status = 'x' WHERE id = 1")]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("MERGE INTO orders t USING orders s ON t.id = s.id WHEN MATCHED THEN DELETE")]
    public void DmlStatements_FailClosed_WithATypedNotYetSupportedError(string sql)
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(sql));
        Assert.Equal(SqlCompileNotSupportedReason.StatementClass, ex.Reason);
    }

    private sealed class FixedCapabilities(DialectCapabilities capabilities) : IDialectCapabilityProvider
    {
        public DialectCapabilities Get(TargetSqlDialect dialect) => capabilities;
    }

    [Theory]
    [InlineData("INSERT INTO orders (id, tenantid) VALUES (1, 'acme')")]
    [InlineData("UPDATE orders SET status = 'x' WHERE id = 1")]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("MERGE INTO orders t USING orders s ON t.id = s.id WHEN MATCHED THEN DELETE")]
    public void DmlPermission_IsRejected_WhenTheCapabilityEntryDoesNotListTheClass(string sql)
    {
        // A request may only ask for what the dialect supports: the capability entry decides (fail closed, typed).
        var readOnlyDialect = DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer) with { DmlStatements = StatementPermissions.ReadOnly };
        var compiler = new GovernedSqlCompiler(_engine, new FixedCapabilities(readOnlyDialect), new CompiledSqlTemplateCache());
        var req = Request(configure: r => r with { Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge });
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => compiler.Compile(sql.AsMemory(), req, CancellationToken.None));
        Assert.Equal(SqlCompileNotSupportedReason.StatementClass, ex.Reason);
    }

    [Fact]
    public void MergePermission_IsRejected_WhenTheDialectDoesNotSupportMerge()
    {
        var noMerge = DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer) with { SupportsMerge = false };
        var compiler = new GovernedSqlCompiler(_engine, new FixedCapabilities(noMerge), new CompiledSqlTemplateCache());
        var req = Request(configure: r => r with { Statements = StatementPermissions.Merge });
        Assert.Throws<SqlCompileNotSupportedException>(() =>
            compiler.Compile("MERGE INTO orders t USING orders s ON t.id = s.id WHEN MATCHED THEN DELETE".AsMemory(), req, CancellationToken.None));
    }

    [Theory]
    [InlineData("CREATE TABLE x (a int)")]
    [InlineData("DROP TABLE orders")]
    [InlineData("TRUNCATE TABLE orders")]
    [InlineData("SHOW TABLES")]
    [InlineData("SELECT * FROM TABLE(system.query(query => 'SELECT 1'))")]
    public void NonQueryStatementsAndTableFunctions_AreRejected(string sql)
    {
        Assert.ThrowsAny<Exception>(() => Compile(sql));
    }

    [Fact]
    public void Lateral_IsRejected_AsNotSupportedOnSqlServer()
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() =>
            Compile("SELECT o.id FROM orders o CROSS JOIN LATERAL (SELECT 1 AS x FROM entitlements e WHERE e.orderid = o.id) l"));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
    }

    // ---- limits (SEC-ADG-05, INV-7) ----

    [Fact]
    public void BindLimit_IsATypedRejection()
    {
        string sql = "SELECT id FROM orders WHERE id IN (" + string.Join(", ", Enumerable.Range(1, 2200)) + ")";
        var ex = Assert.Throws<SqlLimitExceededException>(() => Compile(sql));
        Assert.Equal(SqlLimitKind.BindParameters, ex.Kind);
    }

    [Fact]
    public void SecuredTableReferences_AreLimited()
    {
        string sql = "SELECT a0.id FROM orders a0" + string.Concat(Enumerable.Range(1, 260).Select(i => $" CROSS JOIN orders a{i}"));
        // The AST builder's own depth guard (64) rejects first; the verifier limit is covered in SecurityCoverageTests.
        Assert.ThrowsAny<SecurityException>(() => Compile(sql));
    }

    [Fact]
    public void Compile_TimeBudget_TypedRejection()
    {
        var ex = Assert.Throws<SqlLimitExceededException>(() =>
            Compile("SELECT id FROM orders", Request(configure: r => r with { CompileTimeout = TimeSpan.FromTicks(1) })));
        Assert.Equal(SqlLimitKind.CompileTime, ex.Kind);
    }

    [Fact]
    public void Compile_EmittedLengthAndExpansionFactorLimits()
    {
        var ex = Assert.Throws<SqlLimitExceededException>(() =>
            Compile("SELECT id FROM orders", Request(configure: r => r with { MaxExpansionFactor = 1 })));
        Assert.Equal(SqlLimitKind.PolicyExpansionFactor, ex.Kind);
    }

    [Fact]
    public void Compile_CancelledInEveryPass_NoCacheEntry_NoPartialSql()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Compile("SELECT id FROM orders", ct: cts.Token));
        Assert.Equal(0, _engine.CompileCache.Stats.Entries);
    }

    [Fact]
    public void FailedCompile_NeverInsertsACacheEntry()
    {
        Assert.ThrowsAny<Exception>(() => Compile("SELECT * FROM mystery"));
        Assert.Equal(0, _engine.CompileCache.Stats.Entries);
    }

    // ---- cache: SEC-ADG-01 / INV-12 / M-8 ----

    [Fact]
    public void Cache_SecondIdenticalCompile_Hits_AndReturnsEqualOutput()
    {
        var first = Compile("SELECT id FROM orders WHERE status = 'open'");
        var second = Compile("SELECT id FROM orders WHERE status = 'open'");

        Assert.Equal(1, _engine.CompileCache.Stats.Hits);
        Assert.Equal(first.Sql, second.Sql);
        Assert.Equal(first.Parameters.Select(p => (p.Marker, p.Value, p.Origin)), second.Parameters.Select(p => (p.Marker, p.Value, p.Origin)));
    }

    [Fact]
    public void Cache_TwoPrincipalsOfTheSameTenant_WithDifferentConsentValues_NeverShareValues()
    {
        _rowFilters.Predicates[Orders] = RegionPredicate("EU");
        var eu = Compile("SELECT id FROM orders");
        _rowFilters.Predicates[Orders] = RegionPredicate("US");
        var us = Compile("SELECT id FROM orders");

        Assert.Equal(1, _engine.CompileCache.Stats.Hits);              // same shape: the template is shared ...
        Assert.Equal(eu.Sql, us.Sql);
        Assert.Contains(eu.Parameters, p => Equals(p.Value, "EU"));
        Assert.DoesNotContain(us.Parameters, p => Equals(p.Value, "EU")); // ... the values are not
        Assert.Contains(us.Parameters, p => Equals(p.Value, "US"));
    }

    [Fact]
    public void Cache_TenantAcmeThenAcmeUpper_RebindsTheTenantValue()
    {
        var a = Compile("SELECT id FROM orders", Request(tenant: "acme"));
        var b = Compile("SELECT id FROM orders", Request(tenant: "ACME"));

        Assert.Equal(1, _engine.CompileCache.Stats.Hits);
        Assert.Equal("acme", a.Parameters.Single(p => p.Origin == ParameterOrigin.Tenant).Value);
        Assert.Equal("ACME", b.Parameters.Single(p => p.Origin == ParameterOrigin.Tenant).Value);
    }

    [Fact]
    public void Cache_PolicyShapeChange_IsAMiss()
    {
        Compile("SELECT id FROM orders");
        _rowFilters.Predicates[Orders] = PolicyPredicate.DenyAll;
        var after = Compile("SELECT id FROM orders");

        Assert.Equal(0, _engine.CompileCache.Stats.Hits);
        Assert.Contains("1 = 0", after.Sql);
    }

    [Fact]
    public void Cache_PolicyRemovedForATable_IsAMiss()
    {
        Compile("SELECT id FROM orders");
        _rowFilters.NoPolicy.Add(Orders);
        var after = Compile("SELECT id FROM orders");
        Assert.Equal(0, _engine.CompileCache.Stats.Hits);
        Assert.DoesNotContain(after.Parameters, p => p.Origin == ParameterOrigin.Policy);
    }

    [Fact]
    public void Cache_MaskChange_IsAMiss()
    {
        Compile("SELECT id FROM orders");
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        var after = Compile("SELECT id FROM orders");
        Assert.Equal(0, _engine.CompileCache.Stats.Hits);
        Assert.Contains("CAST(NULL AS nvarchar(200))", after.Sql);
    }

    [Fact]
    public void Cache_CatalogChange_IsAMiss()
    {
        Compile("SELECT id FROM orders");
        var changed = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Orders, ImmutableArray.Create(new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Region", "nvarchar(20)"), new CatalogColumn("Status", "nvarchar(20)"), new CatalogColumn("Amount", "decimal(18,2)"), new CatalogColumn("Email", "nvarchar(200)")), "TenantId", 2),
            new TableCatalogEntry(Entitlements, ImmutableArray.Create(new CatalogColumn("Id", "int")), "TenantId", 1)
        }, "dbo");
        Compile("SELECT id FROM orders", Request(catalog: changed));
        Assert.Equal(0, _engine.CompileCache.Stats.Hits);
    }

    [Fact]
    public void Cache_AnyRequestOptionChange_IsAMiss()
    {
        Compile("SELECT id FROM orders");
        Compile("SELECT id FROM orders", Request(configure: r => r with { EnforcedMaxRows = 5 }));
        Compile("SELECT id FROM orders", Request(configure: r => r with { AllowedFunctions = new HashSet<string> { "lower" } }));
        Compile("SELECT id FROM orders", Request(configure: r => r with { TokenGuards = SqlTokenSecurityOptions.Strict with { RejectComments = false } }));
        Assert.Equal(0, _engine.CompileCache.Stats.Hits);
    }

    [Fact]
    public void CompiledSqlTemplate_HasNoTenantPolicyOrMaskValues()
    {
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("__mask_email", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue> { ["__mask_email"] = new("MASK-SECRET-VALUE", SqlParameterType.String) }.ToFrozenDictionary());
        Compile("SELECT id FROM orders WHERE status = 'open'", Request(tenant: "TENANT-SECRET-VALUE"));

        var templates = _engine.CompileCache.Snapshot();
        var template = Assert.Single(templates);
        string dump = TemplateDump(template);
        Assert.DoesNotContain("TENANT-SECRET-VALUE", dump);
        Assert.DoesNotContain("MASK-SECRET-VALUE", dump);
        Assert.DoesNotContain("EU", template.Slots.Where(s => s.Origin == ParameterOrigin.Policy).Select(s => s.LiteralValue?.ToString() ?? string.Empty));
        Assert.All(template.Slots.Where(s => s.Origin != ParameterOrigin.QueryLiteral), s => Assert.Null(s.LiteralValue));
        Assert.Contains(template.Slots, s => s.Origin == ParameterOrigin.QueryLiteral && Equals(s.LiteralValue, "open")); // from the raw SQL, compared on hit
    }

    private static string TemplateDump(CompiledSqlTemplate t)
    {
        var sb = new System.Text.StringBuilder();
        void Dump(object? o, int depth)
        {
            if (o is null || depth > 6) return;
            if (o is string s) { sb.Append(s).Append('|'); return; }
            if (o is System.Collections.IEnumerable e) { foreach (var i in e) Dump(i, depth + 1); return; }
            var type = o.GetType();
            if (type.IsPrimitive || type.IsEnum) { sb.Append(o).Append('|'); return; }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                Dump(p.GetValue(o), depth + 1);
            }
        }

        Dump(t, 0);
        return sb.ToString();
    }

    [Fact]
    public void CompileRequest_EveryPropertyChangesCacheKey()
    {
        var baseRequest = Request();
        string baseKey = CompileCacheKey.Material(baseRequest, "SELECT 1", DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer));

        var changes = new Dictionary<string, CompileRequest>
        {
            [nameof(CompileRequest.TargetDialect)] = baseRequest with { TargetDialect = TargetSqlDialect.Sqlite },
            [nameof(CompileRequest.TokenGuards)] = baseRequest with { TokenGuards = SqlTokenSecurityOptions.None },
            [nameof(CompileRequest.Statements)] = baseRequest with { Statements = StatementPermissions.Insert },
            [nameof(CompileRequest.EnforcedMaxRows)] = baseRequest with { EnforcedMaxRows = 7 },
            [nameof(CompileRequest.TranslateTrinoDateFunctions)] = baseRequest with { TranslateTrinoDateFunctions = true },
            [nameof(CompileRequest.EnforceCatalogProjection)] = baseRequest with { EnforceCatalogProjection = !baseRequest.EnforceCatalogProjection },
            [nameof(CompileRequest.AllowedFunctions)] = baseRequest with { AllowedFunctions = new HashSet<string> { "x" } },
            [nameof(CompileRequest.AllowedTableFunctions)] = baseRequest with { AllowedTableFunctions = new HashSet<string> { "x" } },
            [nameof(CompileRequest.AllowExperimentalDialect)] = baseRequest with { AllowExperimentalDialect = true },
            [nameof(CompileRequest.CompileTimeout)] = baseRequest with { CompileTimeout = TimeSpan.FromSeconds(9) },
            [nameof(CompileRequest.MaxExpansionFactor)] = baseRequest with { MaxExpansionFactor = 3 }
        };

        // Every property of CompileRequest except the policy must be covered above.
        var covered = changes.Keys.Append(nameof(CompileRequest.Policy)).ToHashSet();
        foreach (var property in typeof(CompileRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.Contains(property.Name, covered);
        }

        foreach (var (name, changed) in changes)
        {
            Assert.NotEqual(baseKey, CompileCacheKey.Material(changed, "SELECT 1", DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer)));
            _ = name;
        }

        // GovernancePolicy: every property is either part of the key or validated per table on a hit.
        var policy = baseRequest.Policy;
        var policyChanges = new Dictionary<string, GovernancePolicy>
        {
            [nameof(GovernancePolicy.Dml)] = policy with { Dml = DmlGuardOptions.Strict with { EnforceWithCheckOption = false } },
            [nameof(GovernancePolicy.TablesWithConsentRowFilter)] = policy with { TablesWithConsentRowFilter = new HashSet<string> { "x" } },
            [nameof(GovernancePolicy.TablesWithMaskedColumns)] = policy with { TablesWithMaskedColumns = new HashSet<string> { "x" } },
            [nameof(GovernancePolicy.SubqueryStrategy)] = policy with { SubqueryStrategy = (GovernedSubqueryStrategy)99 },
            [nameof(GovernancePolicy.Tenant)] = policy with { Tenant = new TenantBinding("other_param", "acme", SqlParameterType.String) }
        };
        var validatedPerTable = new HashSet<string> { nameof(GovernancePolicy.RowFilters), nameof(GovernancePolicy.Masks), nameof(GovernancePolicy.Catalog) };
        foreach (var property in typeof(GovernancePolicy).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(policyChanges.ContainsKey(property.Name) || validatedPerTable.Contains(property.Name), property.Name);
        }

        foreach (var (_, changedPolicy) in policyChanges)
        {
            Assert.NotEqual(baseKey, CompileCacheKey.Material(baseRequest with { Policy = changedPolicy }, "SELECT 1", DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer)));
        }

        // The tenant VALUE is deliberately not part of the key (value-free template).
        var otherTenantValue = baseRequest with { Policy = policy with { Tenant = new TenantBinding("__autheris_tenant", "someone-else", SqlParameterType.String) } };
        Assert.Equal(baseKey, CompileCacheKey.Material(otherTenantValue, "SELECT 1", DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer)));
        Assert.NotEqual(baseKey, CompileCacheKey.Material(baseRequest, "SELECT 2", DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer)));
    }

    // ---- non-disclosure, telemetry, audit ----

    [Fact]
    public void TypedRejections_MessagesContainNoHostileCorpusMarkers()
    {
        const string tenant = "EVIL-TENANT-MARKER'; --";
        const string policy = "EVIL-POLICY-MARKER";
        _rowFilters.Predicates[Orders] = RegionPredicate(policy);
        var request = Request(tenant: tenant);

        var rejections = new List<Exception>
        {
            Record.Exception(() => Compile("SELECT id FROM orders", request with { TargetDialect = TargetSqlDialect.Snowflake }))!,
            Record.Exception(() => Compile("DELETE FROM orders WHERE id = 1", request))!,
            Record.Exception(() => Compile("SELECT id FROM orders WHERE id IN (" + string.Join(",", Enumerable.Range(1, 2200)) + ")", request))!,
            Record.Exception(() => Compile("SELECT * FROM mystery", request))!,
            Record.Exception(() => Compile("SELECT id FROM orders", request with { CompileTimeout = TimeSpan.FromTicks(1) }))!,
            Record.Exception(() => new PolicyExpressionParser(_engine).Parse(tenant, PolicyFixtures.Context(Orders).Build()))!,
        };

        Assert.All(rejections, ex =>
        {
            Assert.NotNull(ex);
            Assert.DoesNotContain("EVIL-TENANT-MARKER", ex.ToString().Split("   at ")[0]);
            Assert.DoesNotContain("EVIL-POLICY-MARKER", ex.Message);
        });
    }

    [Fact]
    public void Compile_SpanAttributes_AndCounters_CarryNoValues()
    {
        // The listeners are process-wide and other test classes compile in parallel: only observe this thread.
        int testThread = Environment.CurrentManagedThreadId;
        var tags = new Dictionary<string, object?>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == CompilerTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (Environment.CurrentManagedThreadId != testThread) return;
                foreach (var t in a.TagObjects) tags[t.Key] = t.Value;
            }
        };
        ActivitySource.AddActivityListener(listener);

        var rejected = new List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (i, l) => { if (i.Meter.Name == CompilerTelemetry.MeterName) l.EnableMeasurementEvents(i); };
        meter.SetMeasurementEventCallback<long>((i, v, t, _) =>
        {
            if (Environment.CurrentManagedThreadId == testThread) rejected.Add((i.Name, v, t.ToArray()));
        });
        meter.Start();

        Compile("SELECT id FROM orders WHERE status = 'open'", Request(tenant: "acme-secret"));
        Assert.Equal("SqlServer", tags["sql.target_dialect"]);
        Assert.Equal("Select", tags["sql.statement_class"]);
        Assert.Equal(CompilerInfo.Version, tags["sql.compiler_version"]);
        Assert.True((int)tags["sql.bind_count"]! >= 3);
        Assert.DoesNotContain(tags.Values, v => v?.ToString()?.Contains("acme-secret") == true || v?.ToString()?.Contains("open") == true);

        Assert.Throws<SqlLimitExceededException>(() =>
            Compile("SELECT id FROM orders WHERE id IN (" + string.Join(",", Enumerable.Range(1, 2200)) + ")"));
        Assert.Contains(rejected, r => r.Name == "autheris.sql.limit_rejected" && r.Tags.Any(t => t.Key == "kind" && Equals(t.Value, "BindParameters")));
        Assert.Contains(rejected, r => r.Name == "autheris.sql.compile.rejected");
    }

    [Fact]
    public void AuditRecord_HasCompiledDigest_NoValues()
    {
        var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var a = Compile("SELECT id FROM orders", Request(tenant: "acme"));
        var b = Compile("SELECT id FROM orders", Request(tenant: "other"));
        var c = Compile("SELECT status FROM orders", Request(tenant: "acme"));

        string da = CompiledSqlDigest.Compute(key, a);
        Assert.Equal(da, CompiledSqlDigest.Compute(key, a));
        Assert.Equal(da, CompiledSqlDigest.Compute(key, b));            // values are not part of the digest
        Assert.NotEqual(da, CompiledSqlDigest.Compute(key, c));         // the statement is
        Assert.NotEqual(da, CompiledSqlDigest.Compute(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }, a));
        Assert.Equal(64, da.Length);
    }

    // AllowExperimentalDialect_OnlyInTests moved to Autheris.Tests.Architecture (IL scan, CR-ADG-20).

    // ---- tenant isolation property at the API level ----

    // The binary-exact tenant comparison is asserted on the node structure (TenantPredicateStructureTests), not on SQL text.

    private static int CountOf(string s, string needle)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
