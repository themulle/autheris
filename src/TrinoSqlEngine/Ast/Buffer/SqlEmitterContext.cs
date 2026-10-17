namespace TrinoSqlEngine.Ast.Buffer;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Encapsulates state during SQL dialect generation (dialect, parameter tracking, indentation, context).
/// The legacy constructor keeps the string-only behavior (<see cref="ISqlDialectGenerator.GenerateSql(SqlStatement)"/>).
/// A context created with capabilities and a <see cref="ParameterSource"/> is in bound mode: every value reaches the database
/// as a <see cref="BoundParameter"/> that is counted against the capability bind limit.
/// </summary>
public sealed class SqlEmitterContext
{
    private static readonly Regex ClientNameRegex = new(
        @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // SEC-ADG-19: client names that could look like internal markers or reserved parameters are rejected.
    private static readonly Regex ReservedClientNameRegex = new(
        @"\A(p[0-9]|__|gql_|autheris)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    private readonly List<BoundParameter>? _bound;
    private readonly Dictionary<DedupKey, int>? _dedup;
    private readonly List<EmittedRange>? _ranges;
    private int _ticks;

    public TargetSqlDialect Dialect { get; init; }
    public int MaxParameterBudget { get; init; }
    public int ParameterCount { get; set; }
    public bool InPredicateContext { get; set; }

    /// <summary>
    /// REQ-SQL-130 (BC-130-12): set by the compiler or the federated planner after the time zone call validator ran over the statement. The default is
    /// false: a generator refuses to emit <c>convert_timezone</c> for an unvalidated statement, so a future emit site that skips the validator fails closed.
    /// </summary>
    internal bool TimeZoneCallsValidated { get; init; }
    public bool InProjectionContext { get; set; }
    public int IndentLevel { get; set; }

    /// <summary>Capabilities of the target dialect; set in bound mode.</summary>
    public DialectCapabilities? Capabilities { get; }

    /// <summary>Gateway-bound values; set in bound mode.</summary>
    public ParameterSource? Values { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>True when values are collected as <see cref="BoundParameter"/> (compiler path).</summary>
    public bool IsBound => _bound != null;

    public SqlEmitterContext(TargetSqlDialect dialect, int maxParameterBudget = int.MaxValue)
    {
        Dialect = dialect;
        MaxParameterBudget = maxParameterBudget;
        ParameterCount = 0;
    }

    public SqlEmitterContext(TargetSqlDialect dialect, DialectCapabilities capabilities, ParameterSource values, CancellationToken cancellationToken)
        : this(dialect, capabilities.MaxBindParameters)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        CancellationToken = cancellationToken;
        _bound = new List<BoundParameter>(8);
        _dedup = new Dictionary<DedupKey, int>(8); // string fields hash with the randomized default string comparer (SEC-ADG-05)
        _ranges = new List<EmittedRange>(16);
    }

    public ImmutableArray<BoundParameter> Parameters => _bound is null ? ImmutableArray<BoundParameter>.Empty : _bound.ToImmutableArray();

    public IReadOnlyList<EmittedRange> Ranges => _ranges ?? (IReadOnlyList<EmittedRange>)Array.Empty<EmittedRange>();

    /// <summary>Cooperative cancellation: checked every 256 emitted nodes (SEC-ADG-05).</summary>
    public void Tick()
    {
        if ((++_ticks & 255) == 0)
        {
            CancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Registers text at [start, start+length) as an inline structural number.</summary>
    public void RegisterInlineNumericPosition(int start, int length) =>
        _ranges?.Add(new EmittedRange(start, length, EmittedRangeKind.Numeric));

    /// <summary>Registers text at [start, start+length) as a constant fragment of a reviewed generator template.</summary>
    public void RegisterConstantFragment(int start, int length) =>
        _ranges?.Add(new EmittedRange(start, length, EmittedRangeKind.ConstantFragment));

    /// <summary>
    /// Registers the single statement terminator at <paramref name="start"/>. Only the SQL Server <c>MERGE</c> generator uses it,
    /// at the last position of the statement (SEC-ADG-08 c); the emitted-text checker rejects it anywhere else.
    /// </summary>
    public void RegisterStatementTerminator(int start) =>
        _ranges?.Add(new EmittedRange(start, 1, EmittedRangeKind.StatementTerminator));

    /// <summary>Binds a value (deduplicated by type, origin and value) and returns its marker.</summary>
    public string BindValue(object? value, SqlParameterType type, ParameterOrigin origin, string? columnType = null)
    {
        // The identity of a value is its CLR type plus a lossless value text (see ValueKey). Integers, the common literal, are keyed by
        // their number: no key string per literal.
        var key = value switch
        {
            long l => new DedupKey(DedupKind.Int64, (int)type, (int)origin, null, columnType, l, null),
            int n => new DedupKey(DedupKind.Int32, (int)type, (int)origin, null, columnType, n, null),
            _ => new DedupKey(DedupKind.Value, (int)type, (int)origin, null, columnType, 0, ValueKey(value))
        };
        return Bind(key, value, type, origin, null, columnType);
    }

    private enum DedupKind { Value, Int64, Int32, Policy, PolicyLike, Client, ClientZone }

    /// <summary>Identity of one bound value: a struct instead of a concatenated string (compared by every field, ordinal).</summary>
    private readonly record struct DedupKey(DedupKind Kind, int Type, int Origin, string? Name, string? ColumnType, long Number, string? Text);

    /// <summary>Binds a gateway value for a policy parameter node; the value comes from the <see cref="ParameterSource"/>.</summary>
    public string BindPolicy(PolicyParameterExpression parameter, bool escapeLikePattern = false)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (Values is null || !Values.PolicyValues.TryGetValue(parameter.Name, out var policyValue))
        {
            // Fail closed: a policy node without its value must never be emitted (INV-1).
            throw new SecurityException($"No value was supplied for policy parameter '{parameter.Name}'.");
        }

        if (escapeLikePattern)
        {
            // SEC-ADG-18: a value used as a LIKE pattern must not act as a pattern (%, _, [ and the escape character).
            if (policyValue.Value is not string text)
            {
                throw new SecurityException("A LIKE pattern parameter must be a string.");
            }

            return Bind(new DedupKey(DedupKind.PolicyLike, 0, 0, parameter.Name, parameter.ColumnType, 0, null), EscapeLike(text), policyValue.Type, parameter.Origin, parameter.Name, parameter.ColumnType);
        }

        // The compared column's type is part of the identity: one tenant value is bound once per distinct column type.
        return Bind(new DedupKey(DedupKind.Policy, 0, 0, parameter.Name, parameter.ColumnType, 0, null), policyValue.Value, policyValue.Type, parameter.Origin, parameter.Name, parameter.ColumnType);
    }

    private static string EscapeLike(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 4);
        foreach (char c in text)
        {
            if (c is '\\' or '%' or '_' or '[') sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Binds a client named parameter; the binder resolves the value per request.</summary>
    public string BindClient(ParameterReference parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (!parameter.IsSynthetic)
        {
            throw new SecurityException("Positional parameters are not supported by the governed compiler; use named parameters.");
        }

        if (!ClientNameRegex.IsMatch(parameter.Name) || ReservedClientNameRegex.IsMatch(parameter.Name))
        {
            throw new SecurityException("The client parameter name is invalid or uses a reserved prefix.");
        }

        // List element slots (REQ-SQL-128): both members or none, and the index lies inside the count (SEC-128-10 c).
        if (parameter.ListIndex.HasValue != parameter.ListCount.HasValue
            || (parameter.ListIndex is { } index && (index < 0 || index >= parameter.ListCount!.Value)))
        {
            throw new ClientListException(GovernedSqlErrorCodes.ListParamPosition);
        }

        // The element index is part of the identity: the elements of one list are distinct slots, the same element of a list that is
        // used twice is one slot.
        long listKey = parameter.ListIndex is { } element ? element + 1L : 0L;
        return Bind(new DedupKey(DedupKind.Client, 0, 0, parameter.Name, null, listKey, null), null, SqlParameterType.String, ParameterOrigin.ClientNamed, parameter.Name, null, parameter.ListIndex, parameter.ListCount);
    }

    /// <summary>
    /// REQ-SQL-130 (A-4): binds a client named parameter that is used as a time zone argument. The slot carries <see cref="BoundParameter.ZoneArgument"/>;
    /// only the production binder may consume it (it maps the value through the closed zone set before the command exists).
    /// </summary>
    public string BindZoneClient(ParameterReference parameter) => throw new NotImplementedException();

    private string Bind(DedupKey key, object? value, SqlParameterType type, ParameterOrigin origin, string? sourceName, string? columnType = null, int? listIndex = null, int? listCount = null)
    {
        if (_bound is null || _dedup is null || Capabilities is null)
        {
            throw new InvalidOperationException("Values can only be bound on a bound emitter context.");
        }

        if (_dedup.TryGetValue(key, out int existing))
        {
            return _bound[existing].Marker;
        }

        int ordinal = _bound.Count;
        if (ordinal + 1 > MaxParameterBudget)
        {
            throw new SqlLimitExceededException(SqlLimitKind.BindParameters, Dialect, ordinal + 1, MaxParameterBudget);
        }

        var (marker, name) = MarkerAndName(Capabilities.MarkerStyle, ordinal);
        _bound.Add(new BoundParameter(marker, name, ordinal, value, type, origin, sourceName, columnType, listIndex, listCount));
        _dedup[key] = ordinal;
        ParameterCount = _bound.Count;
        return marker;
    }

    // G8: markers and names are a pure function of (style, ordinal); the first 256 ordinals of every style are formatted once per process.
    private const int CachedMarkerOrdinals = 256;
    private static readonly MarkerPair?[][] MarkerCache = new MarkerPair?[Enum.GetValues<ParameterMarkerStyle>().Length][];

    // A class (not a tuple) so that a racing writer can never expose a torn value: a reference write is atomic.
    private sealed record MarkerPair(string Marker, string Name);

    private static (string Marker, string Name) MarkerAndName(ParameterMarkerStyle style, int ordinal)
    {
        if ((uint)ordinal < CachedMarkerOrdinals && (uint)(int)style < (uint)MarkerCache.Length)
        {
            var perStyle = MarkerCache[(int)style] ??= new MarkerPair?[CachedMarkerOrdinals];
            var known = perStyle[ordinal];
            if (known is null)
            {
                var (m, n) = Compute(style, ordinal);
                known = new MarkerPair(m, n);
                perStyle[ordinal] = known;   // a racing writer stores an equal value
            }

            return (known.Marker, known.Name);
        }

        return Compute(style, ordinal);

        static (string, string) Compute(ParameterMarkerStyle style, int ordinal)
        {
            string marker = FormatMarker(style, ordinal);
            string name = style switch
            {
                ParameterMarkerStyle.AtNamedOrdinal => marker,
                ParameterMarkerStyle.DollarOrdinal => marker.TrimStart('$'),
                ParameterMarkerStyle.QuestionOrdinal => marker.TrimStart('?'),
                _ => marker.TrimStart(':')
            };
            return (marker, name);
        }
    }

    private static string FormatMarker(ParameterMarkerStyle style, int ordinal) => style switch
    {
        ParameterMarkerStyle.AtNamedOrdinal => string.Create(CultureInfo.InvariantCulture, $"@p{ordinal}"),
        ParameterMarkerStyle.DollarOrdinal => string.Create(CultureInfo.InvariantCulture, $"${ordinal + 1}"),
        ParameterMarkerStyle.QuestionOrdinal => string.Create(CultureInfo.InvariantCulture, $"?{ordinal + 1}"),
        ParameterMarkerStyle.ColonNamedOrdinal => string.Create(CultureInfo.InvariantCulture, $":p{ordinal + 1}"),
        ParameterMarkerStyle.ColonOrdinal => string.Create(CultureInfo.InvariantCulture, $":{ordinal + 1}"),
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown marker style.")
    };

    /// <summary>
    /// CR-ADG-05: key by CLR type plus a lossless value text. Date and time types use the round-trip format (they keep every
    /// tick and the kind or offset); culture-invariant default formatting loses fractional seconds and merged distinct literals.
    /// </summary>
    private static string ValueKey(object? value) => value switch
    {
        null => "null",
        byte[] bytes => "bytes:" + Convert.ToHexString(bytes),
        DateTime dt => "DateTime:" + dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => "DateTimeOffset:" + dto.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => "DateOnly:" + date.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly time => "TimeOnly:" + time.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan span => "TimeSpan:" + span.ToString("c", CultureInfo.InvariantCulture),
        double d => "Double:" + d.ToString("R", CultureInfo.InvariantCulture),
        float f => "Single:" + f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => value.GetType().Name + ":" + f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.GetType().Name + ":" + (value.ToString() ?? string.Empty)
    };

    public void CheckParameterBudget(int additional = 1)
    {
        if (ParameterCount + additional > MaxParameterBudget)
        {
            throw new SqlLimitExceededException(SqlLimitKind.BindParameters, Dialect, ParameterCount + additional, MaxParameterBudget);
        }
    }

    // Dialects without a capability entry (string-only generation for Ansi and Snowflake) keep an explicit marker style; any other
    // dialect without an entry fails closed instead of falling back to a silent default.
    private static readonly FrozenDictionary<TargetSqlDialect, ParameterMarkerStyle> UnboundOnlyMarkerStyles =
        new Dictionary<TargetSqlDialect, ParameterMarkerStyle>
        {
            [TargetSqlDialect.Ansi] = ParameterMarkerStyle.QuestionOrdinal,
            [TargetSqlDialect.Snowflake] = ParameterMarkerStyle.ColonOrdinal
        }.ToFrozenDictionary();

    /// <summary>
    /// Appends the next positional marker of an unbound (string-only) emission. The style comes from the capability table, so
    /// marker syntax is data and not an enum switch.
    /// </summary>
    public void FormatNextParameterMarker(ref ValueStringBuilder builder)
    {
        CheckParameterBudget(1);
        var style = Capabilities?.MarkerStyle ?? ResolveUnboundMarkerStyle(Dialect);
        var (marker, _) = MarkerAndName(style, ParameterCount);
        ParameterCount++;
        builder.Append(marker);
    }

    private static ParameterMarkerStyle ResolveUnboundMarkerStyle(TargetSqlDialect dialect)
    {
        if (DialectCapabilityTable.Default.TryGet(dialect, out var caps))
        {
            return caps.MarkerStyle;
        }

        if (UnboundOnlyMarkerStyles.TryGetValue(dialect, out var style))
        {
            return style;
        }

        throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "No parameter marker style is defined for this dialect.");
    }
}
