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
                model.Check(operand, diagnostics, on, written: true);
        }
        new Checks(model, diagnostics, on).Check(directive);
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

    /// <summary>
    /// Returns why an operand whose value only the linker knows does not fit a slot of
    /// <paramref name="bytes"/> bytes, or null when it fits. ca65 refuses the fragment with a
    /// range error, or ld65 refuses the value, so nt65 reports it first, with the fix. An
    /// address, or an address plus or minus a constant, that is wider than the slot is reported
    /// as such. Any other operand in a one-byte or two-byte slot that names an address wider than
    /// the slot, or placed past its reach, is reported unless <see cref="LinkRange"/> shows its
    /// value always fits the slot. The output then keeps the low part of the value.
    /// </summary>
    /// <param name="operand">The value of the slot.</param>
    /// <param name="bytes">The width of the slot.</param>
    /// <param name="slot">The words that say how wide the slot is, as the message shows them.</param>
    /// <param name="model">The model the operand is read in.</param>
    /// <param name="on">The expansion the operand is in.</param>
    /// <param name="inBank">
    /// A value indicating whether the slot holds the address within its bank, as an <c>.addr</c>
    /// does, so that an address placed past the slot's reach is not refused.
    /// </param>
    public static DiagnosticMessage? TooWide(
        SyntaxNode operand, int bytes, string slot, SemanticModel model, Expansion? on, bool inBank = false)
    {
        if (model.ValueOf(operand, on).AsNumber() is not null)
            return null;

        if (AddressIn(operand, model, on) is { } address && model.AddressSizeOf(address, null, on) is { } size
            && (int)size > bytes)
        {
            var text = address.GetText().Trim();
            var fix = bytes == 1 ? $"use `<{text}` for its low byte" : $"use `.loword({text})` for its low 16 bits";
            return Catalogue.AddressDoesNotFit.Message(
                text, size == AddressSize.Far ? "a far" : "an absolute", slot, fix);
        }

        // ca65 refuses an address wider than the slot whatever the value comes to, and ld65
        // refuses a value that does not fit it. The output keeps the low part of a value that
        // always fits the slot, and anything else is refused here.
        if (bytes is not (1 or 2) || !LinkRange.NamesWideAddress(model, operand, on, bytes, placed: !inBank)
            || LinkRange.Fits(LinkRange.Of(model, operand, on), bytes))
        {
            return null;
        }
        var value = operand.GetText().Trim();
        return Catalogue.LinkedValueMayNotFit.Message(value, slot,
            bytes == 1 ? $"use `<({value})` for its low byte" : $"use `.loword({value})` for its low 16 bits");
    }

    /// <summary>Returns the unit of an element count as a message states it, in agreement with the count.</summary>
    private static string Elements(long count) => count == 1 ? "element" : "elements";

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
    /// Checks one directive's values for what the assembler would refuse, reading each value in
    /// one expansion and reporting to one list.
    /// </summary>
    /// <param name="model">The model the values are read in.</param>
    /// <param name="diagnostics">Where problems are reported, or null when the caller has already reported them.</param>
    /// <param name="on">The expansion the directive is in.</param>
    private sealed class Checks(SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        /// <summary>Reports what the assembler would refuse about a directive's values.</summary>
        public void Check(StatementSyntax directive)
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
                Report(directive,
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
                CheckCount(counted);

            if (element.IsRecord)
            {
                if (element.Type is { } named && model.SymbolOf(named) is { IsLayout: true } type)
                    Records(type, directive, operands);
                return;
            }
            foreach (var operand in operands)
            {
                if (operand is RecordValuesSyntax or ValueListSyntax)
                    Report(operand, Catalogue.ElementNotAValue.Message(name));
            }

            switch (kind)
            {
                case DirectiveKind.Strz:
                    Terminated(directive, operands);
                    break;
                case DirectiveKind.Byte:
                    Values(operands, kind);
                    foreach (var operand in operands)
                    {
                        if (TooWide(operand, 1, "`.byte` holds 8 bits", model, on) is { } message)
                            Report(operand, message);
                    }
                    break;

                // A far address in a 16-bit slot is not the address meant. In an `.addr`, ca65
                // would silently keep only its low 16 bits.
                case DirectiveKind.Word:
                case DirectiveKind.BeWord:
                case DirectiveKind.Addr:
                    Values(operands, kind);
                    Words(name, kind, operands);
                    break;
                case DirectiveKind.Long:
                case DirectiveKind.BeLong:
                case DirectiveKind.FarAddr:
                case DirectiveKind.Dword:
                case DirectiveKind.BeDword:
                    Values(operands, kind);
                    break;

                // The byte directives take an address and keep one byte of it, so no range limit
                // applies to their values.
                case DirectiveKind.LoBytes:
                case DirectiveKind.HiBytes:
                case DirectiveKind.BankBytes:
                    Values(operands, null);
                    break;

                case DirectiveKind.Res:
                    Reserved(operands);
                    break;

                case DirectiveKind.Align:
                    Alignment(operands);
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
        private void Terminated(StatementSyntax directive, SeparatedSyntaxList<SyntaxNode> operands)
        {
            if (operands.Count != 1 || TextOf(operands[0]) is not { } text)
            {
                Report(operands.Count > 0 ? operands[^1] : directive, Catalogue.StrzNotText);
                return;
            }
            var operand = operands[0];
            Values(operands, null);
            var at = Bytes(operand, model, on)?.ToList().IndexOf(0) ?? -1;
            if (at < 0)
                return;
            Report(operand, Catalogue.StrzZeroInText.Message(
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
        private string? TextOf(SyntaxNode operand)
        {
            if (operand is CallExpressionSyntax { Callee: { } callee } call
                && model.SymbolOf(callee, on) is { Kind: SymbolKind.Charmap })
            {
                return call.Arguments.Arguments is [ExpressionSyntax given] ? TextOf(given) : null;
            }
            return model.ValueOf(operand, on) is { Kind: ValueKind.String, Text: { } text } ? text : null;
        }

        /// <summary>
        /// Checks an array's element count. The count must be a constant, and when values are given
        /// it must match the number of values, and <c>[]</c> needs at least one value. A short table
        /// is exactly the mistake a count is there to catch, so values are never padded out to it,
        /// except for a single text, which is padded with zeros to the declared count.
        /// </summary>
        private void CheckCount(DataDirectiveSyntax directive)
        {
            if (directive.Count is not { } count)
                return;
            var (declared, given) = model.ElementsOf(directive, on);
            if (count.Count is not { } countExpression)
            {
                // Values nt65 cannot count, such as a macro call's, leave the count unknown, not empty.
                if (given == 0 || (given is null && directive.Tail is not BracedDataSyntax && DataSyntax.BodyOf(directive) is null))
                    Report(count, Catalogue.ElementCountEmpty.Message(directive.Directive.Text));
                return;
            }
            if (declared is null)
                Report(countExpression, Catalogue.ElementCountNotConstant);
            else if (declared < 0)
                Report(countExpression, Catalogue.ElementCountNegative.Message(declared));
            else if (given is { } values && values != declared && PaddedText.Padding(directive, model, on) is null)
                Report(count, Catalogue.ElementCountMismatch.Message(
                    declared, Elements(declared.Value), values, values == 1 ? "value is" : "values are"));
        }

        /// <summary>
        /// Checks the records given for an element type <c>.type T</c>. Each value must be one braced
        /// record, or a single record spread over several lines as the block the directive's line
        /// opens.
        /// </summary>
        private void Records(Symbol type, StatementSyntax directive, SeparatedSyntaxList<SyntaxNode> operands)
        {
            if (directive is DataDirectiveSyntax data)
            {
                if (data.Tail is BracedDataSyntax { Value: RecordValuesSyntax one })
                {
                    Initialized(type, one.Members);
                    return;
                }
                if (DataSyntax.BodyOf(data) is { BlockKind: BlockKind.RecordInitializer } body)
                {
                    Initialized(type, body.Members.Skip(1).OfType<LineSyntax>().Select(line => line.Statement)
                        .OfType<MemberValueSyntax>());
                    return;
                }
            }
            foreach (var operand in operands)
            {
                if (operand is RecordValuesSyntax record)
                    Initialized(type, record.Members);
                else
                    Report(operand, Catalogue.ElementNotARecord.Message($"a `{type.Name}` array", "a record"));
            }
        }

        /// <summary>
        /// Checks each value of a directive whose element type is <paramref name="holding"/>, or
        /// of one whose values have no range limit when <paramref name="holding"/> is null. Text
        /// must be ASCII and map to bytes, and a number must fit the element type.
        /// </summary>
        private void Values(SeparatedSyntaxList<SyntaxNode> operands, DirectiveKind? holding)
        {
            var limit = holding is { } kind ? Holds(kind) : null;
            foreach (var operand in operands)
            {
                CheckAscii(operand);
                if (Bytes(operand, model, on) is { } bytes)
                {
                    foreach (var value in bytes)
                    {
                        if (value is < 0 or > 255)
                            Report(operand, Catalogue.CharmapValueNotAByte.Message(Value.Of(value)));
                    }
                    continue;
                }
                if (limit is { } range)
                    CheckRange(operand, range, $"`{SyntaxFacts.TextOf(holding!.Value)}`");
            }
        }

        /// <summary>
        /// Checks the values a record gives its members, each of which must fit the room the member
        /// has. A one-element member takes one value, and an array member takes a braced list of as
        /// many values as it holds. A record member takes a braced record, and a member reserved with
        /// <c>.res</c> takes text no longer than its room. Anything else would be emitted past the
        /// member, or cut short, without a diagnostic.
        /// </summary>
        private void Initialized(Symbol type, IEnumerable<MemberValueSyntax> values)
        {
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                var given = value.Value;
                var name = value.Name.Text;
                if (type.Body?.FindMember(name) is not { Kind: SymbolKind.Member } member)
                {
                    Report(value, Catalogue.MemberUnknown.Message(type.Name, name));
                    continue;
                }

                if (!named.Add(name))
                {
                    Report(value, Catalogue.MemberGivenTwice.Message(name));
                    continue;
                }

                // Every member of a union is at offset 0, so a second value would write over the first.
                if (type.Kind == SymbolKind.Union && named.Count > 1)
                {
                    Report(value, Catalogue.UnionManyMembersGiven.Message(type.Name));
                    continue;
                }

                var element = member.Data as DataDirectiveSyntax;
                if (element?.Count is not null)
                {
                    ArrayMember(member, element, given);
                    continue;
                }
                if (member.Type is { IsLayout: true } inner)
                {
                    if (given is RecordValuesSyntax record)
                        Initialized(inner, record.Members);
                    else
                        Report(given, Catalogue.MemberNeedsARecord.Message(name, inner.Name));
                    continue;
                }
                if (given is RecordValuesSyntax or ValueListSyntax)
                {
                    Report(given, Catalogue.MemberTakesOneValue.Message(name));
                    continue;
                }
                Scalar(member, element?.Directive.DirectiveKind ?? DirectiveKind.Res, given, name);
            }
        }

        /// <summary>
        /// Checks an array member's value, which must be a braced list of exactly as many elements as
        /// the member holds.
        /// </summary>
        private void ArrayMember(Symbol member, DataDirectiveSyntax element, SyntaxNode given)
        {
            var spelled = element.Directive.Text;
            if (given is not ValueListSyntax list)
            {
                // Text given for an array most likely means the member should hold text, which an
                // array cannot, so the message also shows the `.res` member that would hold it, as
                // `member-not-text` does.
                var text = Bytes(given, model, on) is { Count: > 1 } bytes
                    ? $"; a member that holds text is reserved with `.res` and its length, as `{member.Name}: .res {Math.Max(member.Size ?? 0, bytes.Count)}`"
                    : "";
                Report(given, Catalogue.MemberNeedsAList.Message(member.Name, member.Name, text));
                return;
            }
            var items = list.Values;
            if (member.Count is { } count && items.Count != count)
                Report(given, Catalogue.MemberCountMismatch.Message(member.Name, count, Elements(count), items.Count));
            foreach (var item in items)
            {
                if (member.Type is { IsLayout: true } inner)
                {
                    if (item is RecordValuesSyntax record)
                        Initialized(inner, record.Members);
                    else
                        Report(item, Catalogue.ElementNotARecord.Message($"`{member.Name}`", $"a `{inner.Name}`"));
                    continue;
                }
                if (item is RecordValuesSyntax or ValueListSyntax)
                {
                    Report(item, Catalogue.ElementIsOneValue.Message(member.Name, spelled));
                    continue;
                }
                Scalar(member, element.Directive.DirectiveKind, item, member.Name, inArray: true);
            }
        }

        /// <summary>
        /// Checks one value for a one-element member, or for one element of an array member, which
        /// <paramref name="inArray"/> says.
        /// </summary>
        private void Scalar(Symbol member, DirectiveKind element, SyntaxNode given, string name, bool inArray = false)
        {
            var bytes = Bytes(given, model, on);
            if (element == DirectiveKind.Res)
            {
                if (bytes is not null && bytes.Count > member.Size)
                    Report(given, Catalogue.MemberTextTooLong.Message(name, member.Size, bytes.Count));
                return;
            }
            if (bytes is { Count: > 1 })
            {
                // Text goes in a member reserved with `.res`, so the message shows the one that would
                // hold this text and keep at least the room the member has now.
                var type = SyntaxFacts.TextOf(element);
                Report(given, Catalogue.MemberNotText.Message(
                    inArray ? $"each element of `{name}` is one `{type}`" : $"`{name}` is one `{type}`",
                    name, Math.Max(member.Size ?? 0, bytes.Count)));
                return;
            }
            if (bytes is null && Holds(element) is { } range)
                CheckRange(given, range, $"`{name}`, a `{SyntaxFacts.TextOf(element)}`");
            var width = element switch
            {
                DirectiveKind.Byte => 1,
                DirectiveKind.Word or DirectiveKind.BeWord or DirectiveKind.Addr => 2,
                _ => 0,
            };
            if (bytes is null && width > 0
                && TooWide(given, width, $"`{name}` holds {8 * width} bits", model, on, inBank: element == DirectiveKind.Addr) is { } message)
            {
                Report(given, message);
            }
        }

        /// <summary>
        /// Reports a value of a 16-bit slot that may not fit it. A far address, or a far address plus
        /// or minus a constant, is reported as such. ca65 keeps the low 16 bits of a far address in an
        /// <c>.addr</c> and refuses it in a <c>.word</c>, so in either case the source does not say
        /// what is meant. Any other value that names an address is checked as
        /// <see cref="TooWide"/> checks it. An <c>.addr</c> holds the address within its bank, so an
        /// absolute address placed past <c>$ffff</c> is not reported there.
        /// </summary>
        private void Words(string directive, DirectiveKind kind, SeparatedSyntaxList<SyntaxNode> operands)
        {
            foreach (var operand in operands)
            {
                if (AddressIn(operand, model, on) is { } address && model.AddressSizeOf(address, null, on) == AddressSize.Far)
                {
                    var text = address.GetText().Trim();
                    Report(operand, Catalogue.FarAddressInWord.Message(text, directive, text));
                }
                else if (TooWide(operand, 2, $"`{directive}` holds 16 bits", model, on, inBank: kind == DirectiveKind.Addr) is { } message)
                {
                    Report(operand, message);
                }
            }
        }

        /// <summary>
        /// Checks a <c>.res n</c> or <c>.res n, fill</c>, verifying that the count is a constant in
        /// range and that the fill is a byte.
        /// </summary>
        private void Reserved(SeparatedSyntaxList<SyntaxNode> operands)
        {
            if (operands.Count == 0)
                return;
            if (operands.Count > 1)
                CheckFill(operands[1], "a `.res`");

            // `.res` is padding, passed straight through to ca65's own `.res`, which reserves at
            // most $ffff bytes, so padding of more than a bank's worth is not padding. A declaration
            // is not limited that way, because one bigger than this is emitted as several directives.
            var count = model.ValueOf(operands[0], on).AsNumber();
            if (count is null)
                Report(operands[0], Catalogue.ResCountNotConstant);
            else if (count is < 0 or > MaxReservation)
                Report(operands[0], Catalogue.ResCountOutOfRange.Message(count));
        }

        /// <summary>
        /// Checks the byte a <c>.res</c> or an <c>.align</c> fills with, which ca65 has to know when it
        /// reaches the line and which has to fit in a byte.
        /// </summary>
        private void CheckFill(SyntaxNode fill, string directive)
        {
            if (model.ValueOf(fill, on).AsNumber() is null && !model.ValueOf(fill, on).IsString)
                Report(fill, Catalogue.FillNotConstant.Message(directive));
            else
                CheckRange(fill, Holds(DirectiveKind.Byte)!.Value, "a byte");
        }

        /// <summary>
        /// Checks an <c>.align</c>. The alignment must be a constant power of two, which is what ca65
        /// will take, and the fill it pads with must be a byte, as a <c>.res</c> fill is.
        /// </summary>
        private void Alignment(SeparatedSyntaxList<SyntaxNode> operands)
        {
            if (operands.Count == 0)
                return;
            if (operands.Count > 1)
                CheckFill(operands[1], "an `.align`");
            var boundary = model.ValueOf(operands[0], on).AsNumber();
            if (boundary is null)
                Report(operands[0], Catalogue.AlignBoundaryNotConstant);
            else if (boundary is < 1 or > 0x10000 || (boundary & (boundary - 1)) != 0)
                Report(operands[0], Catalogue.AlignBoundaryNotPowerOfTwo.Message(boundary));
        }

        /// <summary>
        /// Checks that text typed directly is ASCII. Outside a charmap, text is ASCII and
        /// <c>\xHH</c> emits any byte, so a character typed directly above <c>$7f</c> is an error
        /// rather than a byte of some encoding.
        /// </summary>
        private void CheckAscii(SyntaxNode argument)
        {
            if (argument is LiteralExpressionSyntax literal and (StringExpressionSyntax or CharacterExpressionSyntax)
                && literal.Token.Text.Any(c => c > 127))
            {
                Report(argument, Catalogue.TextNotAscii);
            }
        }

        /// <summary>
        /// Checks that a constant value fits <paramref name="limit"/>, the range of the slot that
        /// <paramref name="slot"/> names in the message. A negative value in an address slot is
        /// reported as such, because no address is negative.
        /// </summary>
        private void CheckRange(SyntaxNode argument, (long Low, long High) limit, string slot)
        {
            if (model.ValueOf(argument, on).AsNumber() is not { } value)
                return;
            if (value < 0 && limit.Low == 0)
                Report(argument, Catalogue.AddressNegative.Message(value));
            else if (value < limit.Low || value > limit.High)
                Report(argument, Catalogue.ValueTooWide.Message(Value.Of(value), slot));
        }

        /// <summary>
        /// Reports a problem at <paramref name="node"/> in the expansion being checked, with
        /// <paramref name="fix"/> where one is offered. Nothing is reported when the caller has
        /// already reported the directive's problems.
        /// </summary>
        private void Report(SyntaxNode node, DiagnosticMessage message, DiagnosticFix? fix = null) =>
            diagnostics?.Add(Expansion.Problem(model.Tree, node.Tree, node.Span, on, null, message) with { Fix = fix });
    }
}
