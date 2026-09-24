namespace Norristown.Syntax.InternalSyntax;

// The data directives and their parts: element types and their counts, declarations, bodies,
// braced values, and the members of an enum or a charmap.
internal sealed partial class Parser
{
    /// <summary>
    /// Parses a data directive. An element type, which is <c>.byte</c>, <c>.word</c>,
    /// <c>.addr</c>, <c>.faraddr</c>, <c>.dword</c> or <c>.type T</c>, may take a count,
    /// <c>[16]</c> or <c>[]</c>, and then values. The values follow it on the line, sit in braces
    /// on the line, or fill the body the line opens. Any other directive takes its operands as
    /// ca65's does.
    /// </summary>
    private DataDirectiveSyntax ParseDataDirective(bool elsewhere = false)
    {
        var directive = Advance();
        if (!SyntaxFacts.IsElementType(directive.DirectiveKind))
            return new DataDirectiveSyntax(directive, null, null, null, AtEnd ? null : ParseInlineData());

        var record = directive.DirectiveKind == DirectiveKind.Type;
        var (type, skipped) = ParseRecordType(directive);
        var count = Kind == SyntaxKind.OpenBracket ? ParseElementCount() : null;

        DataTailSyntax? tail = null;
        if (Kind == SyntaxKind.OpenBrace)
        {
            // A body holds an array's values, or one record's `member = value` lines.
            if (Next == SyntaxKind.EndOfLine)
            {
                if (count is null && !record)
                    ReportOnce(Catalogue.DataBodyNeedsACount.Message(directive.Text));
                tail = new DataBodySyntax(Advance());
            }
            else
            {
                tail = new BracedDataSyntax(ParseBracedValue());
            }
        }
        else if (!AtEnd && !(elsewhere && Kind == SyntaxKind.Equals))
        {
            if (count is not null || record)
            {
                ReportOnce(Catalogue.DataValuesNeedBraces.Message(count is not null
                    ? $"the values of an array go in braces: `{directive.Text}[n] {{ 1, 2 }}`"
                    : "a record's values go in braces: `.type T { member = value }`"));
            }
            tail = ParseInlineData();
        }
        return new DataDirectiveSyntax(directive, type, skipped, count, tail);
    }

    /// <summary>
    /// Parses the type after <c>.type</c>, or returns no type for any other element type. A
    /// <c>.type</c> with no type after it is reported, and also gives no type. Tokens where the
    /// type belongs that cannot be one, as in <c>.type @T[2]</c>, are reported once and skipped, so
    /// the count and the values after them are still read.
    /// </summary>
    private (NameExpressionSyntax? Type, SkippedTokensSyntax? Skipped) ParseRecordType(GreenToken directive)
    {
        if (directive.DirectiveKind != DirectiveKind.Type)
            return (null, null);
        if (AtName || Kind == SyntaxKind.ColonColon)
            return (ParseName(), null);
        var message = Catalogue.ExpectedDataType.Message("the type: `.type T`");
        if (AtEnd || Kind is SyntaxKind.OpenBracket or SyntaxKind.OpenBrace or SyntaxKind.Comma)
        {
            Report(message);
            return (null, null);
        }
        return (null, SkipUntil(() => Kind is SyntaxKind.OpenBracket or SyntaxKind.OpenBrace or SyntaxKind.Comma, message));
    }

    /// <summary>
    /// Parses the values after the directive on the same line as an inline tail, or returns null
    /// if there are none, in which case the directive has no tail.
    /// </summary>
    private DataTailSyntax? ParseInlineData() =>
        ParseSeparatedList(ParseExpression) is { } values ? new InlineDataSyntax(values) : null;

    /// <summary>Parses <c>[n]</c>, or <c>[]</c> for as many elements as there are values.</summary>
    private ElementCountSyntax ParseElementCount()
    {
        var bracket = Advance();
        var count = Kind != SyntaxKind.CloseBracket && !AtEnd ? ParseExpression() : null;
        return new ElementCountSyntax(bracket, count, Expect(SyntaxKind.CloseBracket, Catalogue.ExpectedBracket.Message(
            "`]`")));
    }

    /// <summary>
    /// Parses <c>.data name: element</c>, or <c>.data name {</c> for mixed data. The name is
    /// required, because <c>.data</c> always declares named data. To switch to the segment called
    /// DATA, a program uses <c>.segment DATA</c>.
    /// </summary>
    private GreenNode ParseDataDeclaration()
    {
        var keyword = Advance();
        var name = ExpectName(Kind == SyntaxKind.OpenBrace
            ? Catalogue.DataNeedsAName.Message()
            : Catalogue.ExpectedName.Message("a name: `.data name: .byte 1, 2` or `.data name { }`"));

        // Data found elsewhere gives only its address, and takes its element from the data there.
        if (Kind == SyntaxKind.Equals)
            return new DataDeclarationSyntax(keyword, name, null, null, null, Advance(), ParseExpression(), null);

        // Tokens between the name and a `:` later on the line are reported once and skipped, and
        // the line is read on from the `:`, so the element type and the body it opens still count.
        SkippedTokensSyntax? skipped = null;
        if (Kind is not (SyntaxKind.Colon or SyntaxKind.OpenBrace) && ColonAhead())
        {
            skipped = SkipUntil(
                () => Kind == SyntaxKind.Colon,
                Catalogue.ExpectedColon.Message("`:` and what the data is, or `{` for mixed data"));
        }

        GreenToken? colon = null;
        DataDirectiveSyntax? element = null;
        GreenToken? brace = null;
        if (Kind == SyntaxKind.Colon)
        {
            colon = Advance();
            if (SyntaxFacts.LineDirectiveKind(Current.DirectiveKind) == SyntaxKind.DataDirective)
            {
                element = ParseDataDirective(elsewhere: true);
                if (Kind == SyntaxKind.Equals && SyntaxFacts.IsElementType(element.Directive.DirectiveKind))
                    return new DataDeclarationSyntax(keyword, name, skipped, colon, element, Advance(), ParseExpression(), null);
            }
            else
            {
                var instead = Kind == SyntaxKind.Directive ? Replaced(Current.Text) : null;
                ReportOnce(
                    instead?.Message ?? Catalogue.ExpectedDataType.Message(
                        "what the data is: a number such as `.byte` or `.word`, an address such as `.addr`, "
                        + "`.type T`, or bytes such as `.incbin`"),
                    instead is { } replacement ? Spelling(replacement, wholeLine: false) : null);
            }
        }
        else if (Kind == SyntaxKind.OpenBrace)
        {
            brace = Advance();
        }
        else if (SyntaxFacts.LineDirectiveKind(Current.DirectiveKind) == SyntaxKind.DataDirective)
        {
            // Only the `:` is missing, as in `.data name .byte[2] {`, so the element type is read.
            colon = Expect(SyntaxKind.Colon, Catalogue.ExpectedColon.Message("`:` before what the data is"));
            element = ParseDataDirective();
        }
        else
        {
            ReportOnce(Catalogue.ExpectedColon.Message("`:` and what the data is, or `{` for mixed data"));
        }
        return new DataDeclarationSyntax(keyword, name, skipped, colon, element, null, null, brace);
    }

    /// <summary>Returns a value indicating whether a <c>:</c> appears from the current token on.</summary>
    private bool ColonAhead()
    {
        for (var at = index; at < tokens.Length; at++)
        {
            if (tokens[at].Kind == SyntaxKind.Colon)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Parses one line of a data body, which holds values separated by commas, one element each.
    /// </summary>
    private GreenNode ParseDataValuesLine()
    {
        if (Kind == SyntaxKind.Directive && !SyntaxFacts.IsBuiltinFunction(Current.Text))
        {
            return ErrorLine(Catalogue.DataBodyHoldsValues.Message(Current.Text));
        }
        return Finish(new DataValuesSyntax(ParseSeparatedList(ParseDataValue)));
    }

    /// <summary>Parses a value, which is an expression or a braced record or list.</summary>
    private GreenNode ParseDataValue() => Kind == SyntaxKind.OpenBrace ? ParseBracedValue() : ParseExpression();

    /// <summary>
    /// Parses <c>{ member = value, … }</c>, a record, or <c>{ value, … }</c>, a list of elements.
    /// No expression contains <c>=</c>, so the first item decides which one it is, and
    /// <c>{}</c> is a record that sets no member.
    /// </summary>
    private GreenNode ParseBracedValue()
    {
        nesting++;
        var value = TooDeeplyNested() ?? ParseBraced();
        nesting--;
        return value;
    }

    /// <summary>Parses a braced value, once there is room on the stack to read one.</summary>
    private GreenNode ParseBraced()
    {
        var record = Next == SyntaxKind.CloseBrace
            || (index + 2 < tokens.Length
                && tokens[index + 1].Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic
                && tokens[index + 2].Kind == SyntaxKind.Equals);
        var openBrace = Advance();
        var items = Kind != SyntaxKind.CloseBrace && !AtEnd
            ? ParseSeparatedList(record ? ParseMemberValue : ParseDataValue)
            : null;
        var closeBrace = Expect(SyntaxKind.CloseBrace, Catalogue.ExpectedBrace.Message("`}`"));
        return record
            ? new RecordValuesSyntax(openBrace, items, closeBrace)
            : new ValueListSyntax(openBrace, items, closeBrace);
    }

    /// <summary>
    /// Parses one <c>member = value</c>. A member that is a record or an array takes a braced
    /// value, which is on one line.
    /// </summary>
    private GreenNode? ParseMemberValue()
    {
        if (!AtName)
        {
            Report(Catalogue.ExpectedName.Message("a member name"));
            return null;
        }
        var name = Advance();
        var equals = Require(SyntaxKind.Equals, Catalogue.ExpectedEquals.Message("`=`"));
        return new MemberValueSyntax(name, equals, ParseDataValue());
    }

    /// <summary>
    /// Parses one line of a multi-line initializer, which holds one <c>member = value</c>.
    /// </summary>
    private GreenNode ParseMemberValueLine() =>
        ParseMemberValue() is { } value
            ? Finish(value)
            : ErrorLine(Catalogue.ExpectedMemberValue);

    /// <summary>
    /// Parses one member of an <c>.enum</c>, which is a name, or a name and the value it is given.
    /// </summary>
    private GreenNode ParseEnumMember()
    {
        if (!AtName)
            return ErrorLine(Catalogue.ExpectedName.Message("a member name, or `name = expr`"));
        var name = Advance();
        if (Kind != SyntaxKind.Equals)
            return Finish(new EnumMemberSyntax(name, null, null));
        var equals = Advance();
        return Finish(new EnumMemberSyntax(name, equals, ParseExpression()));
    }

    /// <summary>
    /// Parses one entry of a <c>.charmap</c>, which is a character or a range of characters, and a
    /// value.
    /// </summary>
    private GreenNode ParseCharmapEntry()
    {
        var first = ParseExpression();
        GreenToken? dotDot = null;
        ExpressionSyntax? last = null;
        if (Kind == SyntaxKind.DotDot)
        {
            dotDot = Advance();
            last = ParseExpression();
        }
        var equals = Require(SyntaxKind.Equals, Catalogue.ExpectedEquals.Message("`=`"));
        return Finish(new CharmapEntrySyntax(first, dotDot, last, equals, ParseExpression()));
    }
}
