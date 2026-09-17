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
        SyntaxNode directive, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on = null)
    {
        if (directive.Kind != SyntaxKind.DataValues && directive.ChildTokens.Length == 0)
            return null;
        if (diagnostics is not null)
        {
            foreach (var operand in ElementsOf(directive))
                model.Check(operand, diagnostics, on);
        }
        Check(directive, model, diagnostics, on);
        if (DataSyntax.NameOf(directive) == ".align")
            return Unpredictable;
        if (DataSyntax.BodyOf(directive) is { Green: GreenBlock { BlockKind: BlockKind.DataBody } })
            return 0;
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
    public static IReadOnlyList<SyntaxNode> ElementsOf(SyntaxNode directive) =>
        directive.Kind == SyntaxKind.DataValues
            ? directive.ChildNodes
            : DataSyntax.BracedOf(directive) is { Kind: SyntaxKind.ValueList } list
                ? list.ChildNodes
                : DataSyntax.ValuesOf(directive);

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
        SyntaxNode directive, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        var element = directive.Kind == SyntaxKind.DataValues ? DataSyntax.DirectiveOfValues(directive) : directive;
        if (element is null)
            return;
        var name = DataSyntax.NameOf(element);
        var operands = ElementsOf(directive);

        // Storage is declared with its type, and padding has no name to declare.
        if (directive.Parent is { Kind: SyntaxKind.DataDeclaration } && name is ".res" or ".align")
        {
            Report(directive, model, diagnostics, on, name == ".res"
                ? "`.res` is only padding: data is declared with its type, `.byte[n]`, and holds zeros where it gives no values"
                : "`.align` is only padding, and has no name: it goes between declarations",

                // The room a `.res` reserves is the count of the `.byte[n]` that replaces it.
                // An `.align` is a place to write the declaration rather than a way to write it.
                name == ".res" && on is null && directive.Tree == model.Tree
                    ? new DiagnosticFix(FixKind.Storage)
                    : null);
            return;
        }
        if (directive.Kind == SyntaxKind.DataDirective && DataSyntax.IsElementType(directive))
            CheckCount(directive, model, diagnostics, on);

        if (DataSyntax.IsRecord(element))
        {
            if (DataSyntax.TypeOf(element) is { } named && model.SymbolOf(named) is { IsLayout: true } type)
                Records(type, directive, operands, model, diagnostics, on);
            return;
        }
        foreach (var operand in operands)
        {
            if (operand.Kind is SyntaxKind.RecordValues or SyntaxKind.ValueList)
                Report(operand, model, diagnostics, on, $"a `{name}` element is one value, and braces hold a record or a list");
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
        SyntaxNode directive, IReadOnlyList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics,
        Expansion? on)
    {
        if (operands.Count != 1 || TextOf(operands[0], model, on) is not { } text)
        {
            Report(operands.Count > 0 ? operands[^1] : directive, model, diagnostics, on,
                "`.strz` takes one text: a string, a string constant, or a charmap applied to one");
            return;
        }
        var operand = operands[0];
        Values(operands, model, diagnostics, null, on);
        var at = Bytes(operand, model, on)?.ToList().IndexOf(0) ?? -1;
        if (at < 0)
            return;
        Report(operand, model, diagnostics, on,
            operand.Kind == SyntaxKind.CallExpression && at < text.Length
                ? $"`{operand.ChildNodes[0].GetText().Trim()}` maps `{text[at]}` to $00, which would end the text early: `.strz` writes the zero that ends it"
                : "the text holds a zero, which would end it early: `.strz` writes the zero that ends it");
    }

    /// <summary>
    /// The text an operand is: a string or a string constant, or the text a charmap is applied
    /// to. Null for anything else, a character among them, which is a number.
    /// </summary>
    private static string? TextOf(SyntaxNode operand, SemanticModel model, Expansion? on)
    {
        if (operand is { Kind: SyntaxKind.CallExpression, ChildNodes: [{ Kind: SyntaxKind.NameExpression } callee, var arguments] }
            && model.SymbolOf(callee, on) is { Kind: SymbolKind.Charmap })
        {
            return arguments.ChildNodes is [var given] ? TextOf(given, model, on) : null;
        }
        return model.ValueOf(operand, on) is { Kind: ValueKind.String, Text: { } text } ? text : null;
    }

    /// <summary>
    /// An array's count: a constant, and the number its values come to when it has values. A
    /// short table is exactly the mistake a count is there to catch, so values are never padded
    /// out to it.
    /// </summary>
    private static void CheckCount(SyntaxNode directive, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (DataSyntax.CountOf(directive) is not { } count)
            return;
        var (declared, given) = model.ElementsOf(directive, on);
        if (DataSyntax.CountExpressionOf(directive) is not { } written)
        {
            if (given is null && DataSyntax.BracedOf(directive) is null && DataSyntax.BodyOf(directive) is null)
            {
                Report(count, model, diagnostics, on,
                    $"`[]` counts the values given, and there are none: `{directive.ChildTokens[0].Text}[n]` holds n");
            }
            return;
        }
        if (declared is null)
            Report(written, model, diagnostics, on, "an array's count is a constant");
        else if (declared < 0)
            Report(written, model, diagnostics, on, $"an array's count cannot be negative, and this one is {declared}");
        else if (given is { } values && values != declared)
            Report(count, model, diagnostics, on, $"this array holds {declared} {Elements(declared.Value)}, and its values come to {values}");
    }

    private static string Elements(long count) => count == 1 ? "element" : "elements";

    /// <summary>
    /// The records an element type of <c>.type T</c> gives: each value is one braced record,
    /// and one record written over several lines is the block the directive's line opens.
    /// </summary>
    private static void Records(
        Symbol type, SyntaxNode directive, IReadOnlyList<SyntaxNode> operands, SemanticModel model,
        List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (directive.Kind == SyntaxKind.DataDirective)
        {
            if (DataSyntax.BracedOf(directive) is { Kind: SyntaxKind.RecordValues } one)
            {
                Initialized(type, one.ChildNodes.Where(c => c.Kind == SyntaxKind.MemberValue), model, diagnostics, on);
                return;
            }
            if (DataSyntax.BodyOf(directive) is { Green: GreenBlock { BlockKind: BlockKind.RecordInitializer } } lines)
            {
                Initialized(type, lines.ChildNodes.Skip(1).Select(line => line.Statement).OfType<SyntaxNode>()
                    .Where(statement => statement.Kind == SyntaxKind.MemberValue), model, diagnostics, on);
                return;
            }
        }
        foreach (var operand in operands)
        {
            if (operand.Kind == SyntaxKind.RecordValues)
                Initialized(type, operand.ChildNodes.Where(c => c.Kind == SyntaxKind.MemberValue), model, diagnostics, on);
            else
                Report(operand, model, diagnostics, on, $"each element of a `{type.Name}` array is a record, written `{{ member = value }}`");
        }
    }

    private static void Values(
        IReadOnlyList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics,
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
                        Report(operand, model, diagnostics, on, $"`{(char)value}` is not a byte; a charmap maps text to bytes");
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
        Symbol type, IEnumerable<SyntaxNode> values, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value.ChildTokens.Length == 0 || value.ChildNodes.LastOrDefault() is not { } given)
                continue;
            var name = value.ChildTokens[0].Text;
            if (type.Body?.FindMember(name) is not { Kind: SymbolKind.Member } member)
            {
                Report(value, model, diagnostics, on, $"`{type.Name}` has no member `{name}`");
                continue;
            }

            // Every member of a union is at offset 0, so a second value would write over the first.
            if (!named.Add(name))
            {
                Report(value, model, diagnostics, on, $"`{name}` is given a value twice: a member is named at most once");
                continue;
            }
            if (type.Kind == SymbolKind.Union && named.Count > 1)
            {
                Report(value, model, diagnostics, on,
                    $"`{type.Name}` is a union, whose members all start at offset 0, so it takes a value for at most one of them");
                continue;
            }

            var element = member.Data is { Kind: SyntaxKind.DataDirective } data ? data : null;
            if (element is not null && DataSyntax.CountOf(element) is not null)
            {
                ArrayMember(member, element, given, model, diagnostics, on);
                continue;
            }
            if (member.Type is { IsLayout: true } inner)
            {
                if (given.Kind == SyntaxKind.RecordValues)
                    Initialized(inner, given.ChildNodes.Where(c => c.Kind == SyntaxKind.MemberValue), model, diagnostics, on);
                else
                    Report(given, model, diagnostics, on, $"`{name}` is a `{inner.Name}`, which takes a braced list of its members");
                continue;
            }
            if (given.Kind is SyntaxKind.RecordValues or SyntaxKind.ValueList)
            {
                Report(given, model, diagnostics, on, $"`{name}` is not a record or an array, and takes one value");
                continue;
            }
            Scalar(member, element is null ? ".res" : DataSyntax.NameOf(element), given, name, model, diagnostics, on);
        }
    }

    /// <summary>An array member's value: a braced list, of exactly as many elements as the member holds.</summary>
    private static void ArrayMember(
        Symbol member, SyntaxNode element, SyntaxNode given, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        var spelled = element.ChildTokens[0].Text;
        if (given.Kind != SyntaxKind.ValueList)
        {
            Report(given, model, diagnostics, on, $"`{member.Name}` is an array, which takes a braced list: `{member.Name} = {{ … }}`");
            return;
        }
        var items = given.ChildNodes;
        if (member.Count is { } count && items.Length != count)
            Report(given, model, diagnostics, on, $"`{member.Name}` holds {count} {Elements(count)}, and this list gives {items.Length}");
        foreach (var item in items)
        {
            if (member.Type is { IsLayout: true } inner)
            {
                if (item.Kind == SyntaxKind.RecordValues)
                    Initialized(inner, item.ChildNodes.Where(c => c.Kind == SyntaxKind.MemberValue), model, diagnostics, on);
                else
                    Report(item, model, diagnostics, on, $"each element of `{member.Name}` is a `{inner.Name}`, written `{{ member = value }}`");
                continue;
            }
            if (item.Kind is SyntaxKind.RecordValues or SyntaxKind.ValueList)
            {
                Report(item, model, diagnostics, on, $"each element of `{member.Name}` is one `{spelled}`");
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
                Report(given, model, diagnostics, on, $"`{name}` has room for {member.Size} bytes, and this is {bytes.Count}");
            return;
        }
        if (bytes is { Count: > 1 })
        {
            Report(given, model, diagnostics, on,
                $"`{name}` is one `{element}`, and this text is {bytes.Count} bytes: text takes a member reserved with `.res`");
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
    public static string? TooWide(SyntaxNode operand, int bytes, string slot, SemanticModel model, Expansion? on)
    {
        if (model.ValueOf(operand, on).AsNumber() is not null || AddressIn(operand, model, on) is not { } address
            || model.AddressSizeOf(address, null, on) is not { } size || (int)size <= bytes)
        {
            return null;
        }
        var written = address.GetText().Trim();
        var fix = bytes == 1 ? $"`<{written}` is its low byte" : $"`.loword({written})` is its low 16 bits";
        return $"`{written}` is {(size == AddressSize.Far ? "a far" : "an absolute")} address, and {slot}: {fix}";
    }

    /// <summary>The name an operand is, or is a constant away from, or null when it is neither.</summary>
    private static SyntaxNode? AddressIn(SyntaxNode operand, SemanticModel model, Expansion? on)
    {
        var address = operand;
        if (operand is { Kind: SyntaxKind.BinaryExpression, ChildNodes: [var left, var right] }
            && operand.ChildTokens.Any(t => t.Kind is SyntaxKind.Plus or SyntaxKind.Minus))
        {
            address = model.ValueOf(right, on).AsNumber() is not null ? left
                : model.ValueOf(left, on).AsNumber() is not null && operand.ChildTokens.Any(t => t.Kind == SyntaxKind.Plus) ? right
                : operand;
        }
        return address.Kind == SyntaxKind.NameExpression ? address : null;
    }

    /// <summary>
    /// A far address in a 16-bit slot. ca65 keeps the low 16 bits of one in an <c>.addr</c>
    /// and refuses one in a <c>.word</c>, so either way what was written is not what is meant.
    /// An address, or an address plus or minus a constant, is what is looked at: anything
    /// else, such as <c>.loword(far)</c> or the difference of two addresses, says what it keeps.
    /// </summary>
    private static void NoFarAddresses(
        string directive, IReadOnlyList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics,
        Expansion? on)
    {
        foreach (var operand in operands)
        {
            if (AddressIn(operand, model, on) is { } address && model.AddressSizeOf(address, null, on) == AddressSize.Far)
            {
                var written = address.GetText().Trim();
                Report(operand, model, diagnostics, on,
                    $"`{written}` is a far address, and `{directive}` holds 16 bits: `.faraddr` holds all of it, "
                    + $"and `.loword({written})` the low 16 bits");
            }
        }
    }

    /// <summary><c>.res n</c> or <c>.res n, fill</c>: the count is a constant.</summary>
    private static void Reserved(
        IReadOnlyList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (operands.Count == 0)
            return;
        if (operands.Count > 1)
            CheckRange(operands[1], model, diagnostics, Holds(".byte")!.Value, on);

        var count = model.ValueOf(operands[0], on).AsNumber();
        if (count is null)
            Report(operands[0], model, diagnostics, on, "a `.res` count must be a constant");
        else if (count is < 0 or > 0xffffff)
            Report(operands[0], model, diagnostics, on, $"a `.res` count must be between 0 and $ffffff, not {count}");
    }

    /// <summary>An alignment is a constant power of two, which is what ca65 will take.</summary>
    private static void Alignment(
        IReadOnlyList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (operands.Count == 0)
            return;
        var boundary = model.ValueOf(operands[0], on).AsNumber();
        if (boundary is null)
            Report(operands[0], model, diagnostics, on, "an `.align` boundary must be a constant");
        else if (boundary is < 1 or > 0x10000 || (boundary & (boundary - 1)) != 0)
            Report(operands[0], model, diagnostics, on, $"an `.align` boundary must be a power of two, not {boundary}");
    }

    /// <summary>
    /// Outside a charmap, text is ASCII and <c>\xHH</c> writes any byte, so a character
    /// typed directly above <c>$7f</c> is an error rather than a byte of some encoding.
    /// </summary>
    private static void CheckAscii(SyntaxNode argument, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (argument.Kind is not (SyntaxKind.StringExpression or SyntaxKind.CharacterExpression))
            return;
        foreach (var token in argument.ChildTokens)
        {
            if (token.Text.Any(c => c > 127))
            {
                Report(argument, model, diagnostics, on,
                    "text is ASCII outside a charmap; write `\\xHH` for a byte above $7f");
                return;
            }
        }
    }

    private static void CheckRange(
        SyntaxNode argument, SemanticModel model, List<Diagnostic>? diagnostics, (long Low, long High) limit,
        Expansion? on, string slot = "this directive")
    {
        if (model.ValueOf(argument, on).AsNumber() is not { } value)
            return;
        if (value < 0 && limit.Low == 0)
            Report(argument, model, diagnostics, on, $"{value} is negative, and an address is not");
        else if (value < limit.Low || value > limit.High)
            Report(argument, model, diagnostics, on, $"{Value.Of(value)} does not fit in {slot}");
    }

    private static void Report(
        SyntaxNode node, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on, string message,
        DiagnosticFix? fix = null) =>
        diagnostics?.Add(Expansion.Problem(model.Tree, node.Tree, node.Span, on, Severity.Error, message) with { Fix = fix });
}
