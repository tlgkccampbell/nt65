using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

// Parses expressions, including the operator levels, the names an expression uses, and the
// places where the language requires parentheses.
internal sealed partial class Parser
{
    /// <summary>
    /// Parses an expression at the loosest binding level. Every expression inside another is read
    /// through here or through <see cref="ParseUnary"/>, so this is where the nesting is
    /// counted and where parsing stops when the nesting gets too deep.
    /// </summary>
    private ExpressionSyntax ParseExpression()
    {
        nesting++;
        var expression = TooDeeplyNested() ?? ParseBinary(LowestPrecedence);
        nesting--;
        return expression;
    }

    /// <summary>
    /// Parses a binary expression whose operators bind no more loosely than
    /// <paramref name="loosest"/>, by precedence climbing. Each operand is read once, and the
    /// operand on the right of an operator takes only operators that bind more tightly, so every
    /// operator is left-associative.
    /// </summary>
    private ExpressionSyntax ParseBinary(int loosest)
    {
        var left = ParseUnary();
        while (SyntaxFacts.BinaryPrecedence(Current.Kind, Current.Text) is var level and > 0
            && level <= loosest)
        {
            var operatorIndex = index;
            var op = Advance();
            var right = ParseBinary(level - 1);
            CheckRequiredParentheses(operatorIndex, op, left, right);

            // A missing-parentheses diagnostic, reported over the operator, is about this whole
            // expression, so it is attached here, along with any diagnostic on an operand that no
            // inner node claimed.
            left = Own(new BinaryExpressionSyntax(left, op, right));
        }
        return left;
    }

    private ExpressionSyntax ParseUnary()
    {
        if (!SyntaxFacts.IsUnaryOperator(Kind))
            return ParsePrimary();
        var op = Advance();
        nesting++;
        var operand = TooDeeplyNested() ?? ParseUnary();
        nesting--;
        return new UnaryExpressionSyntax(op, operand);
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
            case SyntaxKind.OpenBracket:
                return ParseSet();
            case SyntaxKind.Directive:
                return ParseBuiltinCall();
            case SyntaxKind.Identifier or SyntaxKind.CheapLocal or SyntaxKind.ColonColon
                or SyntaxKind.Register or SyntaxKind.Mnemonic:
                var name = ParseName(indexed: true);
                return Kind == SyntaxKind.OpenParen
                    ? new CallExpressionSyntax(name, null, ParseArgumentList())
                    : name;
            default:
                Report(Catalogue.ExpectedExpression);
                return new ErrorExpressionSyntax(null);
        }
    }

    private ExpressionSyntax ParseParenthesized()
    {
        var open = Advance();
        var expression = ParseExpression();
        return new ParenthesizedExpressionSyntax(open, expression, Expect(SyntaxKind.CloseParen, Catalogue.ExpectedParenthesis.Message(
            "`)`")));
    }

    /// <summary>
    /// Parses a set of values, such as <c>[Mode::zpx, Mode::absx]</c> or <c>[$00..$3f, $80]</c>.
    /// Only <c>.in</c> and <c>.switch</c> take one, and the binder reports a set anywhere else.
    /// </summary>
    private ExpressionSyntax ParseSet()
    {
        var open = Advance();
        var items = Kind is not SyntaxKind.CloseBracket && !AtEnd ? ParseSeparatedList(ParseRange) : null;
        return new SetExpressionSyntax(open, items, Expect(SyntaxKind.CloseBracket, Catalogue.ExpectedBracket.Message("`]`")));
    }

    private ExpressionSyntax ParseBuiltinCall()
    {
        if (!SyntaxFacts.IsBuiltinFunction(Current.Text))
        {
            Report(Catalogue.NotAFunction.Message(Current.Text));
            return new ErrorExpressionSyntax(Advance());
        }
        var name = Advance();
        if (Kind == SyntaxKind.OpenParen)
            return new CallExpressionSyntax(null, name, ParseArgumentList());
        Report(Catalogue.ExpectedParenthesis.Message($"`(` after `{name.Text}`"));
        return new ErrorExpressionSyntax(name);
    }

    private ArgumentListSyntax ParseArgumentList()
    {
        var openParen = Advance();
        var arguments = Kind is not SyntaxKind.CloseParen && !AtEnd ? ParseSeparatedList(ParseExpression) : null;
        var closeParen = Require(SyntaxKind.CloseParen, Catalogue.ExpectedParenthesis.Message("`)`"));
        return new ArgumentListSyntax(openParen, arguments, closeParen);
    }

    /// <summary>
    /// Parses a <c>::</c>-separated name, as in <c>gfx::init</c>, <c>::top_level</c> and
    /// <c>Point::x</c>. <paramref name="indexed"/> allows <c>[i]</c> after a component, which only
    /// an expression does. After the <c>T</c> of a <c>.type T[n]</c>, the brackets are the
    /// declaration's count.
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
            return new NameExpressionSyntax(global, MissingParts(Catalogue.ExpectedName.Message("a name")));
        }

        var parts = ImmutableArray.CreateBuilder<GreenNode>();
        parts.Add(ParseNamePart(indexed));
        while (Kind == SyntaxKind.ColonColon)
        {
            var separator = Advance();

            // A member of a named struct, union or enum may have the same name as a register or a
            // mnemonic, because after `::` it could be nothing else.
            if (Kind is not (SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic))
            {
                parts.Add(separator);
                parts.Add(MissingPart(Catalogue.ExpectedName.Message("a name after `::`")));
                break;
            }
            parts.Add(separator);
            parts.Add(ParseNamePart(indexed));
        }
        return new NameExpressionSyntax(global, new GreenSeparatedList(parts.ToImmutable()));
    }

    /// <summary>
    /// Parses one name of a path, with the <c>[i]</c> after it where an expression allows one.
    /// </summary>
    private IdentifierNameSyntax ParseNamePart(bool indexed)
    {
        var name = Advance();
        return new IdentifierNameSyntax(name, indexed && Kind == SyntaxKind.OpenBracket ? ParseElementIndex() : null);
    }

    /// <summary>
    /// Returns a name part for a name that is absent from the source, reporting
    /// <paramref name="message"/> on it. A null message reports nothing, for when another
    /// diagnostic already covers the absence.
    /// </summary>
    private IdentifierNameSyntax MissingPart(DiagnosticMessage? message) =>
        new(message is not { } text ? GreenToken.Missing(SyntaxKind.Identifier) : Missing(SyntaxKind.Identifier, text),
            null);

    /// <summary>Returns a path whose only part is a missing name.</summary>
    private GreenSeparatedList MissingParts(DiagnosticMessage? message) => new([MissingPart(message)]);

    /// <summary>Returns a name expression for a name that is absent from the source.</summary>
    private NameExpressionSyntax MissingName(DiagnosticMessage? message) => new(null, MissingParts(message));

    /// <summary>
    /// Parses <c>[i]</c> after a name, which selects the element of a counted declaration that the
    /// name refers to.
    /// </summary>
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
            Report(Catalogue.ExpectedElementIndex);
            index = new ErrorExpressionSyntax(null);
        }
        return new ElementIndexSyntax(open, index, Expect(SyntaxKind.CloseBracket, Catalogue.ExpectedBracket.Message(
            "`]`")));
    }

    /// <summary>
    /// Reports a diagnostic when a binary operation leaves out parentheses in one of the three
    /// places where the language requires them. Those places, which are the cases a reader
    /// misjudges, are a shift or bitwise operator next to a different operator, mixed logical
    /// operators, and a byte operator that looks as if it applied to a whole expression.
    /// </summary>
    private void CheckRequiredParentheses(int operatorIndex, GreenToken op, GreenNode left, GreenNode right)
    {
        if (SyntaxFacts.IsBitwiseOperator(op.Kind) || SyntaxFacts.IsLogicalOperator(op.Kind))
        {
            foreach (var operand in (ReadOnlySpan<GreenNode>)[left, right])
            {
                if (OperatorOf(operand) is not { } inner || inner.Kind == op.Kind)
                    continue;

                // A logical operator needs parentheses only next to another logical operator.
                // `a && (b | c)` reads clearly enough that the language leaves `a && b | c` alone.
                if (SyntaxFacts.IsLogicalOperator(op.Kind) && !SyntaxFacts.IsLogicalOperator(inner.Kind))
                    continue;
                Report(operatorIndex, Catalogue.OperatorsNeedParentheses.Message(op.Text, inner.Text),
                    new DiagnosticFix(FixKind.Parentheses));
                return;
            }
        }

        if (RightmostByteOperator(left) is { } byteOperator)
        {
            Report(operatorIndex,
                Catalogue.ByteOperatorNeedsParentheses.Message(byteOperator.Text, op.Text, byteOperator.Text),
                new DiagnosticFix(FixKind.Parentheses));
        }
    }

    /// <summary>
    /// Returns the operator of <paramref name="node"/> if it is a binary expression, or null
    /// otherwise.
    /// </summary>
    private static GreenToken? OperatorOf(GreenNode node) =>
        node is BinaryExpressionSyntax binary ? binary.OperatorToken : null;

    /// <summary>
    /// Returns the <c>&lt;</c>, <c>&gt;</c> or <c>^</c> at the right edge of an operand, if any.
    /// <c>&lt;label + 1</c> is <c>(&lt;label) + 1</c>, and in <c>1 + &lt;label + 2</c> the unary
    /// operator sits at the end of the left operand rather than at its head, so the method follows
    /// the right spine down.
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
