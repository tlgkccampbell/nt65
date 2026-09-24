using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Checks what a data directive must satisfy for ca65 to accept it, and computes how many bytes
/// it comes to. How much room a directive takes is a question about what the program means, so
/// the semantic model answers it once. What is left here is checking for what the assembler
/// will refuse. That includes a value too wide for the directive holding it, text that is not
/// bytes, a reservation whose count nt65 cannot work out, and an element count that does not
/// match the number of values given.
/// </summary>
public static class DataLengths
{
    /// <summary>
    /// The length given for a line whose length depends on where it lands rather than on what it
    /// contains. An <c>.align</c> generates as many bytes as it takes to reach the next boundary,
    /// so nt65 emits it and makes no claim about its length.
    /// </summary>
    public const int Unpredictable = -1;

    /// <summary>The most bytes ca65 reserves in one <c>.res</c> directive.</summary>
    internal const int MaxReservation = 0xffff;

    /// <summary>
    /// Returns the length of <paramref name="directive"/>, a data directive or a line of a data
    /// body. Returns <see cref="Unpredictable"/> for a directive whose length only the assembler
    /// determines, or null for a directive nt65 cannot emit at all. An array whose values are in
    /// the body it opens takes no bytes on its own line, because the lines of the body take them.
    /// Problems with its values go to <paramref name="diagnostics"/>, which callers that have
    /// already reported pass as null.
    /// </summary>
    public static int? Of(
        StatementSyntax directive, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on = null)
    {
        if (diagnostics is not null)
        {
            foreach (var operand in ElementsOf(directive))
                model.Check(operand, diagnostics, on);
        }
        Check(directive, model, diagnostics, on);
        if (directive is DataDirectiveSyntax data)
        {
            if (data.Directive.DirectiveKind == DirectiveKind.Align)
                return Unpredictable;
            if (DataSyntax.BodyOf(data) is { BlockKind: BlockKind.DataBody })
                return 0;
        }
        return model.RoomFor(directive, on) is { } room && room.Bytes is >= 0 and <= int.MaxValue
            ? (int)room.Bytes
            : null;
    }

    /// <summary>Returns the bytes an operand becomes, as a literal or as text that a charmap maps.</summary>
    public static IReadOnlyList<long>? Bytes(SyntaxNode argument, SemanticModel model, Expansion? on = null) =>
        model.BytesOf(argument, on);

    /// <summary>
    /// Returns the values a directive or a line of a body gives, one element each. These are its
    /// operands, the values of a braced list, or a line's values. A record is one element.
    /// </summary>
    public static SeparatedSyntaxList<SyntaxNode> ElementsOf(StatementSyntax directive) => directive switch
    {
        DataValuesSyntax values => values.Values,
        DataDirectiveSyntax { Tail: InlineDataSyntax inline } => inline.Values,
        DataDirectiveSyntax { Tail: BracedDataSyntax { Value: ValueListSyntax list } } => list.Values,
        _ => default,
    };

    /// <summary>
    /// Returns the range of values one element of a data directive holds, or null for a directive
    /// that is not an element type. A number slot takes a signed or an unsigned value of its
    /// width, and is emitted as the two's complement. An address slot takes only an address,
    /// which is not negative.
    /// </summary>
    public static (long Low, long High)? Holds(DirectiveKind directive) => directive switch
    {
        DirectiveKind.Byte => (-0x80, 0xff),
        DirectiveKind.Word or DirectiveKind.BeWord => (-0x8000, 0xffff),
        DirectiveKind.Long or DirectiveKind.BeLong => (-0x800000, 0xffffff),
        DirectiveKind.Dword or DirectiveKind.BeDword => (-0x80000000L, 0xffffffffL),
        DirectiveKind.Addr => (0, 0xffff),
        DirectiveKind.FarAddr => (0, 0xffffff),
        _ => null,
    };

    /// <summary>Reports what the assembler would refuse about a directive's values.</summary>
    private static void Check(
        StatementSyntax directive, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        var element = directive is DataValuesSyntax values ? DataSyntax.DirectiveOfValues(values) : directive as DataDirectiveSyntax;
        if (element is null)
            return;
        var kind = element.Directive.DirectiveKind;
        var name = DataSyntax.NameOf(element);
        var operands = ElementsOf(directive);

        // A declaration's storage is given by its element type, not by `.res`, and padding
        // such as `.align` is not something a name can declare.
        if (directive.Parent is DataDeclarationSyntax && kind is DirectiveKind.Res or DirectiveKind.Align)
        {
            Report(directive, model, diagnostics, on,
                kind == DirectiveKind.Res ? Catalogue.ResNotADeclaration.Message() : Catalogue.AlignNotADeclaration.Message(),

                // The room a `.res` reserves is the count of the `.byte[n]` that replaces it.
                // An `.align` only positions a declaration and cannot become one, so it gets
                // no fix.
                kind == DirectiveKind.Res && on is null && directive.Tree == model.Tree
                    ? new DiagnosticFix(FixKind.Storage)
                    : null);
            return;
        }
        if (directive is DataDirectiveSyntax counted && DataSyntax.IsElementType(counted))
            CheckCount(counted, model, diagnostics, on);

        if (element.IsRecord)
        {
            if (element.Type is { } named && model.SymbolOf(named) is { IsLayout: true } type)
                Records(type, directive, operands, model, diagnostics, on);
            return;
        }
        foreach (var operand in operands)
        {
            if (operand is RecordValuesSyntax or ValueListSyntax)
                Report(operand, model, diagnostics, on, Catalogue.ElementNotAValue.Message(name));
        }

        switch (kind)
        {
            case DirectiveKind.Strz:
                Terminated(directive, operands, model, diagnostics, on);
                break;
            case DirectiveKind.Byte:
                Values(operands, model, diagnostics, Holds(kind), on);
                foreach (var operand in operands)
                {
                    if (TooWide(operand, 1, "`.byte` holds 8 bits", model, on) is { } message)
                        Report(operand, model, diagnostics, on, message);
                }
                break;

            // A far address in a 16-bit slot is not the address meant. In an `.addr`, ca65
            // would silently keep only its low 16 bits.
            case DirectiveKind.Word:
            case DirectiveKind.BeWord:
            case DirectiveKind.Addr:
                Values(operands, model, diagnostics, Holds(kind), on);
                NoFarAddresses(name, operands, model, diagnostics, on);
                break;
            case DirectiveKind.Long:
            case DirectiveKind.BeLong:
            case DirectiveKind.FarAddr:
            case DirectiveKind.Dword:
            case DirectiveKind.BeDword:
                Values(operands, model, diagnostics, Holds(kind), on);
                break;

            // The byte directives take an address and keep one byte of it, so no range limit
            // applies to their values.
            case DirectiveKind.LoBytes:
            case DirectiveKind.HiBytes:
            case DirectiveKind.BankBytes:
                Values(operands, model, diagnostics, null, on);
                break;

            case DirectiveKind.Res:
                Reserved(operands, model, diagnostics, on);
                break;

            case DirectiveKind.Align:
                Alignment(operands, model, diagnostics, on);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Checks a <c>.strz</c>, which emits one text and the zero that ends it. It therefore takes
    /// exactly one text, and a zero inside the text would end it early. Any code that reads the
    /// text would stop there, including a routine declared <c>inline .strz</c>.
    /// </summary>
    private static void Terminated(
        StatementSyntax directive, SeparatedSyntaxList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics,
        Expansion? on)
    {
        if (operands.Count != 1 || TextOf(operands[0], model, on) is not { } text)
        {
            Report(operands.Count > 0 ? operands[^1] : directive, model, diagnostics, on,
                Catalogue.StrzNotText);
            return;
        }
        var operand = operands[0];
        Values(operands, model, diagnostics, null, on);
        var at = Bytes(operand, model, on)?.ToList().IndexOf(0) ?? -1;
        if (at < 0)
            return;
        Report(operand, model, diagnostics, on, Catalogue.StrzZeroInText.Message(
            operand is CallExpressionSyntax { Callee: { } callee } && at < text.Length
                && model.SymbolOf(callee, on) is { Kind: SymbolKind.Charmap }
                ? $"`{callee.GetText().Trim()}` maps `{text[at]}` to $00, "
                    + "which would end the text early"
                : "the text holds a zero, which would end it early"));
    }

    /// <summary>
    /// Returns the text an operand is, which is a string or a string constant, or the text a
    /// charmap is applied to. Returns null for anything else, including a character literal,
    /// which is a number.
    /// </summary>
    private static string? TextOf(SyntaxNode operand, SemanticModel model, Expansion? on)
    {
        if (operand is CallExpressionSyntax { Callee: { } callee } call
            && model.SymbolOf(callee, on) is { Kind: SymbolKind.Charmap })
        {
            return call.Arguments.Arguments is [var given] ? TextOf(given, model, on) : null;
        }
        return model.ValueOf(operand, on) is { Kind: ValueKind.String, Text: { } text } ? text : null;
    }

    /// <summary>
    /// Checks an array's element count. The count must be a constant, and when values are given
    /// it must match the number of values. A short table is exactly the mistake a count is there to
    /// catch, so values are never padded out to it, except for a single text, which is padded
    /// with zeros to the declared count.
    /// </summary>
    private static void CheckCount(DataDirectiveSyntax directive, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (directive.Count is not { } count)
            return;
        var (declared, given) = model.ElementsOf(directive, on);
        if (count.Count is not { } countExpression)
        {
            if (given is null && directive.Tail is not BracedDataSyntax && DataSyntax.BodyOf(directive) is null)
            {
                Report(count, model, diagnostics, on,
                    Catalogue.ElementCountEmpty.Message(directive.Directive.Text));
            }
            return;
        }
        if (declared is null)
            Report(countExpression, model, diagnostics, on, Catalogue.ElementCountNotConstant);
        else if (declared < 0)
            Report(countExpression, model, diagnostics, on, Catalogue.ElementCountNegative.Message(declared));
        else if (given is { } values && values != declared && PaddedText.Padding(directive, model, on) is null)
            Report(count, model, diagnostics, on, Catalogue.ElementCountMismatch.Message(
                declared, Elements(declared.Value), values));
    }

    private static string Elements(long count) => count == 1 ? "element" : "elements";

    /// <summary>
    /// Checks the records given for an element type <c>.type T</c>. Each value must be one braced
    /// record, or a single record spread over several lines as the block the directive's line
    /// opens.
    /// </summary>
    private static void Records(
        Symbol type, StatementSyntax directive, SeparatedSyntaxList<SyntaxNode> operands, SemanticModel model,
        List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (directive is DataDirectiveSyntax data)
        {
            if (data.Tail is BracedDataSyntax { Value: RecordValuesSyntax one })
            {
                Initialized(type, one.Members, model, diagnostics, on);
                return;
            }
            if (DataSyntax.BodyOf(data) is { BlockKind: BlockKind.RecordInitializer } body)
            {
                Initialized(type, body.Members.Skip(1).OfType<LineSyntax>().Select(line => line.Statement)
                    .OfType<MemberValueSyntax>(), model, diagnostics, on);
                return;
            }
        }
        foreach (var operand in operands)
        {
            if (operand is RecordValuesSyntax record)
                Initialized(type, record.Members, model, diagnostics, on);
            else
                Report(operand, model, diagnostics, on, Catalogue.ElementNotARecord.Message(
                    $"a `{type.Name}` array", "a record"));
        }
    }

    private static void Values(
        SeparatedSyntaxList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics,
        (long Low, long High)? limit, Expansion? on)
    {
        foreach (var operand in operands)
        {
            CheckAscii(operand, model, diagnostics, on);
            if (Bytes(operand, model, on) is { } bytes)
            {
                foreach (var value in bytes)
                {
                    if (value is < 0 or > 255)
                        Report(operand, model, diagnostics, on, Catalogue.CharmapValueNotAByte.Message(Value.Of(value)));
                }
                continue;
            }
            if (limit is { } range)
                CheckRange(operand, model, diagnostics, range, on);
        }
    }

    /// <summary>
    /// Checks the values a record gives its members, each of which must fit the room the member
    /// has. A one-element member takes one value, and an array member takes a braced list of as
    /// many values as it holds. A record member takes a braced record, and a member reserved with
    /// <c>.res</c> takes text no longer than its room. Anything else would be emitted past the
    /// member, or cut short, without a diagnostic.
    /// </summary>
    private static void Initialized(
        Symbol type, IEnumerable<MemberValueSyntax> values, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var given = value.Value;
            var name = value.Name.Text;
            if (type.Body?.FindMember(name) is not { Kind: SymbolKind.Member } member)
            {
                Report(value, model, diagnostics, on, Catalogue.MemberUnknown.Message(type.Name, name));
                continue;
            }

            if (!named.Add(name))
            {
                Report(value, model, diagnostics, on, Catalogue.MemberGivenTwice.Message(name));
                continue;
            }

            // Every member of a union is at offset 0, so a second value would write over the first.
            if (type.Kind == SymbolKind.Union && named.Count > 1)
            {
                Report(value, model, diagnostics, on,
                    Catalogue.UnionManyMembersGiven.Message(type.Name));
                continue;
            }

            var element = member.Data as DataDirectiveSyntax;
            if (element?.Count is not null)
            {
                ArrayMember(member, element, given, model, diagnostics, on);
                continue;
            }
            if (member.Type is { IsLayout: true } inner)
            {
                if (given is RecordValuesSyntax record)
                    Initialized(inner, record.Members, model, diagnostics, on);
                else
                    Report(given, model, diagnostics, on, Catalogue.MemberNeedsARecord.Message(name, inner.Name));
                continue;
            }
            if (given is RecordValuesSyntax or ValueListSyntax)
            {
                Report(given, model, diagnostics, on, Catalogue.MemberTakesOneValue.Message(name));
                continue;
            }
            Scalar(member, element?.Directive.DirectiveKind ?? DirectiveKind.Res, given, name, model, diagnostics, on);
        }
    }

    /// <summary>
    /// Checks an array member's value, which must be a braced list of exactly as many elements as
    /// the member holds.
    /// </summary>
    private static void ArrayMember(
        Symbol member, DataDirectiveSyntax element, SyntaxNode given, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        var spelled = element.Directive.Text;
        if (given is not ValueListSyntax list)
        {
            Report(given, model, diagnostics, on, Catalogue.MemberNeedsAList.Message(member.Name, member.Name));
            return;
        }
        var items = list.Values;
        if (member.Count is { } count && items.Count != count)
            Report(given, model, diagnostics, on, Catalogue.MemberCountMismatch.Message(
                member.Name, count, Elements(count), items.Count));
        foreach (var item in items)
        {
            if (member.Type is { IsLayout: true } inner)
            {
                if (item is RecordValuesSyntax record)
                    Initialized(inner, record.Members, model, diagnostics, on);
                else
                    Report(item, model, diagnostics, on, Catalogue.ElementNotARecord.Message(
                        $"`{member.Name}`", $"a `{inner.Name}`"));
                continue;
            }
            if (item is RecordValuesSyntax or ValueListSyntax)
            {
                Report(item, model, diagnostics, on, Catalogue.ElementIsOneValue.Message(member.Name, spelled));
                continue;
            }
            Scalar(member, element.Directive.DirectiveKind, item, member.Name, model, diagnostics, on);
        }
    }

    /// <summary>Checks one value for a one-element member, or for one element of an array member.</summary>
    private static void Scalar(
        Symbol member, DirectiveKind element, SyntaxNode given, string name, SemanticModel model, List<Diagnostic>? diagnostics,
        Expansion? on)
    {
        var bytes = Bytes(given, model, on);
        if (element == DirectiveKind.Res)
        {
            if (bytes is not null && bytes.Count > member.Size)
                Report(given, model, diagnostics, on, Catalogue.MemberTextTooLong.Message(
                    name, member.Size, bytes.Count));
            return;
        }
        if (bytes is { Count: > 1 })
        {
            Report(given, model, diagnostics, on,
                Catalogue.MemberNotText.Message(name, SyntaxFacts.TextOf(element), bytes.Count));
            return;
        }
        if (bytes is null && Holds(element) is { } range)
        {
            CheckRange(given, model, diagnostics, range, on, $"`{name}`, a `{SyntaxFacts.TextOf(element)}`");
        }
    }

    /// <summary>
    /// Returns why an operand that is an address, or an address plus or minus a constant, does
    /// not fit a slot of <paramref name="bytes"/> bytes, or null when it fits or is not such an
    /// address. ca65 refuses the fragment with a range error, so nt65 reports it first, with the
    /// fix.
    /// </summary>
    public static DiagnosticMessage? TooWide(
        SyntaxNode operand, int bytes, string slot, SemanticModel model, Expansion? on)
    {
        if (model.ValueOf(operand, on).AsNumber() is not null || AddressIn(operand, model, on) is not { } address
            || model.AddressSizeOf(address, null, on) is not { } size || (int)size <= bytes)
        {
            return null;
        }
        var text = address.GetText().Trim();
        var fix = bytes == 1 ? $"`<{text}` is its low byte" : $"`.loword({text})` is its low 16 bits";
        return Catalogue.AddressDoesNotFit.Message(
            text, size == AddressSize.Far ? "a far" : "an absolute", slot, fix);
    }

    /// <summary>
    /// Returns the name an operand consists of, or the name it adds a constant to or subtracts a
    /// constant from, or null when it is neither.
    /// </summary>
    private static NameExpressionSyntax? AddressIn(SyntaxNode operand, SemanticModel model, Expansion? on)
    {
        var address = operand;
        if (operand is BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Plus or SyntaxKind.Minus } binary)
        {
            address = model.ValueOf(binary.Right, on).AsNumber() is not null ? binary.Left
                : model.ValueOf(binary.Left, on).AsNumber() is not null && binary.OperatorToken.Kind == SyntaxKind.Plus ? binary.Right
                : operand;
        }
        return address as NameExpressionSyntax;
    }

    /// <summary>
    /// Reports a far address in a 16-bit slot. ca65 keeps the low 16 bits of a far address in an
    /// <c>.addr</c> and refuses it in a <c>.word</c>, so in either case the source does not say
    /// what is meant. Only an address, or an address plus or minus a constant, is checked.
    /// Anything else, such as <c>.loword(far)</c> or the difference of two addresses, already
    /// states explicitly which bits it keeps.
    /// </summary>
    private static void NoFarAddresses(
        string directive, SeparatedSyntaxList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics,
        Expansion? on)
    {
        foreach (var operand in operands)
        {
            if (AddressIn(operand, model, on) is { } address && model.AddressSizeOf(address, null, on) == AddressSize.Far)
            {
                var text = address.GetText().Trim();
                Report(operand, model, diagnostics, on,
                    Catalogue.FarAddressInWord.Message(text, directive, text));
            }
        }
    }

    /// <summary>
    /// Checks a <c>.res n</c> or <c>.res n, fill</c>, verifying that the count is a constant in
    /// range and that the fill is a byte.
    /// </summary>
    private static void Reserved(
        SeparatedSyntaxList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (operands.Count == 0)
            return;
        if (operands.Count > 1)
            CheckRange(operands[1], model, diagnostics, Holds(DirectiveKind.Byte)!.Value, on);

        // `.res` is padding, passed straight through to ca65's own `.res`, which reserves at
        // most $ffff bytes, so padding of more than a bank's worth is not padding. A declaration
        // is not limited that way, because one bigger than this is emitted as several directives.
        var count = model.ValueOf(operands[0], on).AsNumber();
        if (count is null)
            Report(operands[0], model, diagnostics, on, Catalogue.ResCountNotConstant);
        else if (count is < 0 or > MaxReservation)
            Report(operands[0], model, diagnostics, on, Catalogue.ResCountOutOfRange.Message(count));
    }

    /// <summary>
    /// Checks an <c>.align</c>. The alignment must be a constant power of two, which is what ca65
    /// will take, and the fill it pads with must be a byte, as a <c>.res</c> fill is.
    /// </summary>
    private static void Alignment(
        SeparatedSyntaxList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (operands.Count == 0)
            return;
        if (operands.Count > 1)
            CheckRange(operands[1], model, diagnostics, Holds(DirectiveKind.Byte)!.Value, on);
        var boundary = model.ValueOf(operands[0], on).AsNumber();
        if (boundary is null)
            Report(operands[0], model, diagnostics, on, Catalogue.AlignBoundaryNotConstant);
        else if (boundary is < 1 or > 0x10000 || (boundary & (boundary - 1)) != 0)
            Report(operands[0], model, diagnostics, on, Catalogue.AlignBoundaryNotPowerOfTwo.Message(boundary));
    }

    /// <summary>
    /// Checks that text typed directly is ASCII. Outside a charmap, text is ASCII and
    /// <c>\xHH</c> emits any byte, so a character typed directly above <c>$7f</c> is an error
    /// rather than a byte of some encoding.
    /// </summary>
    private static void CheckAscii(SyntaxNode argument, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (argument is LiteralExpressionSyntax literal and (StringExpressionSyntax or CharacterExpressionSyntax)
            && literal.Token.Text.Any(c => c > 127))
        {
            Report(argument, model, diagnostics, on,
                Catalogue.TextNotAscii);
        }
    }

    private static void CheckRange(
        SyntaxNode argument, SemanticModel model, List<Diagnostic>? diagnostics, (long Low, long High) limit,
        Expansion? on, string slot = "this directive")
    {
        if (model.ValueOf(argument, on).AsNumber() is not { } value)
            return;
        if (value < 0 && limit.Low == 0)
            Report(argument, model, diagnostics, on, Catalogue.AddressNegative.Message(value));
        else if (value < limit.Low || value > limit.High)
            Report(argument, model, diagnostics, on, Catalogue.ValueTooWide.Message(Value.Of(value), slot));
    }

    private static void Report(
        SyntaxNode node, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on,
        DiagnosticMessage message, DiagnosticFix? fix = null) =>
        diagnostics?.Add(Expansion.Problem(model.Tree, node.Tree, node.Span, on, null, message) with { Fix = fix });
}
