using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// What a data directive must satisfy for ca65 to accept it, and how many bytes it comes
/// to. How much room a directive takes is a question about what the program means, so it is
/// answered once, by the semantic model; what is left here is what the assembler will
/// refuse: a value too wide for the directive holding it, text that is not bytes, and a
/// reservation whose count nt65 cannot work out.
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
    /// The length of <paramref name="directive"/>, <see cref="Unpredictable"/> for one whose
    /// length only the assembler settles, or null for one nt65 cannot write at all. Anything
    /// wrong with its values goes to <paramref name="diagnostics"/>, which callers that have
    /// already reported pass as null.
    /// </summary>
    public static int? Of(
        SyntaxNode directive, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on = null)
    {
        if (directive.ChildTokens.Length == 0)
            return null;
        if (diagnostics is not null)
        {
            foreach (var operand in directive.ChildNodes)
                model.Check(operand, diagnostics, on);
        }
        Check(directive, model, diagnostics, on);
        if (directive.ChildTokens[0].Text.Equals(".align", StringComparison.OrdinalIgnoreCase))
            return Unpredictable;
        return model.RoomFor(directive, on) is { } room && room.Bytes is >= 0 and <= int.MaxValue
            ? (int)room.Bytes
            : null;
    }

    /// <summary>The bytes an operand becomes: a literal, or text a charmap maps.</summary>
    public static IReadOnlyList<long>? Bytes(SyntaxNode argument, SemanticModel model, Expansion? on = null) =>
        model.BytesOf(argument, on);

    /// <summary>What the assembler would refuse about a directive's values.</summary>
    private static void Check(
        SyntaxNode directive, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        var name = directive.ChildTokens[0].Text.ToLowerInvariant();
        var operands = directive.ChildNodes;

        switch (name)
        {
            case ".byte":
            case ".asciiz":
                Values(operands, model, diagnostics, (0, 255), on);
                foreach (var operand in operands)
                {
                    if (TooWide(operand, 1, "`.byte` holds 8 bits", model, on) is { } message)
                        Report(operand, model, diagnostics, on, message);
                }
                break;
            case ".word":
                Values(operands, model, diagnostics, (0, 65535), on);
                NoFarAddresses(name, operands, model, diagnostics, on);
                break;
            case ".dword":
                Values(operands, model, diagnostics, (-2147483648, 4294967295), on);
                break;

            // An address is unsigned, and one that does not fit is not the address meant: ca65
            // would keep the low bits of it without a word.
            case ".addr":
                Values(operands, model, diagnostics, (0, 0xffff), on);
                NoFarAddresses(name, operands, model, diagnostics, on);
                break;
            case ".faraddr":
                Values(operands, model, diagnostics, (0, 0xffffff), on);
                break;

            // The byte directives take an address and keep one byte of it, so they limit nothing.
            case ".lobytes":
            case ".hibytes":
                Values(operands, model, diagnostics, null, on);
                break;

            case ".res":
                Reserved(operands, model, diagnostics, on);
                break;

            case ".align":
                Alignment(operands, model, diagnostics, on);
                break;

            case ".tag" when operands.Length > 0 && model.SymbolOf(operands[0]) is { IsLayout: true } type:
                Initialized(type, InitializerValues(directive), model, diagnostics, on);
                break;

            default:
                break;
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
    /// The <c>member = value</c>s of an initialized instance: braced on the directive's line,
    /// or the lines of the block the line opens.
    /// </summary>
    private static IEnumerable<SyntaxNode> InitializerValues(SyntaxNode directive)
    {
        if (directive.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.TagValues) is { } braced)
            return braced.ChildNodes.Where(c => c.Kind == SyntaxKind.TagValue);

        var line = directive.Parent;
        while (line is not null && line.Green is not GreenLine)
            line = line.Parent;
        if (line?.Parent is { Green: GreenBlock { BlockKind: BlockKind.TagInitializer } } block
            && block.ChildNodes.Length > 0 && block.ChildNodes[0] == line)
        {
            return block.ChildNodes.Skip(1)
                .Select(child => child.Statement)
                .OfType<SyntaxNode>()
                .Where(statement => statement.Kind == SyntaxKind.TagValue);
        }
        return [];
    }

    /// <summary>
    /// The values an initialized instance gives its members, each of which must fit the room
    /// the member has: a one-element member takes one value, and text a member reserved with
    /// <c>.res</c>, no longer than the room. Anything else would be written past the member,
    /// or cut short, with nothing said.
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

            if (member.Type is { IsLayout: true } inner)
            {
                if (given.Kind == SyntaxKind.TagValues)
                    Initialized(inner, given.ChildNodes.Where(c => c.Kind == SyntaxKind.TagValue), model, diagnostics, on);
                else
                    Report(given, model, diagnostics, on, $"`{name}` is a `{inner.Name}`, which takes a braced list of its members");
                continue;
            }
            if (given.Kind == SyntaxKind.TagValues)
            {
                Report(given, model, diagnostics, on, $"`{name}` is not a record, and takes one value");
                continue;
            }

            var element = member.Data is { ChildTokens.Length: > 0 } data ? data.ChildTokens[0].Text.ToLowerInvariant() : ".res";
            var bytes = Bytes(given, model, on);
            if (element == ".res")
            {
                if (bytes is not null && bytes.Count > member.Size)
                    Report(given, model, diagnostics, on, $"`{name}` has room for {member.Size} bytes, and this is {bytes.Count}");
                continue;
            }
            if (bytes is { Count: > 1 })
            {
                Report(given, model, diagnostics, on,
                    $"`{name}` is one `{element}`, and this text is {bytes.Count} bytes: text takes a member reserved with `.res`");
                continue;
            }
            if (bytes is null && element switch
            {
                ".byte" => (-128L, 255L),
                ".word" => (-32768L, 65535L),
                ".dword" => (-2147483648L, 4294967295L),
                _ => ((long, long)?)null,
            } is { } range)
            {
                CheckRange(given, model, diagnostics, range, on, $"`{name}`, a `{element}`");
            }
        }
    }

    /// <summary>
    /// A far address in a 16-bit slot. ca65 keeps the low 16 bits of one in an <c>.addr</c>
    /// and refuses one in a <c>.word</c>, so either way what was written is not what is meant.
    /// An address, or an address plus or minus a constant, is what is looked at: anything
    /// else, such as <c>.loword(far)</c> or the difference of two addresses, says what it keeps.
    /// </summary>
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
            CheckRange(operands[1], model, diagnostics, (0, 255), on);

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
        if (value < 0 && limit.Low == 0 && Negative(value, limit.High) is { } negative)
            Report(argument, model, diagnostics, on, negative);
        else if (value < limit.Low || value > limit.High)
            Report(argument, model, diagnostics, on, $"{Value.Of(value)} does not fit in {slot}");
    }

    /// <summary>
    /// What to say about a negative value in a slot that holds 0 to <paramref name="high"/>,
    /// or null when it does not fit even as a two's complement. ca65 refuses a negative
    /// value in a byte or a word, so the unsigned value is what has to be written.
    /// </summary>
    public static string? Negative(long value, long high) =>
        value >= -(high + 1) / 2
            ? $"{value} is negative, and ca65 takes no negative value here: its two's complement is {Value.Of(value & high)}"
            : null;

    private static void Report(
        SyntaxNode node, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on, string message) =>
        diagnostics?.Add(Expansion.Problem(model.Tree, node.Tree, node.Span, on, Severity.Error, message));
}
