using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

// Parses the declarations a line can make, and the names they give: routines, scopes, segments,
// types, functions, settings, and the directives that move names between modules.
internal sealed partial class Parser
{
    private GreenNode ParseLabeledLine()
    {
        var label = new LabelSyntax(Advance(), Advance());
        if (AtEnd)
            return Finish(new LabeledLineSyntax(label, null));

        // A label may be followed by an instruction, a data directive or a macro call, and a
        // macro may be named after an instruction.
        if (Lines.IsMacroCall(tokens, index))
            return Finish(new LabeledLineSyntax(label, ParseMacroCall()));
        if (Kind == SyntaxKind.Mnemonic)
            return Finish(new LabeledLineSyntax(label, ParseInstruction()));
        if (Kind != SyntaxKind.Directive)
        {
            Report(Catalogue.ExpectedStatement.Message(
                "an instruction, a data directive or a macro call after a label"));
            return Finish(new LabeledLineSyntax(label, null));
        }

        switch (SyntaxFacts.LineDirectiveKind(Current.DirectiveKind))
        {
            case SyntaxKind.DataDirective:
                return Finish(new LabeledLineSyntax(label, ParseDataDirective()));
            case SyntaxKind.None:
                Report(Catalogue.DirectiveUnknown.Message(Current.Text));
                return Finish(new LabeledLineSyntax(label, null));
            default:
                Report(Catalogue.DirectiveAfterLabel.Message(Current.Text));
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
    /// Parses the opener of an <c>.enum</c>, <c>.struct</c>, <c>.union</c>, <c>.charmap</c> or
    /// <c>.list</c>. <paramref name="named"/> says whether the name is required. An anonymous enum
    /// or struct declares into the scope around it, and a charmap or a list is only ever used by
    /// name.
    /// </summary>
    private GreenNode ParseTypeBlock(SyntaxKind kind, bool named)
    {
        var keyword = Advance();
        GreenToken? name = null;
        if (AtName)
            name = Advance();
        else if (named)
            Report(Catalogue.ExpectedName.Message("a name"));

        // A missing name and a missing brace are separate problems, so a line missing both gets
        // a diagnostic for each.
        var brace = Kind == SyntaxKind.OpenBrace ? Advance() : Missing(SyntaxKind.OpenBrace, Catalogue.ExpectedBrace.Message(
            "`{`"));
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

    /// <summary>Parses <c>.func name(a, b) = expr</c>, a pure expression function.</summary>
    private GreenNode ParseFunc()
    {
        var keyword = Advance();
        var name = ExpectName(Catalogue.ExpectedName.Message("a function name"));
        ParameterListSyntax? parameters = null;
        if (Kind == SyntaxKind.OpenParen)
            parameters = ParseParameterList();
        else
            Report(Catalogue.ExpectedParenthesis.Message("`(` and the parameter names"));

        // The `=` and the body are separate pieces, so a line that has neither gets a diagnostic
        // for each.
        var equals = Kind == SyntaxKind.Equals
            ? Advance()
            : Missing(SyntaxKind.Equals, Catalogue.ExpectedEquals.Message("`=` and the body"));
        return new FuncDeclarationSyntax(keyword, name, parameters, equals, ParseExpression());
    }

    /// <summary>
    /// Parses <c>.signature std = a8, i16, dp = 0</c>, which names a set of items that signatures
    /// can use.
    /// </summary>
    private GreenNode ParseSignatureDeclaration()
    {
        var keyword = Advance();
        var name = ExpectName(Catalogue.ExpectedName.Message("a name for the signature set"));
        var equals = Expect(SyntaxKind.Equals, Catalogue.ExpectedEquals.Message(
            "`=` and the items: `.signature std = a8, i16`"));

        // The items come after the `=`, so a line without one has no items to read.
        return new SignatureDeclarationSyntax(
            keyword, name, equals, equals.IsMissing ? null : ParseStateList());
    }

    /// <summary>Parses <c>.config NAME = value</c>, a setting whose value the build may override.</summary>
    private GreenNode ParseConfig()
    {
        var keyword = Advance();
        if (Kind != SyntaxKind.Identifier)
        {
            return Incomplete(
                Missing(SyntaxKind.Identifier, Catalogue.ExpectedName.Message(
                    "the setting's name: `.config NAME = value`")),
                GreenToken.Missing(SyntaxKind.Equals));
        }
        var name = Advance();
        if (Kind != SyntaxKind.Equals)
        {
            return Incomplete(
                name, Missing(SyntaxKind.Equals, Catalogue.ExpectedEquals.Message(
                    "`=` and the setting's value: `.config NAME = value`")));
        }
        return new ConfigDeclarationSyntax(keyword, name, Advance(), ParseExpression());

        // A line that stops short still gets slots for the `=` and the value, filled with
        // missing pieces. The diagnostic on the piece where it stopped is enough for one line.
        GreenNode Incomplete(GreenToken setting, GreenToken equals) =>
            new ConfigDeclarationSyntax(keyword, setting, equals, new ErrorExpressionSyntax(null));
    }

    private ParameterListSyntax ParseParameterList()
    {
        var openParen = Advance();
        var parameters = Kind != SyntaxKind.CloseParen && !AtEnd ? ParseSeparatedList(ParseParameter) : null;
        var closeParen = Kind == SyntaxKind.CloseParen
            ? Advance()
            : Missing(SyntaxKind.CloseParen, Catalogue.ExpectedParenthesis.Message("`)`"));
        return new ParameterListSyntax(openParen, parameters, closeParen);
    }

    /// <summary>Parses one parameter of a <c>.func</c>, which is only its name.</summary>
    private GreenNode? ParseParameter()
    {
        if (AtName)
            return new ParameterSyntax(Advance());
        Report(Catalogue.ExpectedName.Message("a parameter name"));
        return null;
    }

    private GreenNode ParseCpuDirective()
    {
        var keyword = Advance();
        if (Kind is SyntaxKind.CpuName or SyntaxKind.NumberLiteral or SyntaxKind.Identifier && SyntaxFacts.IsCpuName(Current.Text))
            return new CpuDirectiveSyntax(keyword, Advance());
        return new CpuDirectiveSyntax(keyword, Missing(SyntaxKind.CpuName, Catalogue.ExpectedCpu.Message(
            SyntaxFacts.ListedCpuNames)));
    }

    /// <summary>
    /// Parses a segment declaration, <c>.segment NAME: size</c> with its attributes; the line
    /// opening a segment block, <c>.segment NAME {</c>; or a region line, <c>.segment NAME</c>.
    /// The brace and the size decide which. A segment name is an identifier, because segments are
    /// a table of their own, and share no namespace with symbols.
    /// </summary>
    private GreenNode ParseSegment()
    {
        var keyword = Advance();
        var form = Lines.SegmentForm(tokens, opensBlock);
        GreenToken name;
        if (AtName)
        {
            name = Advance();
        }
        else if (Kind == SyntaxKind.StringLiteral)
        {
            Report(Catalogue.SegmentNameQuoted.Message(Current.Text.Trim('"')));
            name = Advance();
        }
        else
        {
            // A line that does not name its segment is parsed no further, so the missing name is
            // the only thing reported about it.
            name = Missing(SyntaxKind.Identifier, Catalogue.ExpectedName.Message("a segment name"));
            return form switch
            {
                SyntaxKind.SegmentBlock => new SegmentBlockSyntax(keyword, name, GreenToken.Missing(SyntaxKind.OpenBrace)),
                SyntaxKind.SegmentDeclaration => new SegmentDeclarationSyntax(
                    keyword, name, GreenToken.Missing(SyntaxKind.Colon), GreenToken.Missing(SyntaxKind.Identifier), null, null),
                _ => new SegmentRegionSyntax(keyword, name, null),
            };
        }

        // A `{` after the name opens a block only when it ends the line. Otherwise the line opens
        // nothing, and the brace is kept on the region line as a misplaced token.
        if (form == SyntaxKind.SegmentBlock)
            return new SegmentBlockSyntax(keyword, name, Expect(SyntaxKind.OpenBrace));
        if (form == SyntaxKind.SegmentRegion)
            return new SegmentRegionSyntax(keyword, name, Kind == SyntaxKind.OpenBrace ? Advance() : null);

        if (Kind != SyntaxKind.Colon)
        {
            return new SegmentDeclarationSyntax(
                keyword, name, Missing(SyntaxKind.Colon, Catalogue.ExpectedColon.Message("`:` and an address size")),
                GreenToken.Missing(SyntaxKind.Identifier), null, null);
        }
        var colon = Advance();
        if (Kind != SyntaxKind.Identifier || !SyntaxFacts.IsAddressSize(Current.Text))
        {
            return new SegmentDeclarationSyntax(
                keyword, name, colon, Missing(SyntaxKind.Identifier, Catalogue.ExpectedAddressSize.Message(
                    "`zp`, `abs` or `far`")), null, null);
        }
        var size = Advance();

        // The attributes are a list of their own, so the `,` between the size and the first of
        // them belongs to the declaration rather than to the list.
        GreenToken? comma = null;
        GreenSeparatedList? attributes = null;
        if (Kind == SyntaxKind.Comma)
        {
            comma = Advance();
            attributes = ParseSeparatedList(ParseSegmentAttribute);
        }
        return new SegmentDeclarationSyntax(keyword, name, colon, size, comma, attributes);
    }

    /// <summary>
    /// Parses a segment attribute, which is <c>dp = expr</c>, <c>bank = expr</c>,
    /// <c>space = expr</c> or <c>mirrors = [$00..$3f, $80..$bf]</c>.
    /// </summary>
    private GreenNode ParseSegmentAttribute()
    {
        if (!AtWord("dp") && !AtWord("bank") && !AtWord("mirrors") && !AtWord("space"))
        {
            return new SegmentAttributeSyntax(
                Missing(SyntaxKind.Identifier, Catalogue.ExpectedSegmentAttribute.Message("`dp`, `bank`, `mirrors` or `space`")),
                GreenToken.Missing(SyntaxKind.Equals), null, null, null, null);
        }
        var mirrors = AtWord("mirrors");
        var name = Advance();
        if (Kind != SyntaxKind.Equals)
        {
            return new SegmentAttributeSyntax(
                name, Missing(SyntaxKind.Equals, Catalogue.ExpectedEquals.Message("`=`")), null, null, null, null);
        }
        var equals = Advance();
        if (!mirrors)
            return new SegmentAttributeSyntax(name, equals, ParseExpression(), null, null, null);

        if (Kind != SyntaxKind.OpenBracket)
        {
            Report(Catalogue.ExpectedBracket.Message("`[` and the banks: `mirrors = [$00..$3f, $80..$bf]`"));
            return new SegmentAttributeSyntax(name, equals, null, null, null, null);
        }
        var openBracket = Advance();
        var ranges = Kind != SyntaxKind.CloseBracket ? ParseSeparatedList(ParseBankRange) : null;
        GreenToken? closeBracket = null;
        if (Kind == SyntaxKind.CloseBracket)
            closeBracket = Advance();
        else
            Report(Catalogue.ExpectedBracket.Message("`]`"));
        return new SegmentAttributeSyntax(name, equals, null, openBracket, ranges, closeBracket);
    }

    /// <summary>
    /// Parses one bank, such as <c>$80</c>, or a range of banks, such as <c>$00..$3f</c>.
    /// </summary>
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
        var name = ExpectName(Catalogue.ExpectedName.Message("a routine name"));

        // An address and a signature both come after the name, so a routine with no name is
        // parsed no further. A `{` after it still opens the routine's block.
        if (name.IsMissing)
            return new ProcDeclarationSyntax(keyword, name, null, ExpectOpenBrace());

        // `.proc name = expr` is an extern proc, with an address, an optional signature and no body.
        if (Kind == SyntaxKind.Equals)
        {
            var equals = Advance();
            var address = ParseExpression();
            return new ExternProcDeclarationSyntax(
                keyword, name, equals, address, Kind == SyntaxKind.Colon ? ParseSignature() : null);
        }

        var signature = Kind == SyntaxKind.Colon ? ParseSignature() : null;
        return new ProcDeclarationSyntax(keyword, name, signature,
            Expect(SyntaxKind.OpenBrace, Catalogue.ExpectedBrace.Message(
                "`{`, or `= address` for a routine with no body")));
    }

    /// <summary>
    /// Parses <c>.multiproc E, b: signature {</c>, which declares one routine per member of the
    /// enum <c>E</c>, named after the member. It combines a repetition and a routine on one line,
    /// so it is parsed as a repetition's opener followed by a routine's signature. The routines
    /// are named from the bound name, so unlike a repetition's bound name it is required.
    /// </summary>
    private GreenNode ParseMultiProc()
    {
        var keyword = Advance();
        var walked = ParseExpression();
        var comma = Expect(SyntaxKind.Comma, Catalogue.ExpectedComma.Message(
            "`,` and the name to bind: `.multiproc Channel, ch {`"));

        // The name comes after the `,`, so when the comma is missing the name cannot be present
        // either, and only the missing comma is reported.
        var name = comma.IsMissing
            ? GreenToken.Missing(SyntaxKind.Identifier)
            : ExpectName(Catalogue.ExpectedName.Message("the name to bind, which each routine is named from"));
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
    /// Parses <c>.export</c> before a declaration, which exports what the declaration declares,
    /// or <c>.export</c> with a list of names, such as
    /// <c>.export a, outer::inner, K: abs, init as "_init"</c>. An exported declaration parses
    /// exactly as it would without the <c>.export</c>. The line holds the <c>.export</c> token, not
    /// the declaration.
    /// </summary>
    private GreenNode ParseExport()
    {
        var export = Advance();
        if (Lines.IsExportedDeclaration(tokens, index)
            && ParseDirective(SyntaxFacts.LineDirectiveKind(Current.DirectiveKind)) is { } declaration)
        {
            exportKeyword = export;
            return declaration;
        }
        if (Kind == SyntaxKind.Directive)
        {
            Report(Catalogue.ExportDeclaresNothing.Message(Current.Text));
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
    /// Parses <c>name</c> or <c>outer::inner</c>, followed by <c>: size</c> or
    /// <c>as "linker_name"</c>.
    /// </summary>
    private GreenNode? ParseExportItem()
    {
        if (!AtName)
        {
            Report(Catalogue.ExpectedName.Message("a name to export"));
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
                Report(Catalogue.ExpectedAddressSize.Message("`zp`, `abs` or `far`"));
        }

        GreenToken? asKeyword = null;
        GreenToken? linkerName = null;
        if (AtWord("as"))
        {
            asKeyword = Advance();
            if (Kind == SyntaxKind.StringLiteral)
                linkerName = Advance();
            else
                Report(Catalogue.ExpectedText.Message("the linker name, in quotes: `as \"_name\"`"));
        }
        return new ExportItemSyntax(name, colon, addressSize, asKeyword, linkerName);
    }

    /// <summary>
    /// Parses <c>.module name</c> or <c>.module outer::inner</c>, with <c>: placed</c> or
    /// <c>: placeable</c> after it where another module may place this one.
    /// </summary>
    private GreenNode ParseModule()
    {
        var keyword = Advance();

        // The quotes are the mistake, not the name, so the string is not read as a name. It is
        // left for the line to hold as skipped tokens.
        if (Kind == SyntaxKind.StringLiteral)
        {
            Report(Catalogue.ModuleNameQuoted);
            return new ModuleDirectiveSyntax(keyword, MissingName(null), null, null);
        }
        var name = ParsePath(Catalogue.ExpectedName.Message("the module's name: `.module name`"));
        if (Kind != SyntaxKind.Colon)
            return new ModuleDirectiveSyntax(keyword, name, null, null);
        var colon = Advance();
        if (AtWord("placed") || AtWord("placeable"))
            return new ModuleDirectiveSyntax(keyword, name, colon, Advance());
        return new ModuleDirectiveSyntax(keyword, name, colon, Missing(SyntaxKind.Identifier,
            Catalogue.ExpectedPlacement.Message("`placed` or `placeable`: `.module name: placed`")));
    }

    /// <summary>
    /// Parses <c>.place name</c> or <c>.place outer::inner</c>, which names the module whose bytes
    /// go here.
    /// </summary>
    private GreenNode ParsePlace()
    {
        var keyword = Advance();
        if (Kind == SyntaxKind.StringLiteral)
        {
            Report(Catalogue.ModuleNameQuoted);
            return new PlaceDirectiveSyntax(keyword, MissingName(null));
        }
        return new PlaceDirectiveSyntax(keyword, ParsePath(Catalogue.ExpectedName.Message("the module to place: `.place name`")));
    }

    /// <summary>
    /// Parses <c>.use a::b</c>, <c>.use a::{b, c as d}</c>, <c>.use a::*</c> or
    /// <c>.use a::b as c</c>. A path always starts from the root of the module hierarchy, and
    /// names a module, or a module and a name in it.
    /// </summary>
    private GreenNode ParseUse()
    {
        var keyword = Advance();

        // With no path, nothing that follows can refer to part of one, so the rest of the line
        // is left for the line to hold as skipped tokens rather than parsed by the directive.
        var named = AtName;
        var path = ParsePath(Catalogue.ExpectedName.Message("what to use: `.use module::name`"));
        if (!named)
            return new UseDirectiveSyntax(keyword, path, null, null, null, null, null, null, null);

        if (Kind == SyntaxKind.ColonColon && Next is SyntaxKind.Star or SyntaxKind.OpenBrace)
        {
            var colonColon = Advance();
            if (Kind == SyntaxKind.Star)
                return new UseDirectiveSyntax(keyword, path, colonColon, Advance(), null, null, null, null, null);

            var openBrace = Advance();
            var items = ParseSeparatedList(ParseUseItem);

            // The `{` is present, so the closing `}` gets a slot whether or not the source contains
            // it. If the source does not, a missing token fills the slot.
            var closeBrace = Expect(SyntaxKind.CloseBrace, Catalogue.ExpectedBrace.Message("`}`"));
            return new UseDirectiveSyntax(
                keyword, path, colonColon, null, openBrace, items, closeBrace, null, null);
        }
        var (asKeyword, alias) = ParseUseAlias();
        return new UseDirectiveSyntax(keyword, path, null, null, null, null, null, asKeyword, alias);
    }

    /// <summary>
    /// Parses one name in the braces of a <c>.use</c>, and the name it is brought in as.
    /// </summary>
    private GreenNode? ParseUseItem()
    {
        if (!AtName)
        {
            ReportOnce(Catalogue.ExpectedName.Message("a name"));
            return null;
        }
        var name = Advance();
        var (asKeyword, alias) = ParseUseAlias();
        return new UseItemSyntax(name, asKeyword, alias);
    }

    /// <summary>Parses the <c>as</c> and the name after it, if they are present.</summary>
    private (GreenToken? AsKeyword, GreenToken? Alias) ParseUseAlias()
    {
        if (!AtWord("as"))
            return (null, null);
        var keyword = Advance();
        if (AtName)
            return (keyword, Advance());
        ReportOnce(Catalogue.ExpectedName.Message("the name to bring it in as: `as name`"));
        return (keyword, null);
    }

    /// <summary>
    /// Parses <c>a::b::c</c> as a name, stopping before a <c>::</c> that is not followed by a
    /// name. The <c>::</c> of a <c>::*</c> or a <c>::{</c> is left for the directive to take, and
    /// any other is left for the line as skipped tokens. When even the first name is absent, this
    /// returns a missing name with <paramref name="expected"/> reported on it.
    /// </summary>
    private NameExpressionSyntax ParsePath(DiagnosticMessage expected)
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

    /// <summary>
    /// Parses an import item, which is <c>name</c>, <c>name: size</c>, <c>name: proc(...)</c>,
    /// <c>name: .word[8]</c> or a checked <c>name = expr</c>. An element type may follow a size,
    /// so that an import of zero-page data can say both what the data is and where it lives.
    /// </summary>
    private GreenNode? ParseImportItem()
    {
        if (!AtName)
        {
            Report(Catalogue.ExpectedName.Message("a name to import"));
            return null;
        }
        var name = Advance();
        GreenToken? equals = null;
        ExpressionSyntax? value = null;
        GreenToken? colon = null;
        GreenToken? addressSize = null;
        ImportSignatureSyntax? signature = null;
        DataDirectiveSyntax? element = null;

        if (Kind == SyntaxKind.Equals)
        {
            equals = Advance();
            value = ParseExpression();
        }
        else if (Kind == SyntaxKind.Colon)
        {
            colon = Advance();
            if (AtWord("proc"))
            {
                signature = ParseImportSignature();
            }
            else
            {
                if (Kind == SyntaxKind.Identifier && SyntaxFacts.IsAddressSize(Current.Text))
                    addressSize = Advance();
                if (SyntaxFacts.LineDirectiveKind(Current.DirectiveKind) == SyntaxKind.DataDirective)
                    element = ParseImportElement();
                else if (addressSize is null)
                    Report(Catalogue.ExpectedAddressSize.Message("`zp`, `abs`, `far`, `proc(...)` or what the data is"));
            }
        }
        // `in SEGMENT` says which segment an imported address is in; references to the name are
        // checked against that segment.
        GreenToken? inKeyword = null;
        GreenToken? segment = null;
        if (colon is not null && AtWord("in"))
        {
            inKeyword = Advance();
            segment = ExpectName(Catalogue.ExpectedName.Message("the segment the name is in"));
        }
        return new ImportItemSyntax(name, equals, value, colon, addressSize, signature, element, inKeyword, segment);
    }

    /// <summary>
    /// Parses the element type of a typed import, which has the form a <c>.data</c> declaration
    /// would use, with a count and without values. The bytes are in another object, so there is
    /// nothing here to give values to and nothing to read from a file.
    /// </summary>
    private DataDirectiveSyntax ParseImportElement()
    {
        var at = index;
        var directive = Advance();
        var element = SyntaxFacts.IsElementType(directive.DirectiveKind);
        if (!element)
            Report(at, Catalogue.ImportNeedsAnElementType.Message(directive.Text));
        var type = ParseRecordType(directive);
        var count = Kind == SyntaxKind.OpenBracket ? ParseElementCount() : null;

        // One cause gets one diagnostic. A directive that is not an element type has been
        // reported already, and anything after it is part of the same mistake.
        if (element && !AtEnd && Kind != SyntaxKind.Comma)
            Report(Catalogue.ImportHoldsNoValues.Message(directive.Text));
        return new DataDirectiveSyntax(directive, type, count, null);
    }

    private ImportSignatureSyntax ParseImportSignature()
    {
        var keyword = Advance();
        if (Kind != SyntaxKind.OpenParen)
        {
            return new ImportSignatureSyntax(
                keyword, Missing(SyntaxKind.OpenParen, Catalogue.ExpectedParenthesis.Message("`(`")), null, null, null,
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
            : Missing(SyntaxKind.CloseParen, Catalogue.ExpectedParenthesis.Message("`)`"));
        return new ImportSignatureSyntax(keyword, openParen, entry, arrow, exit, closeParen);
    }
}
