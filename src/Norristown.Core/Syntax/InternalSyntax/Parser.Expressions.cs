using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

// Expressions: the operator levels, the names an expression is written with, and the two
// places the language wants parentheses.
internal sealed partial class Parser
{
    private ExpressionSyntax ParseExpression() => ParseBinary(LowestPrecedence);

    private ExpressionSyntax ParseBinary(int level)
    {
        if (level < TightestPrecedence)
            return ParseUnary();

        var left = ParseBinary(level - 1);
        while (SyntaxFacts.BinaryPrecedence(Current.Kind, Current.Text) == level)
        {
            var operatorIndex = index;
            var op = Advance();
            var right = ParseBinary(level - 1);
            CheckRequiredParentheses(operatorIndex, op, left, right);

            // The expression is the node the missing parentheses are about, so it takes what was
            // said over its operator, and whatever an operand of it was reported for.
            left = Own(new BinaryExpressionSyntax(left, op, right));
        }
        return left;
    }

    private ExpressionSyntax ParseUnary()
    {
        if (!SyntaxFacts.IsUnaryOperator(Kind))
            return ParsePrimary();
        var op = Advance();
        return new UnaryExpressionSyntax(op, ParseUnary());
    }

    private ExpressionSyntax ParsePrimary()
    {
        switch (Kind)
        {
            case SyntaxKind.NumberLiteral:
                return new NumberExpressionSyntax(Advance());
            case SyntaxKind.CharacterLiteral:
                return new CharacterExpressionSyntax(Advance());
            case SyntaxKind.StringLiteral:
                return new StringExpressionSyntax(Advance());
            case SyntaxKind.CpuName:
                return new CpuNameExpressionSyntax(Advance());
            case SyntaxKind.Star:
                return new CurrentAddressExpressionSyntax(Advance());
            case SyntaxKind.OpenParen:
                return ParseParenthesized();
            case SyntaxKind.Directive:
                return ParseBuiltinCall();
            case SyntaxKind.Identifier or SyntaxKind.CheapLocal or SyntaxKind.ColonColon
                or SyntaxKind.Register or SyntaxKind.Mnemonic:
                var name = ParseName(indexed: true);
                return Kind == SyntaxKind.OpenParen
                    ? new CallExpressionSyntax(name, null, ParseArgumentList())
                    : name;
            default:
                Report("expected an expression");
                return new ErrorExpressionSyntax(null);
        }
    }

    private ExpressionSyntax ParseParenthesized()
    {
        var open = Advance();
        var expression = ParseExpression();
        return new ParenthesizedExpressionSyntax(open, expression, Expect(SyntaxKind.CloseParen, "expected `)`"));
    }

    private ExpressionSyntax ParseBuiltinCall()
    {
        if (!SyntaxFacts.IsBuiltinFunction(Current.Text))
        {
            Report($"`{Current.Text}` is not a function");
            return new ErrorExpressionSyntax(Advance());
        }
        var name = Advance();
        if (Kind == SyntaxKind.OpenParen)
            return new CallExpressionSyntax(null, name, ParseArgumentList());
        Report($"expected `(` after `{name.Text}`");
        return new ErrorExpressionSyntax(name);
    }

    private ArgumentListSyntax ParseArgumentList()
    {
        var openParen = Advance();
        var arguments = Kind is not SyntaxKind.CloseParen && !AtEnd ? ParseSeparatedList(ParseExpression) : null;
        var closeParen = Kind == SyntaxKind.CloseParen
            ? Advance()
            : Missing(SyntaxKind.CloseParen, "expected `)`");
        return new ArgumentListSyntax(openParen, arguments, closeParen);
    }

    /// <summary>
    /// <c>::</c>-separated, as in <c>gfx::init</c>, <c>::top_level</c> and <c>Point::x</c>.
    /// <paramref name="indexed"/> allows <c>[i]</c> after a component, which only an expression
    /// does: after the <c>T</c> of a <c>.type T[n]</c> the brackets are the declaration's count.
    /// </summary>
    private NameExpressionSyntax ParseName(bool indexed = false)
    {
        var global = Kind == SyntaxKind.ColonColon ? Advance() : null;

        // A register or a mnemonic is kept as a name rather than refused here: inside a macro
        // body it is a word, and everywhere else the binder's reserved-word error says more
        // than the parser could.
        if (Kind is not (SyntaxKind.Identifier or SyntaxKind.CheapLocal
            or SyntaxKind.Register or SyntaxKind.Mnemonic))
        {
            return new NameExpressionSyntax(global, MissingParts("expected a name"));
        }

        var parts = ImmutableArray.CreateBuilder<GreenNode>();
        parts.Add(ParseNamePart(indexed));
        while (Kind == SyntaxKind.ColonColon)
        {
            var separator = Advance();

            // A member of a named struct, union or enum may be spelled like a register or a
            // mnemonic: after `::` there is nothing else it could be.
            if (Kind is not (SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic))
            {
                parts.Add(separator);
                parts.Add(MissingPart("expected a name after `::`"));
                break;
            }
            parts.Add(separator);
            parts.Add(ParseNamePart(indexed));
        }
        return new NameExpressionSyntax(global, new GreenSeparatedList(parts.ToImmutable()));
    }

    /// <summary>One name of a path, with the <c>[i]</c> after it where an expression allows one.</summary>
    private IdentifierNameSyntax ParseNamePart(bool indexed)
    {
        var name = Advance();
        return new IdentifierNameSyntax(name, indexed && Kind == SyntaxKind.OpenBracket ? ParseElementIndex() : null);
    }

    /// <summary>
    /// The part that stands where a name belongs the source does not have, saying so unless
    /// <paramref name="message"/> is null, which is where something else has said it better.
    /// </summary>
    private IdentifierNameSyntax MissingPart(string? message) =>
        new(message is null ? GreenToken.Missing(SyntaxKind.Identifier) : Missing(SyntaxKind.Identifier, message),
            null);

    /// <summary>A path of one part, that part being only the place a name belongs.</summary>
    private GreenSeparatedList MissingParts(string? message) => new([MissingPart(message)]);

    /// <summary>The name that stands where one belongs the source does not have.</summary>
    private NameExpressionSyntax MissingName(string? message) => new(null, MissingParts(message));

    /// <summary><c>[i]</c> after a name: which element of a counted declaration it stands for.</summary>
    private ElementIndexSyntax ParseElementIndex()
    {
        var open = Advance();
        ExpressionSyntax index;
        if (Kind != SyntaxKind.CloseBracket && !AtEnd)
        {
            index = ParseExpression();
        }
        else
        {
            Report("expected the element: `name[i]` is the i-th of what `name` declares");
            index = new ErrorExpressionSyntax(null);
        }
        return new ElementIndexSyntax(open, index, Expect(SyntaxKind.CloseBracket, "expected `]`"));
    }

    /// <summary>
    /// The three places the language requires parentheses, which are the cases a reader misjudges:
    /// a shift or bitwise operator next to a different operator, logical operators mixed,
    /// and a byte operator that looks as if it applied to a whole expression.
    /// </summary>
    private void CheckRequiredParentheses(int operatorIndex, GreenToken op, GreenNode left, GreenNode right)
    {
        if (SyntaxFacts.IsBitwiseOperator(op.Kind) || SyntaxFacts.IsLogicalOperator(op.Kind))
        {
            foreach (var operand in (ReadOnlySpan<GreenNode>)[left, right])
            {
                if (OperatorOf(operand) is not { } inner || inner.Kind == op.Kind)
                    continue;

                // Only the logical operators among themselves: `a && (b | c)` reads clearly
                // enough that the language leaves `a && b | c` alone.
                if (SyntaxFacts.IsLogicalOperator(op.Kind) && !SyntaxFacts.IsLogicalOperator(inner.Kind))
                    continue;
                Report(operatorIndex, $"`{op.Text}` and `{inner.Text}` need parentheses to show which applies first",
                    new DiagnosticFix(FixKind.Parentheses));
                return;
            }
        }

        if (RightmostByteOperator(left) is { } byteOperator)
        {
            Report(operatorIndex,
                $"unary `{byteOperator.Text}` before `{op.Text}` needs parentheses to show what `{byteOperator.Text}` applies to",
                new DiagnosticFix(FixKind.Parentheses));
        }
    }

    /// <summary>The operator of <paramref name="node"/> when it is a binary expression, or null.</summary>
    private static GreenToken? OperatorOf(GreenNode node) =>
        node is BinaryExpressionSyntax binary ? binary.OperatorToken : null;

    /// <summary>
    /// The <c>&lt;</c>, <c>&gt;</c> or <c>^</c> at the right edge of an operand, if any.
    /// <c>&lt;label + 1</c> is <c>(&lt;label) + 1</c>, and in <c>1 + &lt;label + 2</c> the
    /// unary sits at the end of the left operand rather than at its head, so follow the
    /// right spine down.
    /// </summary>
    private static GreenToken? RightmostByteOperator(GreenNode node)
    {
        while (node is BinaryExpressionSyntax binary)
            node = binary.Right;
        if (node is not UnaryExpressionSyntax unary)
            return null;
        var op = unary.OperatorToken;
        return SyntaxFacts.IsByteOperator(op.Kind) ? op : null;
    }
}
