using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

// Expressions: the operator levels, the names an expression is written with, and the places
// where the language requires parentheses.
internal sealed partial class Parser
{
    /// <summary>
    /// An expression, at the loosest binding level. Every expression inside another is read
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
        return new ParenthesizedExpressionSyntax(open, expression, Expect(SyntaxKind.CloseParen, Catalogue.ExpectedParenthesis.Says(
            "`)`")));
    }

    private ExpressionSyntax ParseBuiltinCall()
    {
        if (!SyntaxFacts.IsBuiltinFunction(Current.Text))
        {
            Report(Catalogue.NotAFunction.Says(Current.Text));
            return new ErrorExpressionSyntax(Advance());
        }
        var name = Advance();
        if (Kind == SyntaxKind.OpenParen)
            return new CallExpressionSyntax(null, name, ParseArgumentList());
        Report(Catalogue.ExpectedParenthesis.Says($"`(` after `{name.Text}`"));
        return new ErrorExpressionSyntax(name);
    }

    private ArgumentListSyntax ParseArgumentList()
    {
        var openParen = Advance();
        var arguments = Kind is not SyntaxKind.CloseParen && !AtEnd ? ParseSeparatedList(ParseExpression) : null;
        var closeParen = Kind == SyntaxKind.CloseParen
            ? Advance()
            : Missing(SyntaxKind.CloseParen, Catalogue.ExpectedParenthesis.Says("`)`"));
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
            return new NameExpressionSyntax(global, MissingParts(Catalogue.ExpectedName.Says("a name")));
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
                parts.Add(MissingPart(Catalogue.ExpectedName.Says("a name after `::`")));
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
    /// A name part for a name the source does not write, reporting <paramref name="message"/>
    /// on it; a null message reports nothing, for when another diagnostic already covers it.
    /// </summary>
    private IdentifierNameSyntax MissingPart(DiagnosticMessage? message) =>
        new(message is not { } said ? GreenToken.Missing(SyntaxKind.Identifier) : Missing(SyntaxKind.Identifier, said),
            null);

    /// <summary>A path whose only part is a missing name.</summary>
    private GreenSeparatedList MissingParts(DiagnosticMessage? message) => new([MissingPart(message)]);

    /// <summary>A name expression for a name the source does not write.</summary>
    private NameExpressionSyntax MissingName(DiagnosticMessage? message) => new(null, MissingParts(message));

    /// <summary><c>[i]</c> after a name: which element of a counted declaration the name refers to.</summary>
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
        return new ElementIndexSyntax(open, index, Expect(SyntaxKind.CloseBracket, Catalogue.ExpectedBracket.Says(
            "`]`")));
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
                Report(operatorIndex, Catalogue.OperatorsNeedParentheses.Says(op.Text, inner.Text),
                    new DiagnosticFix(FixKind.Parentheses));
                return;
            }
        }

        if (RightmostByteOperator(left) is { } byteOperator)
        {
            Report(operatorIndex,
                Catalogue.ByteOperatorNeedsParentheses.Says(byteOperator.Text, op.Text, byteOperator.Text),
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
