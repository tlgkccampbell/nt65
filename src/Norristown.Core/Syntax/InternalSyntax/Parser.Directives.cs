namespace Norristown.Syntax.InternalSyntax;

// The directives that describe or control the lines around them rather than declaring a
// name, and the one switch that dispatches every directive line.
internal sealed partial class Parser
{
    /// <summary>
    /// Returns, for a ca65 directive that has a different form in nt65, the message that gives the
    /// nt65 form and, when one word is enough, the word to use in its place. Returns null for any
    /// other directive.
    /// </summary>
    private static (DiagnosticMessage Message, string? Replacement)? Replaced(string directive) => directive.ToLowerInvariant() switch
    {
        ".zeropage" or ".code" or ".bss" or ".rodata" =>
            (Catalog.Ca65Spelling.Message(directive, $".segment {directive[1..].ToUpperInvariant()}"),
                $".segment {directive[1..].ToUpperInvariant()}"),
        ".tag" => (Catalog.Ca65Tag.Message(), ".type"),
        ".asciiz" => (Catalog.Ca65Spelling.Message(directive, ".strz"), ".strz"),
        ".dbyt" => (Catalog.Ca65Spelling.Message(directive, ".beword"), ".beword"),
        ".endproc" or ".endscope" or ".endmacro" or ".endstruct" or ".endunion" or ".endenum"
            or ".endif" or ".endrep" or ".endrepeat" =>
            (Catalog.Ca65BlockEnd.Message(directive), "}"),
        _ => null,
    };

    /// <summary>
    /// Parses a line that starts with a directive, according to the kind
    /// <see cref="SyntaxFacts.LineDirectiveKind"/> gives it. The two <c>.if</c> continuations,
    /// <c>.elseif</c> and <c>.else</c>, may only follow a <c>}</c>, so they are rejected here. Any
    /// other directive with no kind is one nt65 does not have.
    /// </summary>
    private GreenNode ParseDirectiveLine()
    {
        if (ParseDirective(SyntaxFacts.LineDirectiveKind(Current.DirectiveKind)) is { } statement)
            return Finish(statement);
        if (Current.DirectiveKind is DirectiveKind.ElseIf or DirectiveKind.Else)
            return ErrorLine(Catalog.ElseIfMisplaced.Message(Current.Text));
        return Replaced(Current.Text) is { } instead
            ? ErrorLine(instead.Message, Spelling(instead, wholeLine: true))
            : ErrorLine(Catalog.DirectiveUnknown.Message(Current.Text));
    }

    /// <summary>
    /// Parses the statement a directive of <paramref name="kind"/> starts, or returns null if the
    /// kind is not one a line can start with. This is the only place a directive's kind selects its
    /// parser, so an exported directive parses exactly like the same directive on its own.
    /// </summary>
    private GreenNode? ParseDirective(SyntaxKind kind) => kind switch
    {
        SyntaxKind.DataDirective => ParseDataDirective(),
        SyntaxKind.DataDeclaration => ParseDataDeclaration(),
        SyntaxKind.CpuDirective => ParseCpuDirective(),
        SyntaxKind.SegmentDeclaration => ParseSegment(),
        SyntaxKind.ProcDeclaration => ParseProc(),
        SyntaxKind.MultiProcDeclaration => ParseMultiProc(),
        SyntaxKind.ScopeDeclaration => ParseScope(),
        SyntaxKind.ExportDirective => ParseExport(),
        SyntaxKind.ImportDirective => ParseImport(),
        SyntaxKind.ModuleDirective => ParseModule(),
        SyntaxKind.PlaceDirective => ParsePlace(),
        SyntaxKind.UseDirective => ParseUse(),
        SyntaxKind.EnumDeclaration => ParseTypeBlock(SyntaxKind.EnumDeclaration, named: false),
        SyntaxKind.StructDeclaration => ParseTypeBlock(SyntaxKind.StructDeclaration, named: false),
        SyntaxKind.UnionDeclaration => ParseTypeBlock(SyntaxKind.UnionDeclaration, named: false),
        SyntaxKind.CharmapDeclaration => ParseTypeBlock(SyntaxKind.CharmapDeclaration, named: true),
        SyntaxKind.ListDeclaration => ParseTypeBlock(SyntaxKind.ListDeclaration, named: true),
        SyntaxKind.FuncDeclaration => ParseFunc(),
        SyntaxKind.SignatureDeclaration => ParseSignatureDeclaration(),
        SyntaxKind.ConstantDeclaration => ParseConst(),
        SyntaxKind.MacroDeclaration => ParseMacro(),
        SyntaxKind.IfDirective => ParseIf(),
        SyntaxKind.RepeatDirective => ParseRepetition(SyntaxKind.RepeatDirective),
        SyntaxKind.EachDirective => ParseRepetition(SyntaxKind.EachDirective),
        SyntaxKind.AssertDirective => ParseAssert(),
        SyntaxKind.ErrorDirective => ParseError(),
        SyntaxKind.NextDirective => ParseNext(),
        SyntaxKind.FallthroughDirective => ParseFallthrough(),
        SyntaxKind.StateDirective => ParseState(),
        SyntaxKind.EnsureDirective => ParseEnsure(),
        SyntaxKind.FrameDirective => ParseFrame(),
        SyntaxKind.PatchDirective => ParsePatch(),
        SyntaxKind.EncodedDirective => ParseEncoded(),
        SyntaxKind.LabelDirective => ParseLabel(),
        SyntaxKind.AllowDirective => ParseAllow(),
        _ => null,
    };

    /// <summary>
    /// Returns the fix that rewrites a ca65 directive in nt65's form, when replacing one word is the
    /// whole fix. A <c>}</c> can only replace a whole line, so it is offered only when the
    /// directive is the whole line. <c>.tag T, n</c> becomes <c>.type T[n]</c>, which moves the
    /// count as well as the word, so that fix is offered only for the plain form with no comma.
    /// </summary>
    private DiagnosticFix? Spelling((DiagnosticMessage Message, string? Replacement) instead, bool wholeLine) =>
        instead.Replacement is { } word && (word != "}" || wholeLine) && (word != ".type" || !Ahead(SyntaxKind.Comma))
            ? new DiagnosticFix(FixKind.Spelling, word)
            : null;

    /// <summary>
    /// Parses <c>.if expr {</c>, or, with <paramref name="closeBrace"/>, the
    /// <c>} .elseif expr {</c> that continues one. The condition tests the build configuration, so
    /// it is an ordinary expression here, and what it may name is checked once the configuration
    /// is known.
    /// </summary>
    private GreenNode ParseIf(GreenToken? closeBrace = null)
    {
        var keyword = Advance();
        var condition = ParseExpression();
        var openBrace = ExpectOpenBrace();
        return closeBrace is { } closed
            ? new ElseIfDirectiveSyntax(closed, keyword, condition, openBrace)
            : new IfDirectiveSyntax(keyword, condition, openBrace);
    }

    /// <summary>Parses <c>} .else {</c>, which takes no condition.</summary>
    private GreenNode ParseElse(GreenToken closeBrace)
    {
        var keyword = Advance();
        return new ElseDirectiveSyntax(closeBrace, keyword, ExpectOpenBrace());
    }

    /// <summary>
    /// Parses <c>.repeat count, name {</c> or <c>.each what, name, index {</c>. The name is bound
    /// to the index or the item, and a body that does not use it may leave the name out. An
    /// <c>.each</c> may also bind the item's zero-based index, as the counter of a <c>.repeat</c>
    /// is bound, after the item's name.
    /// </summary>
    private GreenNode ParseRepetition(SyntaxKind kind)
    {
        var keyword = Advance();
        var expression = ParseExpression();
        var (comma, name) = ParseBoundName("the name to bind");
        if (kind == SyntaxKind.RepeatDirective)
            return new RepeatDirectiveSyntax(keyword, expression, comma, name, ExpectOpenBrace());
        var (indexComma, index) = name is null ? (null, null) : ParseBoundName("the name to bind the index to");
        return new EachDirectiveSyntax(keyword, expression, comma, name, indexComma, index, ExpectOpenBrace());
    }

    /// <summary>
    /// Parses the <c>, name</c> after a repetition's expression or its first name, and returns
    /// both tokens, or two nulls when the line has no comma there.
    /// </summary>
    /// <param name="what">The phrase that names what is expected when the comma has no name after it.</param>
    private (GreenToken? Comma, GreenToken? Name) ParseBoundName(string what)
    {
        if (Kind != SyntaxKind.Comma)
            return (null, null);
        var comma = Advance();
        if (AtName)
            return (comma, Advance());
        Report(Catalog.ExpectedName.Message(what));
        return (comma, null);
    }

    /// <summary>
    /// Parses <c>.assert expr, message</c>, whose message may be left out. The message is an
    /// expression, which the semantic checks require to be text. There is no level, because a
    /// failed assertion is an error, and nt65 decides when it can be checked.
    /// </summary>
    private GreenNode ParseAssert()
    {
        var keyword = Advance();
        var condition = ParseExpression();
        if (Kind != SyntaxKind.Comma)
            return new AssertDirectiveSyntax(keyword, condition, null, null, null, null);
        var comma = Advance();

        // A ca65 assertion level is reported as an error, and the message after it is still read.
        GreenToken? level = null;
        GreenToken? levelComma = null;
        if (AtName && SyntaxFacts.IsAssertLevel(Current.Text))
        {
            Report(Catalog.AssertLevel.Message(Current.Text),
                new DiagnosticFix(FixKind.AssertLevel));
            level = Advance();
            if (Kind != SyntaxKind.Comma)
                return new AssertDirectiveSyntax(keyword, condition, comma, level, null, null);
            levelComma = Advance();
        }

        return new AssertDirectiveSyntax(keyword, condition, comma, level, levelComma, ParseMessage());
    }

    /// <summary>
    /// Parses <c>.error message</c>, which refuses to build the file in the current configuration,
    /// or <c>.warning message</c>, which builds it but reports the message. The message is read as
    /// an <c>.assert</c>'s is.
    /// </summary>
    private GreenNode ParseError()
    {
        var keyword = Advance();
        return new ErrorDirectiveSyntax(keyword, ParseMessage());
    }

    /// <summary>
    /// Parses the message of an <c>.assert</c>, an <c>.error</c> or a <c>.warning</c>, and reports
    /// a line that ends where it belongs. The message is an expression, which the semantic checks
    /// require to be text.
    /// </summary>
    /// <returns>The message, or null where the line has none.</returns>
    private ExpressionSyntax? ParseMessage()
    {
        if (!AtEnd)
            return ParseExpression();
        Report(Catalog.ExpectedText.Message("the message: text in quotes, a text constant or a call that returns text"));
        return null;
    }

    /// <summary>
    /// Parses <c>.next @a, gfx::init</c>, which names the labels execution can continue at after
    /// the statement above, or <c>.next ?</c>, which says execution continues somewhere nt65 is
    /// not told about. <c>.next .return</c> says execution goes back to the routine's caller, and
    /// may carry a count or a <c>?</c> and be followed by labels, as in <c>.next .return 5, @a</c>.
    /// <c>.next {</c> opens an item block for the labels.
    /// </summary>
    private GreenNode ParseNext()
    {
        var keyword = Advance();
        if (Kind == SyntaxKind.Question)
        {
            var question = Advance();
            return new NextDirectiveSyntax(keyword, question, null, null, null, null, null, ParseItemsBrace(keyword, listed: true));
        }
        if (Kind == SyntaxKind.OpenBrace)
            return new NextDirectiveSyntax(keyword, null, null, null, null, null, null, Advance());

        // `.return` is spelled like a directive, so no label can be mistaken for it.
        if (Kind == SyntaxKind.Directive && Current.Text.Equals(".return", StringComparison.OrdinalIgnoreCase))
        {
            var returns = Advance();
            var unknown = Kind == SyntaxKind.Question ? Advance() : null;
            var count = unknown is not null || AtEnd || Kind is SyntaxKind.Comma or SyntaxKind.OpenBrace ? null : ParseExpression();
            if (Kind != SyntaxKind.Comma)
                return new NextDirectiveSyntax(keyword, null, returns, unknown, count, null, null, ParseItemsBrace(keyword, listed: true));
            var comma = Advance();
            var after = ParseSeparatedList(
                () => ParseTarget(Catalog.ExpectedLabel.Message("a label flow continues at")));
            return new NextDirectiveSyntax(keyword, null, returns, unknown, count, comma, after, ParseItemsBrace(keyword, listed: true));
        }
        var targets = ParseSeparatedList(
            () => ParseTarget(Catalog.ExpectedLabel.Message("a label flow continues at, `?` or `.return`")));
        return new NextDirectiveSyntax(keyword, null, null, null, null, null, targets, ParseItemsBrace(keyword, targets is not null));
    }

    /// <summary>
    /// Parses <c>.fallthrough next</c>, which names the routine that execution runs into past the
    /// end of this one.
    /// </summary>
    private GreenNode ParseFallthrough()
    {
        var keyword = Advance();
        return new FallthroughDirectiveSyntax(
            keyword, ParseTarget(Catalog.ExpectedLabel.Message("the routine flow runs into")));
    }

    /// <summary>
    /// Parses <c>.state a16, i8</c>, which gives signature items that are asserted and set at one
    /// point in the code. Which items only make sense for a whole routine rather than a point is
    /// for the analysis to check.
    /// </summary>
    private GreenNode ParseState()
    {
        var keyword = Advance();
        return new StateDirectiveSyntax(keyword, ParseStateList());
    }

    /// <summary>
    /// Parses <c>.ensure a16, i8</c>, which gives the widths to establish at this point. It takes
    /// signature items, and the analysis, not the parser, decides which of them it accepts.
    /// </summary>
    private GreenNode ParseEnsure()
    {
        var keyword = Advance();
        return new EnsureDirectiveSyntax(keyword, ParseStateList());
    }

    /// <summary>
    /// Parses <c>.frame locals: Locals</c>, which gives a name and the struct that describes the
    /// layout of the top of the stack.
    /// </summary>
    private GreenNode ParseFrame()
    {
        var keyword = Advance();
        var name = Expect(SyntaxKind.Identifier, Catalog.ExpectedName.Message("a name for the frame"));
        var colon = Expect(SyntaxKind.Colon, Catalog.ExpectedColon.Message(
            "`:` and the struct the frame is laid out as"));

        // The struct comes after the `:`, so a line without the colon has no struct to read.
        return new FrameDirectiveSyntax(keyword, name, colon, colon.IsMissing ? null : ParseExpression());
    }

    /// <summary>
    /// Parses <c>.patch @op</c>, which names the instruction whose bytes the store above
    /// overwrites, and <c>.patch @op as dex, iny</c>, which also lists the instructions the store
    /// can turn it into.
    /// </summary>
    private GreenNode ParsePatch()
    {
        var keyword = Advance();
        var target = ParseTarget(Catalog.ExpectedLabel.Message("the label of the instruction being written to"));
        if (!AtWord("as"))
            return new PatchDirectiveSyntax(keyword, target, null, null);
        var asKeyword = Advance();
        var variants = ParseSeparatedList(
            () => ParseTarget(Catalog.ExpectedLabel.Message("an instruction the store can write, such as `dex`")));
        return new PatchDirectiveSyntax(keyword, target, asKeyword, variants);
    }

    /// <summary>
    /// Parses <c>.label name = @op + 1</c>, which names a position inside an instruction. Whether
    /// the position is one is for layout to check.
    /// </summary>
    private GreenNode ParseLabel()
    {
        var keyword = Advance();
        var name = Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
            ? Advance()
            : Expect(SyntaxKind.Identifier, Catalog.ExpectedName.Message("a name for the position"));
        var equals = Expect(SyntaxKind.Equals, Catalog.ExpectedEquals.Message(
            "`=` and a position inside an instruction, such as `@op + 1`"));
        return new LabelDirectiveSyntax(keyword, name, equals, equals.IsMissing ? null : ParseExpression());
    }

    /// <summary>
    /// Parses <c>.encoded $34</c>, which gives the opcode byte the instruction below it is written
    /// as. Whether the byte is a constant that runs as that instruction is for layout to check.
    /// </summary>
    private GreenNode ParseEncoded()
    {
        var keyword = Advance();
        if (AtEnd)
        {
            Report(Catalog.ExpectedExpression);
            return new EncodedDirectiveSyntax(keyword, null);
        }
        return new EncodedDirectiveSyntax(keyword, ParseExpression());
    }

    /// <summary>
    /// Parses <c>.allow "unused-symbol", "reason"</c>, which keeps one warning from being reported
    /// on the statement below. Whether the name is one nt65 reports is for the binder to check.
    /// </summary>
    private GreenNode ParseAllow()
    {
        var keyword = Advance();
        var name = Expect(SyntaxKind.StringLiteral, Catalog.ExpectedText.Message(
            "the name of the warning, in quotes"));
        if (Kind != SyntaxKind.Comma)
            return new AllowDirectiveSyntax(keyword, name, null, null);
        var comma = Advance();
        return new AllowDirectiveSyntax(keyword, name, comma, Expect(SyntaxKind.StringLiteral,
            Catalog.ExpectedText.Message("the reason, in quotes")));
    }

    /// <summary>
    /// Parses a label named by a flow directive such as <c>.next</c>, <c>.fallthrough</c> or
    /// <c>.patch</c>, which is a cheap local, a name or a scoped path. Reports
    /// <paramref name="expected"/> and returns null if there is no label.
    /// </summary>
    private NameExpressionSyntax? ParseTarget(DiagnosticMessage expected)
    {
        if (AtName || Kind is SyntaxKind.CheapLocal or SyntaxKind.ColonColon)
            return ParseName();
        Report(expected);
        return null;
    }
}
