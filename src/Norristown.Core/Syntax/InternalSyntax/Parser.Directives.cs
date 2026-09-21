namespace Norristown.Syntax.InternalSyntax;

// The directives that say something about the lines around them rather than declaring a
// name, and the one switch that reads a directive line.
internal sealed partial class Parser
{
    /// <summary>
    /// A line that starts with a directive, read as whatever the directive's row says it is.
    /// The two <c>.if</c> continuations are the only directives with a place of their own —
    /// after a <c>}</c> — and are refused here; anything else the table does not hold is a
    /// directive nt65 does not have.
    /// </summary>
    private GreenNode ParseDirectiveLine()
    {
        if (ParseDirective(SyntaxFacts.LineDirectiveKind(Current.Text)) is { } statement)
            return Finish(statement);
        if (SyntaxFacts.LineDirectiveKind(Current.Text) is SyntaxKind.ElseIfDirective or SyntaxKind.ElseDirective)
            return ErrorLine($"`{Current.Text}` continues an `.if`, and belongs after its `}}`");
        return Replaced(Current.Text) is { } instead
            ? ErrorLine(instead.Message, Spelling(instead, wholeLine: true))
            : ErrorLine($"unknown directive `{Current.Text}`");
    }

    /// <summary>
    /// The statement a directive of <paramref name="kind"/> is written as, or null where the
    /// kind is not one a line starts with. This is the one place a directive's kind chooses how
    /// it is read, so a directive exported and the same directive on its own read alike.
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
        SyntaxKind.StateDirective => ParseState(),
        SyntaxKind.EnsureDirective => ParseEnsure(),
        SyntaxKind.FrameDirective => ParseFrame(),
        SyntaxKind.PatchDirective => ParsePatch(),
        _ => null,
    };

    /// <summary>
    /// Writing a ca65 spelling the nt65 way, where one word is all it takes. A <c>}</c> replaces
    /// a whole line and nothing else, and <c>.tag T, n</c> is <c>.type T[n]</c>, which moves the
    /// count as well as the word, so only the plain form is offered as a change to make.
    /// </summary>
    private DiagnosticFix? Spelling((string Message, string? Write) instead, bool wholeLine) =>
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
    /// How a directive nt65 no longer has is written now, and the word to write in its place
    /// where one word is all it takes; null for a directive it never had.
    /// </summary>
    private static (string Message, string? Write)? Replaced(string directive) => directive.ToLowerInvariant() switch
    {
        ".zeropage" or ".code" or ".bss" or ".rodata" =>
            ($"`{directive}` is written `.segment {directive[1..].ToUpperInvariant()}`",
                $".segment {directive[1..].ToUpperInvariant()}"),
        ".tag" => ("`.tag T` is written `.type T`, and `.tag T, n` is `.type T[n]`", ".type"),
        ".asciiz" => ("`.asciiz` is written `.strz`", ".strz"),
        ".dbyt" => ("`.dbyt` is written `.beword`", ".beword"),
        ".endproc" or ".endscope" or ".endmacro" or ".endstruct" or ".endunion" or ".endenum"
            or ".endif" or ".endrep" or ".endrepeat" =>
            ($"a block ends with `}}`, and `{directive}` closes nothing", "}"),
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
                Report("expected the name to bind");
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

        // ca65's level is reported, and the message after it still read.
        GreenToken? level = null;
        GreenToken? levelComma = null;
        if (AtName && SyntaxFacts.IsAssertLevel(Current.Text))
        {
            Report($"`{Current.Text}` is ca65's: an nt65 assertion that fails is always an error, checked as soon as "
                + "nt65 can and otherwise at link time, so `.assert` takes only the condition and the message",
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
            Report("expected the message, in quotes");
        return new AssertDirectiveSyntax(keyword, condition, comma, level, levelComma, message);
    }

    /// <summary>
    /// <c>.error "message"</c>, a configuration this file refuses to be built in, or
    /// <c>.warning "message"</c>, one it builds in and has something to say about.
    /// </summary>
    private GreenNode ParseError()
    {
        var keyword = Advance();
        return new ErrorDirectiveSyntax(keyword, Expect(SyntaxKind.StringLiteral, "expected the message, in quotes"));
    }

    /// <summary>
    /// <c>.next @a, gfx::init</c>, the labels flow reaches after the statement above, or
    /// <c>.next ?</c>, which ends the path and checks nothing beyond it.
    /// </summary>
    private GreenNode ParseNext()
    {
        var keyword = Advance();
        if (Kind == SyntaxKind.Question)
            return new NextDirectiveSyntax(keyword, Advance(), null);
        return new NextDirectiveSyntax(
            keyword, null, ParseSeparatedList(() => ParseTarget("expected a label flow continues at, or `?`")));
    }

    /// <summary>
    /// <c>.state a16, i8</c>: the items of a signature, asserted and set at one point. Which
    /// items describe a routine rather than a point is the analysis's to say.
    /// </summary>
    private GreenNode ParseState()
    {
        var keyword = Advance();
        return new StateDirectiveSyntax(keyword, ParseStateList());
    }

    /// <summary>
    /// <c>.ensure a16, i8</c>: the widths to make hold. It takes the items of a signature, and
    /// which of them it accepts is the analysis's to say.
    /// </summary>
    private GreenNode ParseEnsure()
    {
        var keyword = Advance();
        return new EnsureDirectiveSyntax(keyword, ParseStateList());
    }

    /// <summary><c>.frame locals: Locals</c>: a name, and the struct the top of the stack is laid out as.</summary>
    private GreenNode ParseFrame()
    {
        var keyword = Advance();
        var name = Expect(SyntaxKind.Identifier, "expected a name for the frame");
        var colon = Expect(SyntaxKind.Colon, "expected `:` and the struct the frame is laid out as");

        // The struct is written after the `:`, so a line without one says nothing about it.
        return new FrameDirectiveSyntax(keyword, name, colon, colon.IsMissing ? null : ParseExpression());
    }

    /// <summary><c>.patch @op</c>: the one instruction the store above writes into.</summary>
    private GreenNode ParsePatch()
    {
        var keyword = Advance();
        return new PatchDirectiveSyntax(
            keyword, ParseTarget("expected the label of the instruction being written to"));
    }

    /// <summary>A label named by an annotation: a cheap local, a name, or a scoped path.</summary>
    private NameExpressionSyntax? ParseTarget(string expected)
    {
        if (AtName || Kind is SyntaxKind.CheapLocal or SyntaxKind.ColonColon)
            return ParseName();
        Report(expected);
        return null;
    }
}
