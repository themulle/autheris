namespace TrinoSqlEngine;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Microsoft.Extensions.ObjectPool;
using TrinoSqlEngine.Parsing;

public sealed class ThrowingErrorListener : BaseErrorListener, IAntlrErrorListener<int>
{
    public static readonly ThrowingErrorListener Instance = new();

    public override void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
    {
        throw new ParseCanceledException($"line {line}:{charPositionInLine}: {msg}", e);
    }

    public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
    {
        throw new ParseCanceledException($"line {line}:{charPositionInLine}: {msg}", e);
    }
}

public sealed partial class FastSqlEngine : ISqlEngine, Governance.IPolicyKeyQueryCompiler
{
    /// <summary>Thread-safe default shared instance of <see cref="FastSqlEngine"/>.</summary>
    public static FastSqlEngine Default { get; } = new();

    /// <summary>SQ-08: Default stack size of the dedicated parser thread (16 MB).</summary>
    public const int DefaultParseThreadStackSize = 16 * 1024 * 1024;

    /// <summary>SQ-08: Smallest accepted parser thread stack size (256 KB).</summary>
    public const int MinParseThreadStackSize = 256 * 1024;

    /// <summary>SQ-08: Default number of concurrently running parser threads (process-wide).</summary>
    public static int DefaultMaxConcurrentParses { get; } = Math.Max(2, Environment.ProcessorCount * 2);

    /// <summary>
    /// SQ-08: Process-wide limiter for parser threads (prevents thread floods). Used unless
    /// <see cref="ParseConcurrencyLimiter"/> is set on the engine.
    /// </summary>
    public static SemaphoreSlim SharedParseConcurrencyLimiter { get; } = new(DefaultMaxConcurrentParses, DefaultMaxConcurrentParses);

    private readonly ParserPooledObjectPolicy _parserPolicy;
    private readonly ObjectPool<SqlBaseParser> _parserPool;

    private int _parseThreadStackSize = DefaultParseThreadStackSize;

    /// <summary>
    /// SEC-04: Maximum allowed SQL query length in characters to prevent algorithmic DoS and StackOverflow attacks.
    /// SEC C-06: Default lowered from 512k to 64k.
    /// </summary>
    public int MaxQueryLength { get; set; } = 65_536;

    /// <summary>
    /// SEC C-06 / SQ-08: Maximum token-level nesting depth (default 100). 0 disables only the nesting check; the
    /// token security checks (SEC P-03) always run.
    /// </summary>
    public int MaxNestingDepth { get; set; } = 100;

    /// <summary>
    /// SEC C-06: Maximum depth of the resulting parse tree (checked iteratively after parsing, before any recursive
    /// tree walk). Guards against left-recursive chains (e.g. "1+1+1+...") that do not increase token nesting. 0 disables the check.
    /// </summary>
    public int MaxParseTreeDepth { get; set; } = 3_000;

    /// <summary>
    /// SQ-08: Time budget for a single parse (lexing, token checks, SLL + LL parsing, tree depth check), including the
    /// wait for a free parser thread slot. Default 5 s. <see cref="TimeSpan.Zero"/> or a negative value disables the budget.
    /// </summary>
    public TimeSpan ParseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// SQ-08: Stack size (bytes) of the dedicated parser thread. Default 4 MB; minimum 256 KB.
    /// </summary>
    public int ParseThreadStackSize
    {
        get => _parseThreadStackSize;
        set
        {
            if (value < MinParseThreadStackSize)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"The parser thread stack size must be at least {MinParseThreadStackSize} bytes.");
            }

            _parseThreadStackSize = value;
        }
    }

    /// <summary>
    /// SQ-08: Optional limiter for concurrently running parser threads of this engine. When null (default), the
    /// process-wide <see cref="SharedParseConcurrencyLimiter"/> is used.
    /// </summary>
    public SemaphoreSlim? ParseConcurrencyLimiter { get; set; }

    /// <summary>
    /// ARC-S-03: Statements up to this many characters are parsed on the calling thread when enough stack is left (no thread
    /// hand-off). 0 (default) disables the inline path; every parse then runs on the pooled parser threads. The concurrency
    /// limiter and the time budget apply unchanged.
    /// </summary>
    public int InlineParseMaxLength { get; set; }

    /// <summary>
    /// ARC-S-09: Time budget of the LL fallback pass that follows a failed SLL pass (default 1 s). The pass is also bound by
    /// <see cref="ParseTimeout"/>. <see cref="TimeSpan.Zero"/> or a negative value disables the separate budget.
    /// </summary>
    public TimeSpan LlFallbackTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// ARC-S-09: When true, an SLL failure caused by a plain token mismatch is final and the LL pass is skipped.
    /// Prediction failures (no viable alternative, failed predicate) always retry in LL mode. Off by default: an SLL
    /// mis-prediction can surface as a later token mismatch on valid SQL (for example quantified comparisons and some
    /// function-call shapes), so skipping LL would reject statements the full grammar accepts.
    /// </summary>
    public bool SkipLlFallbackOnTokenMismatch { get; set; }

    /// <summary>
    /// ARC-S-09: How long a parser syntax failure is remembered so that repeated invalid input is rejected without parsing again
    /// (default 30 s). <see cref="TimeSpan.Zero"/> disables the cache.
    /// </summary>
    public TimeSpan SyntaxErrorCacheTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>ARC-S-09: Maximum number of remembered syntax failures (default 256).</summary>
    public int SyntaxErrorCacheMaxEntries { get; set; } = 256;

    // Created on the first remembered syntax error (SEC-LB-09): most engines never see one. A null cache is a miss on lookup, never a cached "ok".
    private SyntaxErrorNegativeCache? _syntaxErrors;

    /// <summary>Number of remembered syntax failures (diagnostics and tests).</summary>
    internal int SyntaxErrorCacheCount => Volatile.Read(ref _syntaxErrors)?.Count ?? 0;

    /// <summary>True once the first syntax error was remembered (test seam of the lazy cache).</summary>
    internal bool SyntaxErrorCacheInitialized => Volatile.Read(ref _syntaxErrors) is not null;

    private const int StatementRule = 1;
    private const int ExpressionRule = 2;

    // Defaults for direct Parse/ParseExpression/Analyze calls without explicit SqlTokenSecurityOptions.
    // SEC P-06: These stay false so that plain syntax parsing (Trino compliance fixtures with comments, time travel,
    // quoted dotted identifiers) keeps working. The governed compile path (Compile) never uses them; it takes its
    // switches from CompileRequest.TokenGuards.

    /// <summary>SQ-02: When true, comments are rejected to prevent dialect comment discrepancies (direct parsing only).</summary>
    public bool RejectComments { get; set; } = false;

    /// <summary>SQ-01: When true, backslash escapes in string literals are rejected (direct parsing only).</summary>
    public bool RejectBackslashInStrings { get; set; } = false;

    /// <summary>SQ-01: When true, string type constructors like E'...' are rejected (direct parsing only).</summary>
    public bool RejectEscapedStringLiterals { get; set; } = false;

    /// <summary>SQ-02: When true, dollar-quoted strings ($$...$$) are rejected (direct parsing only).</summary>
    public bool RejectDollarQuoting { get; set; } = false;

    /// <summary>SQL-1: When true, brackets and bracket/comment sequences inside literals are rejected (direct parsing only).</summary>
    public bool RejectBracketLexerDifferentials { get; set; } = false;

    /// <summary>SQ-10: When true, unquoted identifiers with non-ASCII characters are rejected (direct parsing only).</summary>
    public bool RejectNonAsciiIdentifiers { get; set; } = false;

    /// <summary>SQ-11: When true, dots inside quoted identifiers are rejected (direct parsing only).</summary>
    public bool RejectDotsInQuotedIdentifiers { get; set; } = false;

    /// <summary>SQ-13: When true, time-travel syntax (FOR TIMESTAMP/VERSION AS OF) is rejected (direct parsing only).</summary>
    public bool RejectTimeTravelQueries { get; set; } = false;

    /// <summary>
    /// Snapshot of the engine-level default token switches (used when no explicit options are passed).
    /// </summary>
    public SqlTokenSecurityOptions CreateDefaultTokenSecurityOptions() => new()
    {
        RejectComments = RejectComments,
        RejectBackslashInStrings = RejectBackslashInStrings,
        RejectEscapedStringLiterals = RejectEscapedStringLiterals,
        RejectDollarQuoting = RejectDollarQuoting,
        RejectBracketLexerDifferentials = RejectBracketLexerDifferentials,
        RejectNonAsciiIdentifiers = RejectNonAsciiIdentifiers,
        RejectDotsInQuotedIdentifiers = RejectDotsInQuotedIdentifiers,
        RejectTimeTravelQueries = RejectTimeTravelQueries
    };

    public (SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens) Parse(ReadOnlyMemory<char> sql)
    {
        return Parse(sql, null, CancellationToken.None);
    }

    /// <summary>
    /// Parses a statement on a dedicated parser thread (SQ-08: own stack, time budget, concurrency limit).
    /// </summary>
    /// <param name="sql">SQL text.</param>
    /// <param name="tokenOptions">Per-call token security switches (SEC P-04); null = engine defaults.</param>
    /// <param name="cancellationToken">Cancels the parse (surfaces as <see cref="OperationCanceledException"/>).</param>
    /// <exception cref="ParseCanceledException">Syntax error, policy violation or time budget exceeded
    /// (then <see cref="Exception.InnerException"/> is a <see cref="TimeoutException"/>).</exception>
    public (SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens) Parse(
        ReadOnlyMemory<char> sql,
        SqlTokenSecurityOptions? tokenOptions,
        CancellationToken cancellationToken = default)
    {
        if (sql.Length > MaxQueryLength)
        {
            throw new ArgumentOutOfRangeException(nameof(sql), $"SQL query length ({sql.Length}) exceeds the maximum allowed limit of {MaxQueryLength} characters.");
        }

        var effectiveOptions = tokenOptions ?? CreateDefaultTokenSecurityOptions();
        ThrowIfKnownSyntaxError(sql, StatementRule, effectiveOptions);
        return RunWithParseBudget(
            cancellation => ParseOnCurrentThread(sql, effectiveOptions, cancellation, StatementRule, static parser => parser.singleStatement()),
            sql.Length,
            cancellationToken);
    }

    public (SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens) ParseExpression(ReadOnlyMemory<char> sql)
    {
        return ParseExpression(sql, null, CancellationToken.None);
    }

    public (SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens) ParseExpression(
        ReadOnlyMemory<char> sql,
        SqlTokenSecurityOptions? tokenOptions,
        CancellationToken cancellationToken = default)
    {
        if (sql.Length > MaxQueryLength)
        {
            throw new ArgumentOutOfRangeException(nameof(sql), $"SQL expression length ({sql.Length}) exceeds the maximum allowed limit of {MaxQueryLength} characters.");
        }

        var effectiveOptions = tokenOptions ?? CreateDefaultTokenSecurityOptions();
        ThrowIfKnownSyntaxError(sql, ExpressionRule, effectiveOptions);
        return RunWithParseBudget(
            cancellation => ParseOnCurrentThread(sql, effectiveOptions, cancellation, ExpressionRule, static parser => parser.standaloneExpression()),
            sql.Length,
            cancellationToken);
    }

    /// <summary>
    /// SQ-08: Runs <paramref name="work"/> on a pooled parser thread (own large stack, <see cref="ParseThreadStackSize"/>) and waits
    /// at most <see cref="ParseTimeout"/>, queue wait included. On timeout or cancellation the parse is aborted cooperatively via
    /// <see cref="ParseCancellation"/>. The parser thread releases the concurrency slot itself, so the limiter bounds the number of
    /// running parses even when callers time out. Short statements may run inline (<see cref="InlineParseMaxLength"/>, off by default).
    /// </summary>
    private TResult RunWithParseBudget<TResult>(Func<ParseCancellation, TResult> work, int sqlLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TimeSpan budget = ParseTimeout;
        long startTimestamp = Stopwatch.GetTimestamp();
        SemaphoreSlim limiter = ParseConcurrencyLimiter ?? SharedParseConcurrencyLimiter;

        if (!limiter.Wait(RemainingMilliseconds(budget, startTimestamp), cancellationToken))
        {
            throw RejectedParse("all concurrent parser slots are in use and none became free within the parse time budget.");
        }

        var cancellation = new ParseCancellation();

        if (InlineParseMaxLength > 0 && sqlLength <= InlineParseMaxLength && RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            try
            {
                if (budget > TimeSpan.Zero)
                {
                    cancellation.TightenDeadline(budget - Stopwatch.GetElapsedTime(startTimestamp), $"SQL parsing exceeded the time budget of {budget.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms.");
                }

                return work(cancellation);
            }
            finally
            {
                limiter.Release();
            }
        }

        var completed = new ManualResetEventSlim(false);
        TResult? result = default;
        ExceptionDispatchInfo? failure = null;

        var item = new ParseWork(
            () =>
            {
                try
                {
                    result = work(cancellation);
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    limiter.Release();
                    completed.Set();
                }
            },
            onCanceledWhileQueued: () =>
            {
                limiter.Release();
                completed.Dispose();
            });

        var pool = ParseWorkerPool.ForStackSize(_parseThreadStackSize);
        bool queued;
        try
        {
            queued = pool.TrySubmit(item, RemainingMilliseconds(budget, startTimestamp), cancellationToken);
        }
        catch
        {
            limiter.Release();
            completed.Dispose();
            throw;
        }

        if (!queued)
        {
            limiter.Release();
            completed.Dispose();
            throw RejectedParse("the parser queue stayed full within the parse time budget.");
        }

        bool finished;
        try
        {
            finished = completed.Wait(RemainingMilliseconds(budget, startTimestamp), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            cancellation.Cancel();
            pool.Abandon(item);
            throw;
        }

        if (!finished)
        {
            // The parser thread observes the flag on its next token access, returns its pooled parser and releases its slot;
            // the pool retires and replaces that thread once the work returns.
            cancellation.Cancel();
            pool.Abandon(item);
            throw new ParseCanceledException(
                $"SQL parsing exceeded the time budget of {budget.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms.",
                new TimeoutException("SQL parsing exceeded the configured time budget."));
        }

        completed.Dispose();
        failure?.Throw();
        return result!;
    }

    private static ParseCanceledException RejectedParse(string reason) =>
        new($"SQL parsing was rejected: {reason}", new TimeoutException("No parser capacity became available within the parse time budget."));

    private int LimitsFingerprint() => HashCode.Combine(MaxNestingDepth, MaxParseTreeDepth, MaxQueryLength);

    private void ThrowIfKnownSyntaxError(ReadOnlyMemory<char> sql, int ruleKind, SqlTokenSecurityOptions options)
    {
        var cache = Volatile.Read(ref _syntaxErrors);
        if (cache is not null && cache.TryGet(sql.Span, ruleKind, options, LimitsFingerprint(), out var message))
        {
            throw new ParseCanceledException(message);
        }
    }

    private void RememberSyntaxError(ReadOnlyMemory<char> sql, int ruleKind, SqlTokenSecurityOptions options, string message)
    {
        if (SyntaxErrorCacheTtl <= TimeSpan.Zero || SyntaxErrorCacheMaxEntries <= 0)
        {
            return; // the cache is switched off: do not create it
        }

        LazyInitializer.EnsureInitialized(ref _syntaxErrors, static () => new SyntaxErrorNegativeCache())
            .Add(sql.Span, ruleKind, options, LimitsFingerprint(), message, SyntaxErrorCacheTtl, SyntaxErrorCacheMaxEntries);
    }

    private static int RemainingMilliseconds(TimeSpan budget, long startTimestamp)
    {
        if (budget <= TimeSpan.Zero)
        {
            return Timeout.Infinite;
        }

        TimeSpan remaining = budget - Stopwatch.GetElapsedTime(startTimestamp);
        if (remaining <= TimeSpan.Zero)
        {
            return 0;
        }

        double milliseconds = remaining.TotalMilliseconds;
        return milliseconds >= int.MaxValue - 1 ? int.MaxValue - 1 : (int)milliseconds;
    }

    /// <summary>
    /// Executed on the dedicated parser thread only: the pooled parser is taken and returned on this thread.
    /// </summary>
    private (TTree Tree, CommonTokenStream Tokens) ParseOnCurrentThread<TTree>(
        ReadOnlyMemory<char> sql,
        SqlTokenSecurityOptions tokenOptions,
        ParseCancellation cancellation,
        int ruleKind,
        Func<SqlBaseParser, TTree> entryRule)
        where TTree : ParserRuleContext
    {
        // A parse that was abandoned while it waited in the queue stops here, before any lexing.
        cancellation.ThrowIfCanceled();
        ExecutionStackGuard.Ensure();

        var charStream = new ZeroCopyCaseInsensitiveStream(sql);
        var lexer = _generations.CreateLexer(charStream);
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(ThrowingErrorListener.Instance);

        var tokens = new CancellableTokenStream(lexer, cancellation);

        // Lexer errors surface here as ParseCanceledException, exactly as they would during parsing.
        tokens.Fill();
        var allTokens = tokens.GetTokens();

        // SEC P-03: token security checks always run, independent of MaxNestingDepth.
        EnsureTokensAreSafe(allTokens, sql.Span, tokenOptions, cancellation);
        EnsureNestingDepthWithinLimit(allTokens, sql.Span, cancellation);

        var parser = _parserPool.Get();
        _parserPolicy.PrepareForUse(parser); // a failure here drops the instance: it was never returned to the pool
        try
        {
            parser.TokenStream = tokens;

            TTree tree;

            // Stufe 1: Schneller SLL-Pfad (im Pool bereits vorkonfiguriert)
            try
            {
                tree = entryRule(parser);
            }
            catch (ParseCanceledException sllFailure) when (!cancellation.IsCanceled && sllFailure.InnerException is not TimeoutException)
            {
                // ARC-S-09: a plain token mismatch is not a prediction problem; LL would report the same error, so it is final.
                if (SkipLlFallbackOnTokenMismatch && sllFailure.InnerException is InputMismatchException mismatch)
                {
                    var offending = mismatch.OffendingToken;
                    var message = offending is null
                        ? "Parsing failed: mismatched input."
                        : $"line {offending.Line}:{offending.Column}: mismatched input '{offending.Text}'";
                    RememberSyntaxError(sql, ruleKind, tokenOptions, message);
                    throw new ParseCanceledException(message, mismatch);
                }

                // Stufe 2: Fallback auf LL(*)-Modus, mit eigenem (kleineren) Zeitbudget
                if (LlFallbackTimeout > TimeSpan.Zero)
                {
                    cancellation.TightenDeadline(LlFallbackTimeout, "SQL parsing exceeded the LL fallback time budget.");
                }

                tokens.Reset();
                parser.Reset();
                parser.AddErrorListener(ThrowingErrorListener.Instance);
                // SEC N1: DefaultErrorStrategy holds per-parse state; never share an instance between threads.
                parser.ErrorHandler = new DefaultErrorStrategy();
                parser.Interpreter.PredictionMode = PredictionMode.LL;

                try
                {
                    tree = entryRule(parser);
                    if (parser.NumberOfSyntaxErrors > 0)
                    {
                        throw new ParseCanceledException($"Parsing failed with {parser.NumberOfSyntaxErrors} syntax errors.");
                    }
                }
                catch (ParseCanceledException llFailure) when (!cancellation.IsCanceled && llFailure.InnerException is not TimeoutException)
                {
                    RememberSyntaxError(sql, ruleKind, tokenOptions, llFailure.Message);
                    throw;
                }
            }

            cancellation.ThrowIfCanceled();

            // SEC C-06: reject overly deep trees before any recursive walk by the caller.
            EnsureParseTreeDepthWithinLimit(tree, cancellation);
            return (tree, tokens);
        }
        finally
        {
            _parserPool.Return(parser);
        }
    }

    /// <summary>
    /// The characters of a lexer token as a slice of the statement, identical to <see cref="IToken.Text"/> (which allocates a string per
    /// call). A token whose interval is not inside the statement (the EOF token) falls back to <see cref="IToken.Text"/>.
    /// </summary>
    internal static ReadOnlySpan<char> TokenText(IToken token, ReadOnlySpan<char> source, out bool hasText)
    {
        int start = token.StartIndex;
        int stop = token.StopIndex;
        if (token is CommonToken && start >= 0 && stop >= start && stop < source.Length)
        {
            hasText = true;
            return source.Slice(start, stop - start + 1);
        }

        string? text = token.Text;
        hasText = text is not null;
        return text.AsSpan();
    }

    /// <summary>
    /// SEC P-03: Token-level security checks (SQ-01, SQ-02, SQ-10, SQ-11, SQ-13). Always executed, independent of
    /// <see cref="MaxNestingDepth"/>.
    /// </summary>
    private static void EnsureTokensAreSafe(IList<IToken> allTokens, ReadOnlySpan<char> source, SqlTokenSecurityOptions options, ParseCancellation cancellation)
    {
        IToken? lastNonWsToken = null;
        int index = 0;

        foreach (var token in allTokens)
        {
            if ((index++ & 1023) == 0)
            {
                cancellation.ThrowIfCanceled();
            }

            int type = token.Type;

            // SQ-02: Block comment nesting check (/* ... /* ... */) - always active
            if (type == SqlBaseLexer.BRACKETED_COMMENT)
            {
                string? commentText = token.Text;
                if (commentText != null && commentText.Length > 4 && commentText.IndexOf("/*", 2, StringComparison.Ordinal) >= 0)
                {
                    throw new ParseCanceledException(
                        $"line {token.Line}:{token.Column}: Nested block comments ('/* ... /* ... */') are strictly prohibited due to dialect lexer differentials.");
                }
            }

            if (options.RejectComments && (type == SqlBaseLexer.SIMPLE_COMMENT || type == SqlBaseLexer.BRACKETED_COMMENT))
            {
                throw new ParseCanceledException(
                    $"line {token.Line}:{token.Column}: SQL comments are not permitted in governed execution.");
            }

            if (type == SqlBaseLexer.WS || type == SqlBaseLexer.SIMPLE_COMMENT || type == SqlBaseLexer.BRACKETED_COMMENT)
                continue;

            // The text of the remaining tokens is read as a slice of the statement (same characters as IToken.Text, no string per token).
            var text = TokenText(token, source, out bool hasText);

            // SEC-ADG-10: ${...} variable substitution sequences (Spark / Databricks)
            if (options.RejectVariableSubstitutionSequences && hasText && text.Contains("${", StringComparison.Ordinal)
                && !CompilerHooks.IsDisabled(SecurityControl.VariableSubstitution)
                )
            {
                throw new ParseCanceledException(
                    $"line {token.Line}:{token.Column}: Variable substitution sequences ('${{') are not permitted.");
            }

            // SQ-01: Backslash escapes in string literals
            if (options.RejectBackslashInStrings && type == SqlBaseLexer.STRING && hasText && text.Contains('\\'))
            {
                throw LiteralTokenRejectedException.Reject(token.Line, token.Column,
                    "Backslash escapes in string literals are not permitted due to dialect lexer differentials.");
            }

            // SQ-01: E'...' / e'...' string type constructor
            if (options.RejectEscapedStringLiterals && (type == SqlBaseLexer.STRING || type == SqlBaseLexer.UNICODE_STRING))
            {
                if (lastNonWsToken != null && lastNonWsToken.Type == SqlBaseLexer.IDENTIFIER &&
                    string.Equals(lastNonWsToken.Text, "E", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ParseCanceledException(
                        $"line {token.Line}:{token.Column}: Escaped string literal type constructors (E'...') are not permitted due to dialect lexer differentials.");
                }
            }

            // SQL-1: UESCAPE changes literal decoding on PostgreSQL in a way the gateway does not model.
            if (options.RejectEscapedStringLiterals && type == SqlBaseLexer.UESCAPE)
            {
                throw new ParseCanceledException(
                    $"line {token.Line}:{token.Column}: UESCAPE is not permitted due to dialect lexer differentials.");
            }

            // SQL-1: Bracket lexer differentials (SQL Server / SQLite lex [...] as quoted identifier)
            if (options.RejectBracketLexerDifferentials)
            {
                if (text.SequenceEqual("[") || text.SequenceEqual("]"))
                {
                    throw new ParseCanceledException(
                        $"line {token.Line}:{token.Column}: Square brackets are not permitted for this SQL dialect due to dialect lexer differentials.");
                }

                if (hasText &&
                    (type == SqlBaseLexer.STRING || type == SqlBaseLexer.UNICODE_STRING ||
                     type == SqlBaseLexer.QUOTED_IDENTIFIER || type == SqlBaseLexer.BACKQUOTED_IDENTIFIER) &&
                    (text.Contains('[') || text.Contains(']') ||
                     text.Contains("--", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal)))
                {
                    throw LiteralTokenRejectedException.Reject(token.Line, token.Column,
                        "Literals containing '[', ']', '--' or '/*' are not permitted for this SQL dialect due to dialect lexer differentials.");
                }
            }

            // SQ-02: Dollar quoting ($$...$$)
            if (options.RejectDollarQuoting && type == SqlBaseLexer.DOLLAR_STRING)
            {
                throw new ParseCanceledException(
                    $"line {token.Line}:{token.Column}: Dollar-quoted strings ($$...$$) are not permitted.");
            }

            // SQ-10: Non-ASCII characters outside strings/quoted identifiers
            if (options.RejectNonAsciiIdentifiers && hasText &&
                type != SqlBaseLexer.STRING &&
                type != SqlBaseLexer.UNICODE_STRING &&
                type != SqlBaseLexer.DOLLAR_STRING &&
                type != SqlBaseLexer.QUOTED_IDENTIFIER &&
                type != SqlBaseLexer.BACKQUOTED_IDENTIFIER)
            {
                for (int ci = 0; ci < text.Length; ci++)
                {
                    if (text[ci] > 127)
                    {
                        throw new ParseCanceledException(
                            $"line {token.Line}:{token.Column}: Non-ASCII characters outside string literals ('{text.ToString()}') are not permitted.");
                    }
                }
            }

            // SQ-11: Dots inside quoted identifiers ("a.b")
            if (options.RejectDotsInQuotedIdentifiers && (type == SqlBaseLexer.QUOTED_IDENTIFIER || type == SqlBaseLexer.BACKQUOTED_IDENTIFIER) && hasText)
            {
                for (int ci = 0; ci < text.Length; ci++)
                {
                    char c = text[ci];
                    if (c == '.' || char.IsControl(c))
                    {
                        throw new ParseCanceledException(
                            $"line {token.Line}:{token.Column}: Dots inside quoted identifiers ({text.ToString()}) are not permitted.");
                    }
                }
            }

            // Phase 5 L3: identifiers with the gateway prefix are reserved in the user statement (typed 400, fixed message, no echo)
            if (options.RejectReservedIdentifiers && hasText &&
                (type == SqlBaseLexer.IDENTIFIER || type == SqlBaseLexer.DIGIT_IDENTIFIER ||
                 type == SqlBaseLexer.QUOTED_IDENTIFIER || type == SqlBaseLexer.BACKQUOTED_IDENTIFIER))
            {
                var name = text;
                if (type is SqlBaseLexer.QUOTED_IDENTIFIER or SqlBaseLexer.BACKQUOTED_IDENTIFIER && name.Length >= 2)
                {
                    name = name[1..^1];
                }

                if (name.StartsWith(ReservedIdentifierException.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ReservedIdentifierException();
                }
            }

            // SQ-13: Time-travel queries (FOR TIMESTAMP/VERSION AS OF)
            if (options.RejectTimeTravelQueries && (type == SqlBaseLexer.TIMESTAMP || type == SqlBaseLexer.VERSION))
            {
                if (lastNonWsToken != null && lastNonWsToken.Type == SqlBaseLexer.FOR)
                {
                    throw new ParseCanceledException(
                        $"line {token.Line}:{token.Column}: Time-travel queries (FOR TIMESTAMP/VERSION AS OF) are not permitted.");
                }
            }

            lastNonWsToken = token;
        }
    }

    /// <summary>
    /// SEC C-06: Token-level nesting check before parsing (0 disables only this check).
    /// </summary>
    private void EnsureNestingDepthWithinLimit(IList<IToken> allTokens, ReadOnlySpan<char> source, ParseCancellation cancellation)
    {
        if (MaxNestingDepth <= 0) return;

        int parenDepth = 0;   // ( ) and [ ]
        int blockDepth = 0;   // CASE/BEGIN ... END
        int typeDepth = 0;    // ARRAY< / MAP< ... >
        int lambdaCount = 0;  // '->' (lambda bodies nest without closing token)
        int unaryRun = 0;     // consecutive prefix operators (- + NOT)
        int previousType = 0;
        int index = 0;

        foreach (var token in allTokens)
        {
            if ((index++ & 1023) == 0)
            {
                cancellation.ThrowIfCanceled();
            }

            int type = token.Type;
            if (type == SqlBaseLexer.WS || type == SqlBaseLexer.SIMPLE_COMMENT || type == SqlBaseLexer.BRACKETED_COMMENT)
                continue;

            var text = TokenText(token, source, out _);
            bool isUnaryCandidate = false;

            if (type == SqlBaseLexer.CASE || type == SqlBaseLexer.BEGIN)
            {
                blockDepth++;
            }
            else if (type == SqlBaseLexer.END)
            {
                if (blockDepth > 0) blockDepth--;
            }
            else if (type == SqlBaseLexer.LT && (previousType == SqlBaseLexer.ARRAY || previousType == SqlBaseLexer.MAP))
            {
                typeDepth++;
            }
            else if (type == SqlBaseLexer.GT)
            {
                if (typeDepth > 0) typeDepth--;
            }
            else if (type == SqlBaseLexer.MINUS || type == SqlBaseLexer.PLUS || type == SqlBaseLexer.NOT)
            {
                isUnaryCandidate = true;
            }
            else if (text.SequenceEqual("(") || text.SequenceEqual("["))
            {
                parenDepth++;
            }
            else if (text.SequenceEqual(")") || text.SequenceEqual("]"))
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (text.SequenceEqual("->"))
            {
                lambdaCount++;
            }

            unaryRun = isUnaryCandidate ? unaryRun + 1 : 0;
            previousType = type;

            int depth = parenDepth + blockDepth + typeDepth + lambdaCount + unaryRun;
            if (depth > MaxNestingDepth)
            {
                throw new ParseCanceledException(
                    $"line {token.Line}:{token.Column}: SQL nesting depth exceeds the maximum allowed limit of {MaxNestingDepth}.");
            }
        }
    }

    /// <summary>
    /// SEC C-06: Iterative (non-recursive) parse tree depth check, executed before any recursive tree walk.
    /// </summary>
    private void EnsureParseTreeDepthWithinLimit(IParseTree tree, ParseCancellation cancellation)
    {
        if (MaxParseTreeDepth <= 0) return;

        var stack = new Stack<(IParseTree Node, int Depth)>();
        stack.Push((tree, 1));
        int visited = 0;
        while (stack.Count > 0)
        {
            if ((visited++ & 1023) == 0)
            {
                cancellation.ThrowIfCanceled();
            }

            var (node, depth) = stack.Pop();
            if (depth > MaxParseTreeDepth)
            {
                throw new ParseCanceledException(
                    $"SQL parse tree depth exceeds the maximum allowed limit of {MaxParseTreeDepth}.");
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                var child = node.GetChild(i);
                if (child.ChildCount > 0)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }
    }

    private readonly Lazy<GovernedSqlCompiler> _compiler;
    private readonly AntlrCacheGenerations _generations;

    public FastSqlEngine()
        : this(AntlrCacheGenerations.Shared)
    {
    }

    /// <summary>ARCH-MNT-15: an engine whose parsers and lexers use the given ANTLR cache generations (a test seam; production uses the shared one).</summary>
    public FastSqlEngine(AntlrCacheGenerations generations)
    {
        ArgumentNullException.ThrowIfNull(generations);
        _generations = generations;
        _parserPolicy = new ParserPooledObjectPolicy(generations);
        _parserPool = new DefaultObjectPool<SqlBaseParser>(_parserPolicy);
        _compiler = new Lazy<GovernedSqlCompiler>(
            () => new GovernedSqlCompiler(this, Ast.Capabilities.DialectCapabilityTable.Default, CompileCache));
    }

    /// <summary>Cache of verified, value-free compile templates (SEC-ADG-01).</summary>
    public Ast.Emit.CompiledSqlTemplateCache CompileCache { get; } = new();

    /// <inheritdoc />
    public Ast.Emit.CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken cancellationToken) =>
        _compiler.Value.Compile(sql, request, cancellationToken);

    private static readonly Governance.PolicyKeyQueryCompiler KeyQueryCompiler = new();

    /// <summary>
    /// Issue #107 (PLAN-GOV-107 A-3): compiles the key query of a registered correlated EXISTS through the policy-subquery controls. The
    /// engine entry point delegates to the stateless <see cref="Governance.PolicyKeyQueryCompiler"/>.
    /// </summary>
    /// <inheritdoc />
    public Governance.CompiledKeyQuery CompileKeyQuery(Governance.PolicyKeyQueryRequest request, CancellationToken ct) =>
        KeyQueryCompiler.CompileKeyQuery(request, ct);

    /// <summary>
    /// Analyzes a SQL query AST for statement type, referenced physical tables, projected columns, and query metrics.
    /// </summary>
    public Analysis.SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql)
    {
        return Analyze(sql, null, CancellationToken.None);
    }

    public Analysis.SqlQueryMetadata Analyze(
        ReadOnlyMemory<char> sql,
        SqlTokenSecurityOptions? tokenOptions,
        CancellationToken cancellationToken = default)
    {
        var (tree, _) = Parse(sql, tokenOptions, cancellationToken);
        var analyzer = new Analysis.SqlQueryAnalyzer();
        return analyzer.Analyze(tree);
    }
}
