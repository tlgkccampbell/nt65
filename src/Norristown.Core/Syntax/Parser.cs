using System.Collections.Immutable;

namespace Norristown.Syntax;

/// <summary>
/// Parses one line's tokens into a statement. A line's syntax depends only on its own tokens
/// and the kind of block around it, so a line parses without looking at any other
/// line and its statement survives an edit anywhere else in the file.
/// <para>
/// The parser never aborts a line: whatever it cannot read becomes a
/// <see cref="SyntaxKind.SkippedTokens"/> child with a diagnostic, and the next line parses
/// normally. It never invents a token either, so a statement's text is exactly its line's
/// text and a piece the parser expected and did not find is simply absent from the node.
/// Everything above the parser therefore has to allow for a missing child.
/// </para>
/// </summary>
internal sealed class Parser
{
    /// <summary>The loosest binding level, which <see cref="ParseExpression"/> starts at.</summary>
    private const int LowestPrecedence = 13;

    /// <summary>The tightest binding level that is still a binary operator.</summary>
    private const int TightestPrecedence = 3;

    private readonly ImmutableArray<GreenToken> tokens;
    private readonly BlockKind context;
    private readonly bool opensBlock;
    private readonly List<Error> errors = [];
    private int index;

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
        return new Result(context, node, [.. parser.errors]);
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

    private void Report(int token, string message) => errors.Add(new Error(token, message));

    private static string Describe(GreenToken token) => $"`{token.Text}`";

    private GreenNode ParseLine(LineKind kind)
    {
        if (kind == LineKind.BlockClose)
            return ParseBlockClose();
        if (kind == LineKind.Blank)
            return Finish(SyntaxKind.BlankLine, []);

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
            case BlockKind.TagInitializer:
                return ParseTagValueLine();
            default:
                break;
        }

        return kind switch
        {
            LineKind.Label => ParseLabeledLine(),
            LineKind.Constant => ParseConstantDeclaration(),
            LineKind.Instruction => Finish(ParseInstruction()),
            LineKind.Directive => ParseDirectiveLine(),
            // A macro call, and a splice of a `block` parameter inside a macro body: Stage 9.
            LineKind.MacroCall => Unsupported(),
            LineKind.BareIdentifier when context == BlockKind.Macro => Unsupported(),
            _ => ErrorLine("expected a label, a constant, an instruction or a directive"),
        };
    }

    /// <summary>The statement, then anything left over, then the line break that ends the line.</summary>
    private GreenNode Finish(SyntaxKind kind, ImmutableArray<GreenNode> children)
    {
        var all = ImmutableArray.CreateBuilder<GreenNode>(children.Length + 2);
        all.AddRange(children);
        if (!AtEnd)
        {
            // One diagnostic per line is enough: where the parser has already said what it
            // wanted, the tokens it then walks past are the same problem said twice.
            if (errors.Count == 0)
                Report($"unexpected {Describe(Current)}");
            all.Add(new GreenSyntax(SyntaxKind.SkippedTokens, TakeRest()));
        }
        all.Add(Advance());
        return new GreenSyntax(kind, all.ToImmutable());
    }

    private GreenNode Finish(GreenSyntax statement) => Finish(statement.Kind, statement.Children);

    /// <summary>
    /// A construct a later stage brings online. Its tokens are kept, so the line is part of
    /// the tree and reads back exactly, and nothing about it is diagnosed yet.
    /// </summary>
    private GreenNode Unsupported(params ReadOnlySpan<GreenNode> leading)
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.AddRange(leading);
        children.AddRange(TakeRest());
        children.Add(Advance());
        return new GreenSyntax(SyntaxKind.UnsupportedLine, children.ToImmutable());
    }

    private GreenNode ErrorLine(string message)
    {
        Report(message);
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.AddRange(TakeRest());
        children.Add(Advance());
        return new GreenSyntax(SyntaxKind.ErrorLine, children.ToImmutable());
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
            return Finish(SyntaxKind.BlockCloseLine, [brace]);

        // `} .elseif expr {` and `} .else {` close one branch and open the next. A macro
        // call's `} name {` is Stage 9's.
        return SyntaxFacts.LineDirectiveKind(Current.Text) switch
        {
            SyntaxKind.ElseIfDirective => Finish(ParseIf(SyntaxKind.ElseIfDirective, brace)),
            SyntaxKind.ElseDirective => Finish(ParseElse(brace)),
            _ => Unsupported(brace),
        };
    }

    private GreenNode ParseLabeledLine()
    {
        var label = new GreenSyntax(SyntaxKind.Label, [Advance(), Advance()]);
        if (AtEnd)
            return Finish(SyntaxKind.LabeledLine, [label]);

        // A label may be followed by an instruction, a data directive or a macro call.
        if (Kind == SyntaxKind.Mnemonic)
            return Finish(SyntaxKind.LabeledLine, [label, ParseInstruction()]);
        if (Kind == SyntaxKind.Identifier && Next == SyntaxKind.Bang)
            return Unsupported(label);
        if (Kind != SyntaxKind.Directive)
        {
            Report("expected an instruction, a data directive or a macro call after a label");
            return Finish(SyntaxKind.LabeledLine, [label]);
        }

        switch (SyntaxFacts.LineDirectiveKind(Current.Text))
        {
            case SyntaxKind.DataDirective:
                return Finish(SyntaxKind.LabeledLine, [label, ParseDataDirective()]);
            case SyntaxKind.UnsupportedLine:
                return Unsupported(label);
            case SyntaxKind.None:
                Report($"unknown directive `{Current.Text}`");
                return Finish(SyntaxKind.LabeledLine, [label]);
            default:
                Report($"`{Current.Text}` may not follow a label");
                return Finish(SyntaxKind.LabeledLine, [label]);
        }
    }

    private GreenNode ParseConstantDeclaration()
    {
        var name = Advance();
        var equals = Advance();
        return Finish(SyntaxKind.ConstantDeclaration, [name, equals, ParseExpression()]);
    }

    private GreenNode ParseDirectiveLine()
    {
        var kind = SyntaxFacts.LineDirectiveKind(Current.Text);
        return kind switch
        {
            SyntaxKind.DataDirective => Finish(ParseDataDirective()),
            SyntaxKind.CpuDirective => Finish(ParseCpuDirective()),
            SyntaxKind.SegmentDeclaration => Finish(ParseSegment()),
            SyntaxKind.SegmentBlock => Finish(ParseSegmentShortcut()),
            SyntaxKind.ProcDeclaration => Finish(ParseProc()),
            SyntaxKind.ScopeDeclaration => Finish(ParseScope()),
            SyntaxKind.ExportDirective => Finish(ParseExport()),
            SyntaxKind.ImportDirective => Finish(ParseImport()),
            SyntaxKind.EnumDeclaration => Finish(ParseTypeBlock(SyntaxKind.EnumDeclaration, named: false)),
            SyntaxKind.StructDeclaration => Finish(ParseTypeBlock(SyntaxKind.StructDeclaration, named: false)),
            SyntaxKind.UnionDeclaration => Finish(ParseTypeBlock(SyntaxKind.UnionDeclaration, named: false)),
            SyntaxKind.CharmapDeclaration => Finish(ParseTypeBlock(SyntaxKind.CharmapDeclaration, named: true)),
            SyntaxKind.ListDeclaration => Finish(ParseTypeBlock(SyntaxKind.ListDeclaration, named: true)),
            SyntaxKind.FuncDeclaration => Finish(ParseFunc()),
            SyntaxKind.IfDirective => Finish(ParseIf(SyntaxKind.IfDirective)),
            SyntaxKind.RepeatDirective => Finish(ParseRepetition(SyntaxKind.RepeatDirective)),
            SyntaxKind.EachDirective => Finish(ParseRepetition(SyntaxKind.EachDirective)),
            SyntaxKind.AssertDirective => Finish(ParseAssert()),
            SyntaxKind.ErrorDirective => Finish(ParseError()),
            SyntaxKind.ElseIfDirective or SyntaxKind.ElseDirective =>
                ErrorLine($"`{Current.Text}` continues an `.if`, and belongs after its `}}`"),
            SyntaxKind.UnsupportedLine => Unsupported(),
            _ => ErrorLine($"unknown directive `{Current.Text}`"),
        };
    }

    private GreenSyntax ParseDataDirective()
    {
        if (Current.Text.Equals(".tag", StringComparison.OrdinalIgnoreCase))
            return ParseTag();

        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (!AtEnd)
            ParseCommaSeparated(children, ParseExpression);
        return new GreenSyntax(SyntaxKind.DataDirective, children.ToImmutable());
    }

    /// <summary>
    /// <c>.tag T</c>, <c>.tag T, n</c> and an initialized instance, whose values are either
    /// braced on the line or, when the line ends at the <c>{</c>, the block it opens.
    /// </summary>
    private GreenSyntax ParseTag()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        children.Add(ParseExpression());
        if (Kind == SyntaxKind.Comma)
        {
            children.Add(Advance());
            children.Add(ParseExpression());
        }
        else if (Kind == SyntaxKind.OpenBrace)
        {
            children.Add(Next == SyntaxKind.EndOfLine ? Advance() : ParseTagValues());
        }
        return new GreenSyntax(SyntaxKind.DataDirective, children.ToImmutable());
    }

    /// <summary>The braced <c>member = value</c> list of an initialized instance.</summary>
    private GreenNode ParseTagValues()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind != SyntaxKind.CloseBrace && !AtEnd)
            ParseCommaSeparated(children, ParseTagValue);
        if (Kind == SyntaxKind.CloseBrace)
            children.Add(Advance());
        else
            Report("expected `}`");
        return new GreenSyntax(SyntaxKind.TagValues, children.ToImmutable());
    }

    /// <summary>
    /// One <c>member = value</c>. A member that is itself a <c>.tag</c> takes a nested
    /// braced list, which is always written on one line.
    /// </summary>
    private GreenNode? ParseTagValue()
    {
        if (!AtName)
        {
            Report("expected a member name");
            return null;
        }
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind == SyntaxKind.Equals)
            children.Add(Advance());
        else
            Report("expected `=`");
        children.Add(Kind == SyntaxKind.OpenBrace ? ParseTagValues() : ParseExpression());
        return new GreenSyntax(SyntaxKind.TagValue, children.ToImmutable());
    }

    /// <summary>One line of a multi-line initializer, which holds one <c>member = value</c>.</summary>
    private GreenNode ParseTagValueLine() =>
        ParseTagValue() is GreenSyntax value
            ? Finish(value)
            : ErrorLine("expected `member = value`");

    /// <summary>
    /// The opener of an <c>.enum</c>, <c>.struct</c>, <c>.union</c>, <c>.charmap</c> or
    /// <c>.list</c>. <paramref name="named"/> says whether the name is required: an
    /// anonymous enum or struct declares into the scope around it, and a charmap or
    /// a list is only ever used by name.
    /// </summary>
    private GreenSyntax ParseTypeBlock(SyntaxKind kind, bool named)
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (AtName)
            children.Add(Advance());
        else if (named)
            Report("expected a name");
        if (Kind == SyntaxKind.OpenBrace)
            children.Add(Advance());
        else
            Report("expected `{`");
        return new GreenSyntax(kind, children.ToImmutable());
    }

    /// <summary>One member of an <c>.enum</c>: a name, or a name and the value it is given.</summary>
    private GreenNode ParseEnumMember()
    {
        if (!AtName)
            return ErrorLine("expected a member name, or `name = expr`");
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind == SyntaxKind.Equals)
        {
            children.Add(Advance());
            children.Add(ParseExpression());
        }
        return Finish(SyntaxKind.EnumMember, children.ToImmutable());
    }

    /// <summary>One entry of a <c>.charmap</c>: a character, or a range of them, and a value.</summary>
    private GreenNode ParseCharmapEntry()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(ParseExpression());
        if (Kind == SyntaxKind.DotDot)
        {
            children.Add(Advance());
            children.Add(ParseExpression());
        }
        if (Kind == SyntaxKind.Equals)
            children.Add(Advance());
        else
            Report("expected `=`");
        children.Add(ParseExpression());
        return Finish(SyntaxKind.CharmapEntry, children.ToImmutable());
    }

    /// <summary>One line of a <c>.list</c>, which holds one or more comma-separated items.</summary>
    private ImmutableArray<GreenNode> ParseListItems()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        ParseCommaSeparated(children, ParseExpression);
        return children.ToImmutable();
    }

    /// <summary><c>.func name(a, b) = expr</c>: a pure expression function.</summary>
    private GreenSyntax ParseFunc()
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
    /// <c>.if expr {</c>, or the <c>.elseif</c> that continues one. The condition tests the
    /// build configuration, so it is an ordinary expression here and what it may name is
    /// settled once the configuration is known.
    /// </summary>
    private GreenSyntax ParseIf(SyntaxKind kind, params ReadOnlySpan<GreenNode> leading)
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.AddRange(leading);
        children.Add(Advance());
        children.Add(ParseExpression());
        ExpectOpenBrace(children);
        return new GreenSyntax(kind, children.ToImmutable());
    }

    /// <summary><c>} .else {</c>, which takes no condition.</summary>
    private GreenSyntax ParseElse(GreenNode brace)
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(brace);
        children.Add(Advance());
        ExpectOpenBrace(children);
        return new GreenSyntax(SyntaxKind.ElseDirective, children.ToImmutable());
    }

    /// <summary>
    /// <c>.repeat count, name {</c> or <c>.each what, name {</c>. The name is bound to the
    /// index or the item, and a body that does not use it may leave the name out.
    /// </summary>
    private GreenSyntax ParseRepetition(SyntaxKind kind)
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
                Report("expected the name to bind");
        }
        ExpectOpenBrace(children);
        return new GreenSyntax(kind, children.ToImmutable());
    }

    /// <summary><c>.assert expr, level, "message"</c>, whose message may be left out.</summary>
    private GreenSyntax ParseAssert()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        children.Add(ParseExpression());
        if (Kind != SyntaxKind.Comma)
        {
            Report("expected `,` and the level to report at");
            return new GreenSyntax(SyntaxKind.AssertDirective, children.ToImmutable());
        }
        children.Add(Advance());
        if (AtName)
            children.Add(Advance());
        else
            Report("expected `warning`, `error`, `ldwarning` or `lderror`");
        if (Kind == SyntaxKind.Comma)
        {
            children.Add(Advance());
            if (Kind == SyntaxKind.StringLiteral)
                children.Add(Advance());
            else
                Report("expected the message, in quotes");
        }
        return new GreenSyntax(SyntaxKind.AssertDirective, children.ToImmutable());
    }

    /// <summary><c>.error "message"</c>: a configuration this file refuses to be built in.</summary>
    private GreenSyntax ParseError()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind == SyntaxKind.StringLiteral)
            children.Add(Advance());
        else
            Report("expected the message, in quotes");
        return new GreenSyntax(SyntaxKind.ErrorDirective, children.ToImmutable());
    }

    private void ExpectOpenBrace(ImmutableArray<GreenNode>.Builder children)
    {
        if (Kind == SyntaxKind.OpenBrace)
            children.Add(Advance());
        else
            Report("expected `{`");
    }

    private GreenSyntax ParseCpuDirective()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind is SyntaxKind.CpuName or SyntaxKind.NumberLiteral && SyntaxFacts.IsCpuName(Current.Text))
            children.Add(Advance());
        else
            Report("expected `6502`, `65c02` or `65816`");
        return new GreenSyntax(SyntaxKind.CpuDirective, children.ToImmutable());
    }

    /// <summary>
    /// A segment declaration, <c>.segment "NAME": size</c> with its attributes, or the line
    /// opening a named segment block. The brace decides which.
    /// </summary>
    private GreenSyntax ParseSegment()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind == SyntaxKind.StringLiteral)
        {
            children.Add(Advance());
        }
        else
        {
            Report("expected a segment name in quotes");
            return new GreenSyntax(opensBlock ? SyntaxKind.SegmentBlock : SyntaxKind.SegmentDeclaration,
                children.ToImmutable());
        }

        if (opensBlock)
        {
            if (Kind == SyntaxKind.OpenBrace)
                children.Add(Advance());
            return new GreenSyntax(SyntaxKind.SegmentBlock, children.ToImmutable());
        }

        if (Kind != SyntaxKind.Colon)
        {
            Report("expected `:` and an address size, or `{`");
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

    /// <summary><c>dp = expr</c> or <c>bank = expr</c>.</summary>
    private GreenNode ParseSegmentAttribute()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        if (!AtWord("dp") && !AtWord("bank"))
        {
            Report("expected `dp` or `bank`");
            return new GreenSyntax(SyntaxKind.SegmentAttribute, children.ToImmutable());
        }
        children.Add(Advance());
        if (Kind != SyntaxKind.Equals)
        {
            Report("expected `=`");
            return new GreenSyntax(SyntaxKind.SegmentAttribute, children.ToImmutable());
        }
        children.Add(Advance());
        children.Add(ParseExpression());
        return new GreenSyntax(SyntaxKind.SegmentAttribute, children.ToImmutable());
    }

    private GreenSyntax ParseSegmentShortcut()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind == SyntaxKind.OpenBrace)
            children.Add(Advance());
        else
            Report($"expected `{{` after `{tokens[index - 1].Text}`");
        return new GreenSyntax(SyntaxKind.SegmentBlock, children.ToImmutable());
    }

    private GreenSyntax ParseProc()
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

    private GreenSyntax ParseScope()
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

    private GreenSyntax ParseExport()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        ParseCommaSeparated(children, () =>
        {
            if (AtName)
                return Advance();
            Report("expected a name to export");
            return null;
        });
        return new GreenSyntax(SyntaxKind.ExportDirective, children.ToImmutable());
    }

    private GreenSyntax ParseImport()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        ParseCommaSeparated(children, ParseImportItem);
        return new GreenSyntax(SyntaxKind.ImportDirective, children.ToImmutable());
    }

    /// <summary><c>name</c>, <c>name: size</c>, <c>name: proc(...)</c> or a checked <c>name = expr</c>.</summary>
    private GreenNode? ParseImportItem()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        if (!AtName)
        {
            Report("expected a name to import");
            return null;
        }
        children.Add(Advance());

        if (Kind == SyntaxKind.Equals)
        {
            children.Add(Advance());
            children.Add(ParseExpression());
        }
        else if (Kind == SyntaxKind.Colon)
        {
            children.Add(Advance());
            if (AtWord("proc"))
                children.Add(ParseImportSignature());
            else if (Kind == SyntaxKind.Identifier && SyntaxFacts.IsAddressSize(Current.Text))
                children.Add(Advance());
            else
                Report("expected `zp`, `abs`, `far` or `proc(...)`");
        }
        return new GreenSyntax(SyntaxKind.ImportItem, children.ToImmutable());
    }

    private GreenNode ParseImportSignature()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        if (Kind != SyntaxKind.OpenParen)
        {
            Report("expected `(`");
            return new GreenSyntax(SyntaxKind.ImportSignature, children.ToImmutable());
        }
        children.Add(Advance());

        // Both halves are optional: on the 6502 and 65C02 a routine's signature may be empty.
        if (Kind is not (SyntaxKind.CloseParen or SyntaxKind.Arrow))
            children.Add(ParseStateList());
        if (Kind == SyntaxKind.Arrow)
        {
            children.Add(Advance());
            if (Kind != SyntaxKind.CloseParen)
                children.Add(ParseStateList());
        }
        if (Kind == SyntaxKind.CloseParen)
            children.Add(Advance());
        else
            Report("expected `)`");
        return new GreenSyntax(SyntaxKind.ImportSignature, children.ToImmutable());
    }

    /// <summary>The <c>: entry -&gt; exit</c> of a proc. Kept from Stage 2; used from Stage 11.</summary>
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
        else if (name.Text.Equals("inline", StringComparison.OrdinalIgnoreCase))
        {
            // `inline n` or `inline .asciiz`: how much data follows each call.
            if (Kind == SyntaxKind.Directive
                && Current.Text.Equals(".asciiz", StringComparison.OrdinalIgnoreCase))
                children.Add(Advance());
            else
                children.Add(ParseExpression());
        }
        return new GreenSyntax(SyntaxKind.StateItem, children.ToImmutable());
    }

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

    private GreenSyntax ParseInstruction()
    {
        var mnemonic = Advance();
        return AtEnd
            ? new GreenSyntax(SyntaxKind.InstructionStatement, [mnemonic])
            : new GreenSyntax(SyntaxKind.InstructionStatement, [mnemonic, ParseOperand()]);
    }

    /// <summary>The operand forms. Which ones each CPU and mnemonic allow is Stage 5's.</summary>
    private GreenNode ParseOperand()
    {
        if (Kind == SyntaxKind.Hash)
            return ParseImmediate();

        // `asl a` is the accumulator; `a:` is an address-size prefix on what follows.
        if (Kind == SyntaxKind.Register && Next != SyntaxKind.Colon
            && Current.Text.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            return new GreenSyntax(SyntaxKind.AccumulatorOperand, [Advance()]);
        }

        if (Kind == SyntaxKind.OpenParen && TryParseIndirect() is { } indirect)
            return indirect;
        if (Kind == SyntaxKind.OpenBracket)
            return ParseLongIndirect();
        return ParseAddressOperand();
    }

    private GreenNode ParseImmediate()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        children.Add(ParseExpression());

        // `mvn #src, #dst` and `mvp` take two bank bytes, written as immediates.
        if (Kind == SyntaxKind.Comma && Next == SyntaxKind.Hash)
        {
            children.Add(Advance());
            children.Add(Advance());
            children.Add(ParseExpression());
        }
        return new GreenSyntax(SyntaxKind.ImmediateOperand, children.ToImmutable());
    }

    /// <summary>
    /// <c>(expr)</c>, <c>(expr),y</c>, <c>(expr,x)</c> and <c>(expr,s),y</c>, or null when the
    /// parentheses turn out to be an ordinary expression: <c>lda (a + b) * 2</c> is not
    /// indirect. Only a whole operand in parentheses is, which is how ca65 reads it too.
    /// </summary>
    private GreenNode? TryParseIndirect()
    {
        var start = index;
        var errorCount = errors.Count;
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        children.Add(ParseExpression());

        var kind = SyntaxKind.IndirectOperand;
        if (Kind == SyntaxKind.Comma && (IsRegister(1, "x") || IsRegister(1, "s")))
        {
            kind = SyntaxKind.IndexedIndirectOperand;
            children.Add(Advance());
            children.Add(Advance());
        }
        if (Kind == SyntaxKind.CloseParen)
        {
            children.Add(Advance());
            if (Kind == SyntaxKind.Comma && IsRegister(1, "y"))
            {
                children.Add(Advance());
                children.Add(Advance());
            }
            if (AtEnd)
                return new GreenSyntax(kind, children.ToImmutable());
        }

        index = start;
        errors.RemoveRange(errorCount, errors.Count - errorCount);
        return null;
    }

    private GreenNode ParseLongIndirect()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        children.Add(ParseExpression());
        if (Kind == SyntaxKind.CloseBracket)
            children.Add(Advance());
        else
            Report("expected `]`");
        if (Kind == SyntaxKind.Comma && IsRegister(1, "y"))
        {
            children.Add(Advance());
            children.Add(Advance());
        }
        return new GreenSyntax(SyntaxKind.LongIndirectOperand, children.ToImmutable());
    }

    private GreenNode ParseAddressOperand()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        if (TryAddressPrefix() is { } prefix)
            children.Add(prefix);
        children.Add(ParseExpression());
        if (Kind == SyntaxKind.Comma)
        {
            children.Add(Advance());

            // `,x`, `,y` and `,s` index; a second expression is what `bbr`/`bbs` take.
            children.Add(Kind == SyntaxKind.Register ? Advance() : ParseExpression());
        }
        return new GreenSyntax(SyntaxKind.AbsoluteOperand, children.ToImmutable());
    }

    /// <summary>
    /// <c>z:</c>, <c>a:</c>, <c>f:</c> or <c>d:</c>. The lexer emits a name and a <c>:</c>
    /// and the parser decides by position; here, in operand position, nothing else can
    /// be written, so a name followed by <c>:</c> is a prefix.
    /// </summary>
    private GreenNode? TryAddressPrefix()
    {
        if (Next != SyntaxKind.Colon || Kind is not (SyntaxKind.Identifier or SyntaxKind.Register)
            || !SyntaxFacts.IsAddressPrefix(Current.Text))
        {
            return null;
        }
        return new GreenSyntax(SyntaxKind.AddressPrefix, [Advance(), Advance()]);
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
            left = new GreenSyntax(SyntaxKind.BinaryExpression, [left, op, right]);
        }
        return left;
    }

    private GreenNode ParseUnary()
    {
        if (!SyntaxFacts.IsUnaryOperator(Kind))
            return ParsePrimary();
        var op = Advance();
        return new GreenSyntax(SyntaxKind.UnaryExpression, [op, ParseUnary()]);
    }

    private GreenNode ParsePrimary()
    {
        switch (Kind)
        {
            case SyntaxKind.NumberLiteral:
                return new GreenSyntax(SyntaxKind.NumberExpression, [Advance()]);
            case SyntaxKind.CharacterLiteral:
                return new GreenSyntax(SyntaxKind.CharacterExpression, [Advance()]);
            case SyntaxKind.StringLiteral:
                return new GreenSyntax(SyntaxKind.StringExpression, [Advance()]);
            case SyntaxKind.CpuName:
                return new GreenSyntax(SyntaxKind.CpuNameExpression, [Advance()]);
            case SyntaxKind.Star:
                return new GreenSyntax(SyntaxKind.CurrentAddressExpression, [Advance()]);
            case SyntaxKind.OpenParen:
                return ParseParenthesized();
            case SyntaxKind.Directive:
                return ParseBuiltinCall();
            case SyntaxKind.Identifier or SyntaxKind.CheapLocal or SyntaxKind.ColonColon
                or SyntaxKind.Register or SyntaxKind.Mnemonic:
                var name = ParseName();
                return Kind == SyntaxKind.OpenParen
                    ? new GreenSyntax(SyntaxKind.CallExpression, [name, ParseArgumentList()])
                    : name;
            default:
                Report("expected an expression");
                return new GreenSyntax(SyntaxKind.ErrorExpression, []);
        }
    }

    private GreenNode ParseParenthesized()
    {
        var children = ImmutableArray.CreateBuilder<GreenNode>();
        children.Add(Advance());
        children.Add(ParseExpression());
        if (Kind == SyntaxKind.CloseParen)
            children.Add(Advance());
        else
            Report("expected `)`");
        return new GreenSyntax(SyntaxKind.ParenthesizedExpression, children.ToImmutable());
    }

    private GreenNode ParseBuiltinCall()
    {
        if (!SyntaxFacts.IsBuiltinFunction(Current.Text))
        {
            Report($"`{Current.Text}` is not a function");
            return new GreenSyntax(SyntaxKind.ErrorExpression, [Advance()]);
        }
        var name = Advance();
        if (Kind == SyntaxKind.OpenParen)
            return new GreenSyntax(SyntaxKind.CallExpression, [name, ParseArgumentList()]);
        Report($"expected `(` after `{name.Text}`");
        return new GreenSyntax(SyntaxKind.ErrorExpression, [name]);
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

    /// <summary><c>::</c>-separated, as in <c>gfx::init</c>, <c>::top_level</c> and <c>Point::x</c>.</summary>
    private GreenNode ParseName()
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
        }
        return new GreenSyntax(SyntaxKind.NameExpression, children.ToImmutable());
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
                Report(operatorIndex, $"`{op.Text}` and `{inner.Text}` need parentheses to show which applies first");
                return;
            }
        }

        if (RightmostByteOperator(left) is { } byteOperator)
        {
            Report(operatorIndex,
                $"unary `{byteOperator.Text}` before `{op.Text}` needs parentheses to show what `{byteOperator.Text}` applies to");
        }
    }

    private static GreenToken? OperatorOf(GreenNode node) =>
        node is GreenSyntax { Kind: SyntaxKind.BinaryExpression } binary ? (GreenToken)binary.Children[1] : null;

    /// <summary>
    /// The <c>&lt;</c>, <c>&gt;</c> or <c>^</c> at the right edge of an operand, if any.
    /// <c>&lt;label + 1</c> is <c>(&lt;label) + 1</c>, and in <c>1 + &lt;label + 2</c> the
    /// unary sits at the end of the left operand rather than at its head, so follow the
    /// right spine down.
    /// </summary>
    private static GreenToken? RightmostByteOperator(GreenNode node)
    {
        while (node is GreenSyntax { Kind: SyntaxKind.BinaryExpression } binary)
            node = binary.Children[2];
        return node is GreenSyntax { Kind: SyntaxKind.UnaryExpression } unary
            && unary.Children[0] is GreenToken op && SyntaxFacts.IsByteOperator(op.Kind)
            ? op
            : null;
    }

    /// <summary>A parser error: the index of the token it is reported on, and the message.</summary>
    public readonly record struct Error(int Token, string Message);

    /// <summary>
    /// One line's statement, the errors found in it, and the block kind it was parsed in, so
    /// a line whose surroundings have not changed can keep the node it already has.
    /// </summary>
    public sealed record Result(BlockKind Context, GreenNode Node, ImmutableArray<Error> Errors);
}
