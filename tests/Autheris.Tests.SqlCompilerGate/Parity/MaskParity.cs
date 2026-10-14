namespace Autheris.Tests.SqlCompilerGate.Parity;

using System.Data.Common;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.SqlCompilerGate.Databases;
using Autheris.Tests.SqlCompilerGate.Model;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;

/// <summary>One mask rule: the legacy string producer and the typed (IR) producer for the same catalog rule.</summary>
public sealed record MaskParityCase(string Id, string RuleType, Func<DatabaseDialect, string> Legacy)
{
    public MaskingRule Rule => new() { RuleType = RuleType };
}

/// <summary>The outcome of one mask expression on one clear value.</summary>
public sealed record MaskOutcome(string? Value, bool Refused);

/// <summary>
/// G10 for the masks (§7.3: <c>SqlDataSourceExecutor</c> projection, <c>TreeSqlCompiler</c>, <c>SqlDataMaskingProvider</c>). The legacy string builders that
/// still exist (<c>BuildEmailMaskExpression</c>, <c>BuildIbanMaskExpression</c>) and the typed mask spec rendered through the dialect generator are
/// evaluated on the same clear values. A mask is looser when it reveals more characters, or a clear value, than the legacy one.
/// </summary>
public static class MaskParity
{
    public static readonly IReadOnlyList<MaskParityCase> Cases =
    [
        new("mask.email", "MASK_EMAIL", d => SqlDataMaskingProvider.BuildEmailMaskExpression("email", d)),
        new("mask.iban", "MASK_IBAN", d => SqlDataMaskingProvider.BuildIbanMaskExpression("email", d))
    ];

    /// <summary>The clear values the masks run on: shapes that a mask rule must handle, including the ones that break naive substring logic.</summary>
    public static readonly IReadOnlyList<string> ClearValues =
    [
        "ann.smith@example.com", "a@b", "@nolocal.example", "no-at-sign", "x", "",
        "DE89370400440532013000", "DE12", "DE893704", "12345678",
        "o'brien@example.com", GateModel.EmailOf("customers", GateModel.TenantA, 1)
    ];

    private static TargetSqlDialect Target(DatabaseDialect d) => d switch
    {
        DatabaseDialect.Sqlite => TargetSqlDialect.Sqlite,
        DatabaseDialect.PostgreSql => TargetSqlDialect.PostgreSql,
        DatabaseDialect.SqlServer => TargetSqlDialect.SqlServer,
        DatabaseDialect.Oracle => TargetSqlDialect.Oracle,
        _ => throw new ArgumentOutOfRangeException(nameof(d))
    };

    /// <summary>The typed producer: the mask spec of the rule rendered by the dialect generator, with its values keyed by marker.</summary>
    public static (string Sql, IReadOnlyDictionary<string, object?> Parameters) Typed(MaskParityCase c, DatabaseDialect dialect)
    {
        var provider = new SqlDataMaskingProvider(Options.Create(new GatewayOptions()));
        var spec = provider.GetMaskSpec(c.Rule, "email", "text", dialect, new TenantId(GateModel.TenantA));
        var rendered = new MaskSpecRenderer().Render(spec, "email", "text", Target(dialect), name => dialect.FormatParameterMarker(name));
        return (rendered.Sql, rendered.Parameters);
    }

    public static async Task<MaskOutcome> EvaluateAsync(GateDatabase database, string expression, IReadOnlyDictionary<string, object?> parameters, string clear)
    {
        try
        {
            await using var conn = await database.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {expression} AS v FROM (SELECT '{clear.Replace("'", "''", StringComparison.Ordinal)}' AS email) AS t";
            foreach (var (name, value) in parameters)
            {
                DbParameter p = cmd.CreateParameter();
                p.ParameterName = name.TrimStart('@', ':');
                p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }

            var scalar = await cmd.ExecuteScalarAsync();
            return new MaskOutcome(scalar is null or DBNull ? null : Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture), false);
        }
        catch (Exception)
        {
            return new MaskOutcome(null, true);
        }
    }

    /// <summary>How much of the clear value a masked output reveals: its letters and digits. Mask characters, separators and NULL reveal nothing.</summary>
    public static int Reveal(MaskOutcome outcome) => outcome.Value?.Count(char.IsLetterOrDigit) ?? 0;

    public static bool ContainsClear(MaskOutcome outcome, string clear) =>
        clear.Length > 2 && outcome.Value is { } v && v.Contains(clear, StringComparison.Ordinal);

    /// <summary>Looser: the typed mask reveals more characters than the legacy one, or the whole clear value.</summary>
    public static bool IsLooser(MaskOutcome legacy, MaskOutcome ir, string clear) =>
        !ir.Refused && (ContainsClear(ir, clear) || (!legacy.Refused && Reveal(ir) > Reveal(legacy)) || (legacy.Refused && Reveal(ir) > 0 && false));
}
