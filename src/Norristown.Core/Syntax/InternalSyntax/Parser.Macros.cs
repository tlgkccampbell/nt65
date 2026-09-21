namespace Norristown.Syntax.InternalSyntax;

// Macros: what a declaration takes, and what a call gives it.
internal sealed partial class Parser
{
    /// <summary>
    /// <c>.macro name(params) {</c>, with the processor state it expects and leaves. The
    /// body is ordinary nt65 and parses on its own, so only the opener is read here.
    /// </summary>
    private GreenNode ParseMacro()
    {
        var keyword = Advance();
        var name = ExpectName("expected a macro name");
        MacroParameterListSyntax? parameters = null;
        if (Kind == SyntaxKind.OpenParen)
            parameters = ParseMacroParameterList();
        else
            ReportOnce("expected `(` and the parameters");
        var signature = Kind == SyntaxKind.Colon ? ParseSignature() : null;
        return new MacroDeclarationSyntax(keyword, name, parameters, signature, ExpectOpenBrace());
    }

    private MacroParameterListSyntax ParseMacroParameterList()
    {
        var openParen = Advance();
        var parameters = Kind != SyntaxKind.CloseParen && !AtEnd ? ParseSeparatedList(ParseMacroParameter) : null;
        return new MacroParameterListSyntax(
            openParen, parameters, Expect(SyntaxKind.CloseParen, "expected `)`"));
    }

    /// <summary><c>name</c>, <c>name: kind</c>, <c>name = default</c> or all three.</summary>
    private GreenNode? ParseMacroParameter()
    {
        if (!AtName)
        {
            Report("expected a parameter name");
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

            // `= {}`: a block parameter a call may leave out, which is empty when it does.
            given = Kind == SyntaxKind.OpenBrace && Next == SyntaxKind.CloseBrace
                ? new EmptyBlockSyntax(Advance(), Advance())
                : ParseExpression();
        }
        return new MacroParameterSyntax(name, colon, parameterKind, equals, given);
    }

    /// <summary>
    /// What a parameter takes: one of the fixed words, the listed words of a
    /// <c>one(...)</c>, or a <c>list(...)</c> of one of those.
    /// </summary>
    private ParameterKindSyntax ParseParameterKind()
    {
        if (Kind != SyntaxKind.Identifier || !SyntaxFacts.IsParameterKind(Current.Text))
        {
            return new ParameterKindSyntax(
                Missing(SyntaxKind.Identifier,
                    "expected `expr`, `const`, `ident`, `operand`, `one(...)`, `list(...)` or `block`"),
                null, null, null, null);
        }

        var listed = AtWord("one");
        var nested = AtWord("list");
        var keyword = Advance();

        // Only a `one` and a `list` say what they take, in parentheses after the word.
        if (!listed && !nested)
            return new ParameterKindSyntax(keyword, null, null, null, null);
        if (Kind != SyntaxKind.OpenParen)
        {
            Report("expected `(`");
            return new ParameterKindSyntax(keyword, null, null, null, null);
        }

        var openParen = Advance();

        // The words a `one` accepts are never looked up, so a register or a mnemonic
        // among them is a word like any other.
        var words = listed ? ParseSeparatedList(ParseListedWord) : null;
        var element = listed ? null : ParseParameterKind();
        return new ParameterKindSyntax(
            keyword, openParen, words, element, Expect(SyntaxKind.CloseParen, "expected `)`"));
    }

    /// <summary>One of the words a <c>one(...)</c> accepts.</summary>
    private GreenNode? ParseListedWord()
    {
        if (AtName)
            return new IdentifierNameSyntax(Advance(), null);
        Report("expected a word");
        return null;
    }

    /// <summary>
    /// <c>name!(args)</c>, with the <c>{</c> of a trailing block argument when one follows.
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
            Report("expected `(` and the arguments");
        return new MacroCallSyntax(name, bang, arguments, Kind == SyntaxKind.OpenBrace ? Advance() : null);
    }

    private ArgumentListSyntax ParseMacroArguments()
    {
        var openParen = Advance();
        var arguments = Kind != SyntaxKind.CloseParen && !AtEnd ? ParseSeparatedList(ParseArgument) : null;
        return new ArgumentListSyntax(openParen, arguments, Expect(SyntaxKind.CloseParen, "expected `)`"));
    }

    /// <summary>
    /// One argument: an expression, a braced operand, or a parameter named and then given
    /// one of those. <c>=</c> appears in no expression, so a named argument is unambiguous.
    /// </summary>
    private GreenNode ParseArgument()
    {
        if (AtName && Next == SyntaxKind.Equals)
            return new NamedArgumentSyntax(Advance(), Advance(), ParseArgument());
        return Kind == SyntaxKind.OpenBrace ? ParseBracedOperand() : ParseExpression();
    }

    /// <summary>
    /// <c>{buf,x}</c>: a whole operand as an argument. Only a braced one is an operand, so an
    /// unbraced <c>(ptr)</c> stays the expression it reads as.
    /// </summary>
    private BracedOperandSyntax ParseBracedOperand()
    {
        var openBrace = Advance();
        var outer = braced;
        braced = true;
        var operand = ParseOperand();
        braced = outer;
        return new BracedOperandSyntax(openBrace, operand, Expect(SyntaxKind.CloseBrace, "expected `}`"));
    }
}
