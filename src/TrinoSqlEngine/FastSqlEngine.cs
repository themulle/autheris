namespace TrinoSqlEngine;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Microsoft.Extensions.ObjectPool;

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

/// <summary>
/// SEC P-04: Immutable, per-call token-level security switches. Passed explicitly to
/// <see cref="FastSqlEngine.Parse(ReadOnlyMemory{char}, SqlTokenSecurityOptions?, CancellationToken)"/> so that
/// concurrent callers sharing one engine instance can never influence each other's checks.
/// </summary>
public sealed record SqlTokenSecurityOptions
{
    /// <summary>All switches off (pure syntax parsing, e.g. Trino compliance fixtures).</summary>
    public static SqlTokenSecurityOptions None { get; } = new();

    /// <summary>Strict preset with all token security switches enabled.</summary>
    public static SqlTokenSecurityOptions Strict { get; } = new()
    {
        RejectComments = true,
        RejectBackslashInStrings = true,
        RejectEscapedStringLiterals = true,
        RejectDollarQuoting = true,
        RejectNonAsciiIdentifiers = true,
        RejectDotsInQuotedIdentifiers = true,
        RejectTimeTravelQueries = true
    };

    /// <summary>SQ-02: Reject comments.</summary>
    public bool RejectComments { get; init; }

    /// <summary>SQ-01: Reject backslashes in string literals.</summary>
    public bool RejectBackslashInStrings { get; init; }

    /// <summary>SQ-01: Reject E'...' string type constructors.</summary>
    public bool RejectEscapedStringLiterals { get; init; }

    /// <summary>SQ-02: Reject dollar-quoted strings.</summary>
    public bool RejectDollarQuoting { get; init; }

    /// <summary>SQ-10: Reject unquoted identifiers containing non-ASCII characters.</summary>
    public bool RejectNonAsciiIdentifiers { get; init; }

    /// <summary>SQ-11: Reject dots inside quoted identifiers.</summary>
    public bool RejectDotsInQuotedIdentifiers { get; init; }

    /// <summary>SQ-13: Reject time-travel syntax (FOR TIMESTAMP/VERSION AS OF).</summary>
    public bool RejectTimeTravelQueries { get; init; }

    /// <summary>
    /// Derives the token switches from <see cref="RlsOptions"/>. Dollar quoting is always rejected for SQL Server targets.
    /// </summary>
    public static SqlTokenSecurityOptions FromRlsOptions(RlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SqlTokenSecurityOptions
        {
            RejectComments = options.RejectComments,
            RejectBackslashInStrings = options.RejectBackslashInStrings,
            RejectEscapedStringLiterals = options.RejectEscapedStringLiterals,
            RejectDollarQuoting = options.RejectDollarQuoting || options.TargetDialect == TargetSqlDialect.SqlServer,
            RejectNonAsciiIdentifiers = options.RejectNonAsciiIdentifiers,
            RejectDotsInQuotedIdentifiers = options.RejectDotsInQuotedIdentifiers,
            RejectTimeTravelQueries = options.RejectTimeTravelQueries
        };
    }
}

/// <summary>
/// SQ-08: Cooperative cancellation flag for a single parse. ANTLR has no native cancellation; the flag is checked by
/// <see cref="CancellableTokenStream"/> on every token access (adaptive prediction and matching).
/// </summary>
internal sealed class ParseCancellation
{
    private volatile bool _canceled;

    public bool IsCanceled => _canceled;

    public void Cancel() => _canceled = true;

    public void ThrowIfCanceled()
    {
        if (_canceled)
        {
            throw new ParseCanceledException("SQL parsing was aborted because the parse time budget was exceeded or the caller canceled the request.");
        }
    }
}

/// <summary>
/// SQ-08: Token stream that aborts the parser (via <see cref="ParseCanceledException"/>) as soon as the parse is canceled.
/// </summary>
internal sealed class CancellableTokenStream : CommonTokenStream
{
    private readonly ParseCancellation _cancellation;

    public CancellableTokenStream(ITokenSource tokenSource, ParseCancellation cancellation)
        : base(tokenSource)
    {
        _cancellation = cancellation;
    }

    public override IToken LT(int k)
    {
        _cancellation.ThrowIfCanceled();
        return base.LT(k);
    }

    public override int LA(int i)
    {
        _cancellation.ThrowIfCanceled();
        return base.LA(i);
    }

    public override void Consume()
    {
        _cancellation.ThrowIfCanceled();
        base.Consume();
    }
}

public sealed partial class FastSqlEngine
{
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

    private readonly ObjectPool<SqlBaseParser> _parserPool = 
        new DefaultObjectPool<SqlBaseParser>(new ParserPooledObjectPolicy());

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

    // Defaults for direct Parse/ParseExpression/Analyze calls without explicit SqlTokenSecurityOptions.
    // SEC P-06: These stay false so that plain syntax parsing (Trino compliance fixtures with comments, time travel,
    // quoted dotted identifiers) keeps working. The security-relevant rewrite path (RewriteRls) never uses them; it
    // derives its switches from RlsOptions, whose defaults are secure (true).

    /// <summary>SQ-02: When true, comments are rejected to prevent dialect comment discrepancies (direct parsing only).</summary>
    public bool RejectComments { get; set; } = false;

    /// <summary>SQ-01: When true, backslash escapes in string literals are rejected (direct parsing only).</summary>
    public bool RejectBackslashInStrings { get; set; } = false;

    /// <summary>SQ-01: When true, string type constructors like E'...' are rejected (direct parsing only).</summary>
    public bool RejectEscapedStringLiterals { get; set; } = false;

    /// <summary>SQ-02: When true, dollar-quoted strings ($$...$$) are rejected (direct parsing only).</summary>
    public bool RejectDollarQuoting { get; set; } = false;

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
        return RunWithParseBudget(
            cancellation => ParseOnCurrentThread(sql, effectiveOptions, cancellation, static parser => parser.singleStatement()),
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
        return RunWithParseBudget(
            cancellation => ParseOnCurrentThread(sql, effectiveOptions, cancellation, static parser => parser.standaloneExpression()),
            cancellationToken);
    }

    /// <summary>
    /// SQ-08: Runs <paramref name="work"/> on a dedicated thread with <see cref="ParseThreadStackSize"/> and waits at most
    /// <see cref="ParseTimeout"/>. On timeout or cancellation the parse is aborted cooperatively via
    /// <see cref="ParseCancellation"/>. The parser thread slot is released by the parser thread itself, so the limiter
    /// bounds the number of live parser threads even when callers time out.
    /// </summary>
    private TResult RunWithParseBudget<TResult>(Func<ParseCancellation, TResult> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TimeSpan budget = ParseTimeout;
        long startTimestamp = Stopwatch.GetTimestamp();
        SemaphoreSlim limiter = ParseConcurrencyLimiter ?? SharedParseConcurrencyLimiter;

        if (!limiter.Wait(RemainingMilliseconds(budget, startTimestamp), cancellationToken))
        {
            throw new ParseCanceledException(
                "SQL parsing was rejected: all concurrent parser slots are in use and none became free within the parse time budget.",
                new TimeoutException("No parser thread slot became available within the parse time budget."));
        }

        var cancellation = new ParseCancellation();
        var completed = new ManualResetEventSlim(false);
        TResult? result = default;
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(
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
            ParseThreadStackSize)
        {
            IsBackground = true,
            Name = "FastSqlEngine.Parse"
        };

        try
        {
            thread.Start();
        }
        catch
        {
            limiter.Release();
            completed.Dispose();
            throw;
        }

        bool finished;
        try
        {
            finished = completed.Wait(RemainingMilliseconds(budget, startTimestamp), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            cancellation.Cancel();
            throw;
        }

        if (!finished)
        {
            // The parser thread observes the flag on its next token access, returns its pooled parser and releases its slot.
            cancellation.Cancel();
            throw new ParseCanceledException(
                $"SQL parsing exceeded the time budget of {budget.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms.",
                new TimeoutException("SQL parsing exceeded the configured time budget."));
        }

        completed.Dispose();
        failure?.Throw();
        return result!;
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
        Func<SqlBaseParser, TTree> entryRule)
        where TTree : ParserRuleContext
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

        var charStream = new ZeroCopyCaseInsensitiveStream(sql);
        var lexer = new SqlBaseLexer(charStream);
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(ThrowingErrorListener.Instance);

        var tokens = new CancellableTokenStream(lexer, cancellation);

        // Lexer errors surface here as ParseCanceledException, exactly as they would during parsing.
        tokens.Fill();
        var allTokens = tokens.GetTokens();

        // SEC P-03: token security checks always run, independent of MaxNestingDepth.
        EnsureTokensAreSafe(allTokens, tokenOptions, cancellation);
        EnsureNestingDepthWithinLimit(allTokens, cancellation);

        var parser = _parserPool.Get();
        try
        {
            parser.TokenStream = tokens;

            TTree tree;

            // Stufe 1: Schneller SLL-Pfad (im Pool bereits vorkonfiguriert)
            try
            {
                tree = entryRule(parser);
            }
            catch (ParseCanceledException) when (!cancellation.IsCanceled)
            {
                // Stufe 2: Fallback auf LL(*)-Modus
                tokens.Reset();
                parser.Reset();
                parser.AddErrorListener(ThrowingErrorListener.Instance);
                // SEC N1: DefaultErrorStrategy holds per-parse state; never share an instance between threads.
                parser.ErrorHandler = new DefaultErrorStrategy();
                parser.Interpreter.PredictionMode = PredictionMode.LL;

                tree = entryRule(parser);
                if (parser.NumberOfSyntaxErrors > 0)
                {
                    throw new ParseCanceledException($"Parsing failed with {parser.NumberOfSyntaxErrors} syntax errors.");
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
    /// SEC P-03: Token-level security checks (SQ-01, SQ-02, SQ-10, SQ-11, SQ-13). Always executed, independent of
    /// <see cref="MaxNestingDepth"/>.
    /// </summary>
    private static void EnsureTokensAreSafe(IList<IToken> allTokens, SqlTokenSecurityOptions options, ParseCancellation cancellation)
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
            string? text = token.Text;

            // SQ-02: Block comment nesting check (/* ... /* ... */) - always active
            if (type == SqlBaseLexer.BRACKETED_COMMENT)
            {
                if (text != null && text.Length > 4 && text.IndexOf("/*", 2, StringComparison.Ordinal) >= 0)
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

            // SQ-01: Backslash escapes in string literals
            if (options.RejectBackslashInStrings && type == SqlBaseLexer.STRING && text != null && text.Contains('\\'))
            {
                throw new ParseCanceledException(
                    $"line {token.Line}:{token.Column}: Backslash escapes in string literals are not permitted due to dialect lexer differentials.");
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

            // SQ-02: Dollar quoting ($$...$$)
            if (options.RejectDollarQuoting && type == SqlBaseLexer.DOLLAR_STRING)
            {
                throw new ParseCanceledException(
                    $"line {token.Line}:{token.Column}: Dollar-quoted strings ($$...$$) are not permitted.");
            }

            // SQ-10: Non-ASCII characters outside strings/quoted identifiers
            if (options.RejectNonAsciiIdentifiers && text != null &&
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
                            $"line {token.Line}:{token.Column}: Non-ASCII characters outside string literals ('{text}') are not permitted.");
                    }
                }
            }

            // SQ-11: Dots inside quoted identifiers ("a.b")
            if (options.RejectDotsInQuotedIdentifiers && (type == SqlBaseLexer.QUOTED_IDENTIFIER || type == SqlBaseLexer.BACKQUOTED_IDENTIFIER) && text != null)
            {
                for (int ci = 0; ci < text.Length; ci++)
                {
                    char c = text[ci];
                    if (c == '.' || char.IsControl(c))
                    {
                        throw new ParseCanceledException(
                            $"line {token.Line}:{token.Column}: Dots inside quoted identifiers ({text}) are not permitted.");
                    }
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
    private void EnsureNestingDepthWithinLimit(IList<IToken> allTokens, ParseCancellation cancellation)
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

            string? text = token.Text;
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
            else if (text == "(" || text == "[")
            {
                parenDepth++;
            }
            else if (text == ")" || text == "]")
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (text == "->")
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

    /// <summary>
    /// Architecture A3: High-level Façade method for secure RLS rewriting.
    /// SEC P-04: The token security switches are derived from <paramref name="options"/> per call and passed to the
    /// parser as an immutable object; the engine's own properties are neither read nor modified.
    /// </summary>
    public string RewriteRls(ReadOnlyMemory<char> sql, RlsOptions? options = null)
    {
        return RewriteRls(sql, options, CancellationToken.None);
    }

    public string RewriteRls(ReadOnlyMemory<char> sql, RlsOptions? options, CancellationToken cancellationToken)
    {
        var effectiveOptions = options ?? new RlsOptions();
        var (tree, tokens) = Parse(sql, SqlTokenSecurityOptions.FromRlsOptions(effectiveOptions), cancellationToken);
        var listener = new RlsListener(tokens, effectiveOptions);
        ParseTreeWalker.Default.Walk(listener, tree);
        return listener.GetSecuredSql();
    }

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
