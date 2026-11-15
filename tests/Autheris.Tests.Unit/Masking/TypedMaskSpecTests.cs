using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Masking;

/// <summary>WP-X0f: column masks as typed descriptors whose every value is a bound parameter (SEC-ADG-02, INV-4).</summary>
public sealed class TypedMaskSpecTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly byte[] MasterKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static SqlDataMaskingProvider Provider(bool preventExposure = false, bool withSecret = true)
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(MasterKey);
        var options = new GatewayOptions { DataMasking = new DataMaskingOptions { PreventInDbHmacKeyExposure = preventExposure } };
        return new SqlDataMaskingProvider(Options.Create(options), withSecret ? secrets : null);
    }

    private static MaskSpec Spec(MaskingRule? rule, string type = "varchar", DatabaseDialect dialect = DatabaseDialect.PostgreSql, SqlDataMaskingProvider? provider = null) =>
        (provider ?? Provider()).GetMaskSpec(rule, "col", type, dialect, Tenant);

    private static object? Value(MaskSpec spec, PolicyParameterExpression? parameter) => spec.Parameters[parameter!.Name].Value;

    private static void EveryParameterIsDeclaredMaskValue(MaskSpec spec)
    {
        var args = spec.Arguments;
        var used = new[] { args.Constant, args.KeepPrefix, args.KeepSuffix, args.MaskChar, args.HmacKey, args.HmacKeyOuter }
            .Where(p => p is not null).Select(p => p!).ToList();
        used.Select(p => p.Name).Order().ShouldBe(spec.Parameters.Keys.Order());
        used.ShouldAllBe(p => p.Origin == ParameterOrigin.Mask);
    }

    [Fact]
    public void MaskSpec_Nullify()
    {
        var spec = Spec(new MaskingRule { RuleType = "NULLIFY" });

        spec.Kind.ShouldBe(MaskKind.Nullify);
        spec.Parameters.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, "***")]
    [InlineData("N/A", "N/A")]
    public void MaskSpec_Redact_ConstantIsParameter(string? replacement, string expected)
    {
        var spec = Spec(new MaskingRule { RuleType = "REDACT", Replacement = replacement });

        spec.Kind.ShouldBe(MaskKind.Redact);
        Value(spec, spec.Arguments.Constant).ShouldBe(expected);
        EveryParameterIsDeclaredMaskValue(spec);
    }

    [Fact]
    public void MaskSpec_ColumnWithoutRule_IsRedact()
    {
        var spec = Spec(null);

        spec.Kind.ShouldBe(MaskKind.Redact);
        Value(spec, spec.Arguments.Constant).ShouldBe("***");
    }

    [Fact]
    public void MaskSpec_GeoJitter_KeepsDecimals()
    {
        var spec = Spec(new MaskingRule { RuleType = "GEO_JITTER", Decimals = 3 }, "double");

        spec.Kind.ShouldBe(MaskKind.GeoJitter);
        spec.Arguments.Decimals.ShouldBe(3);
        spec.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void MaskSpec_PartialMask_CountsAndCharAreParameters()
    {
        var spec = Spec(new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 2, KeepSuffix = 3, MaskChar = '#' });

        spec.Kind.ShouldBe(MaskKind.PartialMask);
        Value(spec, spec.Arguments.KeepPrefix).ShouldBe(2);
        Value(spec, spec.Arguments.KeepSuffix).ShouldBe(3);
        Value(spec, spec.Arguments.MaskChar).ShouldBe("#");
        EveryParameterIsDeclaredMaskValue(spec);
    }

    [Fact]
    public void MaskSpec_PartialMask_FixedLength_IsRedactedFailClosed()
    {
        // A fixed-length mask hides the length; the typed PartialMask cannot, so the stricter Redact is used.
        var spec = Spec(new MaskingRule { RuleType = "PARTIAL_MASK", FixedLength = true, MaskChar = '#' });

        spec.Kind.ShouldBe(MaskKind.Redact);
        Value(spec, spec.Arguments.Constant).ShouldBe("#####");
    }

    [Theory]
    [InlineData("MASK_EMAIL", "***@***")]
    [InlineData("MASK_IBAN", "****")]
    public void MaskSpec_EmailAndIban_AreConstantRedact_NeverRevealingCharactersOrLength(string ruleType, string constant)
    {
        foreach (var dialect in new[] { DatabaseDialect.PostgreSql, DatabaseDialect.SqlServer, DatabaseDialect.Oracle })
        {
            var spec = Spec(new MaskingRule { RuleType = ruleType }, dialect: dialect);

            spec.Kind.ShouldBe(MaskKind.Redact);
            Value(spec, spec.Arguments.Constant).ShouldBe(constant);
            EveryParameterIsDeclaredMaskValue(spec);
        }
    }

    [Theory]
    [InlineData("HMAC")]
    [InlineData("HMAC_SHA256")]
    [InlineData("HASH")]
    public void MaskSpec_Hmac_ForEveryAlias(string ruleType)
    {
        var spec = Spec(new MaskingRule { RuleType = ruleType });

        spec.Kind.ShouldBe(MaskKind.Hmac);
        Value(spec, spec.Arguments.HmacKey).ShouldBeOfType<string>().Length.ShouldBe(64);
        EveryParameterIsDeclaredMaskValue(spec);
    }

    [Fact]
    public void HmacKey_IsParameter_AndNeverInRenderedText()
    {
        var spec = Spec(new MaskingRule { RuleType = "HMAC" });
        var key = (string)Value(spec, spec.Arguments.HmacKey)!;

        var rendered = new MaskSpecRenderer().Render(spec, "col", "varchar", TargetSqlDialect.PostgreSql);

        rendered.Sql.ShouldNotContain(key);
        rendered.Parameters.Values.ShouldContain(key);
        rendered.Sql.ShouldContain("HMAC(");
    }

    [Fact]
    public void HmacKey_SqlServer_UsesTwoBinaryPadParameters()
    {
        var spec = Spec(new MaskingRule { RuleType = "HMAC" }, "nvarchar", DatabaseDialect.SqlServer);

        spec.Kind.ShouldBe(MaskKind.Hmac);
        spec.Arguments.HmacKey.ShouldNotBeNull();
        spec.Arguments.HmacKeyOuter.ShouldNotBeNull();
        spec.Parameters[spec.Arguments.HmacKey!.Name].Type.ShouldBe(SqlParameterType.Binary);
        Value(spec, spec.Arguments.HmacKey).ShouldBeOfType<byte[]>();
        Value(spec, spec.Arguments.HmacKeyOuter).ShouldBeOfType<byte[]>();
        EveryParameterIsDeclaredMaskValue(spec);
    }

    [Fact]
    public void Hmac_IsTenantScoped()
    {
        var provider = Provider();
        var a = provider.GetMaskSpec(new MaskingRule { RuleType = "HMAC" }, "col", "varchar", DatabaseDialect.PostgreSql, new TenantId("tenant-a"));
        var b = provider.GetMaskSpec(new MaskingRule { RuleType = "HMAC" }, "col", "varchar", DatabaseDialect.PostgreSql, new TenantId("tenant-b"));

        Value(a, a.Arguments.HmacKey).ShouldNotBe(Value(b, b.Arguments.HmacKey));
    }

    [Theory]
    [InlineData(true, true, DatabaseDialect.PostgreSql)]
    [InlineData(false, false, DatabaseDialect.PostgreSql)]
    [InlineData(false, true, DatabaseDialect.Oracle)]
    public void Hmac_WhereTheKeyMayNotReachTheDatabase_DegradesToRedact(bool preventExposure, bool withSecret, DatabaseDialect dialect)
    {
        var spec = Spec(new MaskingRule { RuleType = "HMAC" }, "varchar", dialect, Provider(preventExposure, withSecret));

        spec.Kind.ShouldBe(MaskKind.Redact);
        spec.Arguments.HmacKey.ShouldBeNull();
        spec.Parameters.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, DatabaseDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer, DatabaseDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle, DatabaseDialect.Oracle)]
    public void Render_EveryValueIsAParameter_NeverText(TargetSqlDialect target, DatabaseDialect dialect)
    {
        var rules = new[]
        {
            new MaskingRule { RuleType = "REDACT", Replacement = "SECRET-REPL" },
            new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 7, KeepSuffix = 9, MaskChar = '#' },
            new MaskingRule { RuleType = "NULLIFY" }
        };
        foreach (var rule in rules)
        {
            var spec = Spec(rule, "varchar(50)", dialect);
            var rendered = new MaskSpecRenderer().Render(spec, "col", "varchar(50)", target);

            rendered.Sql.ShouldNotBeNullOrWhiteSpace();
            rendered.Sql.ShouldNotContain("SECRET-REPL");
            rendered.Sql.ShouldNotContain("#");
            rendered.Parameters.Count.ShouldBe(spec.Parameters.Count);
            foreach (var marker in rendered.Parameters.Keys)
            {
                rendered.Sql.ShouldContain(marker);
            }
        }
    }

    [Fact]
    public void Render_UsesTheCallersMarkerFormat()
    {
        var spec = Spec(new MaskingRule { RuleType = "REDACT", Replacement = "x" }, "varchar(10)", DatabaseDialect.Oracle);

        var rendered = new MaskSpecRenderer().Render(spec, "COL", "varchar(10)", TargetSqlDialect.Oracle, name => ":" + name);

        rendered.Parameters.Keys.ShouldAllBe(k => k.StartsWith(':'));
        rendered.Sql.ShouldContain(rendered.Parameters.Keys.Single());
    }

    // --- the string facade renders from the typed spec ---

    [Theory]
    [InlineData("PARTIAL_MASK")]
    [InlineData("GEO_JITTER")]
    public void StringFacade_RendersFromTheTypedSpec_WithParametersNotText(string ruleType)
    {
        var provider = Provider();
        var meta = new TableMetadata
        {
            Table = new Table { TableName = "t", SchemaName = "main", SourceName = "d", SourceType = "PostgreSql" },
            Columns = [new TableColumn { ColumnName = "col", DataType = "text" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["col"] = new() { RuleType = ruleType, KeepPrefix = 3, KeepSuffix = 1, MaskChar = '#', Decimals = 2 }
            }
        };
        var parameters = new Dictionary<string, object?>();

        var sql = provider.GetMaskExpressionForRule("col", meta, Tenant, parameters, new Dictionary<string, string>(), out _);

        sql.ShouldNotContain("#");
        if (ruleType == "PARTIAL_MASK")
        {
            parameters.ShouldNotBeEmpty();
        }

        foreach (var marker in parameters.Keys)
        {
            sql.ShouldContain(marker);
        }
    }
}
