using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// What a data directive must satisfy for ca65 to accept it, and how many bytes it comes
/// to. How much room a directive takes is a question about what the program means, so it is
/// answered once, by the semantic model; what is left here is what the assembler will
/// refuse: a value too wide for the directive holding it, text that is not bytes, a
/// reservation whose count nt65 cannot work out, and a count its values do not come to.
/// </summary>
public static class DataLengths
{
    /// <summary>
    /// A line whose length depends on where it lands rather than on what it says. An
    /// <c>.align</c> generates however many bytes it takes to reach the next boundary, so
    /// nt65 writes it out and makes no claim about its length.
    /// </summary>
    public const int Unpredictable = -1;

    /// <summary>
    /// The length of <paramref name="directive"/>, a data directive or a line of a data body,
    /// <see cref="Unpredictable"/> for one whose length only the assembler settles, or null for
    /// one nt65 cannot write at all. An array whose values are in the body it opens takes no
    /// bytes on its own line: the lines of the body do. Anything wrong with its values goes to
    /// <paramref name="diagnostics"/>, which callers that have already reported pass as null.
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
            if (DataSyntax.NameOf(data) == ".align")
                return Unpredictable;
            if (DataSyntax.BodyOf(data) is { BlockKind: BlockKind.DataBody })
                return 0;
        }
        return model.RoomFor(directive, on) is { } room && room.Bytes is >= 0 and <= int.MaxValue
            ? (int)room.Bytes
            : null;
    }

    /// <summary>The bytes an operand becomes: a literal, or text a charmap maps.</summary>
    public static IReadOnlyList<long>? Bytes(SyntaxNode argument, SemanticModel model, Expansion? on = null) =>
        model.BytesOf(argument, on);

    /// <summary>
    /// The values a directive or a line of a body gives, one element each: its operands, the
    /// values of a braced list, or a line's values. A record is one element.
    /// </summary>
    public static SeparatedSyntaxList<SyntaxNode> ElementsOf(StatementSyntax directive) => directive switch
    {
        DataValuesSyntax values => values.Values,
        DataDirectiveSyntax { Tail: InlineDataSyntax inline } => inline.Values,
        DataDirectiveSyntax { Tail: BracedDataSyntax { Value: ValueListSyntax list } } => list.Values,
        _ => default,
    };

    /// <summary>
    /// What one element of a data directive holds, or null for one that is not an element type.
    /// A number slot takes a signed or an unsigned value of its width, and is written as the
    /// two's complement; an address slot takes only an address, which is not negative.
    /// </summary>
    public static (long Low, long High)? Holds(string directive) => directive.ToLowerInvariant() switch
    {
        ".byte" => (-0x80, 0xff),
        ".word" or ".beword" => (-0x8000, 0xffff),
        ".long" or ".belong" => (-0x800000, 0xffffff),
        ".dword" or ".bedword" => (-0x80000000L, 0xffffffffL),
        ".addr" => (0, 0xffff),
        ".faraddr" => (0, 0xffffff),
        _ => null,
    };

    /// <summary>What the assembler would refuse about a directive's values.</summary>
    private static void Check(
        StatementSyntax directive, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        var element = directive is DataValuesSyntax values ? DataSyntax.DirectiveOfValues(values) : directive as DataDirectiveSyntax;
        if (element is null)
            return;
        var name = DataSyntax.NameOf(element);
        var operands = ElementsOf(directive);

        // Storage is declared with its type, and padding has no name to declare.
        if (directive.Parent is DataDeclarationSyntax && name is ".res" or ".align")
        {
            Report(directive, model, diagnostics, on,
                name == ".res" ? Catalogue.ResNotADeclaration.Says() : Catalogue.AlignNotADeclaration.Says(),

                // The room a `.res` reserves is the count of the `.byte[n]` that replaces it.
                // An `.align` is a place to write the declaration rather than a way to write it.
                name == ".res" && on is null && directive.Tree == model.Tree
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
                Report(operand, model, diagnostics, on, Catalogue.ElementNotAValue.Says(name));
        }

        switch (name)
        {
            case ".strz":
                Terminated(directive, operands, model, diagnostics, on);
                break;
            case ".byte":
                Values(operands, model, diagnostics, Holds(name), on);
                foreach (var operand in operands)
                {
                    if (TooWide(operand, 1, "`.byte` holds 8 bits", model, on) is { } message)
                        Report(operand, model, diagnostics, on, message);
                }
                break;

            // A far address in a 16-bit slot is not the address meant: ca65 would keep the low
            // bits of it in an `.addr` without a word.
            case ".word":
            case ".beword":
            case ".addr":
                Values(operands, model, diagnostics, Holds(name), on);
                NoFarAddresses(name, operands, model, diagnostics, on);
                break;
            case ".long":
            case ".belong":
            case ".faraddr":
            case ".dword":
            case ".bedword":
                Values(operands, model, diagnostics, Holds(name), on);
                break;

            // The byte directives take an address and keep one byte of it, so they limit nothing.
            case ".lobytes":
            case ".hibytes":
            case ".bankbytes":
                Values(operands, model, diagnostics, null, on);
                break;

            case ".res":
                Reserved(operands, model, diagnostics, on);
                break;

            case ".align":
                Alignment(operands, model, diagnostics, on);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// <c>.strz</c> writes one text and the zero that ends it, so it takes exactly one text,
    /// and a zero inside the text would end it early: whatever reads the text, a routine
    /// declared <c>inline .strz</c> among them, would stop there.
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
        Report(operand, model, diagnostics, on, Catalogue.StrzZeroInText.Says(
            operand is CallExpressionSyntax call && at < text.Length
                ? $"`{(call.Callee ?? (SyntaxNode)call.Arguments).GetText().Trim()}` maps `{text[at]}` to $00, "
                    + "which would end the text early"
                : "the text holds a zero, which would end it early"));
    }

    /// <summary>
    /// The text an operand is: a string or a string constant, or the text a charmap is applied
    /// to. Null for anything else, a character among them, which is a number.
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
    /// An array's count: a constant, and the number its values come to when it has values. A
    /// short table is exactly the mistake a count is there to catch, so values are never padded
    /// out to it — except one text, which is padded with zero to the count it declares.
    /// </summary>
    private static void CheckCount(DataDirectiveSyntax directive, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (directive.Count is not { } count)
            return;
        var (declared, given) = model.ElementsOf(directive, on);
        if (count.Count is not { } written)
        {
            if (given is null && directive.Tail is not BracedDataSyntax && DataSyntax.BodyOf(directive) is null)
            {
                Report(count, model, diagnostics, on,
                    Catalogue.ElementCountEmpty.Says(directive.Directive.Text));
            }
            return;
        }
        if (declared is null)
            Report(written, model, diagnostics, on, Catalogue.ElementCountNotConstant);
        else if (declared < 0)
            Report(written, model, diagnostics, on, Catalogue.ElementCountNegative.Says(declared));
        else if (given is { } values && values != declared && PaddedText.Padding(directive, model, on) is null)
            Report(count, model, diagnostics, on, Catalogue.ElementCountMismatch.Says(
                declared, Elements(declared.Value), values));
    }

    private static string Elements(long count) => count == 1 ? "element" : "elements";

    /// <summary>
    /// The records an element type of <c>.type T</c> gives: each value is one braced record,
    /// and one record written over several lines is the block the directive's line opens.
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
                Report(operand, model, diagnostics, on, Catalogue.ElementNotARecord.Says(
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
                        Report(operand, model, diagnostics, on, Catalogue.CharmapValueNotAByte.Says((char)value));
                }
                continue;
            }
            if (limit is { } range)
                CheckRange(operand, model, diagnostics, range, on);
        }
    }

    /// <summary>
    /// The values a record gives its members, each of which must fit the room the member has:
    /// a one-element member takes one value, an array member a braced list of as many, a
    /// record member a braced record, and a member reserved with <c>.res</c> text no longer
    /// than its room. Anything else would be written past the member, or cut short, with
    /// nothing said.
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
                Report(value, model, diagnostics, on, Catalogue.MemberUnknown.Says(type.Name, name));
                continue;
            }

            // Every member of a union is at offset 0, so a second value would write over the first.
            if (!named.Add(name))
            {
                Report(value, model, diagnostics, on, Catalogue.MemberGivenTwice.Says(name));
                continue;
            }
            if (type.Kind == SymbolKind.Union && named.Count > 1)
            {
                Report(value, model, diagnostics, on,
                    Catalogue.UnionManyMembersGiven.Says(type.Name));
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
                    Report(given, model, diagnostics, on, Catalogue.MemberNeedsARecord.Says(name, inner.Name));
                continue;
            }
            if (given is RecordValuesSyntax or ValueListSyntax)
            {
                Report(given, model, diagnostics, on, Catalogue.MemberTakesOneValue.Says(name));
                continue;
            }
            Scalar(member, element is null ? ".res" : DataSyntax.NameOf(element), given, name, model, diagnostics, on);
        }
    }

    /// <summary>An array member's value: a braced list, of exactly as many elements as the member holds.</summary>
    private static void ArrayMember(
        Symbol member, DataDirectiveSyntax element, SyntaxNode given, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        var spelled = element.Directive.Text;
        if (given is not ValueListSyntax list)
        {
            Report(given, model, diagnostics, on, Catalogue.MemberNeedsAList.Says(member.Name, member.Name));
            return;
        }
        var items = list.Values;
        if (member.Count is { } count && items.Count != count)
            Report(given, model, diagnostics, on, Catalogue.MemberCountMismatch.Says(
                member.Name, count, Elements(count), items.Count));
        foreach (var item in items)
        {
            if (member.Type is { IsLayout: true } inner)
            {
                if (item is RecordValuesSyntax record)
                    Initialized(inner, record.Members, model, diagnostics, on);
                else
                    Report(item, model, diagnostics, on, Catalogue.ElementNotARecord.Says(
                        $"`{member.Name}`", $"a `{inner.Name}`"));
                continue;
            }
            if (item is RecordValuesSyntax or ValueListSyntax)
            {
                Report(item, model, diagnostics, on, Catalogue.ElementIsOneValue.Says(member.Name, spelled));
                continue;
            }
            Scalar(member, DataSyntax.NameOf(element), item, member.Name, model, diagnostics, on);
        }
    }

    /// <summary>One value for a one-element member, or for one element of an array member.</summary>
    private static void Scalar(
        Symbol member, string element, SyntaxNode given, string name, SemanticModel model, List<Diagnostic>? diagnostics,
        Expansion? on)
    {
        var bytes = Bytes(given, model, on);
        if (element == ".res")
        {
            if (bytes is not null && bytes.Count > member.Size)
                Report(given, model, diagnostics, on, Catalogue.MemberTextTooLong.Says(
                    name, member.Size, bytes.Count));
            return;
        }
        if (bytes is { Count: > 1 })
        {
            Report(given, model, diagnostics, on,
                Catalogue.MemberNotText.Says(name, element, bytes.Count));
            return;
        }
        if (bytes is null && Holds(element) is { } range)
        {
            CheckRange(given, model, diagnostics, range, on, $"`{name}`, a `{element}`");
        }
    }

    /// <summary>
    /// Why an operand that is an address, or an address plus or minus a constant, does not fit
    /// a slot of <paramref name="bytes"/> bytes, or null when it does or is no such address.
    /// ca65 refuses the fragment with a range error, so nt65 says so first, with the fix.
    /// </summary>
    public static DiagnosticMessage? TooWide(
        SyntaxNode operand, int bytes, string slot, SemanticModel model, Expansion? on)
    {
        if (model.ValueOf(operand, on).AsNumber() is not null || AddressIn(operand, model, on) is not { } address
            || model.AddressSizeOf(address, null, on) is not { } size || (int)size <= bytes)
        {
            return null;
        }
        var written = address.GetText().Trim();
        var fix = bytes == 1 ? $"`<{written}` is its low byte" : $"`.loword({written})` is its low 16 bits";
        return Catalogue.AddressDoesNotFit.Says(
            written, size == AddressSize.Far ? "a far" : "an absolute", slot, fix);
    }

    /// <summary>The name an operand is, or is a constant away from, or null when it is neither.</summary>
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
    /// A far address in a 16-bit slot. ca65 keeps the low 16 bits of one in an <c>.addr</c>
    /// and refuses one in a <c>.word</c>, so either way what was written is not what is meant.
    /// An address, or an address plus or minus a constant, is what is looked at: anything
    /// else, such as <c>.loword(far)</c> or the difference of two addresses, says what it keeps.
    /// </summary>
    private static void NoFarAddresses(
        string directive, SeparatedSyntaxList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics,
        Expansion? on)
    {
        foreach (var operand in operands)
        {
            if (AddressIn(operand, model, on) is { } address && model.AddressSizeOf(address, null, on) == AddressSize.Far)
            {
                var written = address.GetText().Trim();
                Report(operand, model, diagnostics, on,
                    Catalogue.FarAddressInWord.Says(written, directive, written));
            }
        }
    }

    /// <summary><c>.res n</c> or <c>.res n, fill</c>: the count is a constant.</summary>
    private static void Reserved(
        SeparatedSyntaxList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (operands.Count == 0)
            return;
        if (operands.Count > 1)
            CheckRange(operands[1], model, diagnostics, Holds(".byte")!.Value, on);

        // `.res` is padding, written straight through to ca65's own, which reserves at most
        // $ffff bytes: padding of more than a bank's worth is not padding. A declaration is
        // not limited that way — one bigger than this is written as several directives.
        var count = model.ValueOf(operands[0], on).AsNumber();
        if (count is null)
            Report(operands[0], model, diagnostics, on, Catalogue.ResCountNotConstant);
        else if (count is < 0 or > 0xffff)
            Report(operands[0], model, diagnostics, on, Catalogue.ResCountOutOfRange.Says(count));
    }

    /// <summary>An alignment is a constant power of two, which is what ca65 will take.</summary>
    private static void Alignment(
        SeparatedSyntaxList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (operands.Count == 0)
            return;
        var boundary = model.ValueOf(operands[0], on).AsNumber();
        if (boundary is null)
            Report(operands[0], model, diagnostics, on, Catalogue.AlignBoundaryNotConstant);
        else if (boundary is < 1 or > 0x10000 || (boundary & (boundary - 1)) != 0)
            Report(operands[0], model, diagnostics, on, Catalogue.AlignBoundaryNotPowerOfTwo.Says(boundary));
    }

    /// <summary>
    /// Outside a charmap, text is ASCII and <c>\xHH</c> writes any byte, so a character
    /// typed directly above <c>$7f</c> is an error rather than a byte of some encoding.
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
            Report(argument, model, diagnostics, on, Catalogue.AddressNegative.Says(value));
        else if (value < limit.Low || value > limit.High)
            Report(argument, model, diagnostics, on, Catalogue.ValueTooWide.Says(Value.Of(value), slot));
    }

    private static void Report(
        SyntaxNode node, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on,
        DiagnosticMessage message, DiagnosticFix? fix = null) =>
        diagnostics?.Add(Expansion.Problem(model.Tree, node.Tree, node.Span, on, null, message) with { Fix = fix });
}
