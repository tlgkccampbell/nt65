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
                Values(operands, model, diagnostics, (-128, 255), on);
                break;
            case ".word":
                Values(operands, model, diagnostics, (-32768, 65535), on);
                break;
            case ".dword":
                Values(operands, model, diagnostics, (-2147483648, 4294967295), on);
                break;

            // An address directive takes whatever fits its own width, and the byte
            // directives take an address and keep one byte of it, so neither limits a value.
            case ".addr":
            case ".faraddr":
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

    /// <summary><c>.res n</c> or <c>.res n, fill</c>: the count is a constant.</summary>
    private static void Reserved(
        IReadOnlyList<SyntaxNode> operands, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on)
    {
        if (operands.Count == 0)
            return;
        if (operands.Count > 1)
            CheckRange(operands[1], model, diagnostics, (-128, 255), on);

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
        Expansion? on)
    {
        if (model.ValueOf(argument, on).AsNumber() is { } value && (value < limit.Low || value > limit.High))
            Report(argument, model, diagnostics, on, $"{Value.Of(value)} does not fit in this directive");
    }

    private static void Report(
        SyntaxNode node, SemanticModel model, List<Diagnostic>? diagnostics, Expansion? on, string message) =>
        diagnostics?.Add(Expansion.Problem(model.Tree, node.Tree, node.Span, on, Severity.Error, message));
}
