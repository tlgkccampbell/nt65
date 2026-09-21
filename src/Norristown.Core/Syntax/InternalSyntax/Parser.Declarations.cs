using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

// What a line declares, and the name it gives it: routines, scopes, segments, types,
// functions, settings, and the directives that move names between modules.
internal sealed partial class Parser
{
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

    /// <summary><c>.func name(a, b) = expr</c>: a pure expression function.</summary>
    private GreenNode ParseFunc()
    {
        var keyword = Advance();
        var name = ExpectName("expected a function name");
        ParameterListSyntax? parameters = null;
        if (Kind == SyntaxKind.OpenParen)
            parameters = ParseParameterList();
        else
            Report("expected `(` and the parameter names");

        // The `=` and the body are two pieces, and a line that writes neither is missing both.
        var equals = Kind == SyntaxKind.Equals
            ? Advance()
            : Missing(SyntaxKind.Equals, "expected `=` and the body");
        return new FuncDeclarationSyntax(keyword, name, parameters, equals, ParseExpression());
    }

    /// <summary><c>.signature std = a8, i16, dp = 0</c>: a name for items a signature uses.</summary>
    private GreenNode ParseSignatureDeclaration()
    {
        var keyword = Advance();
        var name = ExpectName("expected a name for the signature set");
        var equals = Expect(SyntaxKind.Equals, "expected `=` and the items: `.signature std = a8, i16`");

        // The items are written after the `=`, so a line without one names nothing to read them as.
        return new SignatureDeclarationSyntax(
            keyword, name, equals, equals.IsMissing ? null : ParseStateList());
    }

    /// <summary><c>.config NAME = value</c>: a setting, whose value the build may give instead.</summary>
    private GreenNode ParseConfig()
    {
        var keyword = Advance();
        if (Kind != SyntaxKind.Identifier)
        {
            return Unwritten(
                Missing(SyntaxKind.Identifier, "expected the setting's name: `.config NAME = value`"),
                GreenToken.Missing(SyntaxKind.Equals));
        }
        var name = Advance();
        if (Kind != SyntaxKind.Equals)
        {
            return Unwritten(
                name, Missing(SyntaxKind.Equals, "expected `=` and the setting's value: `.config NAME = value`"));
        }
        return new ConfigDeclarationSyntax(keyword, name, Advance(), ParseExpression());

        // A line that stops short has a place for the `=` and the value all the same, and what
        // has been said about the piece it stopped at is news enough for one line.
        GreenNode Unwritten(GreenToken setting, GreenToken equals) =>
            new ConfigDeclarationSyntax(keyword, setting, equals, new ErrorExpressionSyntax(null));
    }

    private ParameterListSyntax ParseParameterList()
    {
        var openParen = Advance();
        var parameters = Kind != SyntaxKind.CloseParen && !AtEnd ? ParseSeparatedList(ParseParameter) : null;
        var closeParen = Kind == SyntaxKind.CloseParen
            ? Advance()
            : Missing(SyntaxKind.CloseParen, "expected `)`");
        return new ParameterListSyntax(openParen, parameters, closeParen);
    }

    /// <summary>One parameter of a <c>.func</c>, which is only its name.</summary>
    private GreenNode? ParseParameter()
    {
        if (AtName)
            return new ParameterSyntax(Advance());
        Report("expected a parameter name");
        return null;
    }

    private GreenNode ParseCpuDirective()
    {
        var keyword = Advance();
        if (Kind is SyntaxKind.CpuName or SyntaxKind.NumberLiteral or SyntaxKind.Identifier && SyntaxFacts.IsCpuName(Current.Text))
            return new CpuDirectiveSyntax(keyword, Advance());
        return new CpuDirectiveSyntax(keyword, Missing(SyntaxKind.CpuName, $"expected {Project.CpuNames.Listed}"));
    }

    /// <summary>
    /// A segment declaration, <c>.segment NAME: size</c> with its attributes; the line opening
    /// a segment block, <c>.segment NAME {</c>; or a region line, <c>.segment NAME</c>. The
    /// brace and the size decide which. A segment name is an identifier: segments are a table
    /// of their own, and share no namespace with symbols.
    /// </summary>
    private GreenNode ParseSegment()
    {
        var keyword = Advance();
        var declaration = !opensBlock && tokens.Any(token => token.Kind == SyntaxKind.Colon);
        GreenToken name;
        if (AtName)
        {
            name = Advance();
        }
        else if (Kind == SyntaxKind.StringLiteral)
        {
            Report($"a segment name is written without quotes: `.segment {Current.Text.Trim('"')}`");
            name = Advance();
        }
        else
        {
            // A line that does not name its segment is read no further: what is written where
            // the name belongs is the whole news about it.
            name = Missing(SyntaxKind.Identifier, "expected a segment name");
            return opensBlock
                ? new SegmentBlockSyntax(keyword, name, GreenToken.Missing(SyntaxKind.OpenBrace))
                : declaration
                    ? new SegmentDeclarationSyntax(
                        keyword, name, GreenToken.Missing(SyntaxKind.Colon),
                        GreenToken.Missing(SyntaxKind.Identifier), null, null)
                    : new SegmentRegionSyntax(keyword, name, null);
        }

        // A `{` after the name opens a block, unless something else follows it on the line: then
        // the line opens nothing and the brace is the region line's, misplaced.
        if (opensBlock)
            return new SegmentBlockSyntax(keyword, name, Expect(SyntaxKind.OpenBrace));
        if (!declaration)
            return new SegmentRegionSyntax(keyword, name, Kind == SyntaxKind.OpenBrace ? Advance() : null);

        if (Kind != SyntaxKind.Colon)
        {
            return new SegmentDeclarationSyntax(
                keyword, name, Missing(SyntaxKind.Colon, "expected `:` and an address size"),
                GreenToken.Missing(SyntaxKind.Identifier), null, null);
        }
        var colon = Advance();
        if (Kind != SyntaxKind.Identifier || !SyntaxFacts.IsAddressSize(Current.Text))
        {
            return new SegmentDeclarationSyntax(
                keyword, name, colon, Missing(SyntaxKind.Identifier, "expected `zp`, `abs` or `far`"), null, null);
        }
        var size = Advance();

        // The attributes are a list of their own, so the `,` between the size and the first of
        // them is the declaration's rather than the list's.
        GreenToken? comma = null;
        GreenSeparatedList? attributes = null;
        if (Kind == SyntaxKind.Comma)
        {
            comma = Advance();
            attributes = ParseSeparatedList(ParseSegmentAttribute);
        }
        return new SegmentDeclarationSyntax(keyword, name, colon, size, comma, attributes);
    }

    /// <summary><c>dp = expr</c>, <c>bank = expr</c> or <c>mirrors = [$00..$3f, $80..$bf]</c>.</summary>
    private GreenNode ParseSegmentAttribute()
    {
        if (!AtWord("dp") && !AtWord("bank") && !AtWord("mirrors"))
        {
            return new SegmentAttributeSyntax(
                Missing(SyntaxKind.Identifier, "expected `dp`, `bank` or `mirrors`"),
                GreenToken.Missing(SyntaxKind.Equals), null, null, null, null);
        }
        var mirrors = AtWord("mirrors");
        var name = Advance();
        if (Kind != SyntaxKind.Equals)
        {
            return new SegmentAttributeSyntax(
                name, Missing(SyntaxKind.Equals, "expected `=`"), null, null, null, null);
        }
        var equals = Advance();
        if (!mirrors)
            return new SegmentAttributeSyntax(name, equals, ParseExpression(), null, null, null);

        if (Kind != SyntaxKind.OpenBracket)
        {
            Report("expected `[` and the banks: `mirrors = [$00..$3f, $80..$bf]`");
            return new SegmentAttributeSyntax(name, equals, null, null, null, null);
        }
        var openBracket = Advance();
        var ranges = Kind != SyntaxKind.CloseBracket ? ParseSeparatedList(ParseBankRange) : null;
        GreenToken? closeBracket = null;
        if (Kind == SyntaxKind.CloseBracket)
            closeBracket = Advance();
        else
            Report("expected `]`");
        return new SegmentAttributeSyntax(name, equals, null, openBracket, ranges, closeBracket);
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
        var keyword = Advance();
        var name = ExpectName("expected a routine name");

        // An address and a signature are both written after the name, so a routine with none is
        // read no further; the `{` after it still opens the block it opens.
        if (name.IsMissing)
            return new ProcDeclarationSyntax(keyword, name, null, ExpectOpenBrace());

        // `.proc name = expr` is an extern proc: a signature and an address, with no body.
        if (Kind == SyntaxKind.Equals)
        {
            var equals = Advance();
            var address = ParseExpression();
            return new ExternProcDeclarationSyntax(
                keyword, name, equals, address, Kind == SyntaxKind.Colon ? ParseSignature() : null);
        }

        var signature = Kind == SyntaxKind.Colon ? ParseSignature() : null;
        return new ProcDeclarationSyntax(keyword, name, signature,
            Expect(SyntaxKind.OpenBrace, "expected `{`, or `= address` for a routine with no body"));
    }

    /// <summary>
    /// <c>.multiproc E, b: signature {</c>: one routine per member of the enum <c>E</c>, named
    /// after the member. It folds a repetition and a routine into one line, so it is read as a
    /// repetition's opener and then a routine's signature. The name to bind is what the
    /// routines are named from, so it is not optional as a repetition's is.
    /// </summary>
    private GreenNode ParseMultiProc()
    {
        var keyword = Advance();
        var walked = ParseExpression();
        var comma = Expect(SyntaxKind.Comma, "expected `,` and the name to bind: `.multiproc Channel, ch {`");

        // The name is written after the `,`, so where there is no comma there is nowhere for it
        // to have been written and the comma is the whole news about the line.
        var name = comma.IsMissing
            ? GreenToken.Missing(SyntaxKind.Identifier)
            : ExpectName("expected the name to bind, which each routine is named from");
        var signature = Kind == SyntaxKind.Colon ? ParseSignature() : null;
        return new MultiProcDeclarationSyntax(keyword, walked, comma, name, signature, ExpectOpenBrace());
    }

    private GreenNode ParseScope()
    {
        var keyword = Advance();

        // `.scope { }` is anonymous: it opens a scope and declares no name for it.
        var name = AtName ? Advance() : null;
        return new ScopeDeclarationSyntax(keyword, name, ExpectOpenBrace());
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
            return new ExportDirectiveSyntax(export, null);
        }
        if (AtName && Next == SyntaxKind.Equals)
        {
            exportKeyword = export;
            var name = Advance();
            var equals = Advance();
            return new ConstantDeclarationSyntax(name, equals, ParseExpression());
        }

        return new ExportDirectiveSyntax(export, ParseSeparatedList(ParseExportItem));
    }

    /// <summary>
    /// The declaration after <c>.export</c>, read exactly as the same line without the
    /// <c>.export</c> is, or null when the directive declares nothing that can be exported.
    /// </summary>
    private GreenNode? ParseExportable() =>
        SyntaxFacts.IsExportable(Current.Text)
            ? ParseDirective(SyntaxFacts.LineDirectiveKind(Current.Text))
            : null;

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
        var keyword = Advance();

        // The quotes are the mistake, not the name, so the string is left for the line to hold
        // rather than read as a name it is not.
        if (Kind == SyntaxKind.StringLiteral)
        {
            Report("a module name is written without quotes: `.module hw::vic`");
            return new ModuleDirectiveSyntax(keyword, MissingName(null));
        }
        return new ModuleDirectiveSyntax(keyword, ParsePath("expected the module's name: `.module name`"));
    }

    /// <summary>
    /// <c>.use a::b</c>, <c>.use a::{b, c as d}</c>, <c>.use a::*</c> or <c>.use a::b as c</c>. A
    /// path is always written from the root of the modules, and names at least a module and
    /// one name in it, or a module.
    /// </summary>
    private GreenNode ParseUse()
    {
        var keyword = Advance();

        // With no path there is nothing for the rest of the line to name a part of, so what
        // follows is the line's to hold rather than the directive's.
        var named = AtName;
        var path = ParsePath("expected what to use: `.use module::name`");
        if (!named)
            return new UseDirectiveSyntax(keyword, path, null, null, null, null, null, null, null);

        if (Kind == SyntaxKind.ColonColon && Next is SyntaxKind.Star or SyntaxKind.OpenBrace)
        {
            var colonColon = Advance();
            if (Kind == SyntaxKind.Star)
                return new UseDirectiveSyntax(keyword, path, colonColon, Advance(), null, null, null, null, null);

            var openBrace = Advance();
            var items = ParseSeparatedList(ParseUseItem);

            // The `{` is written, so the `}` that closes it has a place on the line whether or not
            // the source reached it, and the missing token stands there.
            var closeBrace = Expect(SyntaxKind.CloseBrace, "expected `}`");
            return new UseDirectiveSyntax(
                keyword, path, colonColon, null, openBrace, items, closeBrace, null, null);
        }
        var (asKeyword, alias) = ParseUseAlias();
        return new UseDirectiveSyntax(keyword, path, null, null, null, null, null, asKeyword, alias);
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
    /// <c>a::b::c</c> as a name, stopping before a <c>::</c> that is not followed by a name: the
    /// <c>::</c> of a <c>::*</c> or a <c>::{</c> is the directive's to take, and any other is the
    /// line's. The name that stands where one belongs when not even the first is written, with
    /// <paramref name="expected"/> reported there.
    /// </summary>
    private NameExpressionSyntax ParsePath(string expected)
    {
        if (!AtName)
            return MissingName(expected);
        var parts = ImmutableArray.CreateBuilder<GreenNode>();
        parts.Add(new IdentifierNameSyntax(Advance(), null));
        while (Kind == SyntaxKind.ColonColon && Next is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic)
        {
            parts.Add(Advance());
            parts.Add(new IdentifierNameSyntax(Advance(), null));
        }
        return new NameExpressionSyntax(null, new GreenSeparatedList(parts.ToImmutable()));
    }

    private GreenNode ParseImport()
    {
        var keyword = Advance();
        return new ImportDirectiveSyntax(keyword, ParseSeparatedList(ParseImportItem));
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
        ExpressionSyntax? value = null;
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
            return new ImportSignatureSyntax(
                keyword, Missing(SyntaxKind.OpenParen, "expected `(`"), null, null, null,
                GreenToken.Missing(SyntaxKind.CloseParen));
        }
        var openParen = Advance();

        // Both halves are optional: on the 6502 and its CMOS variants a routine's signature may be empty.
        StateListSyntax? entry = null;
        GreenToken? arrow = null;
        StateListSyntax? exit = null;
        if (Kind is not (SyntaxKind.CloseParen or SyntaxKind.Arrow))
            entry = ParseStateList();
        if (Kind == SyntaxKind.Arrow)
        {
            arrow = Advance();
            if (Kind != SyntaxKind.CloseParen)
                exit = ParseStateList();
        }

        var closeParen = Kind == SyntaxKind.CloseParen
            ? Advance()
            : Missing(SyntaxKind.CloseParen, "expected `)`");
        return new ImportSignatureSyntax(keyword, openParen, entry, arrow, exit, closeParen);
    }
}
