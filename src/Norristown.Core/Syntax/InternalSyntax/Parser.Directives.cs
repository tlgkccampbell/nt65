namespace Norristown.Syntax.InternalSyntax;

// The directives that describe or control the lines around them rather than declaring a
// name, and the one switch that dispatches every directive line.
internal sealed partial class Parser
{
    /// <summary>
    /// A line that starts with a directive, parsed according to the kind
    /// <see cref="SyntaxFacts.LineDirectiveKind"/> gives it. The two <c>.if</c> continuations,
    /// <c>.elseif</c> and <c>.else</c>, may only follow a <c>}</c>, so they are rejected here;
    /// any other directive with no kind is one nt65 does not have.
    /// </summary>
    private GreenNode ParseDirectiveLine()
    {
        if (ParseDirective(SyntaxFacts.LineDirectiveKind(Current.Text)) is { } statement)
            return Finish(statement);
        if (SyntaxFacts.LineDirectiveKind(Current.Text) is SyntaxKind.ElseIfDirective or SyntaxKind.ElseDirective)
            return ErrorLine(Catalogue.ElseIfMisplaced.Says(Current.Text));
        return Replaced(Current.Text) is { } instead
            ? ErrorLine(instead.Message, Spelling(instead, wholeLine: true))
            : ErrorLine(Catalogue.DirectiveUnknown.Says(Current.Text));
    }

    /// <summary>
    /// The statement a directive of <paramref name="kind"/> parses to, or null when the kind is
    /// not one a line can start with. This is the only place a directive's kind selects its
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
        SyntaxKind.ConfigDeclaration => ParseConfig(),
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
        _ => null,
    };

    /// <summary>
    /// The fix that rewrites a ca65 directive in nt65's spelling, when replacing one word is the
    /// whole fix. A <c>}</c> can only replace a whole line, so it is offered only when the
    /// directive is the whole line; and <c>.tag T, n</c> becomes <c>.type T[n]</c>, which moves
    /// the count as well as the word, so the fix is offered only for the plain form with no comma.
    /// </summary>
    private DiagnosticFix? Spelling((DiagnosticMessage Message, string? Write) instead, bool wholeLine) =>
        instead.Write is { } word && (word != "}" || wholeLine) && (word != ".type" || !RestHasComma())
            ? new DiagnosticFix(FixKind.Spelling, word)
            : null;

    /// <summary>Whether a comma is written on the rest of the line.</summary>
    private bool RestHasComma()
    {
        for (var at = index; at < tokens.Length; at++)
        {
            if (tokens[at].Kind == SyntaxKind.Comma)
                return true;
        }
        return false;
    }

    /// <summary>
    /// For a ca65 directive that nt65 writes differently, the message saying how to write it now
    /// and, when one word is enough, the word to write in its place; null for any other directive.
    /// </summary>
    private static (DiagnosticMessage Message, string? Write)? Replaced(string directive) => directive.ToLowerInvariant() switch
    {
        ".zeropage" or ".code" or ".bss" or ".rodata" =>
            (Catalogue.Ca65Spelling.Says(directive, $".segment {directive[1..].ToUpperInvariant()}"),
                $".segment {directive[1..].ToUpperInvariant()}"),
        ".tag" => (Catalogue.Ca65Tag.Says(), ".type"),
        ".asciiz" => (Catalogue.Ca65Spelling.Says(directive, ".strz"), ".strz"),
        ".dbyt" => (Catalogue.Ca65Spelling.Says(directive, ".beword"), ".beword"),
        ".endproc" or ".endscope" or ".endmacro" or ".endstruct" or ".endunion" or ".endenum"
            or ".endif" or ".endrep" or ".endrepeat" =>
            (Catalogue.Ca65BlockEnd.Says(directive), "}"),
        _ => null,
    };

    /// <summary>
    /// <c>.if expr {</c>, or, with <paramref name="closeBrace"/>, the <c>} .elseif expr {</c>
    /// that continues one. The condition tests the build configuration, so it is an ordinary
    /// expression here and what it may name is settled once the configuration is known.
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

    /// <summary><c>} .else {</c>, which takes no condition.</summary>
    private GreenNode ParseElse(GreenToken closeBrace)
    {
        var keyword = Advance();
        return new ElseDirectiveSyntax(closeBrace, keyword, ExpectOpenBrace());
    }

    /// <summary>
    /// <c>.repeat count, name {</c> or <c>.each what, name {</c>. The name is bound to the
    /// index or the item, and a body that does not use it may leave the name out.
    /// </summary>
    private GreenNode ParseRepetition(SyntaxKind kind)
    {
        var keyword = Advance();
        var expression = ParseExpression();
        GreenToken? comma = null;
        GreenToken? name = null;
        if (Kind == SyntaxKind.Comma)
        {
            comma = Advance();
            if (AtName)
                name = Advance();
            else
                Report(Catalogue.ExpectedName.Says("the name to bind"));
        }
        var openBrace = ExpectOpenBrace();
        return kind == SyntaxKind.RepeatDirective
            ? new RepeatDirectiveSyntax(keyword, expression, comma, name, openBrace)
            : new EachDirectiveSyntax(keyword, expression, comma, name, openBrace);
    }

    /// <summary>
    /// <c>.assert expr, "message"</c>, whose message may be left out. There is no level: a
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
            Report(Catalogue.AssertLevel.Says(Current.Text),
                new DiagnosticFix(FixKind.AssertLevel));
            level = Advance();
            if (Kind != SyntaxKind.Comma)
                return new AssertDirectiveSyntax(keyword, condition, comma, level, null, null);
            levelComma = Advance();
        }

        GreenToken? message = null;
        if (Kind == SyntaxKind.StringLiteral)
            message = Advance();
        else
            Report(Catalogue.ExpectedText.Says("the message, in quotes"));
        return new AssertDirectiveSyntax(keyword, condition, comma, level, levelComma, message);
    }

    /// <summary>
    /// <c>.error "message"</c>, which refuses to build the file in the current configuration,
    /// or <c>.warning "message"</c>, which builds it but reports the message.
    /// </summary>
    private GreenNode ParseError()
    {
        var keyword = Advance();
        return new ErrorDirectiveSyntax(keyword, Expect(SyntaxKind.StringLiteral, Catalogue.ExpectedText.Says(
            "the message, in quotes")));
    }

    /// <summary>
    /// <c>.next @a, gfx::init</c>, the labels execution can continue at after the statement
    /// above, or <c>.next ?</c>, which ends the path so that nothing beyond it is checked.
    /// </summary>
    private GreenNode ParseNext()
    {
        var keyword = Advance();
        if (Kind == SyntaxKind.Question)
            return new NextDirectiveSyntax(keyword, Advance(), null);
        return new NextDirectiveSyntax(
            keyword, null, ParseSeparatedList(() => ParseTarget(Catalogue.ExpectedLabel.Says(
                "a label flow continues at, or `?`"))));
    }

    /// <summary><c>.fallthrough next</c>: the routine execution runs into past the end of this one.</summary>
    private GreenNode ParseFallthrough()
    {
        var keyword = Advance();
        return new FallthroughDirectiveSyntax(
            keyword, ParseTarget(Catalogue.ExpectedLabel.Says("the routine flow runs into")));
    }

    /// <summary>
    /// <c>.state a16, i8</c>: signature items, asserted and set at one point in the code. Which
    /// items only make sense for a whole routine rather than a point is for the analysis to check.
    /// </summary>
    private GreenNode ParseState()
    {
        var keyword = Advance();
        return new StateDirectiveSyntax(keyword, ParseStateList());
    }

    /// <summary>
    /// <c>.ensure a16, i8</c>: the widths to establish at this point. It takes signature items,
    /// and the analysis, not the parser, decides which of them it accepts.
    /// </summary>
    private GreenNode ParseEnsure()
    {
        var keyword = Advance();
        return new EnsureDirectiveSyntax(keyword, ParseStateList());
    }

    /// <summary><c>.frame locals: Locals</c>: a name, and the struct that gives the layout of the top of the stack.</summary>
    private GreenNode ParseFrame()
    {
        var keyword = Advance();
        var name = Expect(SyntaxKind.Identifier, Catalogue.ExpectedName.Says("a name for the frame"));
        var colon = Expect(SyntaxKind.Colon, Catalogue.ExpectedColon.Says(
            "`:` and the struct the frame is laid out as"));

        // The struct comes after the `:`, so a line without the colon has no struct to read.
        return new FrameDirectiveSyntax(keyword, name, colon, colon.IsMissing ? null : ParseExpression());
    }

    /// <summary><c>.patch @op</c>: the instruction whose bytes the store above overwrites.</summary>
    private GreenNode ParsePatch()
    {
        var keyword = Advance();
        return new PatchDirectiveSyntax(
            keyword, ParseTarget(Catalogue.ExpectedLabel.Says("the label of the instruction being written to")));
    }

    /// <summary>
    /// A label named by a flow directive such as <c>.next</c>, <c>.fallthrough</c> or
    /// <c>.patch</c>: a cheap local, a name, or a scoped path.
    /// </summary>
    private NameExpressionSyntax? ParseTarget(DiagnosticMessage expected)
    {
        if (AtName || Kind is SyntaxKind.CheapLocal or SyntaxKind.ColonColon)
            return ParseName();
        Report(expected);
        return null;
    }
}
