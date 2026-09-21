namespace Norristown.Syntax.InternalSyntax;

// The data directives and what they are written with: element types and their counts,
// declarations, bodies, braced values, and the members of an enum or a charmap.
internal sealed partial class Parser
{
    /// <summary>
    /// A data directive. An element type — <c>.byte</c>, <c>.word</c>, <c>.addr</c>,
    /// <c>.faraddr</c>, <c>.dword</c> or <c>.type T</c> — may take a count, <c>[16]</c> or
    /// <c>[]</c>, and then values: after it on the line, in braces on the line, or in the body
    /// the line opens. Any other directive takes its operands as ca65's does.
    /// </summary>
    private DataDirectiveSyntax ParseDataDirective()
    {
        var directive = Advance();
        var record = directive.Text.Equals(".type", StringComparison.OrdinalIgnoreCase);
        if (!record && SyntaxFacts.ElementSize(directive.Text) is null)
            return new DataDirectiveSyntax(directive, null, null, AtEnd ? null : ParseInlineData());

        NameExpressionSyntax? type = null;
        if (record)
        {
            if (AtName || Kind == SyntaxKind.ColonColon)
                type = ParseName();
            else
                Report("expected the type: `.type T`");
        }
        var count = Kind == SyntaxKind.OpenBracket ? ParseElementCount() : null;

        DataTailSyntax? tail = null;
        if (Kind == SyntaxKind.OpenBrace)
        {
            // A body holds an array's values, or one record's `member = value` lines.
            if (Next == SyntaxKind.EndOfLine)
            {
                if (count is null && !record)
                    ReportOnce($"values in a body need a count: `{directive.Text}[] {{` counts them");
                tail = new DataBodySyntax(Advance());
            }
            else
            {
                tail = new BracedDataSyntax(ParseBracedValue());
            }
        }
        else if (!AtEnd)
        {
            if (count is not null || record)
            {
                ReportOnce(count is not null
                    ? $"the values of an array go in braces: `{directive.Text}[n] {{ 1, 2 }}`"
                    : "a record's values go in braces: `.type T { member = value }`");
            }
            tail = ParseInlineData();
        }
        return new DataDirectiveSyntax(directive, type, count, tail);
    }

    /// <summary>
    /// The values written after the directive on its own line, as the tail they are; null where
    /// there are none to read, which leaves the directive no tail at all.
    /// </summary>
    private DataTailSyntax? ParseInlineData() =>
        ParseSeparatedList(ParseExpression) is { } values ? new InlineDataSyntax(values) : null;

    /// <summary><c>[n]</c>, or <c>[]</c> for as many elements as the values given.</summary>
    private ElementCountSyntax ParseElementCount()
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
        DataDirectiveSyntax? element = null;
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
        return Finish(new DataValuesSyntax(ParseSeparatedList(ParseDataValue)));
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
        nesting++;
        var value = TooDeeplyNested() ?? ParseBraced();
        nesting--;
        return value;
    }

    /// <summary>A braced value, once there is room on the stack to read one.</summary>
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
        var closeBrace = Expect(SyntaxKind.CloseBrace, "expected `}`");
        return record
            ? new RecordValuesSyntax(openBrace, items, closeBrace)
            : new ValueListSyntax(openBrace, items, closeBrace);
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
        ExpressionSyntax? last = null;
        if (Kind == SyntaxKind.DotDot)
        {
            dotDot = Advance();
            last = ParseExpression();
        }
        var equals = Kind == SyntaxKind.Equals ? Advance() : Missing(SyntaxKind.Equals, "expected `=`");
        return Finish(new CharmapEntrySyntax(first, dotDot, last, equals, ParseExpression()));
    }
}
