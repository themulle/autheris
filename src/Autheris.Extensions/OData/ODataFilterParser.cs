namespace Autheris.Extensions.OData;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;

/// <summary>
/// Befund 2.1: Lightweight recursive-descent parser that translates OData v4 $filter expressions
/// into parameterized SQL WHERE clauses with Zero-Trust column tracking.
/// </summary>
public static class ODataFilterParser
{
    public const int MaxFilterLength = 4096;
    public const int MaxRecursionDepth = 32;

    public static TableFilterClause Parse(string filterExpression, DatabaseDialect defaultDialect = DatabaseDialect.PostgreSql, string paramPrefix = "@p_od_")
    {
        if (string.IsNullOrWhiteSpace(filterExpression))
        {
            throw new ArgumentException("Filter expression cannot be null or empty.", nameof(filterExpression));
        }

        if (filterExpression.Length > MaxFilterLength)
        {
            throw new GatewayInvalidQueryException(
                $"OData $filter expression exceeds maximum allowed length of {MaxFilterLength} characters (received {filterExpression.Length}).");
        }

        var tokens = Tokenize(filterExpression);
        var parser = new FilterAstParser(tokens, filterExpression);
        var ast = parser.Parse();

        // Compile to dialect SQL builder and extract referenced columns
        var referencedCols = new List<string>();
        ast.CollectReferencedColumns(referencedCols);
        var distinctCols = referencedCols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Compile parameterized clause
        string SqlBuilder(DatabaseDialect dialect)
        {
            var ctx = new SqlGenerationContext(dialect, paramPrefix);
            return ast.ToSql(ctx);
        }

        // Evaluate parameters once (values are dialect-independent)
        var paramCtx = new SqlGenerationContext(defaultDialect, paramPrefix);
        var defaultPredicate = ast.ToSql(paramCtx);

        return new TableFilterClause(
            SqlPredicate: defaultPredicate,
            Parameters: paramCtx.Parameters,
            ReferencedColumns: distinctCols)
        {
            DialectSqlFactory = SqlBuilder
        };
    }

    #region Tokenizer

    internal enum TokenType
    {
        Identifier,
        StringLiteral,
        NumberLiteral,
        BooleanLiteral,
        NullLiteral,
        DateTimeLiteral,
        GuidLiteral,
        OpenParen,
        CloseParen,
        Comma,
        Eq, Ne, Gt, Ge, Lt, Le,
        And, Or, Not,
        EndOfInput
    }

    internal readonly record struct Token(TokenType Type, string Text, object? Value, int Position);

    internal static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0;
        int len = input.Length;

        while (i < len)
        {
            char c = input[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '(')
            {
                tokens.Add(new Token(TokenType.OpenParen, "(", null, i++));
                continue;
            }
            if (c == ')')
            {
                tokens.Add(new Token(TokenType.CloseParen, ")", null, i++));
                continue;
            }
            if (c == ',')
            {
                tokens.Add(new Token(TokenType.Comma, ",", null, i++));
                continue;
            }

            // String literal: '...'
            if (c == '\'')
            {
                int start = i++;
                var sb = new StringBuilder();
                bool closed = false;

                while (i < len)
                {
                    if (input[i] == '\'')
                    {
                        if (i + 1 < len && input[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i += 2;
                            continue;
                        }
                        closed = true;
                        i++;
                        break;
                    }
                    sb.Append(input[i++]);
                }

                if (!closed)
                {
                    throw new GatewayInvalidQueryException($"Unterminated string literal in $filter expression at position {start}.");
                }

                tokens.Add(new Token(TokenType.StringLiteral, sb.ToString(), sb.ToString(), start));
                continue;
            }

            // Number: -?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?(m|M|f|F|d|D|l|L)?
            if (char.IsDigit(c) || (c == '-' && i + 1 < len && (char.IsDigit(input[i + 1]) || input[i + 1] == '.')))
            {
                // Check if it's an ISO-8601 date: YYYY-MM-DD...
                if (char.IsDigit(c) && i + 9 < len && input[i + 4] == '-' && input[i + 7] == '-')
                {
                    int start = i;
                    while (i < len && !char.IsWhiteSpace(input[i]) && input[i] != ')' && input[i] != ',')
                    {
                        i++;
                    }
                    var dateStr = input[start..i];
                    if (DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                    {
                        tokens.Add(new Token(TokenType.DateTimeLiteral, dateStr, dto.UtcDateTime, start));
                        continue;
                    }
                    if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                    {
                        tokens.Add(new Token(TokenType.DateTimeLiteral, dateStr, dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc), start));
                        continue;
                    }
                    // Reset if not date
                    i = start;
                }

                int numStart = i;
                if (c == '-') i++;
                while (i < len && char.IsDigit(input[i])) i++;
                bool isDecimal = false;
                if (i < len && input[i] == '.' && i + 1 < len && char.IsDigit(input[i + 1]))
                {
                    isDecimal = true;
                    i++;
                    while (i < len && char.IsDigit(input[i])) i++;
                }

                // Suffix (L, M, F, D)
                char suffix = '\0';
                if (i < len && "lLmMfFdD".Contains(input[i]))
                {
                    suffix = input[i++];
                }

                var numStr = input[numStart..i];
                var rawNum = suffix != '\0' ? numStr[..^1] : numStr;

                object numVal;
                if (suffix is 'l' or 'L')
                {
                    numVal = long.Parse(rawNum, CultureInfo.InvariantCulture);
                }
                else if (suffix is 'm' or 'M' || (isDecimal && suffix == '\0'))
                {
                    numVal = decimal.Parse(rawNum, CultureInfo.InvariantCulture);
                }
                else if (suffix is 'f' or 'F')
                {
                    numVal = float.Parse(rawNum, CultureInfo.InvariantCulture);
                }
                else if (suffix is 'd' or 'D')
                {
                    numVal = double.Parse(rawNum, CultureInfo.InvariantCulture);
                }
                else
                {
                    if (long.TryParse(rawNum, CultureInfo.InvariantCulture, out var parsedLong))
                    {
                        numVal = (parsedLong >= int.MinValue && parsedLong <= int.MaxValue) ? (int)parsedLong : parsedLong;
                    }
                    else
                    {
                        numVal = decimal.Parse(rawNum, CultureInfo.InvariantCulture);
                    }
                }

                tokens.Add(new Token(TokenType.NumberLiteral, numStr, numVal, numStart));
                continue;
            }

            // Word: Identifier or Keyword or Function
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == '/'))
                {
                    i++;
                }

                var word = input[start..i];

                // Check for Guid: guid'...' or prefix
                if (string.Equals(word, "guid", StringComparison.OrdinalIgnoreCase) && i < len && input[i] == '\'')
                {
                    i++; // skip '
                    int guidStart = i;
                    while (i < len && input[i] != '\'') i++;
                    if (i >= len) throw new GatewayInvalidQueryException("Unterminated guid literal in $filter expression.");
                    var guidText = input[guidStart..i];
                    i++; // skip closing '
                    if (Guid.TryParse(guidText, out var parsedGuid))
                    {
                        tokens.Add(new Token(TokenType.GuidLiteral, guidText, parsedGuid, start));
                        continue;
                    }
                    throw new GatewayInvalidQueryException($"Invalid guid literal '{guidText}' in $filter expression.");
                }

                // Check for datetime'...'
                if (string.Equals(word, "datetime", StringComparison.OrdinalIgnoreCase) && i < len && input[i] == '\'')
                {
                    i++;
                    int dtStart = i;
                    while (i < len && input[i] != '\'') i++;
                    if (i >= len) throw new GatewayInvalidQueryException("Unterminated datetime literal in $filter expression.");
                    var dtText = input[dtStart..i];
                    i++;
                    if (DateTimeOffset.TryParse(dtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                    {
                        tokens.Add(new Token(TokenType.DateTimeLiteral, dtText, dto.UtcDateTime, start));
                        continue;
                    }
                    throw new GatewayInvalidQueryException($"Invalid datetime literal '{dtText}' in $filter expression.");
                }

                var lowerWord = word.ToLowerInvariant();
                switch (lowerWord)
                {
                    case "eq":
                        tokens.Add(new Token(TokenType.Eq, word, null, start));
                        break;
                    case "ne":
                        tokens.Add(new Token(TokenType.Ne, word, null, start));
                        break;
                    case "gt":
                        tokens.Add(new Token(TokenType.Gt, word, null, start));
                        break;
                    case "ge":
                        tokens.Add(new Token(TokenType.Ge, word, null, start));
                        break;
                    case "lt":
                        tokens.Add(new Token(TokenType.Lt, word, null, start));
                        break;
                    case "le":
                        tokens.Add(new Token(TokenType.Le, word, null, start));
                        break;
                    case "and":
                        tokens.Add(new Token(TokenType.And, word, null, start));
                        break;
                    case "or":
                        tokens.Add(new Token(TokenType.Or, word, null, start));
                        break;
                    case "not":
                        tokens.Add(new Token(TokenType.Not, word, null, start));
                        break;
                    case "true":
                        tokens.Add(new Token(TokenType.BooleanLiteral, word, true, start));
                        break;
                    case "false":
                        tokens.Add(new Token(TokenType.BooleanLiteral, word, false, start));
                        break;
                    case "null":
                        tokens.Add(new Token(TokenType.NullLiteral, word, null, start));
                        break;
                    default:
                        tokens.Add(new Token(TokenType.Identifier, word, word, start));
                        break;
                }
                continue;
            }

            throw new GatewayInvalidQueryException($"Unexpected character '{c}' in $filter expression at position {i}.");
        }

        tokens.Add(new Token(TokenType.EndOfInput, string.Empty, null, len));
        return tokens;
    }

    #endregion

    #region AST Parser

    private sealed class FilterAstParser
    {
        private readonly List<Token> _tokens;
        private readonly string _rawInput;
        private int _pos;
        private int _depth;

        public FilterAstParser(List<Token> tokens, string rawInput)
        {
            _tokens = tokens;
            _rawInput = rawInput;
            _pos = 0;
            _depth = 0;
        }

        private void EnterRecursion()
        {
            _depth++;
            if (_depth > MaxRecursionDepth)
            {
                throw new GatewayInvalidQueryException(
                    $"OData $filter expression exceeds maximum recursion depth of {MaxRecursionDepth}.");
            }
        }

        private void ExitRecursion()
        {
            _depth--;
        }

        private Token Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];

        private Token Consume(TokenType expected)
        {
            var token = Current;
            if (token.Type != expected)
            {
                throw new GatewayInvalidQueryException(
                    $"Expected '{expected}' but found '{token.Text}' in $filter expression at position {token.Position}.");
            }
            _pos++;
            return token;
        }

        private bool Match(TokenType type)
        {
            if (Current.Type == type)
            {
                _pos++;
                return true;
            }
            return false;
        }

        public AstNode Parse()
        {
            var node = ParseOr();
            if (Current.Type != TokenType.EndOfInput)
            {
                throw new GatewayInvalidQueryException(
                    $"Unexpected token '{Current.Text}' at position {Current.Position} in $filter expression.");
            }
            return node;
        }

        // OrExpression := AndExpression ('or' AndExpression)*
        private AstNode ParseOr()
        {
            EnterRecursion();
            try
            {
                var left = ParseAnd();
                while (Match(TokenType.Or))
                {
                    var right = ParseAnd();
                    left = new BinaryOpNode(left, "OR", right);
                }
                return left;
            }
            finally
            {
                ExitRecursion();
            }
        }

        // AndExpression := NotExpression ('and' NotExpression)*
        private AstNode ParseAnd()
        {
            var left = ParseNot();
            while (Match(TokenType.And))
            {
                var right = ParseNot();
                left = new BinaryOpNode(left, "AND", right);
            }
            return left;
        }

        // NotExpression := ('not')? Comparison
        private AstNode ParseNot()
        {
            if (Match(TokenType.Not))
            {
                EnterRecursion();
                try
                {
                    var inner = ParseNot();
                    return new NotOpNode(inner);
                }
                finally
                {
                    ExitRecursion();
                }
            }
            return ParseComparison();
        }

        // Comparison := Primary ((eq | ne | gt | ge | lt | le) Primary)?
        private AstNode ParseComparison()
        {
            var left = ParsePrimary();

            if (Current.Type is TokenType.Eq or TokenType.Ne or TokenType.Gt or TokenType.Ge or TokenType.Lt or TokenType.Le)
            {
                var opToken = Current;
                _pos++;
                var right = ParsePrimary();

                string op = opToken.Type switch
                {
                    TokenType.Eq => "eq",
                    TokenType.Ne => "ne",
                    TokenType.Gt => "gt",
                    TokenType.Ge => "ge",
                    TokenType.Lt => "lt",
                    TokenType.Le => "le",
                    _ => throw new InvalidOperationException()
                };

                return new BinaryOpNode(left, op, right);
            }

            return left;
        }

        // Primary := FunctionCall | '(' OrExpression ')' | Literal | Identifier
        private AstNode ParsePrimary()
        {
            if (Match(TokenType.OpenParen))
            {
                var inner = ParseOr();
                Consume(TokenType.CloseParen);
                return new GroupingNode(inner);
            }

            var token = Current;
            switch (token.Type)
            {
                case TokenType.StringLiteral:
                case TokenType.NumberLiteral:
                case TokenType.BooleanLiteral:
                case TokenType.DateTimeLiteral:
                case TokenType.GuidLiteral:
                    _pos++;
                    return new LiteralNode(token.Value);

                case TokenType.NullLiteral:
                    _pos++;
                    return new NullLiteralNode();

                case TokenType.Identifier:
                    _pos++;
                    // Check if it's a function call: identifier '('
                    if (Current.Type == TokenType.OpenParen)
                    {
                        return ParseFunctionCall(token.Text);
                    }
                    return new ColumnNode(token.Text);

                default:
                    throw new GatewayInvalidQueryException(
                        $"Unexpected token '{token.Text}' in $filter expression at position {token.Position}.");
            }
        }

        private AstNode ParseFunctionCall(string functionName)
        {
            EnterRecursion();
            try
            {
                Consume(TokenType.OpenParen);
                var args = new List<AstNode>();

                if (Current.Type != TokenType.CloseParen)
                {
                    args.Add(ParseOr());
                    while (Match(TokenType.Comma))
                    {
                        args.Add(ParseOr());
                    }
                }

                Consume(TokenType.CloseParen);

                var fn = functionName.ToLowerInvariant();
                switch (fn)
                {
                    case "contains":
                    case "startswith":
                    case "endswith":
                    case "tolower":
                    case "toupper":
                        return new FunctionCallNode(fn, args);
                    default:
                        throw new GatewayInvalidQueryException(
                            $"The function '{functionName}' is not supported in $filter expressions.");
                }
            }
            finally
            {
                ExitRecursion();
            }
        }
    }

    #endregion

    #region AST Nodes & SQL Generation

    internal sealed class SqlGenerationContext
    {
        public DatabaseDialect Dialect { get; }
        public string ParamPrefix { get; }
        public Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);
        private int _counter;
        public int Depth { get; set; }

        public SqlGenerationContext(DatabaseDialect dialect, string paramPrefix)
        {
            Dialect = dialect;
            ParamPrefix = paramPrefix;
            _counter = 0;
            Depth = 0;
        }

        public void EnterDepth()
        {
            Depth++;
            if (Depth > MaxRecursionDepth + 5)
            {
                throw new GatewayInvalidQueryException(
                    $"AST recursion depth exceeds maximum limit of {MaxRecursionDepth}.");
            }
        }

        public void ExitDepth() => Depth--;

        public string AddParameter(object? value)
        {
            var name = $"{ParamPrefix}{_counter++}";
            Parameters[name] = value;
            return name;
        }
    }

    internal abstract class AstNode
    {
        public abstract string ToSql(SqlGenerationContext ctx);
        public abstract void CollectReferencedColumns(List<string> columns, int depth = 0);
    }

    internal sealed class GroupingNode(AstNode inner) : AstNode
    {
        public AstNode Inner { get; } = inner;

        public override string ToSql(SqlGenerationContext ctx)
        {
            ctx.EnterDepth();
            try
            {
                return $"({Inner.ToSql(ctx)})";
            }
            finally
            {
                ctx.ExitDepth();
            }
        }

        public override void CollectReferencedColumns(List<string> columns, int depth = 0)
        {
            if (depth > MaxRecursionDepth + 5)
            {
                throw new GatewayInvalidQueryException(
                    $"AST recursion depth exceeds maximum limit of {MaxRecursionDepth}.");
            }
            Inner.CollectReferencedColumns(columns, depth + 1);
        }
    }

    internal sealed class ColumnNode(string name) : AstNode
    {
        public string Name { get; } = name.Contains('/') ? name.Split('/')[^1] : name;

        public override string ToSql(SqlGenerationContext ctx) => ctx.Dialect.QuoteIdentifier(Name);

        public override void CollectReferencedColumns(List<string> columns, int depth = 0) => columns.Add(Name);
    }

    internal sealed class LiteralNode(object? value) : AstNode
    {
        public object? Value { get; } = value;

        public override string ToSql(SqlGenerationContext ctx) => ctx.AddParameter(Value);

        public override void CollectReferencedColumns(List<string> columns, int depth = 0) { }
    }

    internal sealed class NullLiteralNode : AstNode
    {
        public override string ToSql(SqlGenerationContext ctx) => "NULL";

        public override void CollectReferencedColumns(List<string> columns, int depth = 0) { }
    }

    internal sealed class NotOpNode(AstNode inner) : AstNode
    {
        public AstNode Inner { get; } = inner;

        public override string ToSql(SqlGenerationContext ctx)
        {
            ctx.EnterDepth();
            try
            {
                return $"NOT ({Inner.ToSql(ctx)})";
            }
            finally
            {
                ctx.ExitDepth();
            }
        }

        public override void CollectReferencedColumns(List<string> columns, int depth = 0)
        {
            if (depth > MaxRecursionDepth + 5)
            {
                throw new GatewayInvalidQueryException(
                    $"AST recursion depth exceeds maximum limit of {MaxRecursionDepth}.");
            }
            Inner.CollectReferencedColumns(columns, depth + 1);
        }
    }

    internal sealed class BinaryOpNode(AstNode left, string op, AstNode right) : AstNode
    {
        public AstNode Left { get; } = left;
        public string Op { get; } = op;
        public AstNode Right { get; } = right;

        public override string ToSql(SqlGenerationContext ctx)
        {
            ctx.EnterDepth();
            try
            {
                // Handle null comparison
                if (Op.Equals("eq", StringComparison.OrdinalIgnoreCase))
                {
                    if (Right is NullLiteralNode)
                    {
                        return $"({Left.ToSql(ctx)} IS NULL)";
                    }
                    if (Left is NullLiteralNode)
                    {
                        return $"({Right.ToSql(ctx)} IS NULL)";
                    }
                    return $"({Left.ToSql(ctx)} = {Right.ToSql(ctx)})";
                }

                if (Op.Equals("ne", StringComparison.OrdinalIgnoreCase))
                {
                    if (Right is NullLiteralNode)
                    {
                        return $"({Left.ToSql(ctx)} IS NOT NULL)";
                    }
                    if (Left is NullLiteralNode)
                    {
                        return $"({Right.ToSql(ctx)} IS NOT NULL)";
                    }
                    return $"({Left.ToSql(ctx)} <> {Right.ToSql(ctx)})";
                }

                var sqlOp = Op.ToLowerInvariant() switch
                {
                    "and" => "AND",
                    "or" => "OR",
                    "gt" => ">",
                    "ge" => ">=",
                    "lt" => "<",
                    "le" => "<=",
                    _ => Op.ToUpperInvariant()
                };

                return $"({Left.ToSql(ctx)} {sqlOp} {Right.ToSql(ctx)})";
            }
            finally
            {
                ctx.ExitDepth();
            }
        }

        public override void CollectReferencedColumns(List<string> columns, int depth = 0)
        {
            if (depth > MaxRecursionDepth + 5)
            {
                throw new GatewayInvalidQueryException(
                    $"AST recursion depth exceeds maximum limit of {MaxRecursionDepth}.");
            }
            Left.CollectReferencedColumns(columns, depth + 1);
            Right.CollectReferencedColumns(columns, depth + 1);
        }
    }

    internal sealed class FunctionCallNode(string name, IReadOnlyList<AstNode> args) : AstNode
    {
        public string Name { get; } = name;
        public IReadOnlyList<AstNode> Args { get; } = args;

        public override string ToSql(SqlGenerationContext ctx)
        {
            ctx.EnterDepth();
            try
            {
                switch (Name)
                {
                    case "contains":
                        if (Args.Count != 2) throw new GatewayInvalidQueryException("Function 'contains' requires 2 arguments.");
                        var targetContains = Args[0].ToSql(ctx);
                        var patternContains = GetLikePattern(Args[1], "%{0}%");
                        var pContains = ctx.AddParameter(patternContains);
                        return $"({targetContains} LIKE {pContains})";

                    case "startswith":
                        if (Args.Count != 2) throw new GatewayInvalidQueryException("Function 'startswith' requires 2 arguments.");
                        var targetStarts = Args[0].ToSql(ctx);
                        var patternStarts = GetLikePattern(Args[1], "{0}%");
                        var pStarts = ctx.AddParameter(patternStarts);
                        return $"({targetStarts} LIKE {pStarts})";

                    case "endswith":
                        if (Args.Count != 2) throw new GatewayInvalidQueryException("Function 'endswith' requires 2 arguments.");
                        var targetEnds = Args[0].ToSql(ctx);
                        var patternEnds = GetLikePattern(Args[1], "%{0}");
                        var pEnds = ctx.AddParameter(patternEnds);
                        return $"({targetEnds} LIKE {pEnds})";

                    case "tolower":
                        if (Args.Count != 1) throw new GatewayInvalidQueryException("Function 'tolower' requires 1 argument.");
                        return $"LOWER({Args[0].ToSql(ctx)})";

                    case "toupper":
                        if (Args.Count != 1) throw new GatewayInvalidQueryException("Function 'toupper' requires 1 argument.");
                        return $"UPPER({Args[0].ToSql(ctx)})";

                    default:
                        throw new GatewayInvalidQueryException($"Function '{Name}' is not supported.");
                }
            }
            finally
            {
                ctx.ExitDepth();
            }
        }

        private static string GetLikePattern(AstNode node, string format)
        {
            if (node is LiteralNode lit && lit.Value != null)
            {
                return string.Format(CultureInfo.InvariantCulture, format, lit.Value);
            }
            throw new GatewayInvalidQueryException("String functions (contains, startswith, endswith) require a literal string as the pattern argument.");
        }

        public override void CollectReferencedColumns(List<string> columns, int depth = 0)
        {
            if (depth > MaxRecursionDepth + 5)
            {
                throw new GatewayInvalidQueryException(
                    $"AST recursion depth exceeds maximum limit of {MaxRecursionDepth}.");
            }
            foreach (var arg in Args)
            {
                arg.CollectReferencedColumns(columns, depth + 1);
            }
        }
    }

    #endregion
}
