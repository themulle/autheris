using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Classification;

[Trait("Category", "Security")]
public sealed class MaskFunctionTests
{
    private const string PrimaryKeyRef = "kv://keys/classification-master";
    private const string MissingKeyRef = "kv://keys/missing";

    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly byte[] MasterSecretA = Enumerable.Range(1, 32).Select(static i => (byte)i).ToArray();

    private static SqlDataMaskingProvider CreateSqlProvider(
        IKeyVaultSecretProvider? secrets = null,
        bool preventInDbHmacExposure = false)
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions
            {
                PreventInDbHmacKeyExposure = preventInDbHmacExposure,
                HmacSecretKeyVaultRef = PrimaryKeyRef
            }
        };
        return new SqlDataMaskingProvider(Options.Create(options), secrets);
    }

    private static InMemorySecretProvider DefaultSecrets() =>
        new(new Dictionary<string, byte[]>(StringComparer.Ordinal) { [PrimaryKeyRef] = MasterSecretA });

    public static IEnumerable<object[]> AllFunctionsAndDialects()
    {
        (MaskFunction Fn, string DataType, string ParamsJson)[] cases =
        [
            (MaskFunction.Nullify, "varchar(100)", "{}"),
            (MaskFunction.Redact, "varchar(100)", """{"replacement":"CANARY_SECRET_MASK"}"""),
            (MaskFunction.TypeSafeRedact, "int", """{"replacement":"CANARY_SECRET_MASK"}"""),
            (MaskFunction.Partial, "varchar(100)", """{"keep_first":2,"keep_last":3,"mask_char":"#"}"""),
            (MaskFunction.Hash, "varchar(100)", $$"""{"key_ref":"{{PrimaryKeyRef}}"}"""),
            (MaskFunction.Tokenize, "varchar(100)", $$"""{"key_ref":"{{PrimaryKeyRef}}","domain":"EMAIL_SECRET_DOM"}"""),
            (MaskFunction.TruncateDate, "timestamp", """{"unit":"MONTH"}"""),
            (MaskFunction.BucketNumber, "decimal(12,2)", """{"width":25}"""),
            (MaskFunction.RoundNumber, "decimal(12,2)", """{"digits":2}"""),
            (MaskFunction.EmailDomain, "varchar(100)", "{}"),
            (MaskFunction.GeoJitter, "decimal(10,4)", """{"decimals":3}"""),
        ];

        TargetSqlDialect[] dialects =
        [
            TargetSqlDialect.PostgreSql,
            TargetSqlDialect.SqlServer,
            TargetSqlDialect.Oracle,
            TargetSqlDialect.Databricks,
            TargetSqlDialect.DuckDb,
            TargetSqlDialect.Ansi,
        ];

        foreach (var c in cases)
        {
            foreach (var d in dialects)
            {
                yield return [c.Fn, c.DataType, c.ParamsJson, d];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllFunctionsAndDialects))]
    public void Each_function_renders_bound_parameters_only(
        MaskFunction function,
        string dataType,
        string paramsJson,
        TargetSqlDialect dialect)
    {
        var sqlProvider = CreateSqlProvider(DefaultSecrets());
        var resolved = sqlProvider.ResolveClassificationMask(
            function,
            paramsJson,
            "sensitive_col",
            dataType,
            dialect,
            TenantA);

        resolved.IsDenied.ShouldBeFalse();
        resolved.Spec.ShouldNotBeNull();

        string sqlDataType = dialect switch
        {
            TargetSqlDialect.SqlServer when dataType == "timestamp" => "datetime2",
            TargetSqlDialect.Oracle when dataType == "varchar(100)" => "varchar2(100)",
            TargetSqlDialect.Oracle when dataType == "int" => "number(10)",
            TargetSqlDialect.Oracle when dataType.StartsWith("decimal", StringComparison.Ordinal) => "number(12,2)",
            TargetSqlDialect.Databricks when dataType == "varchar(100)" => "string",
            _ => dataType
        };

        var rendered = new MaskSpecRenderer().Render(resolved.Spec, "sensitive_col", sqlDataType, dialect);
        rendered.Sql.ShouldNotBeNullOrWhiteSpace();
        rendered.Sql.ShouldNotContain("CANARY_SECRET_MASK", Case.Sensitive);
        rendered.Sql.ShouldNotContain("EMAIL_SECRET_DOM", Case.Sensitive);
        rendered.Sql.ShouldNotContain(PrimaryKeyRef, Case.Sensitive);
        rendered.Sql.ShouldNotContain("#", Case.Sensitive);

        if (function is MaskFunction.Hash or MaskFunction.Tokenize && dialect is not (TargetSqlDialect.PostgreSql or TargetSqlDialect.SqlServer))
        {
            // Non-in-DB-HMAC dialects fail closed to Redact and drop all key material.
            resolved.Spec.Kind.ShouldBe(MaskKind.Redact);
            resolved.Spec.Arguments.HmacKey.ShouldBeNull();
            resolved.Spec.Arguments.HmacKeyOuter.ShouldBeNull();
        }
        else if (function is MaskFunction.Redact or MaskFunction.Partial or MaskFunction.Hash or MaskFunction.Tokenize or MaskFunction.BucketNumber or MaskFunction.RoundNumber)
        {
            rendered.Parameters.ShouldNotBeEmpty();
            foreach (var marker in rendered.Parameters.Keys)
            {
                rendered.Sql.ShouldContain(marker);
            }
        }
    }

    [Fact]
    public void Truncate_date_month()
    {
        var sqlProvider = CreateSqlProvider(DefaultSecrets());
        var monthRes = sqlProvider.ResolveClassificationMask("TRUNCATE_DATE", """{"unit":"MONTH"}""", "created_at", "timestamptz", TargetSqlDialect.PostgreSql, TenantA);
        var yearRes = sqlProvider.ResolveClassificationMask("TRUNCATE_DATE", """{"unit":"YEAR"}""", "created_at", "timestamptz", TargetSqlDialect.PostgreSql, TenantA);
        var dayRes = sqlProvider.ResolveClassificationMask("TRUNCATE_DATE", """{"unit":"DAY"}""", "created_at", "timestamptz", TargetSqlDialect.PostgreSql, TenantA);

        var dt = new DateTime(2025, 7, 19, 14, 23, 5, DateTimeKind.Utc);
        var dOnly = new DateOnly(2025, 7, 19);

        var colMask = new ColumnMaskingProvider();
        colMask.EvaluateClassificationMask(monthRes.Spec!, dt, TenantA).ShouldBe(new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        colMask.EvaluateClassificationMask(yearRes.Spec!, dt, TenantA).ShouldBe(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        colMask.EvaluateClassificationMask(dayRes.Spec!, dt, TenantA).ShouldBe(new DateTime(2025, 7, 19, 0, 0, 0, DateTimeKind.Utc));
        colMask.EvaluateClassificationMask(monthRes.Spec!, dOnly, TenantA).ShouldBe(new DateOnly(2025, 7, 1));

        var pgRendered = new MaskSpecRenderer().Render(monthRes.Spec!, "created_at", "timestamptz", TargetSqlDialect.PostgreSql);
        pgRendered.Sql.ShouldContain("DATE_TRUNC('month',");

        // Reject granularity finer than DAY
        sqlProvider.ResolveClassificationMask("TRUNCATE_DATE", """{"unit":"HOUR"}""", "created_at", "timestamptz", TargetSqlDialect.PostgreSql, TenantA)
            .IsDenied.ShouldBeTrue();
    }

    [Fact]
    public void Bucket_number_width()
    {
        var sqlProvider = CreateSqlProvider(DefaultSecrets());
        var resolved = sqlProvider.ResolveClassificationMask("BUCKET_NUMBER", """{"width":10}""", "salary", "integer", TargetSqlDialect.PostgreSql, TenantA);
        resolved.IsDenied.ShouldBeFalse();

        var colMask = new ColumnMaskingProvider();
        colMask.EvaluateClassificationMask(resolved.Spec!, 123, TenantA).ShouldBe(120);
        colMask.EvaluateClassificationMask(resolved.Spec!, 100, TenantA).ShouldBe(100);
        colMask.EvaluateClassificationMask(resolved.Spec!, -3, TenantA).ShouldBe(-10);
        colMask.EvaluateClassificationMask(resolved.Spec!, 129.99m, TenantA).ShouldBe(120m);

        // Non-positive width must fail closed.
        sqlProvider.ResolveClassificationMask("BUCKET_NUMBER", """{"width":0}""", "salary", "integer", TargetSqlDialect.PostgreSql, TenantA)
            .IsDenied.ShouldBeTrue();
        sqlProvider.ResolveClassificationMask("BUCKET_NUMBER", """{"width":-10}""", "salary", "integer", TargetSqlDialect.PostgreSql, TenantA)
            .IsDenied.ShouldBeTrue();
    }

    [Fact]
    public void Email_domain_keeps_domain_only()
    {
        var sqlProvider = CreateSqlProvider(DefaultSecrets());
        var resolved = sqlProvider.ResolveClassificationMask("EMAIL_DOMAIN", "{}", "email", "varchar(200)", TargetSqlDialect.PostgreSql, TenantA);
        resolved.IsDenied.ShouldBeFalse();

        var colMask = new ColumnMaskingProvider();
        colMask.EvaluateClassificationMask(resolved.Spec!, "alice@example.com", TenantA).ShouldBe("***@example.com");
        colMask.EvaluateClassificationMask(resolved.Spec!, "a@b.io", TenantA).ShouldBe("***@b.io");
        colMask.EvaluateClassificationMask(resolved.Spec!, "invalid-no-at", TenantA).ShouldBe("***@***");
        colMask.EvaluateClassificationMask(resolved.Spec!, "@example.com", TenantA).ShouldBe("***@***");
        colMask.EvaluateClassificationMask(resolved.Spec!, "alice@", TenantA).ShouldBe("***@***");
        colMask.EvaluateClassificationMask(resolved.Spec!, string.Empty, TenantA).ShouldBe("***@***");
        colMask.EvaluateClassificationMask(resolved.Spec!, null, TenantA).ShouldBeNull();
    }

    [Fact]
    public void Partial_rejects_more_than_16_revealed()
    {
        const string tooMany = """{"keep_first":10,"keep_last":10,"mask_char":"*"}""";
        Should.Throw<ArgumentException>(() => MaskFunctionCatalog.ParseAndValidate(MaskFunction.Partial, tooMany));

        var sqlProvider = CreateSqlProvider(DefaultSecrets());
        var denied = sqlProvider.ResolveClassificationMask("PARTIAL", tooMany, "ssn", "varchar(50)", TargetSqlDialect.PostgreSql, TenantA);
        denied.IsDenied.ShouldBeTrue();
        denied.DenialReason.ShouldBe("MASK_INVALID_PARAMETERS");
        denied.Spec.ShouldBeNull();

        // Quote/backslash mask_char is rejected as well.
        Should.Throw<ArgumentException>(() => MaskFunctionCatalog.ParseAndValidate(MaskFunction.Partial, """{"keep_first":2,"keep_last":2,"mask_char":"'"}"""));
        Should.Throw<ArgumentException>(() => MaskFunctionCatalog.ParseAndValidate(MaskFunction.Partial, """{"keep_first":2,"keep_last":2,"mask_char":"\\"}"""));

        // Exact boundary 16 (8 + 8) succeeds.
        var ok = sqlProvider.ResolveClassificationMask("PARTIAL", """{"keep_first":8,"keep_last":8,"mask_char":"*"}""", "ssn", "varchar(50)", TargetSqlDialect.PostgreSql, TenantA);
        ok.IsDenied.ShouldBeFalse();
    }

    [Theory]
    [InlineData(MaskFunction.RoundNumber, "varchar(100)", """{"digits":1}""", "")]
    [InlineData(MaskFunction.BucketNumber, "varchar(100)", """{"width":10}""", "")]
    [InlineData(MaskFunction.TruncateDate, "varchar(100)", """{"unit":"MONTH"}""", "")]
    [InlineData(MaskFunction.TruncateDate, "int", """{"unit":"MONTH"}""", "CAST(NULL AS int)")]
    [InlineData(MaskFunction.TruncateDate, "time", """{"unit":"DAY"}""", "CAST(NULL AS time)")]
    [InlineData(MaskFunction.EmailDomain, "int", "{}", "CAST(NULL AS int)")]
    public void Type_mismatch_falls_back_to_type_safe_redact(
        MaskFunction requested,
        string dataType,
        string paramsJson,
        string expectedSqlFragment)
    {
        long observedFallbackIncrements = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "autheris_mask_type_fallback_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
        {
            observedFallbackIncrements += measurement;
        });
        listener.Start();

        var sqlProvider = CreateSqlProvider(DefaultSecrets());
        var resolved = sqlProvider.ResolveClassificationMask(requested, paramsJson, "col", dataType, TargetSqlDialect.PostgreSql, TenantA);

        resolved.IsDenied.ShouldBeFalse();
        resolved.TypeFallbackApplied.ShouldBeTrue();
        resolved.EffectiveFunction.ShouldBe(MaskFunction.TypeSafeRedact);
        resolved.Spec.ShouldNotBeNull();
        resolved.Spec.Kind.ShouldBe(MaskKind.Redact);
        observedFallbackIncrements.ShouldBeGreaterThanOrEqualTo(1);

        var rendered = new MaskSpecRenderer().Render(resolved.Spec, "col", dataType, TargetSqlDialect.PostgreSql);
        if (!string.IsNullOrEmpty(expectedSqlFragment))
        {
            rendered.Sql.ShouldContain(expectedSqlFragment);
        }
        else
        {
            rendered.Parameters.Values.ShouldContain("***");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AES_RAW_DECRYPT")]
    [InlineData("CUSTOM_UDF")]
    public void Unknown_mask_function_denies_column(string? functionCode)
    {
        var sqlProvider = CreateSqlProvider(DefaultSecrets());
        var resolved = sqlProvider.ResolveClassificationMask(functionCode, "{}", "secret", "varchar(100)", TargetSqlDialect.PostgreSql, TenantA);

        resolved.IsDenied.ShouldBeTrue();
        resolved.DenialReason.ShouldBe("MASK_UNKNOWN_FUNCTION");
        resolved.Spec.ShouldBeNull();
    }

    [Fact]
    public void Hash_key_derived_per_tenant_differs_across_tenants()
    {
        var sqlProvider = CreateSqlProvider(DefaultSecrets());
        var resA = sqlProvider.ResolveClassificationMask("HASH", $$"""{"key_ref":"{{PrimaryKeyRef}}"}""", "ssn", "varchar(50)", TargetSqlDialect.PostgreSql, TenantA);
        var resB = sqlProvider.ResolveClassificationMask("HASH", $$"""{"key_ref":"{{PrimaryKeyRef}}"}""", "ssn", "varchar(50)", TargetSqlDialect.PostgreSql, TenantB);
        var tokA = sqlProvider.ResolveClassificationMask("TOKENIZE", $$"""{"key_ref":"{{PrimaryKeyRef}}","domain":"SSN"}""", "ssn", "varchar(50)", TargetSqlDialect.PostgreSql, TenantA);
        var tokB = sqlProvider.ResolveClassificationMask("TOKENIZE", $$"""{"key_ref":"{{PrimaryKeyRef}}","domain":"SSN"}""", "ssn", "varchar(50)", TargetSqlDialect.PostgreSql, TenantB);

        var colMask = new ColumnMaskingProvider();
        var hashA1 = colMask.EvaluateClassificationMask(resA.Spec!, "123-45-6789", TenantA, PrimaryKeyRef, MasterSecretA) as string;
        var hashA2 = colMask.EvaluateClassificationMask(resA.Spec!, "123-45-6789", TenantA, PrimaryKeyRef, MasterSecretA) as string;
        var hashB = colMask.EvaluateClassificationMask(resB.Spec!, "123-45-6789", TenantB, PrimaryKeyRef, MasterSecretA) as string;

        hashA1.ShouldNotBeNullOrWhiteSpace();
        hashA1.ShouldBe(hashA2);
        hashA1.ShouldNotBe(hashB);

        var tokenA = colMask.EvaluateClassificationMask(tokA.Spec!, "123-45-6789", TenantA, PrimaryKeyRef, MasterSecretA) as string;
        var tokenB = colMask.EvaluateClassificationMask(tokB.Spec!, "123-45-6789", TenantB, PrimaryKeyRef, MasterSecretA) as string;
        tokenA.ShouldStartWith("TOK_SSN_");
        tokenB.ShouldStartWith("TOK_SSN_");
        tokenA.ShouldNotBe(tokenB);
    }

    [Theory]
    [InlineData(MaskFunction.Hash, $$"""{"key_ref":"{{MissingKeyRef}}"}""", TargetSqlDialect.PostgreSql)]
    [InlineData(MaskFunction.Tokenize, $$"""{"key_ref":"{{MissingKeyRef}}","domain":"SSN"}""", TargetSqlDialect.PostgreSql)]
    [InlineData(MaskFunction.Hash, $$"""{"key_ref":"{{MissingKeyRef}}"}""", TargetSqlDialect.Oracle)]
    [InlineData(MaskFunction.Hash, "{}", TargetSqlDialect.PostgreSql)]
    public void Unresolvable_key_ref_denies_column(MaskFunction function, string paramsJson, TargetSqlDialect dialect)
    {
        var providerWithKnownOnly = CreateSqlProvider(DefaultSecrets());
        var denied = providerWithKnownOnly.ResolveClassificationMask(function, paramsJson, "ssn", "varchar(50)", dialect, TenantA);

        denied.IsDenied.ShouldBeTrue();
        denied.DenialReason.ShouldBe("MASK_KEY_UNAVAILABLE");
        denied.Spec.ShouldBeNull();

        var providerWithoutSecretService = CreateSqlProvider(secrets: null);
        var noProviderDenied = providerWithoutSecretService.ResolveClassificationMask(function, $$"""{"key_ref":"{{PrimaryKeyRef}}"}""", "ssn", "varchar(50)", dialect, TenantA);
        noProviderDenied.IsDenied.ShouldBeTrue();
        noProviderDenied.DenialReason.ShouldBe("MASK_KEY_UNAVAILABLE");
    }

    [Fact]
    public void In_memory_and_sql_mask_agree()
    {
        var sqlProvider = CreateSqlProvider(DefaultSecrets());
        var colMask = new ColumnMaskingProvider();

        // NULL / DBNull boundary stays null for every function.
        MaskFunction[] allFunctions =
        [
            MaskFunction.Nullify, MaskFunction.Redact, MaskFunction.TypeSafeRedact, MaskFunction.Partial,
            MaskFunction.Hash, MaskFunction.Tokenize, MaskFunction.TruncateDate, MaskFunction.BucketNumber,
            MaskFunction.RoundNumber, MaskFunction.EmailDomain, MaskFunction.GeoJitter
        ];

        foreach (var fn in allFunctions)
        {
            string dt = fn switch
            {
                MaskFunction.TruncateDate => "timestamp",
                MaskFunction.BucketNumber or MaskFunction.RoundNumber or MaskFunction.GeoJitter => "decimal(12,2)",
                _ => "varchar(100)"
            };
            string pj = fn switch
            {
                MaskFunction.Hash => $$"""{"key_ref":"{{PrimaryKeyRef}}"}""",
                MaskFunction.Tokenize => $$"""{"key_ref":"{{PrimaryKeyRef}}","domain":"ACC"}""",
                MaskFunction.TruncateDate => """{"unit":"MONTH"}""",
                MaskFunction.BucketNumber => """{"width":10}""",
                MaskFunction.RoundNumber => """{"digits":1}""",
                _ => "{}"
            };

            var res = sqlProvider.ResolveClassificationMask(fn, pj, "col", dt, TargetSqlDialect.PostgreSql, TenantA);
            res.IsDenied.ShouldBeFalse();
            colMask.EvaluateClassificationMask(res.Spec!, null, TenantA, PrimaryKeyRef, MasterSecretA).ShouldBeNull();
            colMask.EvaluateClassificationMask(res.Spec!, DBNull.Value, TenantA, PrimaryKeyRef, MasterSecretA).ShouldBeNull();
        }

        // Empty string, 1-char, multi-byte UTF-8 rune, negative numbers & GeoJitter 0.0 boundaries.
        var partialSpec = sqlProvider.ResolveClassificationMask("PARTIAL", """{"keep_first":1,"keep_last":1,"mask_char":"*"}""", "s", "varchar(50)", TargetSqlDialect.PostgreSql, TenantA).Spec!;
        colMask.EvaluateClassificationMask(partialSpec, "", TenantA).ShouldBe("*****");
        colMask.EvaluateClassificationMask(partialSpec, "a", TenantA).ShouldBe("*****");
        colMask.EvaluateClassificationMask(partialSpec, "😀bcdef😀", TenantA).ShouldBe("😀*****😀");

        var emailSpec = sqlProvider.ResolveClassificationMask("EMAIL_DOMAIN", "{}", "e", "varchar(50)", TargetSqlDialect.PostgreSql, TenantA).Spec!;
        colMask.EvaluateClassificationMask(emailSpec, "üser@exämple.org", TenantA).ShouldBe("***@exämple.org");

        var roundSpec = sqlProvider.ResolveClassificationMask("ROUND_NUMBER", """{"digits":-1}""", "n", "decimal(10,2)", TargetSqlDialect.PostgreSql, TenantA).Spec!;
        colMask.EvaluateClassificationMask(roundSpec, 15m, TenantA).ShouldBe(20m);
        colMask.EvaluateClassificationMask(roundSpec, -15m, TenantA).ShouldBe(-20m);

        var hashRes = sqlProvider.ResolveClassificationMask("HASH", $$"""{"key_ref":"{{PrimaryKeyRef}}"}""", "s", "varchar(50)", TargetSqlDialect.PostgreSql, TenantA);
        var boundKey = (string)hashRes.Spec!.Parameters[hashRes.Spec.Arguments.HmacKey!.Name].Value!;
        var expectedDigest = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(boundKey), Encoding.UTF8.GetBytes("café")));
        colMask.EvaluateClassificationMask(hashRes.Spec, "café", TenantA, PrimaryKeyRef, MasterSecretA).ShouldBe(expectedDigest);

        var tokRes = sqlProvider.ResolveClassificationMask("TOKENIZE", $$"""{"key_ref":"{{PrimaryKeyRef}}","domain":"PAY"}""", "s", "varchar(50)", TargetSqlDialect.PostgreSql, TenantA);
        colMask.EvaluateClassificationMask(tokRes.Spec!, "café", TenantA, PrimaryKeyRef, MasterSecretA).ShouldBe("TOK_PAY_" + expectedDigest[..8]);

        var geoSpec = sqlProvider.ResolveClassificationMask("GEO_JITTER", """{"decimals":2}""", "lat", "decimal(9,6)", TargetSqlDialect.PostgreSql, TenantA).Spec!;
        colMask.EvaluateClassificationMask(geoSpec, 0.0d, TenantA).ShouldBe(0.0d);
        colMask.EvaluateClassificationMask(geoSpec, 52.5245d, TenantA).ShouldBe(52.52d);
    }

    [Theory]
    [InlineData("SELECT id FROM orders WHERE masked_col = 'x'")]
    [InlineData("SELECT o.id FROM orders o JOIN customers c ON o.masked_col = c.id")]
    [InlineData("SELECT masked_col FROM orders GROUP BY masked_col")]
    [InlineData("SELECT id FROM orders ORDER BY masked_col")]
    [InlineData("SELECT COUNT(masked_col) FROM orders")]
    public void New_mask_functions_are_blocked_in_predicates_joins_grouping_ordering_and_aggregates(string sql)
    {
        var orders = new TableIdentity("public", "orders");
        var customers = new TableIdentity("public", "customers");
        var catalog = new InMemoryTableCatalog(
        [
            new TableCatalogEntry(
                orders,
                [new("id", "integer"), new("tenant_id", "varchar(64)"), new("masked_col", "varchar(100)")],
                "tenant_id"),
            new TableCatalogEntry(
                customers,
                [new("id", "varchar(100)"), new("tenant_id", "varchar(64)")],
                "tenant_id")
        ], "public");

        var request = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = new TestPolicyProvider(),
                Masks = new TestMaskProvider(orders, "masked_col", new MaskSpec(MaskKind.EmailDomain, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty)),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", TenantA.Value, SqlParameterType.String),
                TablesWithMaskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.orders" }
            }
        };

        var engine = new FastSqlEngine();
        Should.Throw<SecurityException>(() => engine.Compile(sql.AsMemory(), request, System.Threading.CancellationToken.None));

        // Direct projection succeeds.
        var ok = engine.Compile("SELECT id, masked_col FROM orders".AsMemory(), request, System.Threading.CancellationToken.None);
        ok.Sql.ShouldContain("***@***");
    }

    private sealed class InMemorySecretProvider(IReadOnlyDictionary<string, byte[]> secrets) : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) =>
            secrets.TryGetValue(secretRef, out var bytes) ? bytes : throw new KeyNotFoundException(secretRef);
    }

    private sealed class TestPolicyProvider : IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
    }

    private sealed class TestMaskProvider(TableIdentity target, string column, MaskSpec spec) : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string col) =>
            table.Equals(target) && string.Equals(col, column, StringComparison.OrdinalIgnoreCase);

        public MaskSpec GetMask(TableIdentity table, string col) =>
            HasMask(table, col) ? spec : throw new KeyNotFoundException(col);
    }
}
