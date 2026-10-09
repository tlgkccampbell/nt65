using System.Text;

namespace Norristown.LanguageServer;

/// <summary>
/// Rewrites the expressions in a line of ca65 code as nt65 expressions with the same meaning.
/// <para>
/// ca65 and nt65 rank their operators differently. ca65's <c>.not</c> binds loosest of all, its
/// shifts and <c>&amp;</c> bind as tightly as <c>*</c>, and its <c>|</c> binds as tightly as
/// <c>+</c>. nt65 follows C. A textual swap of operator words would therefore change what an
/// expression means, so each expression is read with ca65's precedence and written back with the
/// parentheses that nt65's precedence and its required parentheses call for.
/// </para>
/// <para>
/// The text of the line is kept as it was apart from the operators it renames and the
/// parentheses it adds. A run of tokens that does not read as a ca65 expression is left as it
/// stands.
/// </para>
/// </summary>
internal static class Ca65Expressions
{
    /// <summary>
    /// ca65's operator words, mapped to the symbols nt65 uses for them, and the ca65 directive
    /// names that nt65 renames, mapped to their nt65 names.
    /// </summary>
    private static readonly Dictionary<string, string> words = new(StringComparer.OrdinalIgnoreCase)
    {
        [".bitand"] = "&",
        [".bitor"] = "|",
        [".bitxor"] = "^",
        [".bitnot"] = "~",
        [".and"] = "&&",
        [".or"] = "||",
        [".xor"] = "^^",
        [".not"] = "!",
        [".shl"] = "<<",
        [".shr"] = ">>",
        [".asciiz"] = ".strz",
        [".dbyt"] = ".beword",
        [".tag"] = ".type",
    };

    /// <summary>
    /// The ca65 functions and pseudo-variables that can start an expression where a directive
    /// could also stand. Any other word that starts with a dot is read as a directive there.
    /// </summary>
    private static readonly HashSet<string> functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".addrsize", ".asize", ".bank", ".bankbyte", ".blank", ".cap", ".capability", ".concat",
        ".cpu", ".def", ".defined", ".definedmacro", ".hibyte", ".hiword", ".ident", ".ismnem",
        ".ismnemonic", ".isize", ".left", ".lobyte", ".loword", ".match", ".max", ".mid", ".min",
        ".paramcount", ".ref", ".referenced", ".right", ".sizeof", ".sprintf", ".strat", ".string",
        ".strlen", ".tcount", ".time", ".version", ".xmatch", ".not", ".bitnot",
    };

    /// <summary>
    /// The directives whose operand starts with a name and an <c>=</c> that assigns rather than
    /// compares.
    /// </summary>
    private static readonly HashSet<string> assigning = new(StringComparer.OrdinalIgnoreCase)
    {
        ".const", ".func", ".set",
    };

    /// <summary>
    /// Returns a line of code with its expressions rewritten as nt65. The label, directive or
    /// mnemonic that starts the line is kept as it is, apart from a renamed directive, and so is
    /// the name an assignment defines.
    /// </summary>
    public static string Line(string code)
    {
        var tokens = Lex(code);
        return Written(tokens, Head(tokens));
    }

    /// <summary>
    /// Returns one ca65 expression rewritten as nt65, such as the value of a constant in an
    /// include file.
    /// </summary>
    public static string Expression(string text) => Written(Lex(text), 0);

    /// <summary>
    /// Returns the tokens rewritten as nt65, reading expressions from <paramref name="start"/> on.
    /// </summary>
    private static string Written(List<Token> tokens, int start)
    {
        var marks = new Marks(tokens.Count);
        var at = start;
        while (at < tokens.Count)
        {
            if (StartsExpression(tokens[at]) && new Reader(tokens, at).Read() is { } node)
            {
                Mark(node, marks);
                at = node.Last + 1;
            }
            else
            {
                at++;
            }
        }

        var text = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!marks.Tight[i])
                text.Append(token.Space);
            text.Append('(', marks.Opens[i]);
            text.Append(marks.Spelling[i] ?? Spelled(token));
            text.Append(')', marks.Closes[i]);
        }
        return text.ToString();
    }

    /// <summary>
    /// Returns the index of the first token after the line's labels, its directive or mnemonic,
    /// and the name an assignment defines.
    /// </summary>
    private static int Head(List<Token> tokens)
    {
        var at = 0;
        while (at < tokens.Count)
        {
            var token = tokens[at];
            if (token.Text is "}" or ":")
            {
                at++;
                continue;
            }
            if (token.Kind == TokenKind.Name && Next(tokens, at) is { Text: ":", Space.Length: 0 })
            {
                at += 2;
                continue;
            }
            break;
        }
        if (at >= tokens.Count)
            return at;

        var head = tokens[at];
        if (head.Kind == TokenKind.Name || head.Text == "*")
        {
            // `NAME = value`, `NAME := value` and `* = value` assign; anything else is a mnemonic
            // or a macro.
            return Next(tokens, at) is { Text: "=" or ":=" } ? at + 2 : at + 1;
        }
        if (head.Kind == TokenKind.Word && assigning.Contains(head.Text))
        {
            for (var i = at + 1; i < tokens.Count; i++)
            {
                if (tokens[i].Text is "=" or ":=")
                    return i + 1;
            }
            return at + 1;
        }
        return head.Kind == TokenKind.Word && !functions.Contains(head.Text) ? at + 1 : at;
    }

    /// <summary>Returns the token after <paramref name="at"/>, or null at the end of the line.</summary>
    private static Token? Next(List<Token> tokens, int at) => at + 1 < tokens.Count ? tokens[at + 1] : null;

    /// <summary>
    /// Returns a value indicating whether an expression can start at a token where a directive
    /// or a delimiter could also stand.
    /// </summary>
    private static bool StartsExpression(Token token) => token.Kind switch
    {
        TokenKind.Number or TokenKind.Literal or TokenKind.Name => true,
        TokenKind.Word => functions.Contains(token.Text),
        _ => token.Text is "(" or "*" or "+" or "-" or "~" or "!" or "<" or ">" or "^",
    };

    /// <summary>Returns a token's text with a ca65 word or <c>&lt;&gt;</c> renamed.</summary>
    private static string Spelled(Token token) =>
        token.Text == "<>" ? "!=" : words.TryGetValue(token.Text, out var renamed) ? renamed : token.Text;

    /// <summary>
    /// Marks the parentheses that make an expression read in nt65 as ca65 read it, and the
    /// operators it renames.
    /// </summary>
    private static void Mark(Node node, Marks marks)
    {
        switch (node)
        {
            case Binary binary:
                marks.Spelling[binary.Symbol] = binary.Operator == "==" ? "==" : null;
                if (Wrapped(binary.Operator, binary.Left, right: false))
                    marks.Wrap(binary.Left);
                if (Wrapped(binary.Operator, binary.Right, right: true))
                    marks.Wrap(binary.Right);
                Mark(binary.Left, marks);
                Mark(binary.Right, marks);
                break;

            case Unary unary:
                // A word operator needed the space after it, and the symbol that replaces it does
                // not. Nor does an operator in front of the parentheses added here.
                if (unary.Operand is Binary)
                    marks.Wrap(unary.Operand);
                if (unary.Word || unary.Operand is Binary)
                    marks.Tight[unary.Operand.First] = true;
                Mark(unary.Operand, marks);
                break;

            case Group group:
                foreach (var inner in group.Inner)
                    Mark(inner, marks);
                break;
        }
    }

    /// <summary>
    /// Returns a value indicating whether an operand of a binary operator needs parentheses in
    /// nt65 to keep the grouping ca65 gave it. That is so where nt65 binds the operand's operator
    /// more loosely, and where nt65 requires parentheses to make the order explicit.
    /// </summary>
    /// <param name="parent">The nt65 spelling of the binary operator.</param>
    /// <param name="operand">The operand.</param>
    /// <param name="right">A value indicating whether the operand is the right one.</param>
    private static bool Wrapped(string parent, Node operand, bool right)
    {
        switch (operand)
        {
            case Binary { Operator: var inner }:
                var outer = Level(parent);
                var level = Level(inner);
                return level > outer
                    || (right && level == outer)
                    || (Bitwise(parent) && inner != parent)
                    || (Logical(parent) && Logical(inner) && inner != parent);

            // nt65 rejects a byte operator followed by a binary operator, so `<label+1`, which
            // ca65 reads as the low byte plus one, becomes `(<label)+1`.
            case Unary unary:
                return unary.TakesByte;

            default:
                return false;
        }
    }

    /// <summary>
    /// Returns nt65's precedence level for a binary operator, where a lower level binds more
    /// tightly.
    /// </summary>
    private static int Level(string op) => op switch
    {
        "*" or "/" or ".mod" => 3,
        "+" or "-" => 4,
        "<<" or ">>" => 5,
        "<" or "<=" or ">" or ">=" => 6,
        "==" or "!=" => 7,
        "&" => 8,
        "^" => 9,
        "|" => 10,
        "&&" => 11,
        "^^" => 12,
        _ => 13,
    };

    /// <summary>
    /// Returns a value indicating whether an operator is one whose operands nt65 requires to be
    /// parenthesized when they use a different binary operator.
    /// </summary>
    private static bool Bitwise(string op) => op is "<<" or ">>" or "&" or "^" or "|";

    /// <summary>Returns a value indicating whether an operator is a logical one.</summary>
    private static bool Logical(string op) => op is "&&" or "^^" or "||";

    /// <summary>Splits a line of ca65 code into tokens, each with the whitespace before it.</summary>
    private static List<Token> Lex(string code)
    {
        var tokens = new List<Token>();
        var at = 0;
        while (at < code.Length)
        {
            var space = at;
            while (at < code.Length && char.IsWhiteSpace(code[at]))
                at++;
            if (at >= code.Length)
            {
                // Trailing whitespace stays with the line rather than with a token.
                tokens.Add(new Token(code[space..at], "", TokenKind.Other));
                break;
            }

            var start = at;
            var c = code[at];
            var kind = TokenKind.Other;
            if (c is '"' or '\'')
            {
                at++;
                while (at < code.Length && code[at] != c)
                    at += code[at] == '\\' ? 2 : 1;
                at = Math.Min(at + 1, code.Length);
                kind = TokenKind.Literal;
            }
            else if (char.IsDigit(c)
                || (c == '$' && at + 1 < code.Length && char.IsAsciiHexDigit(code[at + 1]))
                || (c == '%' && at + 1 < code.Length && code[at + 1] is '0' or '1'))
            {
                at++;
                while (at < code.Length && (char.IsLetterOrDigit(code[at]) || code[at] == '_'))
                    at++;
                kind = TokenKind.Number;
            }
            else if (c == '.' && at + 1 < code.Length && char.IsLetter(code[at + 1]))
            {
                at++;
                while (at < code.Length && (char.IsLetterOrDigit(code[at]) || code[at] == '_'))
                    at++;
                kind = TokenKind.Word;
            }
            else if (char.IsLetter(c) || c is '_' or '@' || (c == ':' && NameAfterScope(code, at)))
            {
                while (at < code.Length)
                {
                    if (char.IsLetterOrDigit(code[at]) || code[at] is '_' or '@')
                        at++;
                    else if (code[at] == ':' && NameAfterScope(code, at))
                        at += 2;
                    else
                        break;
                }
                kind = TokenKind.Name;
            }
            else
            {
                var pair = at + 1 < code.Length ? code.Substring(at, 2) : "";
                at += pair is "<>" or "<=" or ">=" or "<<" or ">>" or "&&" or "||" or "^^" or "==" or "!=" or ":="
                    ? 2
                    : 1;
            }
            tokens.Add(new Token(code[space..start], code[start..at], kind));
        }
        return tokens;
    }

    /// <summary>
    /// Returns a value indicating whether the text at <paramref name="at"/> is a <c>::</c> scope
    /// separator followed by a name.
    /// </summary>
    private static bool NameAfterScope(string code, int at) =>
        at + 2 < code.Length && code[at] == ':' && code[at + 1] == ':' && (char.IsLetter(code[at + 2]) || code[at + 2] == '_');

    /// <summary>Identifies the kind of a ca65 token.</summary>
    private enum TokenKind
    {
        /// <summary>A number.</summary>
        Number,

        /// <summary>A quoted character or string.</summary>
        Literal,

        /// <summary>A name, which may be a symbol, a mnemonic or a macro.</summary>
        Name,

        /// <summary>A word that starts with a dot, which is a directive, a function or an operator.</summary>
        Word,

        /// <summary>A symbol or a delimiter.</summary>
        Other,
    }

    /// <summary>Represents one token of a ca65 line and the whitespace before it.</summary>
    /// <param name="Space">The whitespace before the token.</param>
    /// <param name="Text">The token's text.</param>
    /// <param name="Kind">The kind of token.</param>
    private sealed record Token(string Space, string Text, TokenKind Kind);

    /// <summary>Represents an expression read with ca65's precedence, as the tokens it spans.</summary>
    /// <param name="First">The index of the expression's first token.</param>
    /// <param name="Last">The index of the expression's last token.</param>
    private abstract record Node(int First, int Last);

    /// <summary>Represents a binary operation.</summary>
    /// <param name="Operator">The nt65 spelling of the operator.</param>
    /// <param name="Symbol">The index of the operator's token.</param>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    private sealed record Binary(string Operator, int Symbol, Node Left, Node Right) : Node(Left.First, Right.Last);

    /// <summary>Represents a unary operation.</summary>
    /// <param name="First">The index of the operator's token.</param>
    /// <param name="Operand">The operand.</param>
    /// <param name="Word">A value indicating whether the operator is a ca65 word such as <c>.not</c>.</param>
    /// <param name="TakesByte">
    /// A value indicating whether the operator, or one applied inside it, takes a byte of its operand.
    /// </param>
    private sealed record Unary(int First, Node Operand, bool Word, bool TakesByte) : Node(First, Operand.Last);

    /// <summary>
    /// Represents a single token, or a parenthesized expression or a function call, whose inner
    /// expressions are read on their own.
    /// </summary>
    /// <param name="First">The index of the first token.</param>
    /// <param name="Last">The index of the last token.</param>
    /// <param name="Inner">The expressions inside the parentheses.</param>
    private sealed record Group(int First, int Last, IReadOnlyList<Node> Inner) : Node(First, Last);

    /// <summary>Holds the changes the rewrite makes around each token.</summary>
    /// <param name="count">The number of tokens.</param>
    private sealed class Marks(int count)
    {
        /// <summary>Gets the number of parentheses opened before each token.</summary>
        public int[] Opens { get; } = new int[count];

        /// <summary>Gets the number of parentheses closed after each token.</summary>
        public int[] Closes { get; } = new int[count];

        /// <summary>Gets a value for each token indicating whether the whitespace before it is dropped.</summary>
        public bool[] Tight { get; } = new bool[count];

        /// <summary>
        /// Gets the nt65 spelling of each <c>=</c> that the reader found comparing, or null for
        /// every other token.
        /// </summary>
        public string?[] Spelling { get; } = new string?[count];

        /// <summary>Puts an expression in parentheses.</summary>
        public void Wrap(Node node)
        {
            Opens[node.First]++;
            Closes[node.Last]++;
        }
    }

    /// <summary>Reads one expression with ca65's grammar and precedence.</summary>
    /// <param name="tokens">The line's tokens.</param>
    /// <param name="at">The index of the expression's first token.</param>
    private sealed class Reader(List<Token> tokens, int at)
    {
        /// <summary>Returns the expression, or null if the tokens do not read as one.</summary>
        public Node? Read() => Or();

        /// <summary>Reads the loosest level, ca65's <c>.or</c>.</summary>
        private Node? Or() => Chain(And, op => op is "||" or ".or" ? "||" : null);

        /// <summary>Reads ca65's <c>.and</c> and <c>.xor</c>.</summary>
        private Node? And() => Chain(Compare, op => op switch
        {
            "&&" or ".and" => "&&",
            ".xor" => "^^",
            _ => null,
        });

        /// <summary>Reads ca65's comparisons, where <c>=</c> compares.</summary>
        private Node? Compare() => Chain(Sum, op => op switch
        {
            "=" => "==",
            "<>" => "!=",
            "<" or ">" or "<=" or ">=" => op,
            _ => null,
        });

        /// <summary>Reads ca65's additive level, which includes <c>|</c>.</summary>
        private Node? Sum() => Chain(Product, op => op switch
        {
            "+" or "-" => op,
            "|" or ".bitor" => "|",
            _ => null,
        });

        /// <summary>
        /// Reads ca65's multiplicative level, which includes the shifts, <c>&amp;</c> and
        /// <c>^</c>.
        /// </summary>
        private Node? Product() => Chain(Factor, op => op switch
        {
            "*" or "/" => op,
            ".mod" => ".mod",
            "&" or ".bitand" => "&",
            "^" or ".bitxor" => "^",
            "<<" or ".shl" => "<<",
            ">>" or ".shr" => ">>",
            _ => null,
        });

        /// <summary>
        /// Reads a chain of left-associative operations at one level.
        /// </summary>
        /// <param name="operand">Reads an operand, which is the next tighter level.</param>
        /// <param name="spelling">Returns the nt65 spelling of an operator at this level, or null.</param>
        private Node? Chain(Func<Node?> operand, Func<string, string?> spelling)
        {
            if (operand() is not { } left)
                return null;
            while (at < tokens.Count && spelling(tokens[at].Text.ToLowerInvariant()) is { } op)
            {
                var symbol = at;
                at++;
                if (operand() is not { } right)
                    return null;
                left = new Binary(op, symbol, left, right);
            }
            return left;
        }

        /// <summary>Reads an operand with its unary operators.</summary>
        private Node? Factor()
        {
            if (at >= tokens.Count)
                return null;
            var token = tokens[at];
            var first = at;
            var text = token.Text.ToLowerInvariant();
            switch (text)
            {
                // ca65's `.not` binds loosest of all, so it applies to the rest of the expression.
                case "!" or ".not":
                    at++;
                    return Or() is { } negated ? new Unary(first, negated, text == ".not", false) : null;

                case "+" or "-" or "~" or ".bitnot" or "<" or ">" or "^":
                    at++;
                    return Factor() is { } operand
                        ? new Unary(first, operand, text == ".bitnot", text is "<" or ">" or "^" || operand is Unary { TakesByte: true })
                        : null;

                case "(":
                    at++;
                    var inner = Or();
                    if (inner is null || at >= tokens.Count || tokens[at].Text != ")")
                        return null;
                    return new Group(first, at++, [inner]);

                case "*":
                    at++;
                    return new Group(first, first, []);
            }

            switch (token.Kind)
            {
                case TokenKind.Number or TokenKind.Literal:
                    at++;
                    return new Group(first, first, []);

                case TokenKind.Name or TokenKind.Word:
                    at++;
                    return at < tokens.Count && tokens[at].Text == "(" ? Call(first) : new Group(first, first, []);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Reads the arguments of a function call whose name is at <paramref name="name"/>. A call
        /// whose arguments are not expressions, such as <c>.match</c>'s token lists, is kept as it
        /// stands.
        /// </summary>
        private Node? Call(int name)
        {
            var open = at;
            var arguments = new List<Node>();
            at++;
            if (at < tokens.Count && tokens[at].Text == ")")
                return new Group(name, at++, arguments);
            while (Or() is { } argument)
            {
                arguments.Add(argument);
                if (at < tokens.Count && tokens[at].Text == ")")
                    return new Group(name, at++, arguments);
                if (at >= tokens.Count || tokens[at].Text != ",")
                    break;
                at++;
            }

            // The arguments are not all expressions: skip to the matching parenthesis.
            var depth = 0;
            for (at = open; at < tokens.Count; at++)
            {
                if (tokens[at].Text == "(")
                    depth++;
                else if (tokens[at].Text == ")" && --depth == 0)
                    return new Group(name, at++, []);
            }
            return null;
        }
    }
}
