using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Parses one line's tokens into a statement. A line's syntax depends only on its own tokens
/// and the kind of block around it, so a line parses without looking at any other
/// line and its statement survives an edit anywhere else in the file.
/// <para>
/// The parser never aborts a line: whatever it cannot read becomes a
/// <see cref="SyntaxKind.SkippedTokens"/> node with a diagnostic, which the line holds as it
/// holds the line break and the <c>.export</c> before a declaration. A statement is therefore
/// only its own tokens, wherever it is written. The parser never invents a token either, so a
/// piece it expected and did not find is simply absent from the node, and everything above the
/// parser has to allow for a missing child.
/// </para>
/// </summary>
internal sealed class Parser
{
    /// <summary>The loosest binding level, which <see cref="ParseExpression"/> starts at.</summary>
    private const int LowestPrecedence = 13;

    /// <summary>The tightest binding level that is still a binary operator.</summary>
    private const int TightestPrecedence = 3;

    /// <summary>The slot a <see cref="BinaryExpressionSyntax"/> holds its operator in.</summary>
    private const int BinaryOperator = 1;

    /// <summary>The slot a <see cref="BinaryExpressionSyntax"/> holds its right operand in.</summary>
    private const int BinaryRight = 2;

    /// <summary>The slot a <see cref="UnaryExpressionSyntax"/> holds its operator in.</summary>
    private const int UnaryOperator = 0;

    private readonly ImmutableArray<GreenToken> tokens;
    private readonly BlockKind context;
    private readonly bool opensBlock;
    private readonly List<Error> errors = [];
    private int index;

    // The line's own pieces, which no statement holds: the `.export` that exports what the line
    // declares, and the tokens the statement could not take.
    private GreenToken? exportKeyword;
    private GreenNode? skippedTokens;

    // Whether an operand is being read inside the braces of a macro argument, where `}` ends
    // it as the end of a line does elsewhere.
    private bool braced;

    private Parser(GreenLine line, BlockKind context)
    {
        tokens = line.Tokens;
        this.context = context;
        opensBlock = line.Opens;
    }

    private GreenToken Current => tokens[index];

    private SyntaxKind Kind => tokens[index].Kind;

    private SyntaxKind Next => index + 1 < tokens.Length ? tokens[index + 1].Kind : SyntaxKind.EndOfLine;

    private bool AtEnd => Kind == SyntaxKind.EndOfLine;

    /// <summary>Whether an operand has run out: the end of the line, or the <c>}</c> around it.</summary>
    private bool AtOperandEnd => AtEnd || (braced && Kind == SyntaxKind.CloseBrace);

    /// <summary>
    /// Whether a declaration could write its name here. Register names and mnemonics are
    /// reserved, but that rule belongs to name binding rather than to reading a line:
    /// <c>.proc a</c> parses, and is reported where every other reserved-word use is.
    /// </summary>
    private bool AtName => Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic;

    /// <summary>
    /// Whether the current token is a contextual word such as <c>dp</c> or <c>proc</c>.
    /// Those are matched without regard to case, as every other word the language fixes is.
    /// </summary>
    private bool AtWord(string word) =>
        Kind == SyntaxKind.Identifier && Current.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses <paramref name="line"/> as it stands inside a block of <paramref name="context"/>.</summary>
    public static Result Parse(GreenLine line, BlockKind context)
    {
        var parser = new Parser(line, context);
        var node = parser.ParseLine(line.LineKind);
        return new Result(
            context, parser.exportKeyword, node, parser.skippedTokens, [.. parser.errors]);
    }

    /// <summary>The next token, which stays put once the end-of-line token is reached.</summary>
    private GreenToken Advance()
    {
        var token = tokens[index];
        if (index < tokens.Length - 1)
            index++;
        return token;
    }

    private void Report(string message) => Report(index, message);

    /// <summary>Reports at the current token, with the change the message names as its fix.</summary>
    private void Report(string message, DiagnosticFix fix) => Report(index, message, fix);

    /// <summary>
    /// Reports only when nothing has been said about this line yet. A half-typed
    /// <c>m!({</c> runs out of tokens inside an argument, inside the braces and inside the
    /// parentheses; the first of those says what is missing, and the rest is the same news.
    /// </summary>
    private void ReportOnce(string message, DiagnosticFix? fix = null)
    {
        if (errors.Count == 0)
            Report(index, message, fix);
    }

    private void Report(int token, string message, DiagnosticFix? fix = null) =>
        errors.Add(new Error(token, message, fix));

    private static string Describe(GreenToken token) => $"`{token.Text}`";

    private GreenNode ParseLine(LineKind kind)
    {
        if (kind == LineKind.BlockClose)
            return ParseBlockClose();
        if (kind == LineKind.Blank)
            return Finish(new BlankLineSyntax());

        // An unnamed label needs a name rather than a spelling, so it is the whole news about
        // its line: `:` where a name belongs and `:+` in an operand are read no further, and
        // what the rest of the line would otherwise be reported for is this same mistake.
        if (Lines.UnnamedLabel(tokens) is >= 0 and var colon)
        {
            Report(colon, "an unnamed label is written `@name`: a cheap local, private to the routine around it");
            return new GreenSyntax(SyntaxKind.ErrorLine, TakeRest());
        }

        // Blocks with a line grammar of their own. A struct or union member
        // is written like a labelled data declaration and needs no rule of its own.
        //
        // The line that opens a block belongs to that block, so it arrives here in its own
        // context; it is read as the opener it is, and only the lines after it are members.
        switch (opensBlock ? BlockKind.None : context)
        {
            case BlockKind.Enum:
                return ParseEnumMember();
            case BlockKind.Charmap:
                return ParseCharmapEntry();
            case BlockKind.List:
                return Finish(SyntaxKind.ListItems, ParseListItems());
            case BlockKind.RecordInitializer:
                return ParseMemberValueLine();
            case BlockKind.DataBody:
                return ParseDataValuesLine();
            default:
                break;
        }

        return kind switch
        {
            LineKind.Label => ParseLabeledLine(),
            LineKind.Constant => ParseConstantDeclaration(),
            LineKind.Instruction => Finish(ParseInstruction()),
            LineKind.Directive => ParseDirectiveLine(),
            LineKind.MacroCall => Finish(ParseMacroCall()),

            // A name on its own splices a `block` parameter. Which blocks may hold one is not
            // a question about this line — a splice inside an `.if` inside a body is still a
            // splice — so it is read as one everywhere and the binder says where it belongs.
            LineKind.BareIdentifier => Finish(new BlockSpliceSyntax(Advance())),
            _ => ErrorLine("expected a label, a constant, an instruction or a directive"),
        };
    }

    /// <summary>The statement, with whatever is left on the line kept aside for the line to hold.</summary>
    private GreenNode Finish(SyntaxKind kind, ImmutableArray<GreenNode> children)
    {
        SkipRest();
        return new GreenSyntax(kind, children);
    }

    private GreenNode Finish(GreenNode statement)
    {
        SkipRest();
        return statement;
    }

    /// <summary>
    /// Anything the statement could not take, kept for the line as
    /// <see cref="SyntaxKind.SkippedTokens"/>.
    /// </summary>
    private void SkipRest()
    {
        if (AtEnd)
            return;

        // One diagnostic per line is enough: where the parser has already said what it
        // wanted, the tokens it then walks past are the same problem said twice.
        //
        // A block written on one line, `.data name { .byte 1 }`, is that one thing: the
        // `{` was read as the opener it is, and what follows it is the body, on the wrong
        // line rather than unexpected.
        if (errors.Count == 0)
        {
            Report(!opensBlock && index > 0 && tokens[index - 1].Kind == SyntaxKind.OpenBrace
                ? $"a block's `{{` ends the line that opens it: {Describe(Current)} goes on the next line, and `}}` on its own"
                : $"unexpected {Describe(Current)}");
        }
        skippedTokens = new GreenSyntax(SyntaxKind.SkippedTokens, TakeRest());
    }

    private GreenNode ErrorLine(string message, DiagnosticFix? fix = null)
    {
        Report(index, message, fix);
        return new GreenSyntax(SyntaxKind.ErrorLine, TakeRest());
    }

    /// <summary>Every token up to, but not including, the end-of-line token.</summary>
    private ImmutableArray<GreenNode> TakeRest()
    {
        var rest = ImmutableArray.CreateBuilder<GreenNode>();
        while (!AtEnd)
            rest.Add(Advance());
        return rest.ToImmutable();
    }

    private GreenNode ParseBlockClose()
    {
        var brace = Advance();
        if (AtEnd)
            return Finish(new BlockCloseLineSyntax(brace));

        // `} .elseif expr {` and `} .else {` close one branch and open the next; a macro
        // call's `} name {` closes one block argument and opens the next.
        if (AtName && Next == SyntaxKind.OpenBrace)
            return Finish(new BlockContinuationSyntax(brace, Advance(), Advance()));

        return SyntaxFacts.LineDirectiveKind(Current.Text) switch
        {
            SyntaxKind.ElseIfDirective => Finish(ParseIf(brace)),
            SyntaxKind.ElseDirective => Finish(ParseElse(brace)),
            _ => Finish(new BlockCloseLineSyntax(brace)),
        };
    }

    private GreenNode ParseLabeledLine()
    {
        var label = new LabelSyntax(Advance(), Advance());
        if (AtEnd)
            return Finish(new LabeledLineSyntax(label, null));

        // A label may be followed by an instruction, a data directive or a macro call.
        if (Kind == SyntaxKind.Mnemonic)
            return Finish(new LabeledLineSyntax(label, ParseInstruction()));
        if (Kind == SyntaxKind.Identifier && Next == SyntaxKind.Bang)
            return Finish(new LabeledLineSyntax(label, ParseMacroCall()));
        if (Kind != SyntaxKind.Directive)
        {
            Report("expected an instruction, a data directive or a macro call after a label");
            return Finish(new LabeledLineSyntax(label, null));
        }

        switch (SyntaxFacts.LineDirectiveKind(Current.Text))
        {
            case SyntaxKind.DataDirective:
                return Finish(new LabeledLineSyntax(label, ParseDataDirective()));
            case SyntaxKind.None:
                Report($"unknown directive `{Current.Text}`");
                return Finish(new LabeledLineSyntax(label, null));
            default:
                Report($"`{Current.Text}` may not follow a label");
                return Finish(new LabeledLineSyntax(label, null));
        }
    }

    private GreenNode ParseConstantDeclaration()
    {
        var name = Advance();
        var equals = Advance();
        return Finish(new ConstantDeclarationSyntax(name, equals, ParseExpression()));
    }

    private GreenNode ParseDirectiveLine()
    {
        var kind = SyntaxFacts.LineDirectiveKind(Current.Text);
        return kind switch
        {
            SyntaxKind.DataDirective => Finish(ParseDataDirective()),
            SyntaxKind.DataDeclaration => Finish(ParseDataDeclaration()),
            SyntaxKind.CpuDirective => Finish(ParseCpuDirective()),
            SyntaxKind.SegmentDeclaration => Finish(ParseSegment()),
            SyntaxKind.ProcDeclaration => Finish(ParseProc()),
            SyntaxKind.MultiProcDeclaration => Finish(ParseMultiProc()),
            SyntaxKind.ScopeDeclaration => Finish(ParseScope()),
            SyntaxKind.ExportDirective => Finish(ParseExport()),
            SyntaxKind.ImportDirective => Finish(ParseImport()),
            SyntaxKind.ModuleDirective => Finish(ParseModule()),
            SyntaxKind.UseDirective => Finish(ParseUse()),
            SyntaxKind.EnumDeclaration => Finish(ParseTypeBlock(SyntaxKind.EnumDeclaration, named: false)),
            SyntaxKind.StructDeclaration => Finish(ParseTypeBlock(SyntaxKind.StructDeclaration, named: false)),
            SyntaxKind.UnionDeclaration => Finish(ParseTypeBlock(SyntaxKind.UnionDeclaration, named: false)),
            SyntaxKind.CharmapDeclaration => Finish(ParseTypeBlock(SyntaxKind.CharmapDeclaration, named: true)),
            SyntaxKind.ListDeclaration => Finish(ParseTypeBlock(SyntaxKind.ListDeclaration, named: true)),
            SyntaxKind.FuncDeclaration => Finish(ParseFunc()),
            SyntaxKind.SignatureDeclaration => Finish(ParseSignatureDeclaration()),
            SyntaxKind.ConfigDeclaration => Finish(ParseConfig()),
            SyntaxKind.MacroDeclaration => Finish(ParseMacro()),
            SyntaxKind.IfDirective => Finish(ParseIf()),
            SyntaxKind.RepeatDirective => Finish(ParseRepetition(SyntaxKind.RepeatDirective)),
            SyntaxKind.EachDirective => Finish(ParseRepetition(SyntaxKind.EachDirective)),
            SyntaxKind.AssertDirective => Finish(ParseAssert()),
            SyntaxKind.ErrorDirective => Finish(ParseError()),
            SyntaxKind.NextDirective => Finish(ParseNext()),
            SyntaxKind.StateDirective => Finish(ParseState()),
            SyntaxKind.EnsureDirective => Finish(ParseEnsure()),
            SyntaxKind.FrameDirective => Finish(ParseFrame()),
            SyntaxKind.PatchDirective => Finish(ParsePatch()),
            SyntaxKind.ElseIfDirective or SyntaxKind.ElseDirective =>
                ErrorLine($"`{Current.Text}` continues an `.if`, and belongs after its `}}`"),
            _ => ReplacedLine(),
        };

        GreenNode ReplacedLine() =>
            Replaced(Current.Text) is { } instead
                ? ErrorLine(instead.Message, Spelling(instead, wholeLine: true))
                : ErrorLine($"unknown directive `{Current.Text}`");
    }

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
    /// A data directive. An element type — <c>.byte</c>, <c>.word</c>, <c>.addr</c>,
    /// <c>.faraddr</c>, <c>.dword</c> or <c>.type T</c> — may take a count, <c>[16]</c> or
    /// <c>[]</c>, and then values: after it on the line, in braces on the line, or in the body
    /// the line opens. Any other directive takes its operands as ca65's does.
    /// </summary>
    private GreenNode ParseDataDirective()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        var directive = Advance();
        children.Add(directive);
        var record = directive.Text.Equals(".type", StringComparison.OrdinalIgnoreCase);
        if (!record && SyntaxFacts.ElementSize(directive.Text) is null)
        {
            if (!AtEnd)
                ParseCommaSeparated(children, ParseExpression);
            return new GreenSyntax(SyntaxKind.DataDirective, children.ToImmutable());
        }

        if (record)
        {
            if (AtName || Kind == SyntaxKind.ColonColon)
                children.Add(ParseName());
            else
                Report("expected the type: `.type T`");
        }
        var counted = Kind == SyntaxKind.OpenBracket;
        if (counted)
            children.Add(ParseElementCount());

        if (Kind == SyntaxKind.OpenBrace)
        {
            // A body holds an array's values, or one record's `member = value` lines.
            if (Next == SyntaxKind.EndOfLine)
            {
                if (!counted && !record)
                    ReportOnce($"values in a body need a count: `{directive.Text}[] {{` counts them");
                children.Add(Advance());
            }
            else
            {
                children.Add(ParseBracedValue());
            }
        }
        else if (!AtEnd)
        {
            if (counted || record)
            {
                ReportOnce(counted
                    ? $"the values of an array go in braces: `{directive.Text}[n] {{ 1, 2 }}`"
                    : "a record's values go in braces: `.type T { member = value }`");
            }
            ParseCommaSeparated(children, ParseExpression);
        }
        return new GreenSyntax(SyntaxKind.DataDirective, children.ToImmutable());
    }

    /// <summary><c>[n]</c>, or <c>[]</c> for as many elements as the values given.</summary>
    private GreenNode ParseElementCount()
    {
        var bracket = Advance();
        var count = Kind != SyntaxKind.CloseBracket && !AtEnd ? ParseExpression() : null;
        return new ElementCountSyntax(bracket, count, Expect(SyntaxKind.CloseBracket, "expected `]`"));
    }

    /// <summary>
    /// <c>.data name: element</c>, or <c>.data name {</c> for mixed data. The name is
    /// required: data is declared to be named, and the segment of the same name is written
    /// <c>.segment DATA</c>.
    /// </summary>
    private GreenNode ParseDataDeclaration()
    {
        var keyword = Advance();
        var name = ExpectName(Kind == SyntaxKind.OpenBrace
            ? "`.data` declares data, and needs a name: the segment is written `.segment DATA`"
            : "expected a name: `.data name: .byte 1, 2` or `.data name { }`");

        GreenToken? colon = null;
        GreenNode? element = null;
        GreenToken? brace = null;
        if (Kind == SyntaxKind.Colon)
        {
            colon = Advance();
            if (Kind == SyntaxKind.Directive && SyntaxFacts.LineDirectiveKind(Current.Text) == SyntaxKind.DataDirective)
            {
                element = ParseDataDirective();
            }
            else
            {
                var instead = Kind == SyntaxKind.Directive ? Replaced(Current.Text) : null;
                ReportOnce(instead?.Message
                    ?? "expected what the data is: a number such as `.byte` or `.word`, an address such as `.addr`, "
                        + "`.type T`, or bytes such as `.incbin`",
                    instead is { } written ? Spelling(written, wholeLine: false) : null);
            }
        }
        else if (Kind == SyntaxKind.OpenBrace)
        {
            brace = Advance();
        }
        else
        {
            ReportOnce("expected `:` and what the data is, or `{` for mixed data");
        }
        return new DataDeclarationSyntax(keyword, name, colon, element, brace);
    }

    /// <summary>One line of a data body: values separated by commas, one element each.</summary>
    private GreenNode ParseDataValuesLine()
    {
        if (Kind == SyntaxKind.Directive && !SyntaxFacts.IsBuiltinFunction(Current.Text))
        {
            return ErrorLine($"a data body holds values, and `{Current.Text}` is a directive: "
                + "what the values are is the declaration's to say");
        }
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        ParseCommaSeparated(children, ParseDataValue);
        return Finish(SyntaxKind.DataValues, children.ToImmutable());
    }

    /// <summary>A value: an expression, or a braced record or list.</summary>
    private GreenNode ParseDataValue() => Kind == SyntaxKind.OpenBrace ? ParseBracedValue() : ParseExpression();

    /// <summary>
    /// <c>{ member = value, … }</c>, a record, or <c>{ value, … }</c>, a list of elements.
    /// <c>=</c> is in no expression, so the first item says which, and <c>{}</c> is a record
    /// that names no member.
    /// </summary>
    private GreenNode ParseBracedValue()
    {
        var record = Next == SyntaxKind.CloseBrace
            || (index + 2 < tokens.Length
                && tokens[index + 1].Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic
                && tokens[index + 2].Kind == SyntaxKind.Equals);
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind != SyntaxKind.CloseBrace && !AtEnd)
            ParseCommaSeparated(children, record ? ParseMemberValue : ParseDataValue);
        if (Kind == SyntaxKind.CloseBrace)
            children.Add(Advance());
        else
            ReportOnce("expected `}`");
        return new GreenSyntax(record ? SyntaxKind.RecordValues : SyntaxKind.ValueList, children.ToImmutable());
    }

    /// <summary>
    /// One <c>member = value</c>. A member that is a record or an array takes a braced value,
    /// which is written on one line.
    /// </summary>
    private GreenNode? ParseMemberValue()
    {
        if (!AtName)
        {
            Report("expected a member name");
            return null;
        }
        var name = Advance();
        var equals = Kind == SyntaxKind.Equals ? Advance() : Missing(SyntaxKind.Equals, "expected `=`");
        return new MemberValueSyntax(name, equals, ParseDataValue());
    }

    /// <summary>One line of a multi-line initializer, which holds one <c>member = value</c>.</summary>
    private GreenNode ParseMemberValueLine() =>
        ParseMemberValue() is { } value
            ? Finish(value)
            : ErrorLine("expected `member = value`");

    /// <summary>
    /// The opener of an <c>.enum</c>, <c>.struct</c>, <c>.union</c>, <c>.charmap</c> or
    /// <c>.list</c>. <paramref name="named"/> says whether the name is required: an
    /// anonymous enum or struct declares into the scope around it, and a charmap or
    /// a list is only ever used by name.
    /// </summary>
    private GreenNode ParseTypeBlock(SyntaxKind kind, bool named)
    {
        var keyword = Advance();
        GreenToken? name = null;
        if (AtName)
            name = Advance();
        else if (named)
            Report("expected a name");

        // The name and the brace are separate news, so a line missing both is told about both.
        var brace = Kind == SyntaxKind.OpenBrace ? Advance() : Missing(SyntaxKind.OpenBrace, "expected `{`");
        return kind switch
        {
            SyntaxKind.EnumDeclaration => new EnumDeclarationSyntax(keyword, name, brace),
            SyntaxKind.StructDeclaration => new StructDeclarationSyntax(keyword, name, brace),
            SyntaxKind.UnionDeclaration => new UnionDeclarationSyntax(keyword, name, brace),
            SyntaxKind.CharmapDeclaration => new CharmapDeclarationSyntax(keyword, name, brace),
            SyntaxKind.ListDeclaration => new ListDeclarationSyntax(keyword, name, brace),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>One member of an <c>.enum</c>: a name, or a name and the value it is given.</summary>
    private GreenNode ParseEnumMember()
    {
        if (!AtName)
            return ErrorLine("expected a member name, or `name = expr`");
        var name = Advance();
        if (Kind != SyntaxKind.Equals)
            return Finish(new EnumMemberSyntax(name, null, null));
        var equals = Advance();
        return Finish(new EnumMemberSyntax(name, equals, ParseExpression()));
    }

    /// <summary>One entry of a <c>.charmap</c>: a character, or a range of them, and a value.</summary>
    private GreenNode ParseCharmapEntry()
    {
        var first = ParseExpression();
        GreenToken? dotDot = null;
        GreenNode? last = null;
        if (Kind == SyntaxKind.DotDot)
        {
            dotDot = Advance();
            last = ParseExpression();
        }
        var equals = Kind == SyntaxKind.Equals ? Advance() : Missing(SyntaxKind.Equals, "expected `=`");
        return Finish(new CharmapEntrySyntax(first, dotDot, last, equals, ParseExpression()));
    }

    /// <summary>One line of a <c>.list</c>, which holds one or more comma-separated items.</summary>
    private ImmutableArray<GreenNode> ParseListItems()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        ParseCommaSeparated(children, ParseExpression);
        return children.ToImmutable();
    }

    /// <summary><c>.func name(a, b) = expr</c>: a pure expression function.</summary>
    private GreenNode ParseFunc()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (AtName)
            children.Add(Advance());
        else
            Report("expected a function name");
        if (Kind == SyntaxKind.OpenParen)
            children.Add(ParseParameterList());
        else
            Report("expected `(` and the parameter names");
        if (Kind == SyntaxKind.Equals)
            children.Add(Advance());
        else
            Report("expected `=` and the body");
        children.Add(ParseExpression());
        return new GreenSyntax(SyntaxKind.FuncDeclaration, children.ToImmutable());
    }

    /// <summary><c>.signature std = a8, i16, dp = 0</c>: a name for items a signature uses.</summary>
    private GreenNode ParseSignatureDeclaration()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (!AtName)
        {
            Report("expected a name for the signature set");
            return new GreenSyntax(SyntaxKind.SignatureDeclaration, children.ToImmutable());
        }
        children.Add(Advance());
        if (Kind != SyntaxKind.Equals)
        {
            Report("expected `=` and the items: `.signature std = a8, i16`");
            return new GreenSyntax(SyntaxKind.SignatureDeclaration, children.ToImmutable());
        }
        children.Add(Advance());
        children.Add(ParseStateList());
        return new GreenSyntax(SyntaxKind.SignatureDeclaration, children.ToImmutable());
    }

    /// <summary><c>.config NAME = value</c>: a setting, whose value the build may give instead.</summary>
    private GreenNode ParseConfig()
    {
        var keyword = Advance();
        if (Kind != SyntaxKind.Identifier)
        {
            Report("expected the setting's name: `.config NAME = value`");
            return Unwritten(GreenToken.Missing(SyntaxKind.Identifier));
        }
        var name = Advance();
        if (Kind != SyntaxKind.Equals)
        {
            Report("expected `=` and the setting's value: `.config NAME = value`");
            return Unwritten(name);
        }
        return new ConfigDeclarationSyntax(keyword, name, Advance(), ParseExpression());

        // A line that stops short has a place for the `=` and the value all the same, and what
        // has been said about the piece it stopped at is news enough for one line.
        GreenNode Unwritten(GreenToken setting) =>
            new ConfigDeclarationSyntax(keyword, setting, GreenToken.Missing(SyntaxKind.Equals),
                new ErrorExpressionSyntax(null));
    }

    private GreenNode ParseParameterList()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind != SyntaxKind.CloseParen && !AtEnd)
        {
            ParseCommaSeparated(children, () =>
            {
                if (AtName)
                    return Advance();
                Report("expected a parameter name");
                return null;
            });
        }
        if (Kind == SyntaxKind.CloseParen)
            children.Add(Advance());
        else
            Report("expected `)`");
        return new GreenSyntax(SyntaxKind.ParameterList, children.ToImmutable());
    }

    /// <summary>
    /// <c>.macro name(params) {</c>, with the processor state it expects and leaves. The
    /// body is ordinary nt65 and parses on its own, so only the opener is read here.
    /// </summary>
    private GreenNode ParseMacro()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (AtName)
            children.Add(Advance());
        else
            Report("expected a macro name");
        if (Kind == SyntaxKind.OpenParen)
            children.Add(ParseMacroParameterList());
        else
            ReportOnce("expected `(` and the parameters");
        if (Kind == SyntaxKind.Colon)
            children.Add(ParseSignature());
        ExpectOpenBrace(children);
        return new GreenSyntax(SyntaxKind.MacroDeclaration, children.ToImmutable());
    }

    private GreenNode ParseMacroParameterList()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind != SyntaxKind.CloseParen && !AtEnd)
            ParseCommaSeparated(children, ParseMacroParameter);
        if (Kind == SyntaxKind.CloseParen)
            children.Add(Advance());
        else
            ReportOnce("expected `)`");
        return new GreenSyntax(SyntaxKind.MacroParameterList, children.ToImmutable());
    }

    /// <summary><c>name</c>, <c>name: kind</c>, <c>name = default</c> or all three.</summary>
    private GreenNode? ParseMacroParameter()
    {
        if (!AtName)
        {
            Report("expected a parameter name");
            return null;
        }
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind == SyntaxKind.Colon)
        {
            children.Add(Advance());
            children.Add(ParseParameterKind());
        }
        if (Kind == SyntaxKind.Equals)
        {
            children.Add(Advance());

            // `= {}`: a block parameter a call may leave out, which is empty when it does.
            children.Add(Kind == SyntaxKind.OpenBrace && Next == SyntaxKind.CloseBrace
                ? new GreenSyntax(SyntaxKind.EmptyBlock, [Advance(), Advance()])
                : ParseExpression());
        }
        return new GreenSyntax(SyntaxKind.MacroParameter, children.ToImmutable());
    }

    /// <summary>
    /// What a parameter takes: one of the fixed words, the listed words of a
    /// <c>one(...)</c>, or a <c>list(...)</c> of one of those.
    /// </summary>
    private GreenNode ParseParameterKind()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        if (Kind != SyntaxKind.Identifier || !SyntaxFacts.IsParameterKind(Current.Text))
        {
            Report("expected `expr`, `const`, `ident`, `operand`, `one(...)`, `list(...)` or `block`");
            return new GreenSyntax(SyntaxKind.ParameterKind, children.ToImmutable());
        }

        var listed = AtWord("one");
        var nested = AtWord("list");
        children.Add(Advance());
        if (!listed && !nested)
            return new GreenSyntax(SyntaxKind.ParameterKind, children.ToImmutable());

        if (Kind != SyntaxKind.OpenParen)
        {
            Report("expected `(`");
            return new GreenSyntax(SyntaxKind.ParameterKind, children.ToImmutable());
        }
        children.Add(Advance());
        if (listed)
        {
            // The words a `one` accepts are never looked up, so a register or a mnemonic
            // among them is a word like any other.
            ParseCommaSeparated(children, () =>
            {
                if (AtName)
                    return Advance();
                Report("expected a word");
                return null;
            });
        }
        else
        {
            children.Add(ParseParameterKind());
        }
        if (Kind == SyntaxKind.CloseParen)
            children.Add(Advance());
        else
            ReportOnce("expected `)`");
        return new GreenSyntax(SyntaxKind.ParameterKind, children.ToImmutable());
    }

    /// <summary>
    /// <c>name!(args)</c>, with the <c>{</c> of a trailing block argument when one follows.
    /// An argument's syntax never depends on the kind of the parameter it binds to, so every
    /// argument is read the same way and the kinds are checked once names are resolved.
    /// </summary>
    private GreenNode ParseMacroCall()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        children.Add(Advance());
        if (Kind == SyntaxKind.OpenParen)
            children.Add(ParseMacroArguments());
        else
            Report("expected `(` and the arguments");
        if (Kind == SyntaxKind.OpenBrace)
            children.Add(Advance());
        return new GreenSyntax(SyntaxKind.MacroCall, children.ToImmutable());
    }

    private GreenNode ParseMacroArguments()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind != SyntaxKind.CloseParen && !AtEnd)
            ParseCommaSeparated(children, ParseArgument);
        if (Kind == SyntaxKind.CloseParen)
            children.Add(Advance());
        else
            ReportOnce("expected `)`");
        return new GreenSyntax(SyntaxKind.ArgumentList, children.ToImmutable());
    }

    /// <summary>
    /// One argument: an expression, a braced operand, or a parameter named and then given
    /// one of those. <c>=</c> appears in no expression, so a named argument is unambiguous.
    /// </summary>
    private GreenNode? ParseArgument()
    {
        if (AtName && Next == SyntaxKind.Equals)
        {
            var children = ImmutableArray.CreateBuilder<GreenNode>();
            children.Add(Advance());
            children.Add(Advance());
            if (ParseArgument() is { } given)
                children.Add(given);
            return new GreenSyntax(SyntaxKind.NamedArgument, children.ToImmutable());
        }
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
    /// The token of <paramref name="kind"/> written here, or the missing token that stands where
    /// one belongs, with <paramref name="message"/> reported there. A slot the source does not
    /// fill is filled from here and nowhere else.
    /// </summary>
    private GreenToken Expect(SyntaxKind kind, string message)
    {
        if (Kind == kind)
            return Advance();
        ReportOnce(message);
        return GreenToken.Missing(kind);
    }

    /// <summary>
    /// The missing token of <paramref name="kind"/>, standing where one belongs that the source
    /// does not have, with <paramref name="message"/> reported there whether or not the line has
    /// been reported on already: what <see cref="Expect"/> does where the second piece missing on
    /// a line is news of its own.
    /// </summary>
    private GreenToken Missing(SyntaxKind kind, string message)
    {
        Report(message);
        return GreenToken.Missing(kind);
    }

    /// <summary>
    /// The name written here, or the missing identifier that stands where one belongs, with
    /// <paramref name="message"/> reported there. A name may be spelled as an identifier, a
    /// register or a mnemonic, which is why it is not one kind for <see cref="Expect"/>.
    /// </summary>
    private GreenToken ExpectName(string message)
    {
        if (AtName)
            return Advance();
        ReportOnce(message);
        return GreenToken.Missing(SyntaxKind.Identifier);
    }

    /// <summary>The <c>{</c> that opens a block: the one place the parser says a brace is wanted.</summary>
    private GreenToken ExpectOpenBrace() => Expect(SyntaxKind.OpenBrace, "expected `{`");

    private void ExpectOpenBrace(ImmutableArray<GreenNode>.Builder children)
    {
        if (ExpectOpenBrace() is { IsMissing: false } brace)
            children.Add(brace);
    }

    private GreenNode ParseCpuDirective()
    {
        var keyword = Advance();
        if (Kind is SyntaxKind.CpuName or SyntaxKind.NumberLiteral or SyntaxKind.Identifier && SyntaxFacts.IsCpuName(Current.Text))
            return new CpuDirectiveSyntax(keyword, Advance());
        Report($"expected {Project.CpuNames.Listed}");
        return new CpuDirectiveSyntax(keyword, GreenToken.Missing(SyntaxKind.CpuName));
    }

    /// <summary>
    /// A segment declaration, <c>.segment NAME: size</c> with its attributes; the line opening
    /// a segment block, <c>.segment NAME {</c>; or a region line, <c>.segment NAME</c>. The
    /// brace and the size decide which. A segment name is an identifier: segments are a table
    /// of their own, and share no namespace with symbols.
    /// </summary>
    private GreenNode ParseSegment()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        var kind = opensBlock ? SyntaxKind.SegmentBlock
            : tokens.Any(token => token.Kind == SyntaxKind.Colon) ? SyntaxKind.SegmentDeclaration
            : SyntaxKind.SegmentRegion;
        if (AtName)
        {
            children.Add(Advance());
        }
        else if (Kind == SyntaxKind.StringLiteral)
        {
            Report($"a segment name is written without quotes: `.segment {Current.Text.Trim('"')}`");
            children.Add(Advance());
        }
        else
        {
            Report("expected a segment name");
            return new GreenSyntax(kind, children.ToImmutable());
        }

        if (kind != SyntaxKind.SegmentDeclaration)
        {
            if (Kind == SyntaxKind.OpenBrace)
                children.Add(Advance());
            return new GreenSyntax(kind, children.ToImmutable());
        }

        if (Kind != SyntaxKind.Colon)
        {
            Report("expected `:` and an address size");
            return new GreenSyntax(SyntaxKind.SegmentDeclaration, children.ToImmutable());
        }
        children.Add(Advance());
        if (Kind == SyntaxKind.Identifier && SyntaxFacts.IsAddressSize(Current.Text))
        {
            children.Add(Advance());
            while (Kind == SyntaxKind.Comma)
            {
                children.Add(Advance());
                children.Add(ParseSegmentAttribute());
            }
        }
        else
        {
            Report("expected `zp`, `abs` or `far`");
        }
        return new GreenSyntax(SyntaxKind.SegmentDeclaration, children.ToImmutable());
    }

    /// <summary><c>dp = expr</c>, <c>bank = expr</c> or <c>mirrors = [$00..$3f, $80..$bf]</c>.</summary>
    private GreenNode ParseSegmentAttribute()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        if (!AtWord("dp") && !AtWord("bank") && !AtWord("mirrors"))
        {
            Report("expected `dp`, `bank` or `mirrors`");
            return new GreenSyntax(SyntaxKind.SegmentAttribute, children.ToImmutable());
        }
        var mirrors = AtWord("mirrors");
        children.Add(Advance());
        if (Kind != SyntaxKind.Equals)
        {
            Report("expected `=`");
            return new GreenSyntax(SyntaxKind.SegmentAttribute, children.ToImmutable());
        }
        children.Add(Advance());
        if (!mirrors)
        {
            children.Add(ParseExpression());
            return new GreenSyntax(SyntaxKind.SegmentAttribute, children.ToImmutable());
        }

        if (Kind != SyntaxKind.OpenBracket)
        {
            Report("expected `[` and the banks: `mirrors = [$00..$3f, $80..$bf]`");
            return new GreenSyntax(SyntaxKind.SegmentAttribute, children.ToImmutable());
        }
        children.Add(Advance());
        if (Kind != SyntaxKind.CloseBracket)
            ParseCommaSeparated(children, ParseBankRange);
        if (Kind == SyntaxKind.CloseBracket)
            children.Add(Advance());
        else
            Report("expected `]`");
        return new GreenSyntax(SyntaxKind.SegmentAttribute, children.ToImmutable());
    }

    /// <summary><c>$80</c> or <c>$00..$3f</c>: one bank or a range of them.</summary>
    private GreenNode ParseBankRange()
    {
        var first = ParseExpression();
        if (Kind != SyntaxKind.DotDot)
            return new BankRangeSyntax(first, null, null);
        var dotDot = Advance();
        return new BankRangeSyntax(first, dotDot, ParseExpression());
    }

    private GreenNode ParseProc()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (!AtName)
        {
            Report("expected a routine name");
            return new GreenSyntax(SyntaxKind.ProcDeclaration, children.ToImmutable());
        }
        children.Add(Advance());

        // `.proc name = expr` is an extern proc: a signature and an address, with no body.
        if (Kind == SyntaxKind.Equals)
        {
            children.Add(Advance());
            children.Add(ParseExpression());
            if (Kind == SyntaxKind.Colon)
                children.Add(ParseSignature());
            return new GreenSyntax(SyntaxKind.ExternProcDeclaration, children.ToImmutable());
        }

        if (Kind == SyntaxKind.Colon)
            children.Add(ParseSignature());
        if (Kind == SyntaxKind.OpenBrace)
            children.Add(Advance());
        else if (errors.Count == 0)
            Report("expected `{`, or `= address` for a routine with no body");
        return new GreenSyntax(SyntaxKind.ProcDeclaration, children.ToImmutable());
    }

    /// <summary>
    /// <c>.multiproc E, b: signature {</c>: one routine per member of the enum <c>E</c>, named
    /// after the member. It folds a repetition and a routine into one line, so it is read as a
    /// repetition's opener and then a routine's signature. The name to bind is what the
    /// routines are named from, so it is not optional as a repetition's is.
    /// </summary>
    private GreenNode ParseMultiProc()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        children.Add(ParseExpression());
        if (Kind == SyntaxKind.Comma)
        {
            children.Add(Advance());
            if (AtName)
                children.Add(Advance());
            else
                Report("expected the name to bind, which each routine is named from");
        }
        else
        {
            ReportOnce("expected `,` and the name to bind: `.multiproc Channel, ch {`");
        }
        if (Kind == SyntaxKind.Colon)
            children.Add(ParseSignature());
        ExpectOpenBrace(children);
        return new GreenSyntax(SyntaxKind.MultiProcDeclaration, children.ToImmutable());
    }

    private GreenNode ParseScope()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (AtName)
            children.Add(Advance());
        if (Kind == SyntaxKind.OpenBrace)
            children.Add(Advance());
        else
            Report("expected `{`");
        return new GreenSyntax(SyntaxKind.ScopeDeclaration, children.ToImmutable());
    }

    /// <summary>
    /// <c>.next @a, gfx::init</c>, the labels flow reaches after the statement above, or
    /// <c>.next ?</c>, which ends the path and checks nothing beyond it.
    /// </summary>
    private GreenNode ParseNext()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind == SyntaxKind.Question)
            children.Add(Advance());
        else
            ParseCommaSeparated(children, () => ParseTarget("expected a label flow continues at, or `?`"));
        return new GreenSyntax(SyntaxKind.NextDirective, children.ToImmutable());
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
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind != SyntaxKind.Identifier)
        {
            Report("expected a name for the frame");
            return new GreenSyntax(SyntaxKind.FrameDirective, children.ToImmutable());
        }
        children.Add(Advance());
        if (Kind != SyntaxKind.Colon)
        {
            Report("expected `:` and the struct the frame is laid out as");
            return new GreenSyntax(SyntaxKind.FrameDirective, children.ToImmutable());
        }
        children.Add(Advance());
        children.Add(ParseExpression());
        return new GreenSyntax(SyntaxKind.FrameDirective, children.ToImmutable());
    }

    /// <summary><c>.patch @op</c>: the one instruction the store above writes into.</summary>
    private GreenNode ParsePatch()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (ParseTarget("expected the label of the instruction being written to") is { } target)
            children.Add(target);
        return new GreenSyntax(SyntaxKind.PatchDirective, children.ToImmutable());
    }

    /// <summary>A label named by an annotation: a cheap local, a name, or a scoped path.</summary>
    private GreenNode? ParseTarget(string expected)
    {
        if (AtName || Kind is SyntaxKind.CheapLocal or SyntaxKind.ColonColon)
            return ParseName();
        Report(expected);
        return null;
    }

    /// <summary>
    /// <c>.export</c> before a declaration, which exports what it declares, or a list of names:
    /// <c>.export a, outer::inner, K: abs, init as "_init"</c>. A declaration reads as the same
    /// declaration written without the <c>.export</c>, which the line holds instead.
    /// </summary>
    private GreenNode ParseExport()
    {
        var export = Advance();
        if (Kind == SyntaxKind.Directive && ParseExportable() is { } declaration)
        {
            exportKeyword = export;
            return declaration;
        }
        if (Kind == SyntaxKind.Directive)
        {
            Report($"`.export` goes before a declaration, and `{Current.Text}` declares nothing to export");
            return new GreenSyntax(SyntaxKind.ExportDirective, [export]);
        }
        if (AtName && Next == SyntaxKind.Equals)
        {
            exportKeyword = export;
            var name = Advance();
            var equals = Advance();
            return new ConstantDeclarationSyntax(name, equals, ParseExpression());
        }

        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(export);
        ParseCommaSeparated(children, ParseExportItem);
        return new GreenSyntax(SyntaxKind.ExportDirective, children.ToImmutable());
    }

    /// <summary>The declaration after <c>.export</c>, or null when the directive declares nothing that can be exported.</summary>
    private GreenNode? ParseExportable() => SyntaxFacts.LineDirectiveKind(Current.Text) switch
    {
        SyntaxKind.DataDeclaration => ParseDataDeclaration(),
        SyntaxKind.ProcDeclaration => ParseProc(),
        SyntaxKind.MultiProcDeclaration => ParseMultiProc(),
        SyntaxKind.ScopeDeclaration => ParseScope(),
        SyntaxKind.ImportDirective => ParseImport(),
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
        _ => null,
    };

    /// <summary><c>name</c> or <c>outer::inner</c>, then <c>: size</c> or <c>as "linker_name"</c>.</summary>
    private GreenNode? ParseExportItem()
    {
        if (!AtName)
        {
            Report("expected a name to export");
            return null;
        }
        var name = ParseName();
        GreenToken? colon = null;
        GreenToken? addressSize = null;
        if (Kind == SyntaxKind.Colon)
        {
            colon = Advance();
            if (Kind == SyntaxKind.Identifier && SyntaxFacts.IsAddressSize(Current.Text))
                addressSize = Advance();
            else
                Report("expected `zp`, `abs` or `far`");
        }

        GreenToken? asKeyword = null;
        GreenToken? linkerName = null;
        if (AtWord("as"))
        {
            asKeyword = Advance();
            if (Kind == SyntaxKind.StringLiteral)
                linkerName = Advance();
            else
                Report("expected the linker name, in quotes: `as \"_name\"`");
        }
        return new ExportItemSyntax(name, colon, addressSize, asKeyword, linkerName);
    }

    /// <summary><c>.module name</c> or <c>.module outer::inner</c>.</summary>
    private GreenNode ParseModule()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind == SyntaxKind.StringLiteral)
            Report("a module name is written without quotes: `.module hw::vic`");
        else
            ParsePath(children, "expected the module's name: `.module name`");
        return new GreenSyntax(SyntaxKind.ModuleDirective, children.ToImmutable());
    }

    /// <summary>
    /// <c>.use a::b</c>, <c>.use a::{b, c as d}</c>, <c>.use a::*</c> or <c>.use a::b as c</c>. A
    /// path is always written from the root of the modules, and names at least a module and
    /// one name in it, or a module.
    /// </summary>
    private GreenNode ParseUse()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (!ParsePath(children, "expected what to use: `.use module::name`"))
            return new GreenSyntax(SyntaxKind.UseDirective, children.ToImmutable());

        if (Kind == SyntaxKind.ColonColon && Next is SyntaxKind.Star or SyntaxKind.OpenBrace)
        {
            children.Add(Advance());
            if (Kind == SyntaxKind.Star)
            {
                children.Add(Advance());
                return new GreenSyntax(SyntaxKind.UseDirective, children.ToImmutable());
            }
            children.Add(Advance());
            ParseCommaSeparated(children, ParseUseItem);
            if (Kind == SyntaxKind.CloseBrace)
                children.Add(Advance());
            else
                ReportOnce("expected `}`");
            return new GreenSyntax(SyntaxKind.UseDirective, children.ToImmutable());
        }
        var (asKeyword, alias) = ParseUseAlias();
        if (asKeyword is { } written)
            children.Add(written);
        if (alias is { } name)
            children.Add(name);
        return new GreenSyntax(SyntaxKind.UseDirective, children.ToImmutable());
    }

    /// <summary>One name in the braces of a <c>.use</c>, and the name it is brought in as.</summary>
    private GreenNode? ParseUseItem()
    {
        if (!AtName)
        {
            ReportOnce("expected a name");
            return null;
        }
        var name = Advance();
        var (asKeyword, alias) = ParseUseAlias();
        return new UseItemSyntax(name, asKeyword, alias);
    }

    /// <summary>The <c>as</c> and the name after it, when they are written.</summary>
    private (GreenToken? AsKeyword, GreenToken? Alias) ParseUseAlias()
    {
        if (!AtWord("as"))
            return (null, null);
        var keyword = Advance();
        if (AtName)
            return (keyword, Advance());
        ReportOnce("expected the name to bring it in as: `as name`");
        return (keyword, null);
    }

    /// <summary>
    /// <c>a::b::c</c> as tokens, stopping before a <c>::</c> that is not followed by a name.
    /// False when not even the first name is there.
    /// </summary>
    private bool ParsePath(ImmutableArray<GreenNode>.Builder children, string expected)
    {
        if (!AtName)
        {
            Report(expected);
            return false;
        }
        children.Add(Advance());
        while (Kind == SyntaxKind.ColonColon && Next is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic)
        {
            children.Add(Advance());
            children.Add(Advance());
        }
        return true;
    }

    private GreenNode ParseImport()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        ParseCommaSeparated(children, ParseImportItem);
        return new GreenSyntax(SyntaxKind.ImportDirective, children.ToImmutable());
    }

    /// <summary><c>name</c>, <c>name: size</c>, <c>name: proc(...)</c> or a checked <c>name = expr</c>.</summary>
    private GreenNode? ParseImportItem()
    {
        if (!AtName)
        {
            Report("expected a name to import");
            return null;
        }
        var name = Advance();
        GreenToken? equals = null;
        GreenNode? value = null;
        GreenToken? colon = null;
        GreenToken? addressSize = null;
        ImportSignatureSyntax? signature = null;

        if (Kind == SyntaxKind.Equals)
        {
            equals = Advance();
            value = ParseExpression();
        }
        else if (Kind == SyntaxKind.Colon)
        {
            colon = Advance();
            if (AtWord("proc"))
                signature = ParseImportSignature();
            else if (Kind == SyntaxKind.Identifier && SyntaxFacts.IsAddressSize(Current.Text))
                addressSize = Advance();
            else
                Report("expected `zp`, `abs`, `far` or `proc(...)`");
        }
        return new ImportItemSyntax(name, equals, value, colon, addressSize, signature);
    }

    private ImportSignatureSyntax ParseImportSignature()
    {
        var keyword = Advance();
        if (Kind != SyntaxKind.OpenParen)
        {
            Report("expected `(`");
            return new ImportSignatureSyntax(
                keyword, GreenToken.Missing(SyntaxKind.OpenParen), null, null, null,
                GreenToken.Missing(SyntaxKind.CloseParen));
        }
        var openParen = Advance();

        // Both halves are optional: on the 6502 and its CMOS variants a routine's signature may be empty.
        GreenNode? entry = null;
        GreenToken? arrow = null;
        GreenNode? exit = null;
        if (Kind is not (SyntaxKind.CloseParen or SyntaxKind.Arrow))
            entry = ParseStateList();
        if (Kind == SyntaxKind.Arrow)
        {
            arrow = Advance();
            if (Kind != SyntaxKind.CloseParen)
                exit = ParseStateList();
        }

        GreenToken closeParen;
        if (Kind == SyntaxKind.CloseParen)
        {
            closeParen = Advance();
        }
        else
        {
            Report("expected `)`");
            closeParen = GreenToken.Missing(SyntaxKind.CloseParen);
        }
        return new ImportSignatureSyntax(keyword, openParen, entry, arrow, exit, closeParen);
    }

    /// <summary>The <c>: entry -&gt; exit</c> of a proc, an extern proc or a macro.</summary>
    private GreenNode ParseSignature()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        children.Add(ParseStateList());
        if (Kind == SyntaxKind.Arrow)
        {
            children.Add(Advance());
            children.Add(ParseStateList());
        }
        return new GreenSyntax(SyntaxKind.ProcSignature, children.ToImmutable());
    }

    private GreenNode ParseStateList()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        ParseCommaSeparated(children, ParseStateItem);
        return new GreenSyntax(SyntaxKind.StateList, children.ToImmutable());
    }

    private GreenNode? ParseStateItem()
    {
        // A name that is no item's word names a signature set, which stands for its items. One
        // spelled like a width, `a9`, is a width misspelled.
        if (Kind == SyntaxKind.ColonColon
            || (Kind == SyntaxKind.Identifier && !SyntaxFacts.IsStateWord(Current.Text) && !LooksLikeAWidth(Current.Text)))
        {
            return new GreenSyntax(SyntaxKind.StateItem, [ParseName()]);
        }

        // `?` on its own is every tracked part of the state unknown, the state a routine
        // reached from outside nt65 is entered in.
        if (Kind == SyntaxKind.Question)
            return new GreenSyntax(SyntaxKind.StateItem, [Advance()]);

        // `a` and `i` are the accumulator and index widths; `a` arrives as a register token.
        if (Kind is not (SyntaxKind.Identifier or SyntaxKind.Register))
        {
            Report("expected a processor-state item");
            return null;
        }

        var children = ImmutableArray.CreateBuilder<GreenNode>();
        var nameIndex = index;
        var name = Advance();
        children.Add(name);
        var suffix = SyntaxKind.None;
        if (Kind is SyntaxKind.Star or SyntaxKind.Question)
        {
            suffix = Kind;
            children.Add(Advance());
        }
        else if (Kind == SyntaxKind.Equals)
        {
            suffix = Kind;
            children.Add(Advance());
            children.Add(ParseExpression());
        }

        if (!SyntaxFacts.IsStateItem(name.Text, suffix))
        {
            Report(nameIndex, $"`{name.Text}` is not a processor-state item");
        }
        else if (name.Text.Equals("args", StringComparison.OrdinalIgnoreCase))
        {
            // `args n`: how many bytes the caller pushes before the call.
            children.Add(ParseExpression());
        }
        else if (name.Text.Equals("inline", StringComparison.OrdinalIgnoreCase))
        {
            // `inline n` or `inline .strz`: how much data follows each call.
            if (Kind == SyntaxKind.Directive
                && Current.Text.Equals(".strz", StringComparison.OrdinalIgnoreCase))
                children.Add(Advance());
            else
                children.Add(ParseExpression());
        }
        else if (name.Text.Equals("keeps", StringComparison.OrdinalIgnoreCase))
        {
            ParseKeptRegisters(children);
        }
        return new GreenSyntax(SyntaxKind.StateItem, children.ToImmutable());
    }

    /// <summary>
    /// The registers of a <c>keeps a, x</c>. A bare register name is an item nowhere else, so
    /// the list runs on through the commas that separate the signature's own items, and stops
    /// at the first comma that is followed by anything else.
    /// </summary>
    private void ParseKeptRegisters(ImmutableArray<GreenNode>.Builder children)
    {
        if (!AtKeptRegister(index))
        {
            Report("expected the registers it keeps: `keeps a`, `keeps x, y`");
            return;
        }
        children.Add(Advance());
        while (Kind == SyntaxKind.Comma && AtKeptRegister(index + 1))
        {
            children.Add(Advance());
            children.Add(Advance());
        }
    }

    /// <summary>Whether the token at <paramref name="at"/> names a register a <c>keeps</c> may take.</summary>
    private bool AtKeptRegister(int at) =>
        at < tokens.Length && tokens[at].Kind is SyntaxKind.Identifier or SyntaxKind.Register
        && SyntaxFacts.IsKeptRegister(tokens[at].Text);

    private static bool LooksLikeAWidth(string text) =>
        text.Length > 1 && char.ToLowerInvariant(text[0]) is 'a' or 'i' && text[1..].All(char.IsAsciiDigit);

    /// <summary>
    /// One or more items separated by commas, with the commas kept. A parse that yields
    /// nothing stops the list, so an unreadable item cannot loop.
    /// </summary>
    private void ParseCommaSeparated(ImmutableArray<GreenNode>.Builder children, Func<GreenNode?> parseItem)
    {
        if (parseItem() is not { } first)
            return;
        children.Add(first);
        while (Kind == SyntaxKind.Comma)
        {
            children.Add(Advance());
            if (parseItem() is not { } next)
                return;
            children.Add(next);
        }
    }

    private InstructionStatementSyntax ParseInstruction()
    {
        var mnemonic = Advance();
        return new InstructionStatementSyntax(mnemonic, AtEnd ? null : ParseOperand());
    }

    /// <summary>The operand forms. Which ones each CPU and mnemonic allow is layout's to say.</summary>
    private OperandSyntax ParseOperand()
    {
        if (Kind == SyntaxKind.Hash)
            return ParseImmediate();

        // `asl a` is the accumulator; `a:` is an address-size prefix on what follows.
        if (Kind == SyntaxKind.Register && Next != SyntaxKind.Colon
            && Current.Text.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            return new AccumulatorOperandSyntax(Advance());
        }

        if (Kind == SyntaxKind.OpenParen && TryParseIndirect() is { } indirect)
            return indirect;
        if (Kind == SyntaxKind.OpenBracket)
            return ParseLongIndirect();
        return ParseAddressOperand();
    }

    private ImmediateOperandSyntax ParseImmediate()
    {
        var hash = Advance();
        var value = ParseExpression();

        // `mvn #src, #dst` and `mvp` take two bank bytes, written as immediates.
        return Kind == SyntaxKind.Comma && Next == SyntaxKind.Hash
            ? new ImmediateOperandSyntax(hash, value, Advance(), Advance(), ParseExpression())
            : new ImmediateOperandSyntax(hash, value, null, null, null);
    }

    /// <summary>
    /// <c>(expr)</c>, <c>(expr),y</c>, <c>(expr,x)</c> and <c>(expr,s),y</c>, or null when the
    /// parentheses turn out to be an ordinary expression: <c>lda (a + b) * 2</c> is not
    /// indirect. Only a whole operand in parentheses is, which is how ca65 reads it too.
    /// </summary>
    private OperandSyntax? TryParseIndirect()
    {
        var start = index;
        var errorCount = errors.Count;
        var openParen = Advance();
        var address = ParseExpression();

        (GreenToken Comma, GreenToken Register)? inner = null;
        if (Kind == SyntaxKind.Comma && (IsRegister(1, "x") || IsRegister(1, "s")))
            inner = (Advance(), Advance());
        if (Kind == SyntaxKind.CloseParen)
        {
            var closeParen = Advance();
            GreenToken? comma = null, register = null;
            if (Kind == SyntaxKind.Comma && IsRegister(1, "y"))
            {
                comma = Advance();
                register = Advance();
            }
            if (AtOperandEnd)
            {
                return inner is (var innerComma, var innerRegister)
                    ? new IndexedIndirectOperandSyntax(
                        openParen, address, innerComma, innerRegister, closeParen, comma, register)
                    : new IndirectOperandSyntax(openParen, address, closeParen, comma, register);
            }
        }

        // An attempt that comes to nothing leaves nothing: the tokens it read are read again as
        // an ordinary expression, and the nodes it built go with the errors reported over them,
        // so no missing token it stood in for outlives it.
        index = start;
        errors.RemoveRange(errorCount, errors.Count - errorCount);
        return null;
    }

    private LongIndirectOperandSyntax ParseLongIndirect()
    {
        var openBracket = Advance();
        var address = ParseExpression();
        var closeBracket = Kind == SyntaxKind.CloseBracket
            ? Advance()
            : Missing(SyntaxKind.CloseBracket, "expected `]`");
        return Kind == SyntaxKind.Comma && IsRegister(1, "y")
            ? new LongIndirectOperandSyntax(openBracket, address, closeBracket, Advance(), Advance())
            : new LongIndirectOperandSyntax(openBracket, address, closeBracket, null, null);
    }

    private AbsoluteOperandSyntax ParseAddressOperand()
    {
        var prefix = TryAddressPrefix();
        var address = ParseExpression();
        if (Kind != SyntaxKind.Comma)
            return new AbsoluteOperandSyntax(prefix, address, null, null, null);
        var comma = Advance();

        // `,x`, `,y` and `,s` index; a second expression is what `bbr`/`bbs` take.
        return Kind == SyntaxKind.Register
            ? new AbsoluteOperandSyntax(prefix, address, comma, Advance(), null)
            : new AbsoluteOperandSyntax(prefix, address, comma, null, ParseExpression());
    }

    /// <summary>
    /// <c>z:</c>, <c>a:</c>, <c>f:</c> or <c>d:</c>. The lexer emits a name and a <c>:</c>
    /// and the parser decides by position; here, in operand position, nothing else can
    /// be written, so a name followed by <c>:</c> is a prefix.
    /// </summary>
    private AddressPrefixSyntax? TryAddressPrefix()
    {
        if (Next != SyntaxKind.Colon || Kind is not (SyntaxKind.Identifier or SyntaxKind.Register)
            || !SyntaxFacts.IsAddressPrefix(Current.Text))
        {
            return null;
        }
        return new AddressPrefixSyntax(Advance(), Advance());
    }

    private bool IsRegister(int offset, string name) =>
        index + offset < tokens.Length && tokens[index + offset].Kind == SyntaxKind.Register
        && tokens[index + offset].Text.Equals(name, StringComparison.OrdinalIgnoreCase);

    private GreenNode ParseExpression() => ParseBinary(LowestPrecedence);

    private GreenNode ParseBinary(int level)
    {
        if (level < TightestPrecedence)
            return ParseUnary();

        var left = ParseBinary(level - 1);
        while (SyntaxFacts.BinaryPrecedence(Current) == level)
        {
            var operatorIndex = index;
            var op = Advance();
            var right = ParseBinary(level - 1);
            CheckRequiredParentheses(operatorIndex, op, left, right);
            left = new BinaryExpressionSyntax(left, op, right);
        }
        return left;
    }

    private GreenNode ParseUnary()
    {
        if (!SyntaxFacts.IsUnaryOperator(Kind))
            return ParsePrimary();
        var op = Advance();
        return new UnaryExpressionSyntax(op, ParseUnary());
    }

    private GreenNode ParsePrimary()
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

    private GreenNode ParseParenthesized()
    {
        var open = Advance();
        var expression = ParseExpression();
        return new ParenthesizedExpressionSyntax(open, expression, Expect(SyntaxKind.CloseParen, "expected `)`"));
    }

    private GreenNode ParseBuiltinCall()
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

    private GreenNode ParseArgumentList()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind is not SyntaxKind.CloseParen && !AtEnd)
            ParseCommaSeparated(children, ParseExpression);
        if (Kind == SyntaxKind.CloseParen)
            children.Add(Advance());
        else
            Report("expected `)`");
        return new GreenSyntax(SyntaxKind.ArgumentList, children.ToImmutable());
    }

    /// <summary>
    /// <c>::</c>-separated, as in <c>gfx::init</c>, <c>::top_level</c> and <c>Point::x</c>.
    /// <paramref name="indexed"/> allows <c>[i]</c> after a component, which only an expression
    /// does: after the <c>T</c> of a <c>.type T[n]</c> the brackets are the declaration's count.
    /// </summary>
    private GreenNode ParseName(bool indexed = false)
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        if (Kind == SyntaxKind.ColonColon)
            children.Add(Advance());
        // A register or a mnemonic is kept as a name rather than refused here: inside a macro
        // body it is a word, and everywhere else the binder's reserved-word error says more
        // than the parser could.
        if (Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
            or SyntaxKind.Register or SyntaxKind.Mnemonic)
        {
            children.Add(Advance());
        }
        else
        {
            Report("expected a name");
            return new GreenSyntax(SyntaxKind.NameExpression, children.ToImmutable());
        }
        if (indexed && Kind == SyntaxKind.OpenBracket)
            children.Add(ParseElementIndex());

        while (Kind == SyntaxKind.ColonColon)
        {
            children.Add(Advance());

            // A member of a named struct, union or enum may be spelled like a register or a
            // mnemonic: after `::` there is nothing else it could be.
            if (Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic)
            {
                children.Add(Advance());
            }
            else
            {
                Report("expected a name after `::`");
                break;
            }
            if (indexed && Kind == SyntaxKind.OpenBracket)
                children.Add(ParseElementIndex());
        }
        return new GreenSyntax(SyntaxKind.NameExpression, children.ToImmutable());
    }

    /// <summary><c>[i]</c> after a name: which element of a counted declaration it stands for.</summary>
    private GreenNode ParseElementIndex()
    {
        var open = Advance();
        GreenNode index;
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

    /// <summary>
    /// The operator of <paramref name="node"/> when it is a binary expression, or null. A green
    /// node names its pieces by slot, in the order its constructor takes them.
    /// </summary>
    private static GreenToken? OperatorOf(GreenNode node) =>
        node is BinaryExpressionSyntax binary ? (GreenToken)binary.GetSlot(BinaryOperator)! : null;

    /// <summary>
    /// The <c>&lt;</c>, <c>&gt;</c> or <c>^</c> at the right edge of an operand, if any.
    /// <c>&lt;label + 1</c> is <c>(&lt;label) + 1</c>, and in <c>1 + &lt;label + 2</c> the
    /// unary sits at the end of the left operand rather than at its head, so follow the
    /// right spine down.
    /// </summary>
    private static GreenToken? RightmostByteOperator(GreenNode node)
    {
        while (node is BinaryExpressionSyntax binary)
            node = binary.GetSlot(BinaryRight)!;
        if (node is not UnaryExpressionSyntax unary)
            return null;
        var op = (GreenToken)unary.GetSlot(UnaryOperator)!;
        return SyntaxFacts.IsByteOperator(op.Kind) ? op : null;
    }

    /// <summary>
    /// A parser error: the index of the token it is reported on, the message, and the change
    /// the message names as its fix, for an editor to offer, where it names one.
    /// </summary>
    public readonly record struct Error(int Token, string Message, DiagnosticFix? Fix = null);

    /// <summary>
    /// One line's parse, and the block kind it was parsed in, so a line whose surroundings have
    /// not changed can keep the nodes it already has.
    /// </summary>
    /// <param name="Context">The kind of block the line was read in.</param>
    /// <param name="ExportKeyword">The <c>.export</c> before a declaration the line exports, or null.</param>
    /// <param name="Node">What the line's own tokens parse to.</param>
    /// <param name="SkippedTokens">What the statement could not take, or null when nothing was left.</param>
    /// <param name="Errors">The errors found in the line.</param>
    public sealed record Result(
        BlockKind Context, GreenToken? ExportKeyword, GreenNode Node, GreenNode? SkippedTokens,
        ImmutableArray<Error> Errors);
}
