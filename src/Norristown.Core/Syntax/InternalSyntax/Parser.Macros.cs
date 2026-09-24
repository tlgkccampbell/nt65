namespace Norristown.Syntax.InternalSyntax;

// Parses macros, including the parameters a declaration takes and the arguments a call passes.
internal sealed partial class Parser
{
    /// <summary>
    /// Parses <c>.macro name(params) {</c>, with the processor state it expects and leaves. The
    /// body is ordinary nt65 and parses on its own, so only the opener is read here.
    /// </summary>
    private GreenNode ParseMacro()
    {
        var keyword = Advance();
        var name = ExpectName(Catalogue.ExpectedName.Message("a macro name"));
        MacroParameterListSyntax? parameters = null;
        if (Kind == SyntaxKind.OpenParen)
            parameters = ParseMacroParameterList();
        else
            ReportOnce(Catalogue.ExpectedParenthesis.Message("`(` and the parameters"));
        var signature = Kind == SyntaxKind.Colon ? ParseSignature() : null;
        return new MacroDeclarationSyntax(keyword, name, parameters, signature, ExpectOpenBrace());
    }

    private MacroParameterListSyntax ParseMacroParameterList()
    {
        var openParen = Advance();
        var parameters = Kind != SyntaxKind.CloseParen && !AtEnd ? ParseSeparatedList(ParseMacroParameter) : null;
        return new MacroParameterListSyntax(
            openParen, parameters, Expect(SyntaxKind.CloseParen, Catalogue.ExpectedParenthesis.Message("`)`")));
    }

    /// <summary>
    /// Parses a macro parameter, which is <c>name</c>, <c>name: kind</c>, <c>name = default</c>
    /// or all three.
    /// </summary>
    private GreenNode? ParseMacroParameter()
    {
        if (!AtName)
        {
            Report(Catalogue.ExpectedName.Message("a parameter name"));
            return null;
        }
        var name = Advance();
        GreenToken? colon = null;
        ParameterKindSyntax? parameterKind = null;
        if (Kind == SyntaxKind.Colon)
        {
            colon = Advance();
            parameterKind = ParseParameterKind();
        }

        GreenToken? equals = null;
        GreenNode? given = null;
        if (Kind == SyntaxKind.Equals)
        {
            equals = Advance();

            // `= {}` gives a block parameter that a call may leave out, which is empty when it does.
            given = Kind == SyntaxKind.OpenBrace && Next == SyntaxKind.CloseBrace
                ? new EmptyBlockSyntax(Advance(), Advance())
                : ParseExpression();
        }
        return new MacroParameterSyntax(name, colon, parameterKind, equals, given);
    }

    /// <summary>
    /// Parses what a parameter takes, which is one of the fixed words, the listed words of a
    /// <c>one(...)</c>, a <c>list(...)</c> of one of the others, a <c>const</c> with the range it
    /// takes, an <c>operand</c> with the modes it takes, or the name of an enum.
    /// </summary>
    private ParameterKindSyntax ParseParameterKind()
    {
        // A name that is not one of the parameter-kind words names an enum, and the parameter
        // takes that enum's members.
        if (Kind == SyntaxKind.ColonColon || (Kind == SyntaxKind.Identifier && !SyntaxFacts.IsParameterKind(Current.Text)))
        {
            return new ParameterKindSyntax(
                keyword: null, type: ParseName(), openParenToken: null, words: null, element: null,
                low: null, dotDotToken: null, high: null, closeParenToken: null);
        }
        if (Kind != SyntaxKind.Identifier)
        {
            return new ParameterKindSyntax(
                Missing(SyntaxKind.Identifier,
                    Catalogue.ExpectedParameterKind.Message(
                        "`expr`, `const`, `ident`, `operand`, `one(...)`, `list(...)`, `block` or an enum's name")),
                type: null, openParenToken: null, words: null, element: null,
                low: null, dotDotToken: null, high: null, closeParenToken: null);
        }

        var listed = AtWord("one");
        var nested = AtWord("list");
        var ranged = AtWord("const");
        var moded = AtWord("operand");
        var keyword = Advance();

        // A `one` and a `list` must say what they take, in parentheses after the word; a `const`
        // and an `operand` may.
        if (!listed && !nested && !((ranged || moded) && Kind == SyntaxKind.OpenParen))
        {
            return new ParameterKindSyntax(
                keyword, type: null, openParenToken: null, words: null, element: null,
                low: null, dotDotToken: null, high: null, closeParenToken: null);
        }
        if (Kind != SyntaxKind.OpenParen)
        {
            Report(Catalogue.ExpectedParenthesis.Message("`(`"));
            return new ParameterKindSyntax(
                keyword, type: null, openParenToken: null, words: null, element: null,
                low: null, dotDotToken: null, high: null, closeParenToken: null);
        }

        var openParen = Advance();
        if (ranged)
        {
            var low = ParseExpression();
            var dotDot = Expect(SyntaxKind.DotDot, Catalogue.ExpectedDotDot.Message("`..` and the greatest value: `const(0..15)`"));
            var high = ParseExpression();
            return new ParameterKindSyntax(
                keyword, type: null, openParen, words: null, element: null, low, dotDot, high,
                Expect(SyntaxKind.CloseParen, Catalogue.ExpectedParenthesis.Message("`)`")));
        }

        // The words a `one` accepts are never looked up, so a register or a mnemonic
        // among them is a word like any other; nor are the modes an `operand` takes.
        var words = listed || moded ? ParseSeparatedList(ParseListedWord) : null;
        // A `list` of lists nests once for every `list(`, so it counts toward the same limit
        // an expression does.
        ParameterKindSyntax? element = null;
        if (nested)
        {
            nesting++;
            if (TooDeeplyNested() is null)
                element = ParseParameterKind();
            nesting--;
        }
        return new ParameterKindSyntax(
            keyword, type: null, openParen, words, element, low: null, dotDotToken: null, high: null,
            Expect(SyntaxKind.CloseParen, Catalogue.ExpectedParenthesis.Message("`)`")));
    }

    /// <summary>
    /// Parses one of the words a <c>one(...)</c> accepts, or one of the modes an
    /// <c>operand(...)</c> takes.
    /// </summary>
    private GreenNode? ParseListedWord()
    {
        if (AtName)
            return new IdentifierNameSyntax(Advance(), null);
        Report(Catalogue.ExpectedName.Message("a word"));
        return null;
    }

    /// <summary>
    /// Parses <c>name!(args)</c>, with the <c>{</c> of a trailing block argument when one follows.
    /// An argument's syntax never depends on the kind of the parameter it binds to, so every
    /// argument is read the same way and the kinds are checked once names are resolved.
    /// </summary>
    private MacroCallSyntax ParseMacroCall()
    {
        var name = Advance();
        var bang = Advance();
        ArgumentListSyntax? arguments = null;
        if (Kind == SyntaxKind.OpenParen)
            arguments = ParseMacroArguments();
        else
            Report(Catalogue.ExpectedParenthesis.Message("`(` and the arguments"));
        return new MacroCallSyntax(name, bang, arguments, Kind == SyntaxKind.OpenBrace ? Advance() : null);
    }

    private ArgumentListSyntax ParseMacroArguments()
    {
        var openParen = Advance();
        var arguments = Kind != SyntaxKind.CloseParen && !AtEnd ? ParseSeparatedList(ParseArgument) : null;
        return new ArgumentListSyntax(openParen, arguments, Expect(SyntaxKind.CloseParen, Catalogue.ExpectedParenthesis.Message(
            "`)`")));
    }

    /// <summary>
    /// Parses one argument, which is an expression, a braced operand, or a parameter's name
    /// followed by <c>=</c> and one of those. <c>=</c> appears in no expression, so a named
    /// argument is unambiguous. What follows the <c>=</c> is never another name and <c>=</c>, so
    /// <c>a = b = 1</c> stops at the second <c>=</c> rather than nesting once for every one.
    /// </summary>
    private GreenNode ParseArgument()
    {
        if (AtName && Next == SyntaxKind.Equals)
            return new NamedArgumentSyntax(Advance(), Advance(), ParseArgumentValue());
        return ParseArgumentValue();
    }

    /// <summary>Parses what an argument gives, which is an expression or a braced operand.</summary>
    private GreenNode ParseArgumentValue() =>
        Kind == SyntaxKind.OpenBrace ? ParseBracedOperand() : ParseExpression();

    /// <summary>
    /// Parses <c>{buf,x}</c>, a whole operand as an argument. Only a braced argument is parsed as
    /// an operand, so an unbraced <c>(ptr)</c> remains an ordinary parenthesized expression.
    /// </summary>
    private BracedOperandSyntax ParseBracedOperand()
    {
        var openBrace = Advance();
        var outer = braced;
        braced = true;
        var operand = ParseOperand();
        braced = outer;
        return new BracedOperandSyntax(openBrace, operand, Expect(SyntaxKind.CloseBrace, Catalogue.ExpectedBrace.Message(
            "`}`")));
    }
}
